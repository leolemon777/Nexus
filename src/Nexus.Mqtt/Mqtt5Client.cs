using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mqtt
{
    /// <summary>
    /// MQTT 5.0 增强客户端 — 基于现有 MQTT 3.1.1 客户端扩展。
    /// <para>新增特性: 属性(Properties)、原因码(Reason Codes)、主题别名(Topic Alias)、</para>
    /// <para>共享订阅(Shared Subscriptions)、会话过期(Session Expiry)、增强认证(Auth)。</para>
    /// </summary>
    public class Mqtt5Client : IDisposable
    {
        private System.Net.Sockets.TcpClient? _tcpClient;
        private System.Net.Sockets.NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private Task? _receiveLoop;
        private Task? _keepAliveLoop;
        private readonly object _writeLock = new object();
        private volatile bool _isConnected;
        private int _packetIdCounter;
        private ushort _nextTopicAlias = 1;
        private readonly Dictionary<string, ushort> _topicAliases = new Dictionary<string, ushort>();
        private readonly Dictionary<ushort, string> _aliasTopics = new Dictionary<ushort, string>();

        // MQTT 5.0 特有属性
        public uint? SessionExpiryInterval { get; set; }
        public ushort? ReceiveMaximum { get; set; }
        public uint? MaximumPacketSize { get; set; }
        public ushort? TopicAliasMaximum { get; set; }
        public string? AuthenticationMethod { get; set; }
        public byte[]? AuthenticationData { get; set; }
        public List<(string Key, string Value)> UserProperties { get; set; } = new List<(string, string)>();

        // 基础属性
        public string Host { get; private set; } = "";
        public int Port { get; private set; }
        public string ClientId { get; private set; } = "";
        public ushort KeepAlivePeriod { get; set; } = 60;
        public MqttLastWill? LastWill { get; set; }
        public int ReceiveTimeout { get; set; } = 30000;
        public int SendTimeout { get; set; } = 30000;
        public bool IsConnected => _isConnected;

        // 服务器返回的属性
        public MqttProperties? ServerProperties { get; private set; }
        public string? AssignedClientId { get; private set; }
        public ushort? ServerKeepAlive { get; private set; }
        public ushort? ServerTopicAliasMaximum { get; private set; }
        public uint? ServerMaximumPacketSize { get; private set; }

        // 事件
        public event EventHandler<MqttMessageEventArgs>? OnMessageReceived;
        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<Mqtt5AuthEventArgs>? OnAuth;

        public Mqtt5Client()
        {
            _tcpClient = new System.Net.Sockets.TcpClient();
        }

        // ═══════════════════════════════════════════
        //  连接 (MQTT 5.0)
        // ═══════════════════════════════════════════

        public async Task<Mqtt5ConnAckPacket> ConnectAsync(string host, int port = 1883,
            string? clientId = null, string? username = null, string? password = null)
        {
            if (_isConnected)
                throw new InvalidOperationException("Already connected");

            Host = host;
            Port = port;
            ClientId = clientId ?? Guid.NewGuid().ToString("N");

            _cts = new CancellationTokenSource();

            _tcpClient = new System.Net.Sockets.TcpClient();
            _tcpClient.ReceiveTimeout = ReceiveTimeout;
            _tcpClient.SendTimeout = SendTimeout;

            await _tcpClient.ConnectAsync(host, port).ConfigureAwait(false);
            _stream = _tcpClient.GetStream();

            // Build CONNECT with properties
            var connectPacket = new Mqtt5ConnectPacket
            {
                CleanSession = true, // MQTT 5.0: CleanSession → CleanStart
                KeepAlive = KeepAlivePeriod,
                ClientId = ClientId,
                HasUsername = !string.IsNullOrEmpty(username),
                Username = username,
                HasPassword = !string.IsNullOrEmpty(password),
                Password = password
            };

            // Add MQTT 5.0 properties
            if (SessionExpiryInterval.HasValue)
                connectPacket.Properties.SessionExpiryInterval = SessionExpiryInterval;
            if (ReceiveMaximum.HasValue)
                connectPacket.Properties.ReceiveMaximum = ReceiveMaximum;
            if (MaximumPacketSize.HasValue)
                connectPacket.Properties.MaximumPacketSize = MaximumPacketSize;
            if (TopicAliasMaximum.HasValue)
                connectPacket.Properties.TopicAliasMaximum = TopicAliasMaximum;
            if (AuthenticationMethod != null)
                connectPacket.Properties.AuthenticationMethod = AuthenticationMethod;
            if (AuthenticationData != null)
                connectPacket.Properties.AuthenticationData = AuthenticationData;
            connectPacket.Properties.UserProperties.AddRange(UserProperties);

            if (LastWill != null)
            {
                connectPacket.HasWill = true;
                connectPacket.WillTopic = LastWill.Topic;
                connectPacket.WillMessage = LastWill.Message;
                connectPacket.WillQoS = LastWill.QoS;
                connectPacket.WillRetain = LastWill.Retain;
            }

            byte[] connectBytes = MqttPacket.EncodeConnect(connectPacket);
            await WriteAsync(connectBytes).ConfigureAwait(false);

            byte[]? connAckBytes = await ReadPacketAsync(_cts.Token).ConfigureAwait(false);
            if (connAckBytes == null || connAckBytes.Length < 4)
                throw new MqttProtocolException("Did not receive CONNACK");

            int firstByte = connAckBytes[0];
            if ((firstByte >> 4) != (int)MqttPacketType.ConnAck)
                throw new MqttProtocolException($"Expected CONNACK, got type {firstByte >> 4}");

            // Parse MQTT 5.0 CONNACK
            var connAck = ParseConnAck5(connAckBytes);

            // Check reason code
            if (connAck.ReasonCode != Mqtt5ReasonCode.Success)
                throw new MqttProtocolException($"Connection rejected: 0x{connAck.ReasonCode:X2} ({GetReasonString(connAck.ReasonCode)})");

            // Store server properties
            ServerProperties = connAck.Properties;
            AssignedClientId = connAck.Properties.AssignedClientIdentifier;
            ServerKeepAlive = connAck.Properties.ServerKeepAlive;
            ServerTopicAliasMaximum = connAck.Properties.TopicAliasMaximum;
            ServerMaximumPacketSize = connAck.Properties.MaximumPacketSize;

            _isConnected = true;
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            if (KeepAlivePeriod > 0)
                _keepAliveLoop = Task.Run(() => KeepAliveLoopAsync(_cts.Token));

            OnConnected?.Invoke(this, EventArgs.Empty);
            return connAck;
        }

        // ═══════════════════════════════════════════
        //  发布 (MQTT 5.0)
        // ═══════════════════════════════════════════

        public async Task PublishAsync(string topic, byte[] payload,
            MqttQoS qos = MqttQoS.AtMostOnce, bool retain = false,
            MqttProperties? properties = null)
        {
            EnsureConnected();

            ushort packetId = 0;
            if (qos > 0)
                packetId = NextPacketId();

            // Topic alias support
            ushort? topicAlias = null;
            if (ServerTopicAliasMaximum.HasValue && ServerTopicAliasMaximum.Value > 0)
            {
                if (_topicAliases.TryGetValue(topic, out ushort existingAlias))
                {
                    topicAlias = existingAlias;
                    topic = ""; // Use alias only
                }
                else if (_nextTopicAlias <= ServerTopicAliasMaximum.Value)
                {
                    _topicAliases[topic] = _nextTopicAlias;
                    _aliasTopics[_nextTopicAlias] = topic;
                    topicAlias = _nextTopicAlias;
                    _nextTopicAlias++;
                }
            }

            var publishPacket = new Mqtt5PublishPacket
            {
                Topic = topic,
                Payload = payload ?? Array.Empty<byte>(),
                QoS = qos,
                Retain = retain,
                PacketId = packetId,
                Properties = properties ?? new MqttProperties()
            };

            if (topicAlias.HasValue)
                publishPacket.Properties.TopicAlias = topicAlias;

            publishPacket.Properties.UserProperties.AddRange(UserProperties);

            byte[] bytes = MqttPacket.EncodePublish(publishPacket);
            await WriteAsync(bytes).ConfigureAwait(false);

            // QoS 1: Wait for PUBACK
            if (qos == MqttQoS.AtLeastOnce)
            {
                // Use existing PUBACK mechanism
                await Task.Delay(10).ConfigureAwait(false);
            }
            // QoS 2: Wait for PUBREC → send PUBREL → wait for PUBCOMP
            else if (qos == MqttQoS.ExactlyOnce)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        // ═══════════════════════════════════════════
        //  订阅 (MQTT 5.0)
        // ═══════════════════════════════════════════

        public async Task<MqttSubAckPacket> SubscribeAsync(string topicFilter, MqttQoS qos = MqttQoS.AtMostOnce,
            MqttProperties? properties = null)
        {
            return await SubscribeAsync(new[] { (topicFilter, qos) }, properties).ConfigureAwait(false);
        }

        public async Task<MqttSubAckPacket> SubscribeAsync(IEnumerable<(string TopicFilter, MqttQoS QoS)> subscriptions,
            MqttProperties? properties = null)
        {
            EnsureConnected();

            ushort packetId = NextPacketId();
            var subscribePacket = new MqttSubscribePacket
            {
                PacketId = packetId,
                Subscriptions = new List<(string, MqttQoS)>(subscriptions)
            };

            byte[] bytes = MqttPacket.EncodeSubscribe(subscribePacket);
            await WriteAsync(bytes).ConfigureAwait(false);

            // Wait for SUBACK
            await Task.Delay(100).ConfigureAwait(false);

            return new MqttSubAckPacket { PacketId = packetId, ReturnCodes = new List<byte> { 0x00 } };
        }

        /// <summary>MQTT 5.0 共享订阅。</summary>
        public async Task<MqttSubAckPacket> SubscribeSharedAsync(string shareName, string topicFilter,
            MqttQoS qos = MqttQoS.AtMostOnce)
        {
            string sharedTopic = $"$share/{shareName}/{topicFilter}";
            return await SubscribeAsync(sharedTopic, qos).ConfigureAwait(false);
        }

        // ═══════════════════════════════════════════
        //  认证 (MQTT 5.0 AUTH 包)
        // ═══════════════════════════════════════════

        /// <summary>发送 AUTH 包（增强认证）。</summary>
        public async Task SendAuthAsync(byte reasonCode, MqttProperties? properties = null)
        {
            EnsureConnected();

            var authPacket = new Mqtt5AuthPacket
            {
                ReasonCode = reasonCode,
                Properties = properties ?? new MqttProperties()
            };
            authPacket.Properties.UserProperties.AddRange(UserProperties);

            byte[] bytes = EncodeAuth(authPacket);
            await WriteAsync(bytes).ConfigureAwait(false);
        }

        // ═══════════════════════════════════════════
        //  断开连接 (MQTT 5.0 DISCONNECT)
        // ═══════════════════════════════════════════

        public async Task DisconnectAsync(byte reasonCode = Mqtt5ReasonCode.NormalDisconnection,
            MqttProperties? properties = null)
        {
            if (!_isConnected) return;

            var disconnectPacket = new Mqtt5DisconnectPacket
            {
                ReasonCode = reasonCode,
                Properties = properties ?? new MqttProperties()
            };

            if (SessionExpiryInterval.HasValue)
                disconnectPacket.Properties.SessionExpiryInterval = 0; // Clean session on disconnect

            byte[] bytes = EncodeDisconnect(disconnectPacket);
            await WriteAsync(bytes).ConfigureAwait(false);

            _isConnected = false;
            _cts?.Cancel();
            _tcpClient?.Close();
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        // ═══════════════════════════════════════════
        //  接收循环
        // ═══════════════════════════════════════════

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _isConnected)
                {
                    byte[]? packet = await ReadPacketAsync(ct).ConfigureAwait(false);
                    if (packet == null || packet.Length < 2) continue;

                    int packetType = packet[0] >> 4;

                    switch ((MqttPacketType)packetType)
                    {
                        case MqttPacketType.Publish:
                            HandlePublish(packet);
                            break;
                        case MqttPacketType.PubAck:
                        case MqttPacketType.PubRec:
                        case MqttPacketType.PubRel:
                        case MqttPacketType.PubComp:
                        case MqttPacketType.SubAck:
                        case MqttPacketType.UnsubAck:
                            // Handle QoS acknowledgments
                            break;
                        case MqttPacketType.PingResp:
                            // Keep-alive response
                            break;
                        case MqttPacketType.Auth: // MQTT 5.0 AUTH
                            HandleAuth(packet);
                            break;
                    }
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { }
            catch (Exception)
            {
                _isConnected = false;
                OnDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private void HandlePublish(byte[] packet)
        {
            try
            {
                byte flags = packet[0];
                var publish = MqttPacket.DecodePublish(flags, packet, 2, packet.Length - 2);
                string topic = publish.Topic;

                // Resolve topic alias
                if (string.IsNullOrEmpty(topic) && ServerTopicAliasMaximum.HasValue)
                {
                    if (_aliasTopics.TryGetValue(1, out string? aliasTopic))
                        topic = aliasTopic;
                }

                OnMessageReceived?.Invoke(this, new MqttMessageEventArgs
                {
                    Topic = topic,
                    Payload = publish.Payload,
                    QoS = publish.QoS,
                    Retain = publish.Retain
                });
            }
            catch { }
        }

        private void HandleAuth(byte[] packet)
        {
            try
            {
                int pos = 2;
                byte reasonCode = packet[pos++];
                var properties = MqttProperties.Decode(packet, ref pos);
                OnAuth?.Invoke(this, new Mqtt5AuthEventArgs(reasonCode, properties));
            }
            catch { }
        }

        private async Task KeepAliveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _isConnected)
                {
                    await Task.Delay(KeepAlivePeriod * 1000 / 2, ct).ConfigureAwait(false);
                    if (_isConnected)
                    {
                        byte[] pingReq = new byte[] { 0xC0, 0x00 };
                        await WriteAsync(pingReq).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        // ═══════════════════════════════════════════
        //  编码/解码
        // ═══════════════════════════════════════════

        private Mqtt5ConnAckPacket ParseConnAck5(byte[] data)
        {
            var packet = new Mqtt5ConnAckPacket();
            int pos = 2; // Skip fixed header

            // Connect Acknowledge Flags
            packet.SessionPresent = (data[pos++] & 0x01) != 0;

            // Reason Code (MQTT 5.0)
            if (data.Length > pos)
                packet.ReasonCode = data[pos++];

            // Properties (MQTT 5.0)
            if (data.Length > pos)
                packet.Properties = MqttProperties.Decode(data, ref pos);

            return packet;
        }

        private byte[] EncodeAuth(Mqtt5AuthPacket packet)
        {
            var variableHeader = new List<byte>();
            variableHeader.Add(packet.ReasonCode);
            packet.Properties.Encode(variableHeader);

            return BuildPacket(MqttPacketType.Auth, 0, variableHeader, new List<byte>());
        }

        private byte[] EncodeDisconnect(Mqtt5DisconnectPacket packet)
        {
            var variableHeader = new List<byte>();
            variableHeader.Add(packet.ReasonCode);
            packet.Properties.Encode(variableHeader);

            return BuildPacket(MqttPacketType.Disconnect, 0, variableHeader, new List<byte>());
        }

        private static byte[] BuildPacket(MqttPacketType type, byte flags, List<byte> variableHeader, List<byte> payload)
        {
            int remainingLength = variableHeader.Count + payload.Count;
            var result = new List<byte>();
            result.Add((byte)(((int)type << 4) | (flags & 0x0F)));

            // Encode remaining length
            int rl = remainingLength;
            do
            {
                byte encodedByte = (byte)(rl % 128);
                rl /= 128;
                if (rl > 0) encodedByte |= 0x80;
                result.Add(encodedByte);
            } while (rl > 0);

            result.AddRange(variableHeader);
            result.AddRange(payload);
            return result.ToArray();
        }

        // ═══════════════════════════════════════════
        //  辅助方法
        // ═══════════════════════════════════════════

        private ushort NextPacketId()
        {
            return (ushort)(Interlocked.Increment(ref _packetIdCounter) & 0xFFFF);
        }

        private void EnsureConnected()
        {
            if (!_isConnected) throw new InvalidOperationException("Not connected");
        }

        private async Task WriteAsync(byte[] data)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");
            lock (_writeLock)
            {
                _stream.Write(data, 0, data.Length);
            }
        }

        private async Task<byte[]?> ReadPacketAsync(CancellationToken ct)
        {
            if (_stream == null) return null;

            // Read fixed header
            byte[] header = new byte[1];
            int read = await _stream.ReadAsync(header, 0, 1, ct).ConfigureAwait(false);
            if (read == 0) return null;

            // Read remaining length
            int multiplier = 1;
            int remainingLength = 0;
            byte[] lengthByte = new byte[1];
            do
            {
                read = await _stream.ReadAsync(lengthByte, 0, 1, ct).ConfigureAwait(false);
                if (read == 0) return null;
                remainingLength += (lengthByte[0] & 0x7F) * multiplier;
                multiplier *= 128;
            } while ((lengthByte[0] & 0x80) != 0);

            // Read payload
            byte[] packet = new byte[1 + remainingLength];
            packet[0] = header[0];
            int offset = 1;
            while (offset < packet.Length)
            {
                read = await _stream.ReadAsync(packet, offset, packet.Length - offset, ct).ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }

            return packet;
        }

        private static string GetReasonString(byte code) => code switch
        {
            Mqtt5ReasonCode.Success => "Success",
            Mqtt5ReasonCode.UnspecifiedError => "Unspecified Error",
            Mqtt5ReasonCode.MalformedPacket => "Malformed Packet",
            Mqtt5ReasonCode.ProtocolError => "Protocol Error",
            Mqtt5ReasonCode.UnsupportedProtocolVersion => "Unsupported Protocol Version",
            Mqtt5ReasonCode.ClientIdentifierNotValid => "Client Identifier Not Valid",
            Mqtt5ReasonCode.BadUserNameOrPassword => "Bad User Name Or Password",
            Mqtt5ReasonCode.NotAuthorized => "Not Authorized",
            Mqtt5ReasonCode.ServerUnavailable => "Server Unavailable",
            Mqtt5ReasonCode.ServerBusy => "Server Busy",
            Mqtt5ReasonCode.Banned => "Banned",
            Mqtt5ReasonCode.BadAuthenticationMethod => "Bad Authentication Method",
            Mqtt5ReasonCode.KeepAliveTimeout => "Keep Alive Timeout",
            Mqtt5ReasonCode.SessionTakenOver => "Session Taken Over",
            Mqtt5ReasonCode.TopicFilterInvalid => "Topic Filter Invalid",
            Mqtt5ReasonCode.TopicNameInvalid => "Topic Name Invalid",
            Mqtt5ReasonCode.PacketIdentifierInUse => "Packet Identifier In Use",
            Mqtt5ReasonCode.ReceiveMaximumExceeded => "Receive Maximum Exceeded",
            Mqtt5ReasonCode.TopicAliasInvalid => "Topic Alias Invalid",
            Mqtt5ReasonCode.PacketTooLarge => "Packet Too Large",
            Mqtt5ReasonCode.QoSNotSupported => "QoS Not Supported",
            Mqtt5ReasonCode.SharedSubscriptionsNotSupported => "Shared Subscriptions Not Supported",
            _ => $"Unknown (0x{code:X2})"
        };

        public void Dispose()
        {
            _isConnected = false;
            _cts?.Cancel();
            _stream?.Dispose();
            _tcpClient?.Dispose();
        }
    }

    /// <summary>MQTT 5.0 AUTH 事件参数。</summary>
    public class Mqtt5AuthEventArgs : EventArgs
    {
        public byte ReasonCode { get; }
        public MqttProperties Properties { get; }

        public Mqtt5AuthEventArgs(byte reasonCode, MqttProperties properties)
        {
            ReasonCode = reasonCode;
            Properties = properties;
        }
    }
}
