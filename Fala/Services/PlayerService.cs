using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Fala.Models;

namespace Fala.Services;

public sealed class PlayerState
{
    public string EpisodeGuid { get; init; } = "";
    public string EpisodeTitle { get; init; } = "";
    public string ShowTitle { get; init; } = "";
    public string ArtworkUrl { get; init; } = "";
    public bool IsPlaying { get; init; }
    /// <summary>Źródło się otwiera albo ładuje dane (np. po przewinięciu strumienia).</summary>
    public bool IsBuffering { get; init; }
    public bool HasTrack { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public string SleepLabel { get; init; } = "";
}

public sealed class PlayerService : IDisposable
{
    private readonly MediaPlayer _player = new();
    private readonly DatabaseService _db;
    private readonly QueueService _queue;
    private readonly System.Timers.Timer _saveTimer;
    private readonly System.Timers.Timer _tickTimer;
    private double _speed = 1.0;
    private double _volume = 1.0;
    private bool _muted;
    private string _artworkUrl = "";
    private bool _sleepAtEnd;
    private CancellationTokenSource? _sleepCts;
    private string _sleepLabel = "";
    private long _lastRaise;
    private bool _disposed;
    private TimeSpan? _pendingSeek;

    public event EventHandler<PlayerState>? StateChanged;

    public Episode? CurrentEpisode { get; private set; }
    public string CurrentShow { get; private set; } = "";
    public string CurrentArtwork => _artworkUrl;
    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    // Po przywróceniu odcinka przy starcie źródło nie jest jeszcze otwarte (bez ruchu sieciowego
    // do pierwszego „Odtwórz”), więc pozycję bierzemy wtedy z zapisanego postępu.
    private bool HasSource => _player.Source is not null;
    public TimeSpan Position => HasSource
        ? _player.PlaybackSession.Position
        : CurrentEpisode?.Position ?? TimeSpan.Zero;

    public PlayerService(DatabaseService db, QueueService queue)
    {
        _db = db;
        _queue = queue;

        try { _player.CommandManager.IsEnabled = false; } catch { }
        ConfigureSmtc();

        _player.PlaybackSession.PlaybackStateChanged += (_, _) =>
        {
            SyncTickTimer();
            Raise(true);
        };
        _player.PlaybackSession.PositionChanged += (_, _) => Raise(false);
        _player.MediaOpened += (_, _) =>
        {
            try
            {
                _player.PlaybackSession.PlaybackRate = _speed;
                if (_pendingSeek is { } seek)
                {
                    _player.PlaybackSession.Position = seek;
                    _pendingSeek = null;
                }
            }
            catch (Exception ex)
            {
                Fala.Helpers.Log.Warn("MediaOpened: ustawienie tempa/pozycji", ex);
            }
            Raise(true);
        };
        _player.MediaEnded += (_, _) => _ = OnEndedAsync();
        // Bez tego błąd strumienia (404, nieobsługiwany format, zerwane połączenie) znikał bez śladu.
        _player.MediaFailed += (_, e) =>
        {
            Fala.Helpers.Log.Warn($"Odtwarzanie nie powiodło się ({e.Error}, 0x{e.ExtendedErrorCode?.HResult:X8}): " +
                                  $"{e.ErrorMessage} · odcinek {CurrentEpisode?.Id}");
            Raise(true);
        };

        _saveTimer = new System.Timers.Timer(8000) { AutoReset = true };
        // Zapisujemy tylko w trakcie odtwarzania — podczas pauzy pozycja się nie zmienia,
        // a każdy zapis przesuwał HeardUtc w historii i LastHeardUtc odcinka.
        _saveTimer.Elapsed += (_, _) =>
        {
            if (IsPlaying) _ = PersistAsync();
        };
        _tickTimer = new System.Timers.Timer(250) { AutoReset = true };
        _tickTimer.Elapsed += (_, _) =>
        {
            if (_disposed) return;
            Raise(true);
        };
    }

    public void Play(Episode episode, string showTitle, string? artworkUrl = null, TimeSpan? startAt = null)
    {
        var source = episode.LocalPath;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            source = episode.AudioUrl;
        // Walidacja przed zmianą stanu — wcześniej przy złym URL-u zmieniał się tytuł/odcinek,
        // a dalej grało poprzednie audio.
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return;

        SaveCurrentSnapshot();

        CurrentEpisode = episode;
        CurrentShow = showTitle;
        _artworkUrl = artworkUrl ?? "";

        if (startAt is { } at)
            _pendingSeek = at < TimeSpan.Zero ? TimeSpan.Zero : at;
        else
        {
            var nearEnd = episode.Duration is { } d && d - episode.Position < TimeSpan.FromSeconds(5);
            _pendingSeek = !episode.IsPlayed && episode.Position > TimeSpan.FromSeconds(2) && !nearEnd
                ? episode.Position
                : null;
        }

        Fala.Helpers.Log.Info($"Odtwarzanie odcinka {episode.Id} od {_pendingSeek?.ToString() ?? "początku"} " +
                              $"({(uri.IsFile ? "plik lokalny" : uri.Host)})");
        _player.Source = MediaSource.CreateFromUri(uri);
        UpdateSmtc();
        _player.Play();
        _saveTimer.Start();
        _tickTimer.Start();
        _ = Task.Run(NameAudioSessionAsync);
        Raise(true);
    }

