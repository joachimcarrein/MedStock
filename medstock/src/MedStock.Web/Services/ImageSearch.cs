using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MedStock.Web.Services;

public sealed record ImageHit(string ThumbUrl, string Title, string PageUrl);

/// <summary>Searches pictures by name. Only used when the user taps "search" on a medication (the one external call).</summary>
public interface IImageSearch
{
    /// <summary>Throws <see cref="ImageSearchUnavailableException"/> when the service cannot be reached.</summary>
    Task<IReadOnlyList<ImageHit>> SearchAsync(string query, int offset, CancellationToken ct = default);

    /// <summary>Downloads a picture returned by <see cref="SearchAsync"/>; null when the URL is not allowed or the download fails.</summary>
    Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default);
}

public sealed class ImageSearchUnavailableException(Exception inner) : Exception("Image search unavailable", inner);

/// <summary>Wikimedia Commons (no key, no AI). Thumbnails of 400 px, JPEG or PNG only.</summary>
public sealed class WikimediaImageSearch(HttpClient http, ILogger<WikimediaImageSearch> log) : IImageSearch
{
    public const string Api = "https://commons.wikimedia.org/w/api.php";
    /// <summary>Wikimedia serves thumbnails from thumb.wikimedia.org (formerly upload.wikimedia.org).</summary>
    public static readonly string[] ImageHosts = ["thumb.wikimedia.org", "upload.wikimedia.org"];
    public const int PageSize = 12;
    public const int MaxBytes = 8_000_000;

    /// <summary>Only https images from Wikimedia's image hosts may be downloaded (no SSRF via a forged URL).</summary>
    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && ImageHosts.Contains(u.Host)
        && u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo);

    public async Task<IReadOnlyList<ImageHit>> SearchAsync(string query, int offset, CancellationToken ct = default)
    {
        var url = $"{Api}?action=query&format=json&formatversion=2&generator=search&gsrnamespace=6"
                  + $"&gsrlimit={PageSize}&gsroffset={Math.Max(0, offset)}&gsrsearch={Uri.EscapeDataString(query + " filetype:bitmap")}"
                  + "&prop=imageinfo&iiprop=url|mime&iiurlwidth=400";
        try
        {
            var json = await http.GetFromJsonAsync<JsonObject>(url, ct);
            var pages = json?["query"]?["pages"] as JsonArray ?? [];
            return pages
                .OrderBy(p => (int?)p?["index"] ?? int.MaxValue)
                .Select(p => (Title: (string?)p?["title"], Info: p?["imageinfo"]?[0]))
                .Where(x => (string?)x.Info?["mime"] is "image/jpeg" or "image/png" && IsAllowed((string?)x.Info?["thumburl"]))
                .Select(x => new ImageHit((string)x.Info!["thumburl"]!, (x.Title ?? "").Replace("File:", ""),
                    (string?)x.Info!["descriptionurl"] ?? ""))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            log.LogWarning("Image search failed: {Error}", ex.GetType().Name);
            throw new ImageSearchUnavailableException(ex);
        }
    }

    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (!IsAllowed(url)) return null;
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxBytes) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > MaxBytes) return null;
            }
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Image download failed: {Error}", ex.GetType().Name);
            return null;
        }
    }
}
