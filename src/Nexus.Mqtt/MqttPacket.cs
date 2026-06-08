using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Mqtt
{
    public enum MqttPacketType : byte
    {
        Connect = 1,
        ConnAck = 2,
        Publish = 3,
        PubAck = 4,
        PubRec = 5,
        PubRel = 6,
        PubComp = 7,
        Subscribe = 8,
        SubAck = 9,
        Unsubscribe = 10,
        UnsubAck = 11,
        PingReq = 12,
        PingResp = 13,
        Disconnect = 14
    }

    public enum MqttQoS : byte
    {
        AtMostOnce = 0,
        AtLeastOnce = 1,
        ExactlyOnce = 2
    }

    public enum MqttConnectReturnCode : byte
    {
        Accepted = 0,
        UnacceptableProtocol = 1,
        IdentifierRejected = 2,
        ServerUnavailable = 3,
        BadCredentials = 4,
        NotAuthorized = 5
    }

    public class MqttConnectPacket
    {
        public string ProtocolName { get; set; } = "MQTT";
        public byte ProtocolLevel { get; set; } = 4;
        public bool CleanSession { get; set; } = true;
        public bool HasWill { get; set; }
        public MqttQoS WillQoS { get; set; }
        public bool WillRetain { get; set; }
        public bool HasPassword { get; set; }
        public bool HasUsername { get; set; }
        public ushort KeepAlive { get; set; }
        public string ClientId { get; set; } = "";
        public string WillTopic { get; set; }
        public byte[] WillMessage { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class MqttConnAckPacket
    {
        public bool SessionPresent { get; set; }
        public MqttConnectReturnCode ReturnCode { get; set; }
    }

    public class MqttPublishPacket
    {
        public bool Dup { get; set; }
        public MqttQoS QoS { get; set; }
        public bool Retain { get; set; }
        public string Topic { get; set; } = "";
        public ushort PacketId { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    public class MqttPubAckPacket
    {
        public ushort PacketId { get; set; }
    }

    public class MqttPubRecPacket
    {
        public ushort PacketId { get; set; }
    }

    public class MqttPubRelPacket
    {
        public ushort PacketId { get; set; }
    }

    public class MqttPubCompPacket
    {
        public ushort PacketId { get; set; }
    }

    public class MqttSubscribePacket
    {
        public ushort PacketId { get; set; }
        public List<(string TopicFilter, MqttQoS QoS)> Subscriptions { get; set; } = new List<(string, MqttQoS)>();
    }

    public class MqttSubAckPacket
    {
        public ushort PacketId { get; set; }
        public List<byte> ReturnCodes { get; set; } = new List<byte>();
    }

    public class MqttUnsubscribePacket
    {
        public ushort PacketId { get; set; }
        public List<string> TopicFilters { get; set; } = new List<string>();
    }

    public class MqttUnsubAckPacket
    {
        public ushort PacketId { get; set; }
    }

    public static class MqttPacket
    {
        private static readonly Encoding Utf8 = Encoding.UTF8;

        public static byte[] EncodeConnect(MqttConnectPacket packet)
        {
            var variableHeader = new List<byte>();
            EncodeString(variableHeader, packet.ProtocolName);
            variableHeader.Add(packet.ProtocolLevel);

            byte flags = 0;
            if (packet.CleanSession) flags |= 0x02;
            if (packet.HasWill)
            {
                flags |= 0x04;
                flags |= (byte)((byte)packet.WillQoS << 3);
                if (packet.WillRetain) flags |= 0x20;
            }
            if (packet.HasPassword) flags |= 0x40;
            if (packet.HasUsername) flags |= 0x80;
            variableHeader.Add(flags);

            variableHeader.Add((byte)(packet.KeepAlive >> 8));
            variableHeader.Add((byte)packet.KeepAlive);

            var payload = new List<byte>();
            EncodeString(payload, packet.ClientId);
            if (packet.HasWill)
            {
                EncodeString(payload, packet.WillTopic ?? "");
                EncodeBinary(payload, packet.WillMessage ?? Array.Empty<byte>());
            }
            if (packet.HasUsername)
                EncodeString(payload, packet.Username ?? "");
            if (packet.HasPassword)
                EncodeString(payload, packet.Password ?? "");

            return BuildPacket(MqttPacketType.Connect, 0, variableHeader, payload);
        }

        public static MqttConnectPacket DecodeConnect(byte[] data, int offset, int length)
        {
            int pos = offset;
            var packet = new MqttConnectPacket();

            packet.ProtocolName = DecodeString(data, ref pos);
            packet.ProtocolLevel = data[pos++];

            byte flags = data[pos++];
            packet.CleanSession = (flags & 0x02) != 0;
            packet.HasWill = (flags & 0x04) != 0;
            packet.WillQoS = (MqttQoS)((flags >> 3) & 0x03);
            packet.WillRetain = (flags & 0x20) != 0;
            packet.HasPassword = (flags & 0x40) != 0;
            packet.HasUsername = (flags & 0x80) != 0;

            packet.KeepAlive = (ushort)((data[pos] << 8) | data[pos + 1]);
            pos += 2;

            packet.ClientId = DecodeString(data, ref pos);
            if (packet.HasWill)
            {
                packet.WillTopic = DecodeString(data, ref pos);
                packet.WillMessage = DecodeBinary(data, ref pos);
            }
            if (packet.HasUsername)
                packet.Username = DecodeString(data, ref pos);
            if (packet.HasPassword)
                packet.Password = DecodeString(data, ref pos);

            return packet;
        }

        public static byte[] EncodeConnAck(MqttConnAckPacket packet)
        {
            return new byte[]
            {
                (byte)((byte)MqttPacketType.ConnAck << 4),
                2,
                (byte)(packet.SessionPresent ? 1 : 0),
                (byte)packet.ReturnCode
            };
        }

        public static MqttConnAckPacket DecodeConnAck(byte[] data, int offset)
        {
            return new MqttConnAckPacket
            {
                SessionPresent = (data[offset] & 0x01) != 0,
                ReturnCode = (MqttConnectReturnCode)data[offset + 1]
            };
        }

        public static byte[] EncodePublish(MqttPublishPacket packet)
        {
            byte flags = (byte)((byte)MqttPacketType.Publish << 4);
            if (packet.Dup) flags |= 0x08;
            flags |= (byte)((byte)packet.QoS << 1);
            if (packet.Retain) flags |= 0x01;

            var variableHeader = new List<byte>();
            EncodeString(variableHeader, packet.Topic);
            if (packet.QoS > 0)
            {
                variableHeader.Add((byte)(packet.PacketId >> 8));
                variableHeader.Add((byte)packet.PacketId);
            }

            return BuildPacket(flags, variableHeader, new List<byte>(packet.Payload));
        }

        public static MqttPublishPacket DecodePublish(byte flags, byte[] data, int offset, int length)
        {
            var packet = new MqttPublishPacket();
            packet.Dup = (flags & 0x08) != 0;
            packet.QoS = (MqttQoS)((flags >> 1) & 0x03);
            packet.Retain = (flags & 0x01) != 0;

            int pos = offset;
            packet.Topic = DecodeString(data, ref pos);

            if (packet.QoS > 0)
            {
                packet.PacketId = (ushort)((data[pos] << 8) | data[pos + 1]);
                pos += 2;
            }

            int payloadLen = (offset + length) - pos;
            if (payloadLen > 0)
            {
                packet.Payload = new byte[payloadLen];
                Buffer.BlockCopy(data, pos, packet.Payload, 0, payloadLen);
            }

            return packet;
        }

        public static byte[] EncodePubAck(ushort packetId)
        {
            return new byte[]
            {
                (byte)((byte)MqttPacketType.PubAck << 4),
                2,
                (byte)(packetId >> 8),
                (byte)packetId
            };
        }

        public static byte[] EncodePubRec(ushort packetId)
        {
            return new byte[]
            {
                (byte)((byte)MqttPacketType.PubRec << 4),
                2,
                (byte)(packetId >> 8),
                (byte)packetId
            };
        }

        public static byte[] EncodePubRel(ushort packetId)
        {
            return new byte[]
            {
                (byte)((byte)MqttPacketType.PubRel << 4 | 0x02),
                2,
                (byte)(packetId >> 8),
                (byte)packetId
            };
        }

        public static byte[] EncodePubComp(ushort packetId)
        {
            return new byte[]
            {
                (byte)((byte)MqttPacketType.PubComp << 4),
                2,
                (byte)(packetId >> 8),
                (byte)packetId
            };
        }

        public static byte[] EncodeSubscribe(MqttSubscribePacket packet)
        {
            var variableHeader = new List<byte>();
            variableHeader.Add((byte)(packet.PacketId >> 8));
            variableHeader.Add((byte)packet.PacketId);

            var payload = new List<byte>();
            foreach (var (topicFilter, qos) in packet.Subscriptions)
            {
                EncodeString(payload, topicFilter);
                payload.Add((byte)qos);
            }

            return BuildPacket((byte)((byte)MqttPacketType.Subscribe << 4 | 0x02), variableHeader, payload);
        }

        public static MqttSubscribePacket DecodeSubscribe(byte[] data, int offset, int length)
        {
            var packet = new MqttSubscribePacket();
            int pos = offset;

            packet.PacketId = (ushort)((data[pos] << 8) | data[pos + 1]);
            pos += 2;

            int end = offset + length;
            while (pos < end)
            {
                string topicFilter = DecodeString(data, ref pos);
                MqttQoS qos = (MqttQoS)data[pos++];
                packet.Subscriptions.Add((topicFilter, qos));
            }

            return packet;
        }

        public static byte[] EncodeSubAck(MqttSubAckPacket packet)
        {
            var variableHeader = new List<byte>();
            variableHeader.Add((byte)(packet.PacketId >> 8));
            variableHeader.Add((byte)packet.PacketId);

            var payload = new List<byte>(packet.ReturnCodes);

            return BuildPacket(MqttPacketType.SubAck, variableHeader, payload);
        }

        public static byte[] EncodeUnsubscribe(MqttUnsubscribePacket packet)
        {
            var variableHeader = new List<byte>();
            variableHeader.Add((byte)(packet.PacketId >> 8));
            variableHeader.Add((byte)packet.PacketId);

            var payload = new List<byte>();
            foreach (string topicFilter in packet.TopicFilters)
                EncodeString(payload, topicFilter);

            return BuildPacket((byte)((byte)MqttPacketType.Unsubscribe << 4 | 0x02), variableHeader, payload);
        }

        public static MqttUnsubscribePacket DecodeUnsubscribe(byte[] data, int offset, int length)
        {
            var packet = new MqttUnsubscribePacket();
            int pos = offset;

            packet.PacketId = (ushort)((data[pos] << 8) | data[pos + 1]);
            pos += 2;

            int end = offset + length;
            while (pos < end)
                packet.TopicFilters.Add(DecodeString(data, ref pos));

            return packet;
        }

        public static byte[] EncodeUnsubAck(ushort packetId)
        {
            return new byte[]
            {
                (byte)((byte)MqttPacketType.UnsubAck << 4),
                2,
                (byte)(packetId >> 8),
                (byte)packetId
            };
        }

        public static byte[] EncodePingReq()
        {
            return new byte[] { (byte)((byte)MqttPacketType.PingReq << 4), 0 };
        }

        public static byte[] EncodePingResp()
        {
            return new byte[] { (byte)((byte)MqttPacketType.PingResp << 4), 0 };
        }

        public static byte[] EncodeDisconnect()
        {
            return new byte[] { (byte)((byte)MqttPacketType.Disconnect << 4), 0 };
        }

        public static void EncodeRemainingLength(List<byte> buffer, int length)
        {
            do
            {
                byte encodedByte = (byte)(length % 128);
                length /= 128;
                if (length > 0)
                    encodedByte |= 0x80;
                buffer.Add(encodedByte);
            } while (length > 0);
        }

        public static int DecodeRemainingLength(byte[] buffer, ref int offset)
        {
            int multiplier = 1;
            int value = 0;
            byte encodedByte;
            do
            {
                if (offset >= buffer.Length)
                    throw new MqttProtocolException("Unexpected end of data while decoding remaining length");
                encodedByte = buffer[offset++];
                value += (encodedByte & 0x7F) * multiplier;
                if (multiplier > 128 * 128 * 128)
                    throw new MqttProtocolException("Malformed remaining length");
                multiplier *= 128;
            } while ((encodedByte & 0x80) != 0);
            return value;
        }

        internal static void EncodeString(List<byte> buffer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value ?? "");
            buffer.Add((byte)(bytes.Length >> 8));
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        internal static string DecodeString(byte[] data, ref int offset)
        {
            int length = (data[offset] << 8) | data[offset + 1];
            offset += 2;
            string value = Utf8.GetString(data, offset, length);
            offset += length;
            return value;
        }

        internal static void EncodeBinary(List<byte> buffer, byte[] value)
        {
            buffer.Add((byte)(value.Length >> 8));
            buffer.Add((byte)value.Length);
            buffer.AddRange(value);
        }

        internal static byte[] DecodeBinary(byte[] data, ref int offset)
        {
            int length = (data[offset] << 8) | data[offset + 1];
            offset += 2;
            byte[] value = new byte[length];
            Buffer.BlockCopy(data, offset, value, 0, length);
            offset += length;
            return value;
        }

        private static byte[] BuildPacket(MqttPacketType type, int flags, List<byte> variableHeader, List<byte> payload)
        {
            return BuildPacket((byte)(((byte)type << 4) | (flags & 0x0F)), variableHeader, payload);
        }

        private static byte[] BuildPacket(byte fixedHeaderFirstByte, List<byte> variableHeader, List<byte> payload)
        {
            int remainingLength = variableHeader.Count + payload.Count;
            var buffer = new List<byte>();
            buffer.Add(fixedHeaderFirstByte);
            EncodeRemainingLength(buffer, remainingLength);
            buffer.AddRange(variableHeader);
            buffer.AddRange(payload);
            return buffer.ToArray();
        }

        private static byte[] BuildPacket(MqttPacketType type, List<byte> variableHeader, List<byte> payload)
        {
            return BuildPacket(type, 0, variableHeader, payload);
        }
    }

    public class MqttProtocolException : Exception
    {
        public MqttProtocolException(string message) : base(message) { }
        public MqttProtocolException(string message, Exception inner) : base(message, inner) { }
    }

    public class MqttMessageEventArgs : EventArgs
    {
        public string Topic { get; set; } = "";
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public MqttQoS QoS { get; set; }
        public bool Retain { get; set; }
        public string PayloadString => Encoding.UTF8.GetString(Payload ?? Array.Empty<byte>());
    }

    public class MqttLastWill
    {
        public string Topic { get; set; } = "";
        public byte[] Message { get; set; } = Array.Empty<byte>();
        public MqttQoS QoS { get; set; }
        public bool Retain { get; set; }
    }
}
