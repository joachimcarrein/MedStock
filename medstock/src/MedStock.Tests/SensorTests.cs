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
    public async Task Failed_connection_is_reported_not_thrown()
    {
        using var e = new Env();
        e.Mqtt.FailConnect = true;
        await e.SyncAsync();
        Assert.Equal("connect-failed", e.F.Services.GetRequiredService<SensorStatus>().Problem);
        Assert.Empty(e.F.Ha.States);
    }

    [Fact]
    public async Task Without_a_broker_the_sensors_are_written_as_states_and_removed_again()
    {
        using var e = new Env();
        e.F.Ha.Broker = null;
        await e.SyncAsync();
        var status = e.F.Services.GetRequiredService<SensorStatus>();
        Assert.Equal("states", status.Mode);
        Assert.Null(status.Problem);
        Assert.Null(e.Mqtt.ConnectedTo);

        var (state, attrs) = e.F.Ha.States["sensor.medstock_nasalix_50_ug_husband"];
        Assert.Equal("20", state);
        Assert.Equal("d", (string?)attrs["unit_of_measurement"]);
        Assert.Equal("MedStock Nasalix 50 µg (Husband)", (string?)attrs["friendly_name"]);
        Assert.Equal(160m, (decimal?)attrs["remaining_units"]);
        Assert.Equal("unknown", e.F.Ha.States["sensor.medstock_paracetamol_husband"].State); // as needed: no days

        e.F.WithDb(db =>
        {
            db.Medications.Single(m => m.Id == e.Paracetamol).Archived = true;
            return db.SaveChanges();
        });
        await e.SyncAsync();
        Assert.False(e.F.Ha.States.ContainsKey("sensor.medstock_paracetamol_husband"));
        Assert.True(e.F.Ha.States.ContainsKey("sensor.medstock_nasalix_50_ug_husband"));

        // A broker shows up: MQTT takes over and the plain states are deleted.
        e.F.Ha.Broker = new MqttBroker("core-mosquitto", 1883, false, null, null);
        await e.SyncAsync();
        Assert.Equal("mqtt", status.Mode);
        Assert.Empty(e.F.Ha.States);
        Assert.True(string.IsNullOrEmpty(e.F.WithDb(db => db.GlobalSettings.Single().PublishedStateEntities)));
    }

    [Fact]
    public async Task Summary_sensor_per_profile_has_no_medication_names()
    {
        using var e = new Env(enabled: false);
        var profileId = e.F.WithDb(db =>
        {
            db.GlobalSettings.Single().SummarySensorsEnabled = true;
            db.SaveChanges();
            return db.Profiles.Single().Id;
        });
        await e.SyncAsync();
        Assert.DoesNotContain(e.Mqtt.Published, m => m.Topic == SensorMessages.ConfigTopic(e.Nasalix));
        var config = e.Payload(SensorMessages.ProfileConfigTopic(profileId));
        Assert.Equal("Husband days left", (string?)config["name"]);
        var state = e.Payload(SensorMessages.ProfileStateTopic(profileId));
        Assert.Equal(20, (int?)state["days"]); // Nasalix; Paracetamol (as needed) has no days
        Assert.Equal(2, (int?)state["medications"]);
        Assert.Equal(0, (int?)state["low"]);
        Assert.Equal(1, (int?)state["warning"]);
        Assert.DoesNotContain(e.Mqtt.Published, m => m.Payload.Contains("Nasalix") || m.Payload.Contains("Paracetamol"));

        e.F.Ha.Broker = null;
        await e.Mqtt.DisconnectAsync(CancellationToken.None);
        await e.SyncAsync();
        Assert.Equal("20", e.F.Ha.States["sensor.medstock_husband_days_left"].State);

        e.F.WithDb(db =>
        {
            db.GlobalSettings.Single().SummarySensorsEnabled = false;
            return db.SaveChanges();
        });
        await e.SyncAsync();
        Assert.Empty(e.F.Ha.States);
    }

    [Theory]
    [InlineData("Nasalix 50 µg", "nasalix_50_ug")]
    [InlineData("Émile's  crème", "emile_s_creme")]
    [InlineData("---", "x")]
    public void Slug_follows_home_assistant(string text, string slug) => Assert.Equal(slug, SensorMessages.Slug(text));

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
