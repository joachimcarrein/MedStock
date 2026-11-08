using System.Globalization;
using System.Text.Json.Nodes;
using MedStock.Web.Data;
using MedStock.Web.Domain;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;

namespace MedStock.Web.Services;

/// <summary>One MQTT message (UTF-8 payload); an empty retained payload deletes a retained message.</summary>
public sealed record MqttMessage(string Topic, string Payload, bool Retain);

/// <summary>Home Assistant MQTT discovery messages for the "days remaining" sensors (pure, testable).</summary>
public static class SensorMessages
{
    public const string AvailabilityTopic = "medstock/status";
    public static string ConfigTopic(int id) => $"homeassistant/sensor/medstock/med_{id}/config";
    public static string StateTopic(int id) => $"medstock/medication/{id}";

    public static MqttMessage Config(MedicationRow r, string version)
    {
        var m = r.Medication;
        var name = string.IsNullOrWhiteSpace(m.Strength) ? m.Name : $"{m.Name} {m.Strength}";
        var json = new JsonObject
        {
            ["name"] = $"{name} ({r.Profile.Name})",
            ["unique_id"] = $"medstock_med_{m.Id}",
            ["state_topic"] = StateTopic(m.Id),
            ["value_template"] = "{{ value_json.days }}",
            ["json_attributes_topic"] = StateTopic(m.Id),
            ["unit_of_measurement"] = "d",
            ["icon"] = "mdi:pill",
            ["availability_topic"] = AvailabilityTopic,
            ["device"] = new JsonObject
            {
                ["identifiers"] = new JsonArray("medstock"),
                ["name"] = "MedStock",
                ["manufacturer"] = "MedStock",
                ["model"] = "Home Assistant app",
                ["sw_version"] = version,
            },
        };
        return new MqttMessage(ConfigTopic(m.Id), json.ToJsonString(), Retain: true);
    }

    /// <summary>State + attributes. "days" is null (→ unknown in HA) for as-needed or paused medication.</summary>
    public static MqttMessage State(MedicationRow r)
    {
        var m = r.Medication;
        var s = r.Stock;
        var json = new JsonObject
        {
            ["days"] = s.DaysRemaining,
            ["state"] = s.State.ToString(),
            ["remaining_units"] = Math.Floor(s.Remaining),
            ["packages"] = s.Packages,
            ["unit"] = Texts.Unit(m.Type, 2, CultureInfo.InvariantCulture),
            ["profile"] = r.Profile.Name,
            ["medication"] = m.Name,
            ["last_dose_date"] = s.LastDoseDate is { } d && s.State != StockState.Empty ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        };
        return new MqttMessage(StateTopic(m.Id), json.ToJsonString(), Retain: true);
    }

    public static IEnumerable<MqttMessage> Remove(int id) =>
        [new(ConfigTopic(id), "", true), new(StateTopic(id), "", true)];

    public static string FormatIds(IEnumerable<int> ids) => string.Join(",", ids.Order());

    public static HashSet<int> ParseIds(string? raw) =>
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : 0).Where(i => i > 0).ToHashSet();
}

/// <summary>Minimal MQTT connection, swappable in tests.</summary>
public interface IMqttConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(MqttBroker broker, CancellationToken ct);
    Task PublishAsync(MqttMessage message, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}

