using Fala.Models;

namespace Fala.Services;

/// <param name="NewEpisodes">Odcinki dodane przy tym pobraniu (przy pierwszej subskrypcji: wszystkie).</param>
public sealed record SubscribeResult(Podcast Podcast, int EpisodeCount, bool AlreadySubscribed, int NewEpisodes = 0);

public sealed class SubscriptionService
{
    private readonly DatabaseService _db;
    private readonly RssService _rss;

    public SubscriptionService(DatabaseService db, RssService rss)
    {
        _db = db;
        _rss = rss;
    }

    // Parsowanie XML i zapis odcinków (Microsoft.Data.Sqlite wykonuje „async” synchronicznie)
    // idą na puli wątków, żeby nie blokować UI przy dużych kanałach.
    public Task<SubscribeResult> SubscribeAsync(string url, CancellationToken ct = default)
        => Task.Run(() => SubscribeCoreAsync(url, ct), ct);

    private async Task<SubscribeResult> SubscribeCoreAsync(string url, CancellationToken ct)
    {
        var requested = RssService.NormalizeUrl(url);
        var (podcast, episodes) = await _rss.FetchAsync(requested, ct);

        var existing = await _db.FindPodcastByFeedUrlAsync(podcast.FeedUrl)
                       ?? await _db.FindPodcastByFeedUrlAsync(requested);

        Podcast saved;
        if (existing is not null)
        {
            podcast.Id = existing.Id;
            podcast.AddedUtc = existing.AddedUtc;
            saved = await _db.UpdatePodcastAsync(podcast);
        }
        else
        {
            saved = await _db.UpsertPodcastAsync(podcast);
        }

        // Flaga „nowy” tylko przy odświeżaniu istniejącej subskrypcji — przy pierwszym dodaniu
        // całe archiwum kanału nie jest „nowe”.
        var added = await _db.UpsertEpisodesAsync(saved.Id, episodes, markNew: existing is not null);
        return new SubscribeResult(saved, episodes.Count, existing is not null, added);
    }

    /// <summary>
    /// Subskrybuje wiele kanałów naraz (import OPML). Kanały już obecne w bibliotece są pomijane
    /// bez pobierania, ale i tak trafiają do grup z pliku. Brakujące grupy są tworzone.
    /// <paramref name="progress"/> dostaje liczbę przetworzonych kanałów.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        IReadOnlyList<OpmlEntry> entries,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var added = 0;
        var skipped = 0;
        var done = 0;
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Grupy tworzymy przed równoległym importem, żeby dwa wątki nie tworzyły tej samej.
        var (groupIds, createdGroups) = await EnsureGroupsAsync(entries.SelectMany(e => e.Groups));

        await Parallel.ForEachAsync(
            entries,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (entry, token) =>
            {
                try
                {
                    var normalized = RssService.NormalizeUrl(entry.Url);
                    long podcastId;
                    if (await _db.FindPodcastByFeedUrlAsync(normalized) is { } existing)
                    {
                        podcastId = existing.Id;
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        var result = await SubscribeAsync(normalized, token);
                        podcastId = result.Podcast.Id;
                        if (result.AlreadySubscribed)
                            Interlocked.Increment(ref skipped);
                        else
                            Interlocked.Increment(ref added);
                    }
                    foreach (var group in entry.Groups)
                        await _db.AddPodcastToGroupAsync(groupIds[group], podcastId);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Fala.Helpers.Log.Warn($"Import OPML: {entry.Url}", ex);
                    failed.Add(entry.Url);
                }
                progress?.Report(Interlocked.Increment(ref done));
            });

        return new ImportResult(added, skipped, failed.ToList(), createdGroups);
    }

    private async Task<(Dictionary<string, long> Ids, int Created)> EnsureGroupsAsync(IEnumerable<string> names)
    {
        var ids = new Dictionary<string, long>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var g in await _db.GetGroupsAsync())
            ids.TryAdd(g.Name, g.Id);
        var created = 0;
        foreach (var name in names.Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            if (ids.ContainsKey(name))
                continue;
            ids[name] = (await _db.CreateGroupAsync(name)).Id;
            created++;
        }
        return (ids, created);
    }
}

public sealed record ImportResult(int Added, int Skipped, IReadOnlyList<string> Failed, int CreatedGroups = 0);