    /// <summary>
    /// Pokazuje odcinek z poprzedniej sesji w pasku odtwarzacza, bez rozpoczynania odtwarzania.
    /// Źródło audio otwiera się dopiero przy pierwszym „Odtwórz”.
    /// </summary>
    public void Restore(Episode episode, string showTitle, string? artworkUrl = null)
    {
        if (CurrentEpisode is not null)
            return;
        CurrentEpisode = episode;
        CurrentShow = showTitle;
        _artworkUrl = artworkUrl ?? "";
        UpdateSmtc();
        Raise(true);
    }

    public void TogglePlayPause()
    {
        if (!HasSource)
        {
            if (CurrentEpisode is { } restored)
                Play(restored, CurrentShow, _artworkUrl);
            return;
        }
        if (IsPlaying) _player.Pause();
        else _player.Play();
        _ = PersistAsync();
    }

    public void Skip(TimeSpan delta)
    {
        if (CurrentEpisode is null)
            return;
        Seek(Position + delta);
    }

    public void Seek(TimeSpan position)
    {
        if (CurrentEpisode is not { } ep)
            return;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        var dur = CurrentDuration;
        if (dur > TimeSpan.Zero && position > dur)
            position = dur;
        if (!HasSource)
        {
            // Przywrócony, jeszcze nieotwarty odcinek: zmieniamy tylko zapisaną pozycję,
            // od której ruszy Play().
            ep.Position = position;
            if (ep.Id > 0)
                _ = _db.SaveProgressAsync(ep.Id, position, ep.IsPlayed);
            Raise(true);
            return;
        }
        _player.PlaybackSession.Position = position;
        _ = PersistAsync();
        Raise(true);
    }

    public void SetSpeed(double rate)
    {
        _speed = rate;
        try { _player.PlaybackSession.PlaybackRate = rate; } catch { }
    }

    public double Volume => _muted ? 0 : _volume;
    public double VolumeLevel => _volume;
    public bool IsMuted => _muted;

    public void SetVolume(double value)
    {
        _volume = Math.Clamp(value, 0, 1);
        if (_volume > 0)
            _muted = false;
        ApplyVolume();
    }

    public void ToggleMute()
    {
        _muted = !_muted;
        ApplyVolume();
    }

    private void ApplyVolume()
    {
        try { _player.Volume = _muted ? 0 : _volume; } catch { }
    }

    public void ClearSleep()
    {
        _sleepCts?.Cancel();
        _sleepCts?.Dispose();
        _sleepCts = null;
        _sleepAtEnd = false;
        _sleepLabel = "";
        Raise(true);
    }

