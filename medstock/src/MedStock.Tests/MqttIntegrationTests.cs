using MedStock.Web.Services;
using MQTTnet;

namespace MedStock.Tests;

/// <summary>Real broker round trip. Runs when MEDSTOCK_TEST_MQTT=host:port is set (CI starts eclipse-mosquitto).</summary>
public class MqttIntegrationTests
{
    private static MqttBroker? Broker()
    {
        var v = Environment.GetEnvironmentVariable("MEDSTOCK_TEST_MQTT");
        if (string.IsNullOrEmpty(v)) return null;
        var parts = v.Split(':');
        return new MqttBroker(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 1883, false, null, null);
    }

    [Fact]
    public async Task Retained_discovery_message_reaches_a_subscriber()
    {
        var broker = Broker();
        if (broker is null) return; // not configured locally

        await using (var conn = new MqttNetConnection())
        {
            await conn.ConnectAsync(broker, CancellationToken.None);
            await conn.PublishAsync(new MqttMessage("homeassistant/sensor/medstock/med_test/config", "{\"name\":\"x\"}", true), CancellationToken.None);
            await conn.DisconnectAsync(CancellationToken.None);
        }

        var received = new TaskCompletionSource<string>();
        using var sub = new MqttClientFactory().CreateMqttClient();
        sub.ApplicationMessageReceivedAsync += e =>
        {
            received.TrySetResult(e.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await sub.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(broker.Host, broker.Port).WithClientId("test-sub").Build());
        await sub.SubscribeAsync("homeassistant/sensor/medstock/med_test/config");
        Assert.Equal("{\"name\":\"x\"}", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
