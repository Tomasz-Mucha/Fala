using System.Collections.Concurrent;
using Fala.Models;

namespace Fala.Services;

public sealed record RefreshSummary(int Ok, int Failed, IReadOnlyList<(Podcast Podcast, int NewEpisodes)> WithNew)
{
    public int TotalNew => WithNew.Sum(x => x.NewEpisodes);
}

/// <summary>
/// Odświeżanie wszystkich subskrypcji: ręczne („Odśwież wszystko”) i automatyczne w tle,
/// co <see cref="AppSettings.AutoRefreshHours"/> godzin.
/// </summary>
public sealed class FeedRefreshService
{
    /// <summary>Dostępne interwały w godzinach; 0 = wyłączone.</summary>
    public static readonly int[] IntervalChoices = [0, 1, 3, 6, 12];

    // Pierwsze automatyczne sprawdzenie najwcześniej minutę po starcie, żeby nie obciążać uruchamiania.
    private static readonly TimeSpan MinDelay = TimeSpan.FromMinutes(1);

    private readonly DatabaseService _db;
    private readonly SubscriptionService _subscriptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _loopCts;

    public FeedRefreshService(DatabaseService db, SubscriptionService subscriptions)
    {
        _db = db;
        _subscriptions = subscriptions;
    }

    /// <summary>Liczba nowych odcinków mogła się zmienić (odświeżenie albo otwarcie podcastu).</summary>
    public event EventHandler? NewEpisodesChanged;

    /// <summary>Zakończone automatyczne odświeżenie (wywoływane z wątku w tle).</summary>
    public event EventHandler<RefreshSummary>? AutoRefreshCompleted;

    public void NotifyNewEpisodesChanged() => NewEpisodesChanged?.Invoke(this, EventArgs.Empty);

    public async Task<RefreshSummary> RefreshAllAsync(CancellationToken ct = default)
    {
        // Ręczne i automatyczne odświeżenie nie idą równolegle — drugie czeka na pierwsze.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        RefreshSummary summary;
        try
        {
            var shows = await Task.Run(() => _db.GetPodcastsAsync(), ct).ConfigureAwait(false);
            var ok = 0;
            var failed = 0;
            var withNew = new ConcurrentBag<(Podcast, int)>();

            await Parallel.ForEachAsync(
                shows,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (show, token) =>
                {
                    try
                    {
                        var result = await _subscriptions.SubscribeAsync(show.FeedUrl, token).ConfigureAwait(false);
                        Interlocked.Increment(ref ok);
                        if (result.AlreadySubscribed && result.NewEpisodes > 0)
                            withNew.Add((result.Podcast, result.NewEpisodes));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Fala.Helpers.Log.Warn($"Odświeżenie kanału {show.FeedUrl}", ex);
                        Interlocked.Increment(ref failed);
                    }
                }).ConfigureAwait(false);

            AppSettings.LastRefreshUtc = DateTimeOffset.UtcNow;
            AppSettings.Save();
            summary = new RefreshSummary(ok, failed, withNew.OrderByDescending(x => x.Item2).ToList());
            Fala.Helpers.Log.Info($"Odświeżono kanały: OK {ok}, błędy {failed}, nowe odcinki {summary.TotalNew}");
        }
        finally
        {
            _gate.Release();
        }

        NotifyNewEpisodesChanged();
        return summary;
    }

    /// <summary>Uruchamia (lub restartuje po zmianie ustawień) pętlę automatycznego odświeżania.</summary>
    public void Reschedule()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;

        var hours = AppSettings.AutoRefreshHours;
        if (hours <= 0)
            return;

        var cts = _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(TimeSpan.FromHours(hours), cts.Token));
    }

    private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeUntilDue(interval), ct).ConfigureAwait(false);
                // Ręczne odświeżenie mogło w międzyczasie przesunąć termin.
                if (TimeUntilDue(interval) > MinDelay)
                    continue;
                var summary = await RefreshAllAsync(ct).ConfigureAwait(false);
                AutoRefreshCompleted?.Invoke(this, summary);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Fala.Helpers.Log.Error("Automatyczne odświeżanie", ex);
                // Błąd jednego przebiegu nie może zatrzymać pętli — spróbujemy przy następnym terminie.
                AppSettings.LastRefreshUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private static TimeSpan TimeUntilDue(TimeSpan interval)
    {
        var last = AppSettings.LastRefreshUtc ?? DateTimeOffset.MinValue;
        var wait = last + interval - DateTimeOffset.UtcNow;
        return wait < MinDelay ? MinDelay : wait;
    }
}
