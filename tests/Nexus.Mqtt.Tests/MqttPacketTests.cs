using Nexus.Mqtt;
using Xunit;

namespace Nexus.Mqtt.Tests
{
    public class MqttPacketTests
    {
        [Fact]
        public void EncodeDecode_ConnectPacket_RoundTrips()
        {
            var original = new MqttConnectPacket
            {
                CleanSession = true,
                KeepAlive = 60,
                ClientId = "test-client",
                HasUsername = true,
                Username = "user",
                HasPassword = true,
                Password = "pass"
            };

            byte[] encoded = MqttPacket.EncodeConnect(original);
            var decoded = MqttPacket.DecodeConnect(encoded, 2, encoded.Length - 2);

            Assert.Equal("MQTT", decoded.ProtocolName);
            Assert.Equal(4, decoded.ProtocolLevel);
            Assert.True(decoded.CleanSession);
            Assert.Equal(60, decoded.KeepAlive);
            Assert.Equal("test-client", decoded.ClientId);
            Assert.True(decoded.HasUsername);
            Assert.Equal("user", decoded.Username);
            Assert.True(decoded.HasPassword);
            Assert.Equal("pass", decoded.Password);
        }

        [Fact]
        public void EncodeDecode_ConnectWithWill_RoundTrips()
        {
            var original = new MqttConnectPacket
            {
                CleanSession = true,
                KeepAlive = 30,
                ClientId = "will-client",
                HasWill = true,
                WillTopic = "will/topic",
                WillMessage = new byte[] { 1, 2, 3 },
                WillQoS = MqttQoS.AtLeastOnce,
                WillRetain = true
            };

            byte[] encoded = MqttPacket.EncodeConnect(original);
            var decoded = MqttPacket.DecodeConnect(encoded, 2, encoded.Length - 2);

            Assert.True(decoded.HasWill);
            Assert.Equal("will/topic", decoded.WillTopic);
            Assert.Equal(new byte[] { 1, 2, 3 }, decoded.WillMessage);
            Assert.Equal(MqttQoS.AtLeastOnce, decoded.WillQoS);
            Assert.True(decoded.WillRetain);
        }

        [Fact]
        public void EncodeDecode_ConnAck_RoundTrips()
        {
            var original = new MqttConnAckPacket
            {
                SessionPresent = true,
                ReturnCode = MqttConnectReturnCode.Accepted
            };

            byte[] encoded = MqttPacket.EncodeConnAck(original);
            var decoded = MqttPacket.DecodeConnAck(encoded, 2);

            Assert.True(decoded.SessionPresent);
            Assert.Equal(MqttConnectReturnCode.Accepted, decoded.ReturnCode);
        }

        [Fact]
        public void EncodeDecode_PublishQoS0_RoundTrips()
        {
            var original = new MqttPublishPacket
            {
                Topic = "test/topic",
                Payload = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F },
                QoS = MqttQoS.AtMostOnce,
                Retain = false
            };

            byte[] encoded = MqttPacket.EncodePublish(original);
            int flags = encoded[0] & 0x0F;
            int offset = 1;
            int remainingLength = MqttPacket.DecodeRemainingLength(encoded, ref offset);
            var decoded = MqttPacket.DecodePublish((byte)flags, encoded, offset, remainingLength);

            Assert.Equal("test/topic", decoded.Topic);
            Assert.Equal(original.Payload, decoded.Payload);
            Assert.Equal(MqttQoS.AtMostOnce, decoded.QoS);
            Assert.False(decoded.Retain);
        }

        [Fact]
        public void EncodeDecode_PublishQoS1_RoundTrips()
        {
            var original = new MqttPublishPacket
            {
                Topic = "sensor/temp",
                Payload = new byte[] { 0x31, 0x32 },
                QoS = MqttQoS.AtLeastOnce,
                Retain = true,
                PacketId = 42
            };

            byte[] encoded = MqttPacket.EncodePublish(original);
            int flags = encoded[0] & 0x0F;
            int offset = 1;
            int remainingLength = MqttPacket.DecodeRemainingLength(encoded, ref offset);
            var decoded = MqttPacket.DecodePublish((byte)flags, encoded, offset, remainingLength);

            Assert.Equal("sensor/temp", decoded.Topic);
            Assert.Equal(original.Payload, decoded.Payload);
            Assert.Equal(MqttQoS.AtLeastOnce, decoded.QoS);
            Assert.True(decoded.Retain);
            Assert.Equal(42, decoded.PacketId);
        }

