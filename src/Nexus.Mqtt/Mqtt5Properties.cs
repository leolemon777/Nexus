using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Mqtt
{
    /// <summary>MQTT 5.0 属性标识符。</summary>
    public enum MqttPropertyId : byte
    {
        PayloadFormatIndicator = 0x01,
        MessageExpiryInterval = 0x02,
        ContentType = 0x03,
        ResponseTopic = 0x08,
        CorrelationData = 0x09,
        SubscriptionIdentifier = 0x0B,
        SessionExpiryInterval = 0x11,
        AssignedClientIdentifier = 0x12,
        ServerKeepAlive = 0x13,
        AuthenticationMethod = 0x15,
        AuthenticationData = 0x16,
        RequestProblemInformation = 0x17,
        WillDelayInterval = 0x18,
        RequestResponseInformation = 0x19,
        ResponseInformation = 0x1A,
        ServerReference = 0x1C,
        ReasonString = 0x1F,
        ReceiveMaximum = 0x21,
        TopicAliasMaximum = 0x22,
        TopicAlias = 0x23,
        MaximumQoS = 0x24,
        RetainAvailable = 0x25,
        UserProperty = 0x26,
        MaximumPacketSize = 0x27,
        WildcardSubscriptionAvailable = 0x28,
        SubscriptionIdentifierAvailable = 0x29,
        SharedSubscriptionAvailable = 0x2A,
    }

    /// <summary>MQTT 5.0 属性集合。</summary>
    public class MqttProperties
    {
        public byte? PayloadFormatIndicator { get; set; }
        public uint? MessageExpiryInterval { get; set; }
        public string? ContentType { get; set; }
        public string? ResponseTopic { get; set; }
        public byte[]? CorrelationData { get; set; }
        public uint? SubscriptionIdentifier { get; set; }
        public uint? SessionExpiryInterval { get; set; }
        public string? AssignedClientIdentifier { get; set; }
        public ushort? ServerKeepAlive { get; set; }
        public string? AuthenticationMethod { get; set; }
        public byte[]? AuthenticationData { get; set; }
        public byte? RequestProblemInformation { get; set; }
        public uint? WillDelayInterval { get; set; }
        public byte? RequestResponseInformation { get; set; }
        public string? ResponseInformation { get; set; }
        public string? ServerReference { get; set; }
        public string? ReasonString { get; set; }
        public ushort? ReceiveMaximum { get; set; }
        public ushort? TopicAliasMaximum { get; set; }
        public ushort? TopicAlias { get; set; }
        public byte? MaximumQoS { get; set; }
        public byte? RetainAvailable { get; set; }
        public uint? MaximumPacketSize { get; set; }
        public byte? WildcardSubscriptionAvailable { get; set; }
        public byte? SubscriptionIdentifierAvailable { get; set; }
        public byte? SharedSubscriptionAvailable { get; set; }
        public List<(string Key, string Value)> UserProperties { get; set; } = new List<(string, string)>();

        /// <summary>编码属性到字节列表。</summary>
        public void Encode(List<byte> buffer)
        {
            var propBytes = new List<byte>();

            if (PayloadFormatIndicator.HasValue) { propBytes.Add((byte)MqttPropertyId.PayloadFormatIndicator); propBytes.Add(PayloadFormatIndicator.Value); }
            if (MessageExpiryInterval.HasValue) { propBytes.Add((byte)MqttPropertyId.MessageExpiryInterval); EncodeUint(propBytes, MessageExpiryInterval.Value); }
            if (ContentType != null) { propBytes.Add((byte)MqttPropertyId.ContentType); EncodeString(propBytes, ContentType); }
            if (ResponseTopic != null) { propBytes.Add((byte)MqttPropertyId.ResponseTopic); EncodeString(propBytes, ResponseTopic); }
            if (CorrelationData != null) { propBytes.Add((byte)MqttPropertyId.CorrelationData); EncodeBinary(propBytes, CorrelationData); }
            if (SubscriptionIdentifier.HasValue) { propBytes.Add((byte)MqttPropertyId.SubscriptionIdentifier); EncodeVariableInt(propBytes, SubscriptionIdentifier.Value); }
            if (SessionExpiryInterval.HasValue) { propBytes.Add((byte)MqttPropertyId.SessionExpiryInterval); EncodeUint(propBytes, SessionExpiryInterval.Value); }
            if (AssignedClientIdentifier != null) { propBytes.Add((byte)MqttPropertyId.AssignedClientIdentifier); EncodeString(propBytes, AssignedClientIdentifier); }
            if (ServerKeepAlive.HasValue) { propBytes.Add((byte)MqttPropertyId.ServerKeepAlive); propBytes.Add((byte)(ServerKeepAlive.Value >> 8)); propBytes.Add((byte)ServerKeepAlive.Value); }
            if (AuthenticationMethod != null) { propBytes.Add((byte)MqttPropertyId.AuthenticationMethod); EncodeString(propBytes, AuthenticationMethod); }
            if (AuthenticationData != null) { propBytes.Add((byte)MqttPropertyId.AuthenticationData); EncodeBinary(propBytes, AuthenticationData); }
            if (RequestProblemInformation.HasValue) { propBytes.Add((byte)MqttPropertyId.RequestProblemInformation); propBytes.Add(RequestProblemInformation.Value); }
            if (WillDelayInterval.HasValue) { propBytes.Add((byte)MqttPropertyId.WillDelayInterval); EncodeUint(propBytes, WillDelayInterval.Value); }
            if (RequestResponseInformation.HasValue) { propBytes.Add((byte)MqttPropertyId.RequestResponseInformation); propBytes.Add(RequestResponseInformation.Value); }
            if (ResponseInformation != null) { propBytes.Add((byte)MqttPropertyId.ResponseInformation); EncodeString(propBytes, ResponseInformation); }
            if (ServerReference != null) { propBytes.Add((byte)MqttPropertyId.ServerReference); EncodeString(propBytes, ServerReference); }
            if (ReasonString != null) { propBytes.Add((byte)MqttPropertyId.ReasonString); EncodeString(propBytes, ReasonString); }
            if (ReceiveMaximum.HasValue) { propBytes.Add((byte)MqttPropertyId.ReceiveMaximum); propBytes.Add((byte)(ReceiveMaximum.Value >> 8)); propBytes.Add((byte)ReceiveMaximum.Value); }
            if (TopicAliasMaximum.HasValue) { propBytes.Add((byte)MqttPropertyId.TopicAliasMaximum); propBytes.Add((byte)(TopicAliasMaximum.Value >> 8)); propBytes.Add((byte)TopicAliasMaximum.Value); }
            if (TopicAlias.HasValue) { propBytes.Add((byte)MqttPropertyId.TopicAlias); propBytes.Add((byte)(TopicAlias.Value >> 8)); propBytes.Add((byte)TopicAlias.Value); }
            if (MaximumQoS.HasValue) { propBytes.Add((byte)MqttPropertyId.MaximumQoS); propBytes.Add(MaximumQoS.Value); }
            if (RetainAvailable.HasValue) { propBytes.Add((byte)MqttPropertyId.RetainAvailable); propBytes.Add(RetainAvailable.Value); }
            if (MaximumPacketSize.HasValue) { propBytes.Add((byte)MqttPropertyId.MaximumPacketSize); EncodeUint(propBytes, MaximumPacketSize.Value); }
            if (WildcardSubscriptionAvailable.HasValue) { propBytes.Add((byte)MqttPropertyId.WildcardSubscriptionAvailable); propBytes.Add(WildcardSubscriptionAvailable.Value); }
            if (SubscriptionIdentifierAvailable.HasValue) { propBytes.Add((byte)MqttPropertyId.SubscriptionIdentifierAvailable); propBytes.Add(SubscriptionIdentifierAvailable.Value); }
            if (SharedSubscriptionAvailable.HasValue) { propBytes.Add((byte)MqttPropertyId.SharedSubscriptionAvailable); propBytes.Add(SharedSubscriptionAvailable.Value); }

            foreach (var (key, value) in UserProperties)
            {
                propBytes.Add((byte)MqttPropertyId.UserProperty);
                EncodeString(propBytes, key);
                EncodeString(propBytes, value);
            }

            // 属性长度（变长整数编码）
            EncodeVariableInt(buffer, (uint)propBytes.Count);
            buffer.AddRange(propBytes);
        }

        /// <summary>从字节数组解码属性。</summary>
        public static MqttProperties Decode(byte[] data, ref int pos)
        {
            var props = new MqttProperties();
            uint propLength = DecodeVariableInt(data, ref pos);
            int endPos = pos + (int)propLength;

            while (pos < endPos)
            {
                byte id = data[pos++];
                switch ((MqttPropertyId)id)
                {
                    case MqttPropertyId.PayloadFormatIndicator: props.PayloadFormatIndicator = data[pos++]; break;
                    case MqttPropertyId.MessageExpiryInterval: props.MessageExpiryInterval = DecodeUint(data, ref pos); break;
                    case MqttPropertyId.ContentType: props.ContentType = DecodeString(data, ref pos); break;
                    case MqttPropertyId.ResponseTopic: props.ResponseTopic = DecodeString(data, ref pos); break;
                    case MqttPropertyId.CorrelationData: props.CorrelationData = DecodeBinary(data, ref pos); break;
                    case MqttPropertyId.SubscriptionIdentifier: props.SubscriptionIdentifier = DecodeVariableInt(data, ref pos); break;
                    case MqttPropertyId.SessionExpiryInterval: props.SessionExpiryInterval = DecodeUint(data, ref pos); break;
                    case MqttPropertyId.AssignedClientIdentifier: props.AssignedClientIdentifier = DecodeString(data, ref pos); break;
                    case MqttPropertyId.ServerKeepAlive: props.ServerKeepAlive = (ushort)((data[pos] << 8) | data[pos + 1]); pos += 2; break;
                    case MqttPropertyId.AuthenticationMethod: props.AuthenticationMethod = DecodeString(data, ref pos); break;
                    case MqttPropertyId.AuthenticationData: props.AuthenticationData = DecodeBinary(data, ref pos); break;
                    case MqttPropertyId.RequestProblemInformation: props.RequestProblemInformation = data[pos++]; break;
                    case MqttPropertyId.WillDelayInterval: props.WillDelayInterval = DecodeUint(data, ref pos); break;
                    case MqttPropertyId.RequestResponseInformation: props.RequestResponseInformation = data[pos++]; break;
                    case MqttPropertyId.ResponseInformation: props.ResponseInformation = DecodeString(data, ref pos); break;
                    case MqttPropertyId.ServerReference: props.ServerReference = DecodeString(data, ref pos); break;
                    case MqttPropertyId.ReasonString: props.ReasonString = DecodeString(data, ref pos); break;
                    case MqttPropertyId.ReceiveMaximum: props.ReceiveMaximum = (ushort)((data[pos] << 8) | data[pos + 1]); pos += 2; break;
                    case MqttPropertyId.TopicAliasMaximum: props.TopicAliasMaximum = (ushort)((data[pos] << 8) | data[pos + 1]); pos += 2; break;
                    case MqttPropertyId.TopicAlias: props.TopicAlias = (ushort)((data[pos] << 8) | data[pos + 1]); pos += 2; break;
                    case MqttPropertyId.MaximumQoS: props.MaximumQoS = data[pos++]; break;
                    case MqttPropertyId.RetainAvailable: props.RetainAvailable = data[pos++]; break;
                    case MqttPropertyId.MaximumPacketSize: props.MaximumPacketSize = DecodeUint(data, ref pos); break;
                    case MqttPropertyId.WildcardSubscriptionAvailable: props.WildcardSubscriptionAvailable = data[pos++]; break;
                    case MqttPropertyId.SubscriptionIdentifierAvailable: props.SubscriptionIdentifierAvailable = data[pos++]; break;
                    case MqttPropertyId.SharedSubscriptionAvailable: props.SharedSubscriptionAvailable = data[pos++]; break;
                    case MqttPropertyId.UserProperty:
                        string key = DecodeString(data, ref pos);
                        string value = DecodeString(data, ref pos);
                        props.UserProperties.Add((key, value));
                        break;
                    default: break; // 未知属性跳过
                }
            }

            return props;
        }

        private static void EncodeString(List<byte> buffer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            buffer.Add((byte)(bytes.Length >> 8));
            buffer.Add((byte)(bytes.Length & 0xFF));
            buffer.AddRange(bytes);
        }

        private static string DecodeString(byte[] data, ref int pos)
        {
            int len = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            string result = Encoding.UTF8.GetString(data, pos, len);
            pos += len;
            return result;
        }

        private static void EncodeBinary(List<byte> buffer, byte[] value)
        {
            buffer.Add((byte)(value.Length >> 8));
            buffer.Add((byte)(value.Length & 0xFF));
            buffer.AddRange(value);
        }

        private static byte[] DecodeBinary(byte[] data, ref int pos)
        {
            int len = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            byte[] result = new byte[len];
            Buffer.BlockCopy(data, pos, result, 0, len);
            pos += len;
            return result;
        }

        private static void EncodeUint(List<byte> buffer, uint value)
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static uint DecodeUint(byte[] data, ref int pos)
        {
            uint result = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
            pos += 4;
            return result;
        }

        private static void EncodeVariableInt(List<byte> buffer, uint value)
        {
            do
            {
                byte encodedByte = (byte)(value % 128);
                value /= 128;
                if (value > 0) encodedByte |= 0x80;
                buffer.Add(encodedByte);
            } while (value > 0);
        }

        private static uint DecodeVariableInt(byte[] data, ref int pos)
        {
            uint multiplier = 1;
            uint value = 0;
            byte encodedByte;
            do
            {
                encodedByte = data[pos++];
                value += (uint)((encodedByte & 0x7F) * multiplier);
                multiplier *= 128;
            } while ((encodedByte & 0x80) != 0);
            return value;
        }
    }

    /// <summary>MQTT 5.0 增强的 CONNECT 包。</summary>
    public class Mqtt5ConnectPacket : MqttConnectPacket
    {
        public new byte ProtocolLevel => 5; // MQTT 5.0
        public MqttProperties Properties { get; set; } = new MqttProperties();
    }

    /// <summary>MQTT 5.0 增强的 CONNACK 包。</summary>
    public class Mqtt5ConnAckPacket
    {
        public bool SessionPresent { get; set; }
        public byte ReasonCode { get; set; }
        public MqttProperties Properties { get; set; } = new MqttProperties();
    }

    /// <summary>MQTT 5.0 增强的 PUBLISH 包。</summary>
    public class Mqtt5PublishPacket : MqttPublishPacket
    {
        public MqttProperties Properties { get; set; } = new MqttProperties();
    }

    /// <summary>MQTT 5.0 增强的 SUBSCRIBE 包。</summary>
    public class Mqtt5SubscribePacket : MqttSubscribePacket
    {
        public MqttProperties Properties { get; set; } = new MqttProperties();
        public List<(string TopicFilter, MqttQoS QoS, MqttSubscriptionOptions Options)> Subscriptions5 { get; set; } = new List<(string, MqttQoS, MqttSubscriptionOptions)>();
    }

    /// <summary>MQTT 5.0 订阅选项。</summary>
    public class MqttSubscriptionOptions
    {
        public byte MaximumQoS { get; set; } = 2;
        public bool NoLocal { get; set; }
        public bool RetainAsPublished { get; set; }
        public byte RetainHandling { get; set; } // 0=send, 1=send if new sub, 2=don't send
    }

    /// <summary>MQTT 5.0 AUTH 包（增强认证）。</summary>
    public class Mqtt5AuthPacket
    {
        public byte ReasonCode { get; set; } // 0x00 = Success, 0x18 = Continue Authentication
        public MqttProperties Properties { get; set; } = new MqttProperties();
    }

    /// <summary>MQTT 5.0 DISCONNECT 包。</summary>
    public class Mqtt5DisconnectPacket
    {
        public byte ReasonCode { get; set; } // 0x00 = Normal disconnection
        public MqttProperties Properties { get; set; } = new MqttProperties();
    }

    /// <summary>MQTT 5.0 原因码。</summary>
    public static class Mqtt5ReasonCode
    {
        public const byte Success = 0x00;
        public const byte NormalDisconnection = 0x00;
        public const byte GrantedQoS0 = 0x00;
        public const byte GrantedQoS1 = 0x01;
        public const byte GrantedQoS2 = 0x02;
        public const byte DisconnectWithWill = 0x04;
        public const byte NoMatchingSubscribers = 0x10;
        public const byte NoSubscriptionExisted = 0x11;
        public const byte ContinueAuthentication = 0x18;
        public const byte ReAuthenticate = 0x19;
        public const byte UnspecifiedError = 0x80;
        public const byte MalformedPacket = 0x81;
        public const byte ProtocolError = 0x82;
        public const byte ImplementationSpecificError = 0x83;
        public const byte UnsupportedProtocolVersion = 0x84;
        public const byte ClientIdentifierNotValid = 0x85;
        public const byte BadUserNameOrPassword = 0x86;
        public const byte NotAuthorized = 0x87;
        public const byte ServerUnavailable = 0x88;
        public const byte ServerBusy = 0x89;
        public const byte Banned = 0x8A;
        public const byte ServerShuttingDown = 0x8B;
        public const byte BadAuthenticationMethod = 0x8C;
        public const byte KeepAliveTimeout = 0x8D;
        public const byte SessionTakenOver = 0x8E;
        public const byte TopicFilterInvalid = 0x8F;
        public const byte TopicNameInvalid = 0x90;
        public const byte PacketIdentifierInUse = 0x91;
        public const byte PacketIdentifierNotFound = 0x92;
        public const byte ReceiveMaximumExceeded = 0x93;
        public const byte TopicAliasInvalid = 0x94;
        public const byte PacketTooLarge = 0x95;
        public const byte MessageRateTooHigh = 0x96;
        public const byte QuotaExceeded = 0x97;
        public const byte AdministrativeAction = 0x98;
        public const byte PayloadFormatInvalid = 0x99;
        public const byte RetainNotSupported = 0x9A;
        public const byte QoSNotSupported = 0x9B;
        public const byte UseAnotherServer = 0x9C;
        public const byte ServerMoved = 0x9D;
        public const byte SharedSubscriptionsNotSupported = 0x9E;
        public const byte ConnectionRateExceeded = 0x9F;
        public const byte MaximumConnectTime = 0xA0;
        public const byte SubscriptionIdentifiersNotSupported = 0xA1;
        public const byte WildcardSubscriptionsNotSupported = 0xA2;
    }
}