public sealed class MqttNetConnection : IMqttConnection
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(MqttBroker broker, CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(broker.Host, broker.Port)
            .WithClientId("medstock")
            .WithCleanSession()
            .WithWillTopic(SensorMessages.AvailabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(broker.Username)) builder.WithCredentials(broker.Username, broker.Password);
        if (broker.Ssl) builder.WithTlsOptions(o => o.UseTls());
        await _client.ConnectAsync(builder.Build(), ct);
    }

    public Task PublishAsync(MqttMessage message, CancellationToken ct) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(message.Payload)
            .WithRetainFlag(message.Retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (_client.IsConnected) await _client.DisconnectAsync(cancellationToken: ct);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Status shown in Settings.</summary>
public sealed class SensorStatus
{
    public DateTime? LastPublishedAt { get; set; }
    public int Count { get; set; }
    public string? Broker { get; set; }
    /// <summary>"no-broker", "connect-failed" or null.</summary>
    public string? Problem { get; set; }
}

/// <summary>Wakes the sensor publisher after a change (coalesced).</summary>
public sealed class SensorSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    public void Request()
    {
        try { if (_signal.CurrentCount == 0) _signal.Release(); } catch (SemaphoreFullException) { }
    }
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Publishes (or removes) the sensors: after every change, at startup and every 15 minutes (the days go down by
/// themselves at midnight). Only when enabled in the global settings; logs ids only.
/// </summary>
public sealed class SensorSync(MedStockDb db, IHaClient ha, IClock clock, SensorStatus status, ILogger<SensorSync> log)
{
    public async Task SyncAsync(IMqttConnection mqtt, CancellationToken ct)
    {
        var global = await db.GlobalSettings.SingleAsync(ct);
        var published = SensorMessages.ParseIds(global.PublishedSensorIds);
        var rows = global.SensorsEnabled ? await MedicationQueries.LoadAsync(db, clock.Today) : [];
        if (!global.SensorsEnabled && published.Count == 0)
        {
            status.Problem = null;
            status.Count = 0;
            if (mqtt.IsConnected) await mqtt.DisconnectAsync(ct);
            return;
        }

        if (!mqtt.IsConnected)
        {
            var broker = await ha.GetMqttBrokerAsync(ct);
            if (broker is null)
            {
                status.Problem = "no-broker";
                return;
            }
            try { await mqtt.ConnectAsync(broker, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.Problem = "connect-failed";
                log.LogWarning("MQTT connection to the broker failed: {Error}", ex.GetType().Name);
                return;
            }
            status.Broker = $"{broker.Host}:{broker.Port}";
        }

        var version = DbInitializer.AppVersion;
        var current = rows.Select(r => r.Medication.Id).ToHashSet();
        foreach (var id in published.Except(current))
            foreach (var msg in SensorMessages.Remove(id)) await mqtt.PublishAsync(msg, ct);

        if (global.SensorsEnabled)
        {
            await mqtt.PublishAsync(new MqttMessage(SensorMessages.AvailabilityTopic, "online", true), ct);
            // Configs are re-sent on every sync (retained, idempotent in HA) so renamed medication or profiles follow.
            foreach (var r in rows)
            {
                await mqtt.PublishAsync(SensorMessages.Config(r, version), ct);
                await mqtt.PublishAsync(SensorMessages.State(r), ct);
            }
        }
        else
        {
            await mqtt.PublishAsync(new MqttMessage(SensorMessages.AvailabilityTopic, "", true), ct);
        }

        global.PublishedSensorIds = SensorMessages.FormatIds(current);
        await db.SaveChangesAsync(ct);
        status.Problem = null;
        status.Count = current.Count;
        status.LastPublishedAt = clock.Now;
        log.LogInformation("Sensors synced: {Count} published, {Removed} removed", current.Count, published.Except(current).Count());
        if (!global.SensorsEnabled) await mqtt.DisconnectAsync(ct);
    }
}

public sealed class SensorWorker(IServiceScopeFactory scopes, SensorSignal signal, Func<IMqttConnection> connect, ILogger<SensorWorker> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await using var mqtt = connect();
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SensorSync>().SyncAsync(mqtt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning("Sensor sync failed: {Error}", ex.GetType().Name);
                try { await mqtt.DisconnectAsync(ct); } catch (Exception) { /* reconnect next time */ }
            }
            try
            {
                if (await signal.WaitAsync(Interval, ct))
                    await Task.Delay(TimeSpan.FromSeconds(2), ct); // coalesce bursts of changes
            }
            catch (OperationCanceledException) { return; }
        }
    }
}
