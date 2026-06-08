using Nexus.Mqtt;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Nexus.Mqtt.Tests
{
    public class MqttClientBrokerTests
    {
        [Fact]
        public async Task Client_CanConnectAndDisconnect()
        {
            using var broker = new MqttBroker();
            broker.Start(0);
            int port = ((System.Net.IPEndPoint)broker.ServerPort).Port;

            using var client = new MqttClient();
            await client.ConnectAsync("127.0.0.1", port, "test-client");
            Assert.True(client.IsConnected);

            client.Disconnect();
            Assert.False(client.IsConnected);
        }

        [Fact]
        public async Task Client_CanPublishAndReceive_QoS0()
        {
            using var broker = new MqttBroker();
            broker.Start(0);
            int port = ((System.Net.IPEndPoint)broker.ServerPort).Port;

            using var subscriber = new MqttClient();
            await subscriber.ConnectAsync("127.0.0.1", port, "sub-client");

            var tcs = new TaskCompletionSource<MqttMessageEventArgs>();
            subscriber.OnMessageReceived += (s, e) => tcs.TrySetResult(e);

            await subscriber.SubscribeAsync("test/topic", MqttQoS.AtMostOnce);
            await Task.Delay(100);

            using var publisher = new MqttClient();
            await publisher.ConnectAsync("127.0.0.1", port, "pub-client");
            await publisher.PublishAsync("test/topic", Encoding.UTF8.GetBytes("hello"), MqttQoS.AtMostOnce);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.Equal(tcs.Task, completed);

            var msg = await tcs.Task;
            Assert.Equal("test/topic", msg.Topic);
            Assert.Equal("hello", msg.PayloadString);
        }

        [Fact]
        public async Task Broker_RetainedMessages_DeliveredToNewSubscribers()
        {
            using var broker = new MqttBroker();
            broker.Start(0);
            int port = ((System.Net.IPEndPoint)broker.ServerPort).Port;

            using var publisher = new MqttClient();
            await publisher.ConnectAsync("127.0.0.1", port, "pub-client");
            await publisher.PublishAsync("retained/topic", Encoding.UTF8.GetBytes("retained-data"), MqttQoS.AtMostOnce, retain: true);

            await Task.Delay(200);

            using var subscriber = new MqttClient();
            await subscriber.ConnectAsync("127.0.0.1", port, "sub-client");

            var tcs = new TaskCompletionSource<MqttMessageEventArgs>();
            subscriber.OnMessageReceived += (s, e) => tcs.TrySetResult(e);

            await subscriber.SubscribeAsync("retained/topic", MqttQoS.AtMostOnce);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.Equal(tcs.Task, completed);

            var msg = await tcs.Task;
            Assert.Equal("retained-data", msg.PayloadString);
            Assert.True(msg.Retain);
        }

        [Fact]
        public async Task Broker_WildcardSubscription_ReceivesMatchingMessages()
        {
            using var broker = new MqttBroker();
            broker.Start(0);
            int port = ((System.Net.IPEndPoint)broker.ServerPort).Port;

            using var subscriber = new MqttClient();
            await subscriber.ConnectAsync("127.0.0.1", port, "sub-client");

            var messages = new System.Collections.Concurrent.ConcurrentBag<MqttMessageEventArgs>();
            subscriber.OnMessageReceived += (s, e) => messages.Add(e);

            await subscriber.SubscribeAsync("sensors/+/data", MqttQoS.AtMostOnce);
            await Task.Delay(100);

            using var publisher = new MqttClient();
            await publisher.ConnectAsync("127.0.0.1", port, "pub-client");
            await publisher.PublishAsync("sensors/temp/data", Encoding.UTF8.GetBytes("25.5"));
            await publisher.PublishAsync("sensors/humidity/data", Encoding.UTF8.GetBytes("60"));
            await publisher.PublishAsync("sensors/other/ignored/extra", Encoding.UTF8.GetBytes("nope"));

            await Task.Delay(500);

            Assert.Equal(2, messages.Count);
        }

        [Fact]
        public async Task Client_QoS1_PublishReceivesPubAck()
        {
            using var broker = new MqttBroker();
            broker.Start(0);
            int port = ((System.Net.IPEndPoint)broker.ServerPort).Port;

            using var subscriber = new MqttClient();
            await subscriber.ConnectAsync("127.0.0.1", port, "sub-client");

            var tcs = new TaskCompletionSource<MqttMessageEventArgs>();
            subscriber.OnMessageReceived += (s, e) => tcs.TrySetResult(e);
            await subscriber.SubscribeAsync("qos1/topic", MqttQoS.AtLeastOnce);
            await Task.Delay(100);

            using var publisher = new MqttClient();
            await publisher.ConnectAsync("127.0.0.1", port, "pub-client");
            await publisher.PublishAsync("qos1/topic", Encoding.UTF8.GetBytes("qos1-msg"), MqttQoS.AtLeastOnce);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.Equal(tcs.Task, completed);

            var msg = await tcs.Task;
            Assert.Equal("qos1-msg", msg.PayloadString);
        }

        [Fact]
        public async Task Broker_OnClientConnected_Fires()
        {
            using var broker = new MqttBroker();
            broker.Start(0);
            int port = ((System.Net.IPEndPoint)broker.ServerPort).Port;

            var connectedTcs = new TaskCompletionSource<string>();
            broker.OnClientConnected += (s, id) => connectedTcs.TrySetResult(id);

            using var client = new MqttClient();
            await client.ConnectAsync("127.0.0.1", port, "event-client");

            var completed = await Task.WhenAny(connectedTcs.Task, Task.Delay(5000));
            Assert.Equal(connectedTcs.Task, completed);
            Assert.Equal("event-client", await connectedTcs.Task);
        }

        [Fact]
        public async Task Client_OnMessageReceived_EventPayloadCorrect()
        {
            using var broker = new MqttBroker();
            broker.Start(0);
            int port = ((System.Net.IPEndPoint)broker.ServerPort).Port;

            using var sub = new MqttClient();
            await sub.ConnectAsync("127.0.0.1", port, "sub2");
            var tcs = new TaskCompletionSource<MqttMessageEventArgs>();
            sub.OnMessageReceived += (s, e) => tcs.TrySetResult(e);
            await sub.SubscribeAsync("big/payload", MqttQoS.AtMostOnce);
            await Task.Delay(100);

            byte[] bigPayload = new byte[1024];
            for (int i = 0; i < bigPayload.Length; i++) bigPayload[i] = (byte)(i & 0xFF);

            using var pub = new MqttClient();
            await pub.ConnectAsync("127.0.0.1", port, "pub2");
            await pub.PublishAsync("big/payload", bigPayload);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.Equal(tcs.Task, completed);

            var msg = await tcs.Task;
            Assert.Equal(bigPayload, msg.Payload);
        }

        [Fact]
        public async Task Broker_MultipleSubscribers_AllReceiveMessage()
        {
            using var broker = new MqttBroker();
            broker.Start(0);
            int port = ((System.Net.IPEndPoint)broker.ServerPort).Port;

            var received1 = new TaskCompletionSource<MqttMessageEventArgs>();
            var received2 = new TaskCompletionSource<MqttMessageEventArgs>();

            using var sub1 = new MqttClient();
            await sub1.ConnectAsync("127.0.0.1", port, "sub1");
            sub1.OnMessageReceived += (s, e) => received1.TrySetResult(e);
            await sub1.SubscribeAsync("broadcast", MqttQoS.AtMostOnce);

            using var sub2 = new MqttClient();
            await sub2.ConnectAsync("127.0.0.1", port, "sub2");
            sub2.OnMessageReceived += (s, e) => received2.TrySetResult(e);
            await sub2.SubscribeAsync("broadcast", MqttQoS.AtMostOnce);

            await Task.Delay(100);

            using var pub = new MqttClient();
            await pub.ConnectAsync("127.0.0.1", port, "pub");
            await pub.PublishAsync("broadcast", Encoding.UTF8.GetBytes("hello-all"));

            await Task.WhenAll(
                Task.WhenAny(received1.Task, Task.Delay(5000)),
                Task.WhenAny(received2.Task, Task.Delay(5000))
            );

            Assert.True(received1.Task.IsCompleted);
            Assert.True(received2.Task.IsCompleted);
            Assert.Equal("hello-all", (await received1.Task).PayloadString);
            Assert.Equal("hello-all", (await received2.Task).PayloadString);
        }
    }
}
