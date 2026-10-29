using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Fala.Models;

namespace Fala.Services;

public sealed class CatalogService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(Helpers.AppInfo.UserAgent);
        return c;
    }

    public async Task<IReadOnlyList<CatalogShow>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var url = "https://itunes.apple.com/search?media=podcast&entity=podcast&limit=25&term="
                  + Uri.EscapeDataString(query);

        var result = await Http.GetFromJsonAsync<ItunesResponse>(url, ct);
        if (result?.Results is null)
            return [];

        return result.Results
            .Where(r => !string.IsNullOrWhiteSpace(r.FeedUrl))
            .Select(r => new CatalogShow
            {
                Title = r.CollectionName ?? r.TrackName ?? "Podcast",
                Author = r.ArtistName ?? "",
                FeedUrl = r.FeedUrl!,
                ArtworkUrl = r.ArtworkUrl600 ?? r.ArtworkUrl100 ?? "",
                Description = ""
            })
            .ToList();
    }

    private sealed class ItunesResponse
    {
        [JsonPropertyName("results")]
        public List<ItunesPodcast>? Results { get; set; }
    }

    private sealed class ItunesPodcast
    {
        [JsonPropertyName("collectionName")] public string? CollectionName { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("feedUrl")] public string? FeedUrl { get; set; }
        [JsonPropertyName("artworkUrl100")] public string? ArtworkUrl100 { get; set; }
        [JsonPropertyName("artworkUrl600")] public string? ArtworkUrl600 { get; set; }
    }
}
