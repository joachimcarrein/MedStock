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
        var json = Config($"{name} ({r.Profile.Name})", $"medstock_med_{m.Id}", StateTopic(m.Id), version, "mdi:pill");
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

    // ---- Per-profile summary (no medication names) ----

    public static string ProfileConfigTopic(int id) => $"homeassistant/sensor/medstock/profile_{id}/config";
    public static string ProfileStateTopic(int id) => $"medstock/profile/{id}";

    public static MqttMessage ProfileConfig(ProfileSummary s, string version)
    {
        var json = Config(SummaryName(s.Profile), $"medstock_profile_{s.Profile.Id}", ProfileStateTopic(s.Profile.Id), version, "mdi:account-heart");
        return new MqttMessage(ProfileConfigTopic(s.Profile.Id), json.ToJsonString(), Retain: true);
    }

    public static MqttMessage ProfileState(ProfileSummary s) =>
        new(ProfileStateTopic(s.Profile.Id), SummaryAttributes(s).ToJsonString(), Retain: true);

    public static IEnumerable<MqttMessage> RemoveProfile(int id) =>
        [new(ProfileConfigTopic(id), "", true), new(ProfileStateTopic(id), "", true)];

    public static string SummaryName(Profile p) => $"{p.Name} days left";

    /// <summary>One summary per profile: lowest days of the medication with a daily/weekly use, and counts per colour.</summary>
    public static List<ProfileSummary> Summaries(IEnumerable<Profile> profiles, IReadOnlyCollection<MedicationRow> rows) =>
        profiles.Select(p =>
        {
            var mine = rows.Where(r => r.Medication.ProfileId == p.Id).ToList();
            var days = mine.Where(r => r.Stock.State != StockState.Paused && r.Medication.UsageMode != UsageMode.AsNeeded)
                .Select(r => r.Stock.State == StockState.Empty ? 0 : r.Stock.DaysRemaining).Where(d => d is not null).ToList();
            return new ProfileSummary(p, days.Count == 0 ? null : days.Min(), mine.Count,
                mine.Count(r => r.ColorClass == "red"), mine.Count(r => r.ColorClass == "orange"));
        }).ToList();

    private static JsonObject SummaryAttributes(ProfileSummary s) => new()
    {
        ["days"] = s.MinDays,
        ["medications"] = s.Count,
        ["low"] = s.Low,
        ["warning"] = s.Warning,
        ["profile"] = s.Profile.Name,
    };

    private static JsonObject Config(string name, string uniqueId, string stateTopic, string version, string icon) => new()
    {
        ["name"] = name,
        ["unique_id"] = uniqueId,
        ["state_topic"] = stateTopic,
        ["value_template"] = "{{ value_json.days }}",
        ["json_attributes_topic"] = stateTopic,
        ["unit_of_measurement"] = "d",
        ["icon"] = icon,
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

    // ---- Without MQTT: plain states through the REST API (lost at a Home Assistant restart until the next sync) ----

    /// <summary>The states to write when there is no MQTT broker; entity ids follow the MQTT naming (sensor.medstock_…).</summary>
    public static List<StateSensor> StateSensors(IEnumerable<MedicationRow> meds, IEnumerable<ProfileSummary> summaries)
    {
        var list = new List<StateSensor>();
        var used = new HashSet<string>();
        string Unique(string id, int key) => used.Add(id) ? id : Unique($"{id}_{key}", key);

        foreach (var r in meds)
        {
            var m = r.Medication;
            var name = string.IsNullOrWhiteSpace(m.Strength) ? m.Name : $"{m.Name} {m.Strength}";
            var attrs = JsonNode.Parse(State(r).Payload)!.AsObject();
            var days = attrs["days"]?.ToJsonString();
            attrs.Remove("days");
            attrs.Remove("state"); // HA would drop it anyway: "state" is not an attribute name
            attrs["friendly_name"] = $"MedStock {name} ({r.Profile.Name})";
            attrs["unit_of_measurement"] = "d";
            attrs["icon"] = "mdi:pill";
            list.Add(new StateSensor(Unique($"sensor.medstock_{Slug(name)}_{Slug(r.Profile.Name)}", m.Id), days ?? "unknown", attrs));
        }
        foreach (var s in summaries)
        {
            var attrs = SummaryAttributes(s);
            attrs.Remove("days");
            attrs["friendly_name"] = $"MedStock {SummaryName(s.Profile)}";
            attrs["unit_of_measurement"] = "d";
            attrs["icon"] = "mdi:account-heart";
            list.Add(new StateSensor(Unique($"sensor.medstock_{Slug(SummaryName(s.Profile))}", s.Profile.Id),
                s.MinDays?.ToString(CultureInfo.InvariantCulture) ?? "unknown", attrs));
        }
        return list;
    }

    /// <summary>Home Assistant style object id: lower-case ASCII letters and digits separated by single underscores.</summary>
    public static string Slug(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text.Normalize(System.Text.NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = ch is 'μ' or 'µ' ? 'u' : char.ToLowerInvariant(ch); // "µg" → "ug", as Home Assistant does
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var slug = sb.ToString().Trim('_');
        return slug.Length == 0 ? "x" : slug;
    }

    public static string FormatKeys(IEnumerable<string> keys) => string.Join(",", keys.Order(StringComparer.Ordinal));

    /// <summary>"12" = medication 12, "p3" = summary of profile 3 (older versions only stored medication ids).</summary>
    public static HashSet<string> ParseKeys(string? raw) =>
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    public static string MedKey(int id) => id.ToString(CultureInfo.InvariantCulture);
    public static string ProfileKey(int id) => "p" + id.ToString(CultureInfo.InvariantCulture);
}

public sealed record ProfileSummary(Profile Profile, int? MinDays, int Count, int Low, int Warning);

/// <summary>A state written with POST /api/states/&lt;entity id&gt;.</summary>
public sealed record StateSensor(string EntityId, string State, JsonObject Attributes);

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
    /// <summary>"mqtt" (discovery, survives HA restarts) or "states" (REST API, no broker).</summary>
    public string? Mode { get; set; }
    /// <summary>"connect-failed", "ha-failed" or null.</summary>
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
/// themselves at midnight). Uses MQTT discovery when the Supervisor offers a broker, otherwise plain states through the
/// REST API. Only when enabled in the global settings; logs ids only.
/// </summary>
public sealed class SensorSync(MedStockDb db, IHaClient ha, IClock clock, SensorStatus status, ILogger<SensorSync> log)
{
    public async Task SyncAsync(IMqttConnection mqtt, CancellationToken ct)
    {
        var global = await db.GlobalSettings.SingleAsync(ct);
        bool any = global.SensorsEnabled || global.SummarySensorsEnabled;
        var published = SensorMessages.ParseKeys(global.PublishedSensorIds);
        var publishedStates = SensorMessages.ParseKeys(global.PublishedStateEntities);
        if (!any && published.Count == 0 && publishedStates.Count == 0)
        {
            status.Problem = null;
            status.Count = 0;
            if (mqtt.IsConnected) await mqtt.DisconnectAsync(ct);
            return;
        }

        var rows = any ? await MedicationQueries.LoadAsync(db, clock.Today) : [];
        var meds = global.SensorsEnabled ? rows : [];
        var profiles = global.SummarySensorsEnabled
            ? await db.Profiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync(ct)
            : [];
        var summaries = SensorMessages.Summaries(profiles, rows);

        if (!mqtt.IsConnected)
        {
            var broker = await ha.GetMqttBrokerAsync(ct);
            if (broker is not null)
            {
                try { await mqtt.ConnectAsync(broker, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status.Problem = "connect-failed";
                    log.LogWarning("MQTT connection to the broker failed: {Error}", ex.GetType().Name);
                    return;
                }
                status.Broker = $"{broker.Host}:{broker.Port}";
            }
        }

        if (mqtt.IsConnected) await PublishMqttAsync(mqtt, global, published, publishedStates, meds, summaries, any, ct);
        else await WriteStatesAsync(global, publishedStates, meds, summaries, ct);
    }

    private async Task PublishMqttAsync(IMqttConnection mqtt, GlobalSettings global, HashSet<string> published, HashSet<string> publishedStates,
        List<MedicationRow> meds, List<ProfileSummary> summaries, bool any, CancellationToken ct)
    {
        var current = meds.Select(r => SensorMessages.MedKey(r.Medication.Id))
            .Concat(summaries.Select(s => SensorMessages.ProfileKey(s.Profile.Id))).ToHashSet();
        var removed = published.Except(current).ToList();
        foreach (var key in removed)
        {
            var messages = key.StartsWith('p')
                ? SensorMessages.RemoveProfile(int.Parse(key[1..], CultureInfo.InvariantCulture))
                : SensorMessages.Remove(int.Parse(key, CultureInfo.InvariantCulture));
            foreach (var msg in messages) await mqtt.PublishAsync(msg, ct);
        }

        if (any)
        {
            var version = DbInitializer.AppVersion;
            await mqtt.PublishAsync(new MqttMessage(SensorMessages.AvailabilityTopic, "online", true), ct);
            // Configs are re-sent on every sync (retained, idempotent in HA) so renamed medication or profiles follow.
            foreach (var r in meds)
            {
                await mqtt.PublishAsync(SensorMessages.Config(r, version), ct);
                await mqtt.PublishAsync(SensorMessages.State(r), ct);
            }
            foreach (var s in summaries)
            {
                await mqtt.PublishAsync(SensorMessages.ProfileConfig(s, version), ct);
                await mqtt.PublishAsync(SensorMessages.ProfileState(s), ct);
            }
        }
        else
        {
            await mqtt.PublishAsync(new MqttMessage(SensorMessages.AvailabilityTopic, "", true), ct);
        }

        // A broker appeared after states were written through the REST API: remove those duplicates.
        foreach (var entity in publishedStates.ToList())
            if (await ha.DeleteStateAsync(entity, ct)) publishedStates.Remove(entity);

        global.PublishedSensorIds = SensorMessages.FormatKeys(current);
        global.PublishedStateEntities = SensorMessages.FormatKeys(publishedStates);
        await db.SaveChangesAsync(ct);
        status.Mode = "mqtt";
        status.Problem = null;
        status.Count = current.Count;
        status.LastPublishedAt = clock.Now;
        log.LogInformation("Sensors synced over MQTT: {Count} published, {Removed} removed", current.Count, removed.Count);
        if (!any) await mqtt.DisconnectAsync(ct);
    }

    private async Task WriteStatesAsync(GlobalSettings global, HashSet<string> publishedStates,
        List<MedicationRow> meds, List<ProfileSummary> summaries, CancellationToken ct)
    {
        var sensors = SensorMessages.StateSensors(meds, summaries);
        var wanted = sensors.Select(s => s.EntityId).ToHashSet();
        var written = new HashSet<string>(publishedStates);
        int failed = 0, removed = 0;
        foreach (var entity in publishedStates.Except(wanted))
        {
            if (await ha.DeleteStateAsync(entity, ct)) { written.Remove(entity); removed++; }
            else failed++;
        }
        foreach (var s in sensors)
        {
            if (await ha.SetStateAsync(s.EntityId, s.State, s.Attributes, ct)) written.Add(s.EntityId);
            else failed++;
        }

        global.PublishedStateEntities = SensorMessages.FormatKeys(written);
        await db.SaveChangesAsync(ct);
        status.Mode = "states";
        status.Problem = failed > 0 ? "ha-failed" : null;
        status.Count = sensors.Count;
        if (failed == 0) status.LastPublishedAt = clock.Now;
        if (failed > 0) log.LogWarning("Sensors through the states API: {Failed} of {Count} writes failed", failed, sensors.Count + removed);
        else log.LogInformation("Sensors synced through the states API: {Count} written, {Removed} removed", sensors.Count, removed);
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
