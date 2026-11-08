using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MedStock.Web.Services;

public enum ImageSource { Wikimedia, DailyMed }

public sealed record ImageHit(string ThumbUrl, string Title, string PageUrl);

/// <summary>Searches pictures by name. Only used when the user taps "search" on a medication (the only external calls).</summary>
public interface IImageSearch
{
    /// <summary>Throws <see cref="ImageSearchUnavailableException"/> when the service cannot be reached.</summary>
    Task<IReadOnlyList<ImageHit>> SearchAsync(ImageSource source, string query, int offset, CancellationToken ct = default);

    /// <summary>Downloads a picture returned by <see cref="SearchAsync"/>; null when the URL is not allowed or the download fails.</summary>
    Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default);
}

public sealed class ImageSearchUnavailableException(Exception inner) : Exception("Image search unavailable", inner);

/// <summary>
/// Wikimedia Commons (free pictures, 400 px thumbnails) and DailyMed (US National Library of Medicine: official
/// carton and label pictures of US medicines). No keys, no AI. JPEG or PNG only.
/// </summary>
public sealed partial class ImageSearchService(HttpClient http, ILogger<ImageSearchService> log) : IImageSearch
{
    public const string WikimediaApi = "https://commons.wikimedia.org/w/api.php";
    public const string DailyMedApi = "https://dailymed.nlm.nih.gov/dailymed/services/v2/";
    public const string DailyMedHost = "dailymed.nlm.nih.gov";
    public const string DailyMedImagePath = "/dailymed/image.cfm";
    /// <summary>Wikimedia serves thumbnails from thumb.wikimedia.org (formerly upload.wikimedia.org).</summary>
    public static readonly string[] WikimediaHosts = ["thumb.wikimedia.org", "upload.wikimedia.org"];
    public const int PageSize = 12;
    public const int MaxBytes = 8_000_000;
    /// <summary>DailyMed: label sets (products) looked at per search; each has many pictures.</summary>
    public const int DailyMedSets = 4;

    /// <summary>Only https images from the two sources may be downloaded (no SSRF via a forged URL).</summary>
    public static bool IsAllowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort
            || !string.IsNullOrEmpty(u.UserInfo)) return false;
        return WikimediaHosts.Contains(u.Host) || (u.Host == DailyMedHost && u.AbsolutePath == DailyMedImagePath);
    }

    public Task<IReadOnlyList<ImageHit>> SearchAsync(ImageSource source, string query, int offset, CancellationToken ct = default) =>
        Guard(source == ImageSource.DailyMed ? () => DailyMedAsync(query, offset, ct) : () => WikimediaAsync(query, offset, ct));

    private async Task<IReadOnlyList<ImageHit>> WikimediaAsync(string query, int offset, CancellationToken ct)
    {
        var url = $"{WikimediaApi}?action=query&format=json&formatversion=2&generator=search&gsrnamespace=6"
                  + $"&gsrlimit={PageSize}&gsroffset={Math.Max(0, offset)}&gsrsearch={Uri.EscapeDataString(query + " filetype:bitmap")}"
                  + "&prop=imageinfo&iiprop=url|mime&iiurlwidth=400";
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

    /// <summary>
    /// DailyMed: find label sets by drug name, then list their pictures. Packaging pictures (carton, label, pen, …) first;
    /// chemical structures and diagrams are left out.
    /// </summary>
    private async Task<IReadOnlyList<ImageHit>> DailyMedAsync(string query, int offset, CancellationToken ct)
    {
        var search = await http.GetFromJsonAsync<JsonObject>(
            $"{DailyMedApi}spls.json?drug_name={Uri.EscapeDataString(query)}&pagesize={DailyMedSets}", ct);
        var sets = (search?["data"] as JsonArray ?? []).Select(s => (string?)s?["setid"]).OfType<string>()
            .Where(s => SetIdRx().IsMatch(s)).Distinct().Take(DailyMedSets).ToList();

        var all = new List<(ImageHit Hit, int Rank, int Order)>();
        foreach (var setId in sets)
        {
            var media = await http.GetFromJsonAsync<JsonObject>($"{DailyMedApi}spls/{setId}/media.json", ct);
            var position = 0;
            foreach (var m in media?["data"]?["media"] as JsonArray ?? [])
            {
                position++;
                var name = (string?)m?["name"] ?? "";
                var url = (string?)m?["url"];
                if ((string?)m?["mime_type"] is not ("image/jpeg" or "image/png") || !IsAllowed(url)) continue;
                var rank = PackagingRx().IsMatch(name) ? 0 : StructureRx().IsMatch(name) ? -1 : 1;
                if (rank < 0) continue;
                // Unnamed pictures ("image-07.jpg"): the package label is usually at the END of a label set, so last first.
                all.Add((new ImageHit(url!, name, $"https://dailymed.nlm.nih.gov/dailymed/drugInfo.cfm?setid={setId}"), rank,
                    rank == 0 ? all.Count : -position));
            }
        }
        return all.OrderBy(x => x.Rank).ThenBy(x => x.Order).Select(x => x.Hit).DistinctBy(h => h.ThumbUrl)
            .Skip(Math.Max(0, offset)).Take(PageSize).ToList();
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

    private async Task<IReadOnlyList<ImageHit>> Guard(Func<Task<IReadOnlyList<ImageHit>>> search)
    {
        try { return await search(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            log.LogWarning("Image search failed: {Error}", ex.GetType().Name);
            throw new ImageSearchUnavailableException(ex);
        }
    }

    [GeneratedRegex("^[0-9a-fA-F-]{36}$")]
    private static partial Regex SetIdRx();

    [GeneratedRegex("carton|package|pack|label|box|principal|pdp|container|pen|bottle|vial|blister|inhaler|tube", RegexOptions.IgnoreCase)]
    private static partial Regex PackagingRx();

    [GeneratedRegex("structure|formula|chem|figure|fig[-_ ]?\\d|graph|chart|table", RegexOptions.IgnoreCase)]
    private static partial Regex StructureRx();
}
