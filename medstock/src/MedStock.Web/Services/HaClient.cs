using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MedStock.Web.Services;

public enum NotifyOutcome { Ok, UnknownService, TransportError }

public interface IHaClient
{
    /// <summary>All notify services as "notify.xxx". Throws <see cref="HaUnavailableException"/> when HA cannot be reached.</summary>
    Task<IReadOnlyList<string>> GetNotifyServicesAsync(CancellationToken ct = default);

    Task<NotifyOutcome> NotifyAsync(string service, string title, string message, string? deepLink, string? tag, CancellationToken ct = default);

    /// <summary>The full add-on slug including the repository prefix (e.g. "a1b2c3d4_medstock").</summary>
    Task<string?> GetSelfSlugAsync(CancellationToken ct = default);
}

public sealed class HaUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Talks to Home Assistant Core through the Supervisor proxy using SUPERVISOR_TOKEN.</summary>
public sealed class HaClient(HttpClient http, ILogger<HaClient> logger) : IHaClient
{
    public const string CoreBase = "http://supervisor/core/api/";
    private static string? _slug;

    public async Task<IReadOnlyList<string>> GetNotifyServicesAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.GetAsync(CoreBase + "services", ct);
            if (!resp.IsSuccessStatusCode)
                throw new HaUnavailableException($"GET services returned {(int)resp.StatusCode}");
            var arr = await resp.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
            var notify = arr.FirstOrDefault(d => (string?)d?["domain"] == "notify");
            if (notify?["services"] is not JsonObject services) return [];
            return services.Select(kv => "notify." + kv.Key).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new HaUnavailableException("Home Assistant not reachable", ex);
        }
    }

    public async Task<NotifyOutcome> NotifyAsync(string service, string title, string message, string? deepLink, string? tag, CancellationToken ct = default)
    {
        var name = service.StartsWith("notify.", StringComparison.Ordinal) ? service["notify.".Length..] : service;
        var data = new JsonObject();
        if (deepLink is not null) { data["url"] = deepLink; data["clickAction"] = deepLink; }
        if (tag is not null) data["tag"] = tag;
        var body = new JsonObject { ["title"] = title, ["message"] = message, ["data"] = data };
        try
        {
            using var resp = await http.PostAsJsonAsync(CoreBase + "services/notify/" + Uri.EscapeDataString(name), body, ct);
            logger.LogDebug("notify {Service} -> {Status}", service, (int)resp.StatusCode);
            if (resp.IsSuccessStatusCode) return NotifyOutcome.Ok;
            if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) return NotifyOutcome.UnknownService;
            return NotifyOutcome.TransportError;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Notification transport error: {Error}", ex.GetType().Name);
            return NotifyOutcome.TransportError;
        }
    }

    public async Task<string?> GetSelfSlugAsync(CancellationToken ct = default)
    {
        if (_slug is not null) return _slug;
        try
        {
            var info = await http.GetFromJsonAsync<JsonObject>("http://supervisor/addons/self/info", ct);
            _slug = (string?)info?["data"]?["slug"];
            return _slug;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Could not read the add-on slug: {Error}", ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>Development stand-in: two fake phones, payloads are logged.</summary>
public sealed class FakeHaClient(ILogger<FakeHaClient> logger) : IHaClient
{
    public List<(string Service, string Title, string Message, string? DeepLink, string? Tag)> Sent { get; } = [];
    public bool Unavailable { get; set; }
    public HashSet<string> Services { get; } = ["notify.mobile_app_dev_iphone", "notify.mobile_app_dev_android", "notify.persistent_notification"];

    public Task<IReadOnlyList<string>> GetNotifyServicesAsync(CancellationToken ct = default) =>
        Unavailable
            ? throw new HaUnavailableException("fake: unavailable")
            : Task.FromResult<IReadOnlyList<string>>(Services.Order(StringComparer.Ordinal).ToList());

    public Task<NotifyOutcome> NotifyAsync(string service, string title, string message, string? deepLink, string? tag, CancellationToken ct = default)
    {
        if (Unavailable) return Task.FromResult(NotifyOutcome.TransportError);
        if (!Services.Contains(service)) return Task.FromResult(NotifyOutcome.UnknownService);
        lock (Sent) Sent.Add((service, title, message, deepLink, tag));
        logger.LogInformation("[FakeHa] {Service}: {Title} - {Message} ({Link}, {Tag})", service, title, message, deepLink, tag);
        return Task.FromResult(NotifyOutcome.Ok);
    }

    public Task<string?> GetSelfSlugAsync(CancellationToken ct = default) => Task.FromResult<string?>("local_medstock");
}
