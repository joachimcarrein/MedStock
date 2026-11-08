using System.Text.Json.Nodes;
using MedStock.Web.Domain;
using MedStock.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MedStock.Tests;

public sealed class FakeMqtt : IMqttConnection
{
    public bool IsConnected { get; private set; }
    public bool FailConnect { get; set; }
    public MqttBroker? ConnectedTo { get; private set; }
    public List<MqttMessage> Published { get; } = [];

    public Task ConnectAsync(MqttBroker broker, CancellationToken ct)
    {
        if (FailConnect) throw new IOException("refused");
        IsConnected = true;
        ConnectedTo = broker;
        return Task.CompletedTask;
    }

    public Task PublishAsync(MqttMessage message, CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("not connected");
        Published.Add(message);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Optional MQTT discovery sensors (fase 2), on 2026-11-17 (fixed clock).</summary>
public class SensorTests
{
    private sealed class Env : IDisposable
    {
        public readonly AddonWebFactory F = new();
        public readonly FakeMqtt Mqtt = new();
        public int Nasalix, Paracetamol;

        public Env(bool enabled = true)
        {
            F.Ha.Broker = new MqttBroker("core-mosquitto", 1883, false, "addons", "secret");
            (Nasalix, Paracetamol) = F.WithDb(db =>
            {
                var p = new Profile { Name = "Husband" };
                var a = new Medication
                {
                    Profile = p, Name = "Nasalix", Strength = "50 µg", Type = MedicationType.NasalSpray, UnitsPerPackage = 140, PerDay = 8,
                    Snapshots = [new Snapshot { Date = T.D("2026-11-07"), TotalUnits = 240 }],
                };
                var b = new Medication
                {
                    Profile = p, Name = "Paracetamol", Type = MedicationType.Pill, UnitsPerPackage = 20, UsageMode = UsageMode.AsNeeded,
                    Snapshots = [new Snapshot { Date = T.D("2026-11-07"), TotalUnits = 6 }],
                };
                db.Medications.AddRange(a, b);
                db.GlobalSettings.Single().SensorsEnabled = enabled;
                db.SaveChanges();
                return (a.Id, b.Id);
            });
        }

        public async Task SyncAsync()
        {
            using var scope = F.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SensorSync>().SyncAsync(Mqtt, CancellationToken.None);
        }

        public JsonNode Payload(string topic) => JsonNode.Parse(Mqtt.Published.Last(m => m.Topic == topic).Payload)!;
        public void Dispose() => F.Dispose();
    }

    [Fact]
    public async Task Disabled_by_default_publishes_nothing_and_does_not_connect()
    {
        using var e = new Env(enabled: false);
        await e.SyncAsync();
        Assert.Null(e.Mqtt.ConnectedTo);
        Assert.Empty(e.Mqtt.Published);
    }

    [Fact]
    public async Task Enabled_publishes_discovery_config_and_state_per_medication()
    {
        using var e = new Env();
        await e.SyncAsync();
        Assert.Equal("core-mosquitto", e.Mqtt.ConnectedTo!.Host);
        Assert.Contains(e.Mqtt.Published, m => m is { Topic: "medstock/status", Payload: "online", Retain: true });

        var config = e.Payload(SensorMessages.ConfigTopic(e.Nasalix));
        Assert.Equal("Nasalix 50 µg (Husband)", (string?)config["name"]);
        Assert.Equal($"medstock_med_{e.Nasalix}", (string?)config["unique_id"]);
        Assert.Equal("d", (string?)config["unit_of_measurement"]);
        Assert.Equal("medstock", (string?)config["device"]!["identifiers"]![0]);
        Assert.True(e.Mqtt.Published.Where(m => m.Topic.StartsWith("homeassistant/")).All(m => m.Retain));

        var state = e.Payload(SensorMessages.StateTopic(e.Nasalix));
        Assert.Equal(20, (int?)state["days"]); // 240 − 10 × 8 = 160 → 20 days
        Assert.Equal(160m, (decimal?)state["remaining_units"]);
        Assert.Equal("2026-12-07", (string?)state["last_dose_date"]);

        var asNeeded = e.Payload(SensorMessages.StateTopic(e.Paracetamol));
        Assert.Null(asNeeded["days"]); // → "unknown" in HA
        Assert.Equal("AsNeededOk", (string?)asNeeded["state"]);
    }

    [Fact]
    public async Task Archived_medication_and_disabling_remove_the_sensors()
    {
        using var e = new Env();
        await e.SyncAsync();
        e.F.WithDb(db =>
        {
            db.Medications.Single(m => m.Id == e.Paracetamol).Archived = true;
            return db.SaveChanges();
        });
        e.Mqtt.Published.Clear();
        await e.SyncAsync();
        Assert.Contains(e.Mqtt.Published, m => m.Topic == SensorMessages.ConfigTopic(e.Paracetamol) && m.Payload == "" && m.Retain);
        Assert.DoesNotContain(e.Mqtt.Published, m => m.Topic == SensorMessages.ConfigTopic(e.Nasalix) && m.Payload == "");

        e.F.WithDb(db =>
        {
            db.GlobalSettings.Single().SensorsEnabled = false;
            return db.SaveChanges();
        });
        e.Mqtt.Published.Clear();
        await e.SyncAsync();
        Assert.Contains(e.Mqtt.Published, m => m.Topic == SensorMessages.ConfigTopic(e.Nasalix) && m.Payload == "");
        Assert.False(e.Mqtt.IsConnected);
        Assert.True(string.IsNullOrEmpty(e.F.WithDb(db => db.GlobalSettings.Single().PublishedSensorIds)));

        e.Mqtt.Published.Clear();
        await e.SyncAsync(); // nothing left to clean up: stays disconnected
        Assert.Empty(e.Mqtt.Published);
    }

    [Fact]
    public async Task No_broker_or_failed_connection_is_reported_not_thrown()
    {
        using var e = new Env();
        e.F.Ha.Broker = null;
        await e.SyncAsync();
        Assert.Equal("no-broker", e.F.Services.GetRequiredService<SensorStatus>().Problem);

        e.F.Ha.Broker = new MqttBroker("core-mosquitto", 1883, false, null, null);
        e.Mqtt.FailConnect = true;
        await e.SyncAsync();
        Assert.Equal("connect-failed", e.F.Services.GetRequiredService<SensorStatus>().Problem);
    }

    [Fact]
    public async Task Sensor_logs_contain_no_names()
    {
        using var e = new Env();
        await e.SyncAsync();
        var lines = e.F.Logs.Lines.Where(l => l.Category.Contains("SensorSync") && l.Level >= LogLevel.Information).ToList();
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, l => l.Message.Contains("Nasalix") || l.Message.Contains("Husband") || l.Message.Contains("secret"));
    }
}
