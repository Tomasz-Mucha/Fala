using System.Collections.ObjectModel;
using Fala.Models;
using Microsoft.UI.Dispatching;

namespace Fala.Services;

public sealed class QueueService
{
    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();
    private readonly DatabaseService _db;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private long _version;

    public QueueService(DatabaseService db)
    {
        _db = db;
    }

    public ObservableCollection<QueueItem> Upcoming { get; } = new();

    public event EventHandler? Changed;

    public int Count => Upcoming.Count;

    /// <summary>Wczytuje kolejkę zapisaną w poprzedniej sesji. Wywoływać na wątku UI.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<QueueItem> saved;
        try
        {
            saved = await _db.GetQueueAsync();
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Wczytanie kolejki", ex);
            return;
        }
        Upcoming.Clear();
        foreach (var item in saved)
            Upcoming.Add(item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(Episode episode, string showTitle, string? artworkUrl = null)
    {
        Upcoming.Add(new QueueItem
        {
            Episode = episode,
            ShowTitle = showTitle,
            ArtworkUrl = artworkUrl ?? ""
        });
        OnChanged();
    }

    public void PlayNext(Episode episode, string showTitle, string? artworkUrl = null)
    {
        Upcoming.Insert(0, new QueueItem
        {
            Episode = episode,
            ShowTitle = showTitle,
            ArtworkUrl = artworkUrl ?? ""
        });
        OnChanged();
    }

    public async Task<QueueItem?> TakeNextAsync()
    {
        if (_ui is null || _ui.HasThreadAccess)
            return Dequeue();

        var tcs = new TaskCompletionSource<QueueItem?>();
        if (!_ui.TryEnqueue(() => tcs.TrySetResult(Dequeue())))
            return null;
        return await tcs.Task;
    }

    public void Remove(QueueItem item)
    {
        Upcoming.Remove(item);
        OnChanged();
    }

    public void Move(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Upcoming.Count || to >= Upcoming.Count || from == to)
            return;
        Upcoming.Move(from, to);
        OnChanged();
    }

    public void Clear()
    {
        Upcoming.Clear();
        OnChanged();
    }

    private QueueItem? Dequeue()
    {
        if (Upcoming.Count == 0)
            return null;
        var item = Upcoming[0];
        Upcoming.RemoveAt(0);
        OnChanged();
        return item;
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        // Migawka na wątku UI (ObservableCollection), zapis do bazy w tle.
        var snapshot = Upcoming
            .Where(q => q.Episode.Id > 0)
            .Select(q => (q.Episode.Id, q.ShowTitle, q.ArtworkUrl))
            .ToList();
        _ = SaveAsync(snapshot, Interlocked.Increment(ref _version));
    }

    private async Task SaveAsync(IReadOnlyList<(long, string, string)> snapshot, long version)
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Przy szybkich zmianach (np. kilka przesunięć) zapisujemy tylko najnowszy stan.
            if (version != Interlocked.Read(ref _version))
                return;
            await Task.Run(() => _db.SaveQueueAsync(snapshot)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Zapis kolejki", ex);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