        [Fact]
        public void Encode_SubscribePacket()
        {
            var packet = new MqttSubscribePacket
            {
                PacketId = 1,
                Subscriptions = new System.Collections.Generic.List<(string, MqttQoS)>
                {
                    ("test/+", MqttQoS.AtLeastOnce),
                    ("data/#", MqttQoS.AtMostOnce)
                }
            };

            byte[] encoded = MqttPacket.EncodeSubscribe(packet);

            Assert.Equal((byte)((byte)MqttPacketType.Subscribe << 4 | 0x02), encoded[0]);

            int offset = 1;
            int remainingLength = MqttPacket.DecodeRemainingLength(encoded, ref offset);
            var decoded = MqttPacket.DecodeSubscribe(encoded, offset, remainingLength);

            Assert.Equal(1, decoded.PacketId);
            Assert.Equal(2, decoded.Subscriptions.Count);
            Assert.Equal("test/+", decoded.Subscriptions[0].TopicFilter);
            Assert.Equal(MqttQoS.AtLeastOnce, decoded.Subscriptions[0].QoS);
            Assert.Equal("data/#", decoded.Subscriptions[1].TopicFilter);
            Assert.Equal(MqttQoS.AtMostOnce, decoded.Subscriptions[1].QoS);
        }

        [Fact]
        public void EncodeDecode_Unsubscribe_RoundTrips()
        {
            var packet = new MqttUnsubscribePacket
            {
                PacketId = 10,
                TopicFilters = new System.Collections.Generic.List<string> { "a/b", "c/d" }
            };

            byte[] encoded = MqttPacket.EncodeUnsubscribe(packet);
            int offset = 1;
            int remainingLength = MqttPacket.DecodeRemainingLength(encoded, ref offset);
            var decoded = MqttPacket.DecodeUnsubscribe(encoded, offset, remainingLength);

            Assert.Equal(10, decoded.PacketId);
            Assert.Equal(2, decoded.TopicFilters.Count);
            Assert.Equal("a/b", decoded.TopicFilters[0]);
            Assert.Equal("c/d", decoded.TopicFilters[1]);
        }

        [Theory]
        [InlineData(0, new byte[] { 0x00 })]
        [InlineData(127, new byte[] { 0x7F })]
        [InlineData(128, new byte[] { 0x80, 0x01 })]
        [InlineData(16383, new byte[] { 0xFF, 0x7F })]
        [InlineData(16384, new byte[] { 0x80, 0x80, 0x01 })]
        [InlineData(2097151, new byte[] { 0xFF, 0xFF, 0x7F })]
        [InlineData(2097152, new byte[] { 0x80, 0x80, 0x80, 0x01 })]
        [InlineData(268435455, new byte[] { 0xFF, 0xFF, 0xFF, 0x7F })]
        public void RemainingLength_EncodeDecode_RoundTrips(int value, byte[] expected)
        {
            var buffer = new System.Collections.Generic.List<byte>();
            MqttPacket.EncodeRemainingLength(buffer, value);
            Assert.Equal(expected, buffer.ToArray());

            int offset = 0;
            int decoded = MqttPacket.DecodeRemainingLength(expected, ref offset);
            Assert.Equal(value, decoded);
            Assert.Equal(expected.Length, offset);
        }

        [Fact]
        public void Encode_PingReq_ReturnsExpectedBytes()
        {
            byte[] encoded = MqttPacket.EncodePingReq();
            Assert.Equal(2, encoded.Length);
            Assert.Equal((byte)((byte)MqttPacketType.PingReq << 4), encoded[0]);
            Assert.Equal(0, encoded[1]);
        }

        [Fact]
        public void Encode_PingResp_ReturnsExpectedBytes()
        {
            byte[] encoded = MqttPacket.EncodePingResp();
            Assert.Equal(2, encoded.Length);
            Assert.Equal((byte)((byte)MqttPacketType.PingResp << 4), encoded[0]);
            Assert.Equal(0, encoded[1]);
        }

        [Fact]
        public void Encode_Disconnect_ReturnsExpectedBytes()
        {
            byte[] encoded = MqttPacket.EncodeDisconnect();
            Assert.Equal(2, encoded.Length);
            Assert.Equal((byte)((byte)MqttPacketType.Disconnect << 4), encoded[0]);
            Assert.Equal(0, encoded[1]);
        }

        [Fact]
        public void EncodeDecode_PubAck_RoundTrips()
        {
            byte[] encoded = MqttPacket.EncodePubAck(12345);
            ushort packetId = (ushort)((encoded[2] << 8) | encoded[3]);
            Assert.Equal(12345, packetId);
        }

        [Fact]
        public void EncodeDecode_PubRel_RoundTrips()
        {
            byte[] encoded = MqttPacket.EncodePubRel(54321);
            Assert.Equal((byte)((byte)MqttPacketType.PubRel << 4 | 0x02), encoded[0]);
            ushort packetId = (ushort)((encoded[2] << 8) | encoded[3]);
            Assert.Equal(54321, packetId);
        }

        [Fact]
        public void Encode_SubAck()
        {
            var subAck = new MqttSubAckPacket
            {
                PacketId = 5,
                ReturnCodes = new System.Collections.Generic.List<byte> { 0, 1, 2 }
            };

            byte[] encoded = MqttPacket.EncodeSubAck(subAck);
            Assert.Equal((byte)((byte)MqttPacketType.SubAck << 4), encoded[0]);
            ushort packetId = (ushort)((encoded[2] << 8) | encoded[3]);
            Assert.Equal(5, packetId);
            Assert.Equal(0, encoded[4]);
            Assert.Equal(1, encoded[5]);
            Assert.Equal(2, encoded[6]);
        }
    }
}
