using Fala.Localization;
using Fala.Models;

namespace Fala.Services;

public sealed class DownloadService
{
    private static readonly HttpClient Http = CreateClient();

    public string RootPath { get; }

    public DownloadService()
    {
        RootPath = Path.Combine(Helpers.AppPaths.DataDirectory, "Downloads");
        Directory.CreateDirectory(RootPath);
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(Helpers.AppInfo.UserAgent);
        return c;
    }

    public string PodcastFolder(long podcastId) => Path.Combine(RootPath, podcastId.ToString());

    public async Task<string> DownloadAsync(
        Episode episode,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (episode.Id == 0)
            throw new InvalidOperationException(Loc.Current.Get("EpisodeNotSaved"));
        if (!Uri.TryCreate(episode.AudioUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(Loc.Current.Get("NoAudioUrl"));

        var dir = PodcastFolder(episode.PodcastId);
        Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (ext is not (".mp3" or ".m4a" or ".aac" or ".ogg" or ".wav" or ".flac" or ".opus"))
            ext = ".mp3";

        var dest = Path.Combine(dir, $"{episode.Id}{ext}");
        if (File.Exists(dest) && new FileInfo(dest).Length > 0)
        {
            progress?.Report(100);
            return dest;
        }

        // ConfigureAwait(false): pętla kopiowania nie wraca na wątek UI po każdym bloku 80 KB.
        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var tmp = dest + ".part";
        try
        {
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long copied = 0;
                var lastReported = -1;
                while (true)
                {
                    var n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    copied += n;
                    // Raportujemy tylko zmianę pełnego procenta — wcześniej każdy blok
                    // (tysiące razy na odcinek) przebudowywał tekst na UI.
                    var pct = total is > 0 ? (int)Math.Clamp(100 * copied / total.Value, 0, 99) : 0;
                    if (pct != lastReported)
                    {
                        lastReported = pct;
                        progress?.Report(pct);
                    }
                }
            }
            File.Move(tmp, dest, overwrite: true);
            progress?.Report(100);
            return dest;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public void DeleteEpisodeFile(Episode episode)
    {
        TryDelete(episode.LocalPath);
        var dir = PodcastFolder(episode.PodcastId);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, episode.Id + ".*"))
            TryDelete(f);
    }

    public void DeletePodcastFiles(long podcastId)
    {
        var dir = PodcastFolder(podcastId);
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn($"Usuwanie folderu {dir}", ex);
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Fala.Helpers.Log.Warn($"Usuwanie pliku {path}", ex);
        }
    }
}
