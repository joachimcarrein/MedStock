using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MedStock.Web.Services;

public enum NotifyOutcome { Ok, UnknownService, TransportError }

/// <summary>An actionable-notification button (Companion app): <paramref name="Action"/> comes back in a mobile_app_notification_action event.</summary>
public sealed record NotifyAction(string Action, string Title);

public interface IHaClient
{
    /// <summary>All notify services as "notify.xxx". Throws <see cref="HaUnavailableException"/> when HA cannot be reached.</summary>
    Task<IReadOnlyList<string>> GetNotifyServicesAsync(CancellationToken ct = default);

    Task<NotifyOutcome> NotifyAsync(string service, string title, string message, string? deepLink, string? tag,
        IReadOnlyList<NotifyAction>? actions = null, CancellationToken ct = default);

    /// <summary>The full add-on slug including the repository prefix (e.g. "a1b2c3d4_medstock").</summary>
    Task<string?> GetSelfSlugAsync(CancellationToken ct = default);

    /// <summary>MQTT broker credentials from the Supervisor (services: mqtt:want); null when no broker is available.</summary>
    Task<MqttBroker?> GetMqttBrokerAsync(CancellationToken ct = default);

    /// <summary>All todo.* entities as (entity id, friendly name). Throws <see cref="HaUnavailableException"/>.</summary>
    Task<IReadOnlyList<(string EntityId, string Name)>> GetTodoListsAsync(CancellationToken ct = default);

    /// <summary>Adds an item unless an open item with the same text exists. Returns false on any failure.</summary>
    Task<bool> AddTodoItemIfMissingAsync(string entityId, string item, CancellationToken ct = default);
}

public sealed record MqttBroker(string Host, int Port, bool Ssl, string? Username, string? Password);

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

    public async Task<NotifyOutcome> NotifyAsync(string service, string title, string message, string? deepLink, string? tag,
        IReadOnlyList<NotifyAction>? actions = null, CancellationToken ct = default)
    {
        var name = service.StartsWith("notify.", StringComparison.Ordinal) ? service["notify.".Length..] : service;
        var data = new JsonObject();
        if (deepLink is not null) { data["url"] = deepLink; data["clickAction"] = deepLink; }
        if (tag is not null) data["tag"] = tag;
        if (actions is { Count: > 0 })
            data["actions"] = new JsonArray(actions.Select(a => (JsonNode)new JsonObject { ["action"] = a.Action, ["title"] = a.Title }).ToArray());
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
    public async Task<IReadOnlyList<(string EntityId, string Name)>> GetTodoListsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.GetAsync(CoreBase + "states", ct);
            if (!resp.IsSuccessStatusCode) throw new HaUnavailableException($"GET states returned {(int)resp.StatusCode}");
            var arr = await resp.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
            return arr.Select(s => ((string?)s?["entity_id"], (string?)s?["attributes"]?["friendly_name"]))
                .Where(x => x.Item1 is not null && x.Item1.StartsWith("todo.", StringComparison.Ordinal))
                .Select(x => (x.Item1!, x.Item2 ?? x.Item1!))
                .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new HaUnavailableException("Home Assistant not reachable", ex);
        }
    }

    public async Task<bool> AddTodoItemIfMissingAsync(string entityId, string item, CancellationToken ct = default)
    {
        try
        {
            // todo.get_items with return_response: skip when the item is still open on the list.
            using (var get = await http.PostAsJsonAsync(CoreBase + "services/todo/get_items?return_response",
                       new JsonObject { ["entity_id"] = entityId, ["status"] = new JsonArray("needs_action") }, ct))
            {
                if (get.IsSuccessStatusCode)
                {
                    var items = (await get.Content.ReadFromJsonAsync<JsonObject>(ct))?["service_response"]?[entityId]?["items"] as JsonArray;
                    if (items?.Any(i => string.Equals((string?)i?["summary"], item, StringComparison.OrdinalIgnoreCase)) == true) return true;
                }
            }
            using var add = await http.PostAsJsonAsync(CoreBase + "services/todo/add_item",
                new JsonObject { ["entity_id"] = entityId, ["item"] = item }, ct);
            logger.LogDebug("todo.add_item {Entity} -> {Status}", entityId, (int)add.StatusCode);
            return add.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Could not add a to-do item: {Error}", ex.GetType().Name);
            return false;
        }
    }

    public async Task<MqttBroker?> GetMqttBrokerAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.GetAsync("http://supervisor/services/mqtt", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogInformation("No MQTT service available from the Supervisor ({Status})", (int)resp.StatusCode);
                return null;
            }
            var d = (await resp.Content.ReadFromJsonAsync<JsonObject>(ct))?["data"];
            var host = (string?)d?["host"];
            if (string.IsNullOrEmpty(host)) return null;
            return new MqttBroker(host, (int?)d?["port"] ?? 1883, (bool?)d?["ssl"] ?? false, (string?)d?["username"], (string?)d?["password"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            logger.LogWarning("Could not read the MQTT service: {Error}", ex.GetType().Name);
            return null;
        }
    }
}



/// <summary>Development stand-in: two fake phones, payloads are logged.</summary>
public sealed class FakeHaClient(ILogger<FakeHaClient> logger) : IHaClient
{
    public List<(string Service, string Title, string Message, string? DeepLink, string? Tag)> Sent { get; } = [];
    public List<IReadOnlyList<NotifyAction>?> SentActions { get; } = [];
    public bool Unavailable { get; set; }
    public HashSet<string> Services { get; } = ["notify.mobile_app_dev_iphone", "notify.mobile_app_dev_android", "notify.persistent_notification"];

    public Task<IReadOnlyList<string>> GetNotifyServicesAsync(CancellationToken ct = default) =>
        Unavailable
            ? throw new HaUnavailableException("fake: unavailable")
            : Task.FromResult<IReadOnlyList<string>>(Services.Order(StringComparer.Ordinal).ToList());

    public Task<NotifyOutcome> NotifyAsync(string service, string title, string message, string? deepLink, string? tag,
        IReadOnlyList<NotifyAction>? actions = null, CancellationToken ct = default)
    {
        if (Unavailable) return Task.FromResult(NotifyOutcome.TransportError);
        if (!Services.Contains(service)) return Task.FromResult(NotifyOutcome.UnknownService);
        lock (Sent)
        {
            Sent.Add((service, title, message, deepLink, tag));
            SentActions.Add(actions);
        }
        logger.LogInformation("[FakeHa] {Service}: {Title} - {Message} ({Link}, {Tag})", service, title, message, deepLink, tag);
        return Task.FromResult(NotifyOutcome.Ok);
    }

    public Task<string?> GetSelfSlugAsync(CancellationToken ct = default) => Task.FromResult<string?>("local_medstock");

    public MqttBroker? Broker { get; set; }
    public Dictionary<string, List<string>> Todo { get; } = new() { ["todo.shopping_list"] = [] };

    public Task<IReadOnlyList<(string EntityId, string Name)>> GetTodoListsAsync(CancellationToken ct = default) =>
        Unavailable
            ? throw new HaUnavailableException("fake: unavailable")
            : Task.FromResult<IReadOnlyList<(string, string)>>(Todo.Keys.Order().Select(k => (k, k)).ToList());

    public Task<bool> AddTodoItemIfMissingAsync(string entityId, string item, CancellationToken ct = default)
    {
        if (Unavailable || !Todo.TryGetValue(entityId, out var list)) return Task.FromResult(false);
        lock (list) if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
        return Task.FromResult(true);
    }
    public Task<MqttBroker?> GetMqttBrokerAsync(CancellationToken ct = default) => Task.FromResult(Broker);
}