    public void SleepAfter(TimeSpan delay)
    {
        _sleepCts?.Cancel();
        _sleepCts?.Dispose();
        _sleepAtEnd = false;
        if (delay <= TimeSpan.Zero)
        {
            _sleepLabel = "";
            Raise(true);
            return;
        }

        _sleepLabel = Fala.Localization.Loc.Current.Format("SleepMinutes", (int)Math.Round(delay.TotalMinutes));
        _sleepCts = new CancellationTokenSource();
        var ct = _sleepCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested) return;
                _player.Pause();
                _sleepLabel = "";
                Raise(true);
                await PersistAsync();
            }
            catch (TaskCanceledException)
            {
            }
        });
        Raise(true);
    }

    public void SleepAtEndOfEpisode()
    {
        _sleepCts?.Cancel();
        _sleepCts?.Dispose();
        _sleepCts = null;
        _sleepAtEnd = true;
        _sleepLabel = Fala.Localization.Loc.Current.SleepEnd;
        Raise(true);
    }

    private void SyncTickTimer()
    {
        var playing = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        if (playing) _tickTimer.Start();
        else _tickTimer.Stop();
    }

    private TimeSpan CurrentDuration
    {
        get
        {
            var n = _player.PlaybackSession.NaturalDuration;
            if (n > TimeSpan.Zero && n < TimeSpan.FromDays(2))
                return n;
            return CurrentEpisode?.Duration ?? TimeSpan.Zero;
        }
    }

    public async Task<bool> PlayNextInQueueAsync()
    {
        var next = await _queue.TakeNextAsync();
        if (next is null)
            return false;
        Play(next.Episode, next.ShowTitle, next.ArtworkUrl);
        return true;
    }

    private async Task OnMediaNextAsync()
    {
        if (!await PlayNextInQueueAsync())
            Skip(TimeSpan.FromSeconds(30));
    }

    private async Task OnEndedAsync()
    {
        _tickTimer.Stop();
        await PersistAsync(completed: true);
        if (_sleepAtEnd)
        {
            _sleepAtEnd = false;
            _sleepLabel = "";
            Raise(true);
            return;
        }
        if (!await PlayNextInQueueAsync())
            Raise(true);
    }

    private void SaveCurrentSnapshot()
    {
        var previous = CurrentEpisode;
        // Bez źródła (odcinek tylko przywrócony) pozycja odtwarzacza to 0 — zapis wyzerowałby postęp.
        if (previous is not { Id: > 0 } || !HasSource)
            return;
        var pos = _player.PlaybackSession.Position;
        previous.Position = pos;
        _ = _db.SaveProgressAsync(previous.Id, pos, previous.IsPlayed);
    }

    private async Task PersistAsync(bool completed = false)
    {
        var ep = CurrentEpisode;
        if (ep is null || ep.Id == 0 || !HasSource) return;
        try
        {
            var pos = _player.PlaybackSession.Position;
            var dur = CurrentDuration;
            var played = completed || (dur > TimeSpan.Zero && pos >= dur - TimeSpan.FromSeconds(5));
            ep.Position = pos;
            if (played) ep.IsPlayed = true;
            await _db.SaveProgressAsync(ep.Id, pos, ep.IsPlayed);
            if (played || pos.TotalSeconds >= 5)
                await _db.RecordHistoryAsync(ep, CurrentShow, _artworkUrl);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn($"Zapis postępu odcinka {ep.Id}", ex);
        }
    }

    private static async Task NameAudioSessionAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(250);
            try
            {
                Fala.Helpers.AppIdentity.ApplyAudioSessionName();
            }
            catch
            {
            }
        }
    }

    private void ConfigureSmtc()
    {
        try
        {
            var smtc = _player.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsStopEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;
            smtc.ButtonPressed += Smtc_ButtonPressed;
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Konfiguracja SMTC", ex);
        }
    }

    private void Smtc_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                if (HasSource) _player.Play();
                else TogglePlayPause();
                break;
            case SystemMediaTransportControlsButton.Pause:
            case SystemMediaTransportControlsButton.Stop:
                _player.Pause();
                _ = PersistAsync();
                break;
            case SystemMediaTransportControlsButton.Next:
                _ = OnMediaNextAsync();
                break;
            case SystemMediaTransportControlsButton.Previous:
                if (_player.PlaybackSession.Position > TimeSpan.FromSeconds(3))
                    Seek(TimeSpan.Zero);
                else
                    Skip(TimeSpan.FromSeconds(-15));
                break;
        }
    }

    private void UpdateSmtc()
    {
        try
        {
            var smtc = _player.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;

            var updater = smtc.DisplayUpdater;
            updater.ClearAll();
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.IsNullOrWhiteSpace(CurrentEpisode?.Title)
                ? "Fala"
                : CurrentEpisode.Title;
            updater.MusicProperties.Artist = string.IsNullOrWhiteSpace(CurrentShow) ? "Fala" : CurrentShow;
            updater.MusicProperties.AlbumTitle = string.IsNullOrWhiteSpace(CurrentShow) ? "Fala" : CurrentShow;
            updater.MusicProperties.AlbumArtist = "Fala";
            if (Uri.TryCreate(_artworkUrl, UriKind.Absolute, out var uri))
                updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
            updater.Update();
            UpdatePlaybackStatus();
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn("Aktualizacja SMTC", ex);
        }
    }

    private void UpdatePlaybackStatus()
    {
        try
        {
            var smtc = _player.SystemMediaTransportControls;
            smtc.PlaybackStatus = _player.PlaybackSession.PlaybackState switch
            {
                MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
                MediaPlaybackState.Opening or MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Stopped
            };
        }
        catch
        {
        }
    }

    private void UpdateTimeline()
    {
        try
        {
            var dur = CurrentDuration;
            _player.SystemMediaTransportControls.UpdateTimelineProperties(
                new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    Position = Position,
                    MaxSeekTime = dur,
                    EndTime = dur
                });
        }
        catch
        {
        }
    }

    private void Raise(bool force)
    {
        if (_disposed) return;
        var now = Environment.TickCount64;
        if (!force && now - _lastRaise < 250) return;
        _lastRaise = now;
        UpdatePlaybackStatus();
        UpdateTimeline();
        var session = _player.PlaybackSession;
        StateChanged?.Invoke(this, new PlayerState
        {
            EpisodeGuid = CurrentEpisode?.Guid ?? "",
            EpisodeTitle = CurrentEpisode?.Title ?? "",
            ShowTitle = CurrentShow,
            ArtworkUrl = _artworkUrl,
            IsPlaying = session.PlaybackState == MediaPlaybackState.Playing,
            IsBuffering = HasSource && session.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering,
            HasTrack = CurrentEpisode is not null,
            Position = Position,
            Duration = CurrentDuration,
            SleepLabel = _sleepLabel
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tickTimer.Stop();
        _tickTimer.Dispose();
        _saveTimer.Stop();
        _saveTimer.Dispose();
        _sleepCts?.Cancel();
        _sleepCts?.Dispose();
        try { PersistAsync().GetAwaiter().GetResult(); } catch { }
        _player.Dispose();
    }
}
