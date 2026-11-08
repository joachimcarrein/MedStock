using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using MedStock.Web.Middleware;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Services;

/// <summary>The "+1 package" button in a notification (Companion app actionable notification).</summary>
public static class NotificationActions
{
    public const string PurchasePrefix = "MEDSTOCK_PURCHASE_";
    public const string EventType = "mobile_app_notification_action";

    public static NotifyAction Purchase(Medication m, CultureInfo c) =>
        new(PurchasePrefix + m.Id.ToString(CultureInfo.InvariantCulture), "+1 " + Texts.Package(m.Type, 1, c));

    public static bool TryParsePurchase(string? action, out int medicationId)
    {
        medicationId = 0;
        return action is not null && action.StartsWith(PurchasePrefix, StringComparison.Ordinal)
               && int.TryParse(action.AsSpan(PurchasePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out medicationId)
               && medicationId > 0;
    }
}

/// <summary>Handles one tapped notification action. Scoped: one per event.</summary>
public sealed class NotificationActionHandler(MedStockDb db, MedicationService service, CurrentUser user, ILogger<NotificationActionHandler> log)
{
    public const string FallbackUserId = "notification";

    /// <summary>Returns true when the action was a MedStock purchase that was booked.</summary>
    public async Task<bool> HandleAsync(string? action, string? haUserId, CancellationToken ct = default)
    {
        if (!NotificationActions.TryParsePurchase(action, out var id)) return false;
        var m = await db.Medications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null || m.Archived)
        {
            log.LogInformation("Notification action for unknown or archived medication {MedicationId} ignored", id);
            return false;
        }
        user.Id = string.IsNullOrWhiteSpace(haUserId) ? FallbackUserId : haUserId;
        await service.PurchaseAsync(id, 1);
        log.LogInformation("Medication {MedicationId}: +1 package from a notification action", id);
        return true;
    }
}

/// <summary>
/// Listens to Home Assistant's event bus (ws://supervisor/core/websocket) for taps on notification buttons.
/// Add-on mode only. Reconnects with backoff; never throws; no connection during host startup.
/// </summary>
public sealed class HaEventListener(IServiceScopeFactory scopes, IConfiguration config, ILogger<HaEventListener> log) : BackgroundService
{
    public const string Url = "ws://supervisor/core/websocket";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    // The same tap can arrive twice (e.g. a retried delivery); ignore repeats of one action within a short window.
    private readonly Dictionary<string, DateTime> _recent = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var token = config["SUPERVISOR_TOKEN"];
        if (string.IsNullOrEmpty(token)) return;
        try { await Task.Delay(StartupDelay, ct); } catch (OperationCanceledException) { return; }

        var backoff = TimeSpan.FromSeconds(5);
        var everConnected = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(Url), ct);
                await AuthenticateAndSubscribeAsync(ws, token, ct);
                log.LogInformation(everConnected ? "Reconnected to Home Assistant events" : "Listening for notification actions");
                everConnected = true;
                backoff = TimeSpan.FromSeconds(5);
                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning("Home Assistant event connection lost ({Error}); retrying in {Seconds} s", ex.GetType().Name, (int)backoff.TotalSeconds);
            }
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    private static async Task AuthenticateAndSubscribeAsync(ClientWebSocket ws, string token, CancellationToken ct)
    {
        var hello = await ReceiveJsonAsync(ws, ct);
        if ((string?)hello?["type"] != "auth_required") throw new InvalidOperationException("unexpected handshake");
        await SendJsonAsync(ws, new JsonObject { ["type"] = "auth", ["access_token"] = token }, ct);
        var auth = await ReceiveJsonAsync(ws, ct);
        if ((string?)auth?["type"] != "auth_ok") throw new UnauthorizedAccessException("auth failed");
        await SendJsonAsync(ws, new JsonObject { ["id"] = 1, ["type"] = "subscribe_events", ["event_type"] = NotificationActions.EventType }, ct);
        var result = await ReceiveJsonAsync(ws, ct);
        if (result?["success"]?.GetValue<bool>() != true) throw new InvalidOperationException("subscribe failed");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var msg = await ReceiveJsonAsync(ws, ct) ?? throw new WebSocketException("closed");
            if ((string?)msg["type"] != "event") continue;
            var ev = msg["event"];
            var action = (string?)ev?["data"]?["action"];
            if (!NotificationActions.TryParsePurchase(action, out _) || IsRepeat(action!)) continue;
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<NotificationActionHandler>()
                    .HandleAsync(action, (string?)ev?["context"]?["user_id"], ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Notification action failed: {Error}", ex.GetType().Name);
            }
        }
    }

    private bool IsRepeat(string action)
    {
        var now = DateTime.UtcNow;
        foreach (var old in _recent.Where(kv => now - kv.Value > TimeSpan.FromSeconds(10)).Select(kv => kv.Key).ToList()) _recent.Remove(old);
        if (_recent.ContainsKey(action)) return true;
        _recent[action] = now;
        return false;
    }

    private static Task SendJsonAsync(ClientWebSocket ws, JsonObject obj, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(obj.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonNode?> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 1_000_000) throw new InvalidDataException("message too large");
            if (r.EndOfMessage) break;
        }
        try { return JsonNode.Parse(ms.ToArray()); }
        catch (JsonException) { return null; }
    }
}
