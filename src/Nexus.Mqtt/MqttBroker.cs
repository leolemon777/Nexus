using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mqtt
{
    public class MqttBroker : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptTask;
        private readonly ConcurrentDictionary<string, BrokerClient> _clients
            = new ConcurrentDictionary<string, BrokerClient>();
        private readonly ConcurrentDictionary<string, List<BrokerSubscription>> _subscriptions
            = new ConcurrentDictionary<string, List<BrokerSubscription>>();
        private readonly ConcurrentDictionary<string, RetainedMessage> _retainedMessages
            = new ConcurrentDictionary<string, RetainedMessage>();
        private readonly ReaderWriterLockSlim _subscriptionLock = new ReaderWriterLockSlim();
        private int _brokerPacketIdCounter;

        public int Port { get; private set; }
        public EndPoint ServerPort => _listener?.LocalEndpoint;
        public int ClientCount => _clients.Count;
        public bool IsRunning { get; private set; }

        public event EventHandler<string> OnClientConnected;
        public event EventHandler<string> OnClientDisconnected;
        public event EventHandler<(string ClientId, string Topic, byte[] Payload)> OnMessagePublished;

        public void Start(int port = 1883)
        {
            if (IsRunning)
                throw new InvalidOperationException("Broker is already running");

            Port = port;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            IsRunning = true;
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            _cts?.Cancel();
            _listener?.Stop();

            foreach (var client in _clients.Values)
            {
                try { client.TcpClient.Close(); } catch { }
            }
            _clients.Clear();

            _subscriptionLock.EnterWriteLock();
            try { _subscriptions.Clear(); } finally { _subscriptionLock.ExitWriteLock(); }

            _retainedMessages.Clear();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _subscriptionLock?.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(tcpClient, ct), ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (ct.IsCancellationRequested) { break; }
                catch { }
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
        {
            string clientId = null;
            var stream = tcpClient.GetStream();
            tcpClient.ReceiveTimeout = 120000;
            tcpClient.SendTimeout = 30000;

            BrokerClient brokerClient = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte[] packet = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
                    if (packet == null || packet.Length < 2)
                        break;

                    int firstByte = packet[0];
                    MqttPacketType packetType = (MqttPacketType)(firstByte >> 4);

                    int offset = 1;
                    int remainingLength = MqttPacket.DecodeRemainingLength(packet, ref offset);

                    switch (packetType)
                    {
                        case MqttPacketType.Connect:
                            var connectPacket = MqttPacket.DecodeConnect(packet, offset, remainingLength);
                            clientId = connectPacket.ClientId;

                            if (string.IsNullOrEmpty(clientId))
                                clientId = Guid.NewGuid().ToString("N");

                            brokerClient = new BrokerClient
                            {
                                ClientId = clientId,
                                TcpClient = tcpClient,
                                Stream = stream,
                                CleanSession = connectPacket.CleanSession,
                                KeepAlive = connectPacket.KeepAlive,
                                LastActivity = DateTime.UtcNow
                            };

                            if (connectPacket.HasWill)
                            {
                                brokerClient.HasWill = true;
                                brokerClient.WillTopic = connectPacket.WillTopic;
                                brokerClient.WillMessage = connectPacket.WillMessage;
                                brokerClient.WillQoS = connectPacket.WillQoS;
                                brokerClient.WillRetain = connectPacket.WillRetain;
                            }

                            if (!_clients.TryAdd(clientId, brokerClient))
                            {
                                if (_clients.TryRemove(clientId, out var existing))
                                {
                                    try { existing.TcpClient.Close(); } catch { }
                                    RemoveAllSubscriptions(clientId);
                                }
                                _clients[clientId] = brokerClient;
                            }

                            var connAck = new MqttConnAckPacket
                            {
                                SessionPresent = false,
                                ReturnCode = MqttConnectReturnCode.Accepted
                            };
                            byte[] connAckBytes = MqttPacket.EncodeConnAck(connAck);
                            await SafeWriteAsync(stream, connAckBytes).ConfigureAwait(false);

                            OnClientConnected?.Invoke(this, clientId);

                            if (connectPacket.KeepAlive > 0)
                                _ = Task.Run(() => KeepAliveCheckAsync(brokerClient, ct), ct);

                            break;

                        case MqttPacketType.Publish:
                            if (brokerClient == null) break;
                            brokerClient.LastActivity = DateTime.UtcNow;

                            byte pubFlags = (byte)(firstByte & 0x0F);
                            var pubPacket = MqttPacket.DecodePublish(pubFlags, packet, offset, remainingLength);

                            OnMessagePublished?.Invoke(this, (clientId, pubPacket.Topic, pubPacket.Payload));

                            if (pubPacket.Retain)
                            {
                                if (pubPacket.Payload.Length == 0)
                                    _retainedMessages.TryRemove(pubPacket.Topic, out _);
                                else
                                    _retainedMessages[pubPacket.Topic] = new RetainedMessage
                                    {
                                        Topic = pubPacket.Topic,
                                        Payload = pubPacket.Payload,
                                        QoS = pubPacket.QoS
                                    };
                            }

                            if (pubPacket.QoS == MqttQoS.AtLeastOnce)
                            {
                                byte[] pubAckBytes = MqttPacket.EncodePubAck(pubPacket.PacketId);
                                await SafeWriteAsync(stream, pubAckBytes).ConfigureAwait(false);
                            }
                            else if (pubPacket.QoS == MqttQoS.ExactlyOnce)
                            {
                                byte[] pubRecBytes = MqttPacket.EncodePubRec(pubPacket.PacketId);
                                await SafeWriteAsync(stream, pubRecBytes).ConfigureAwait(false);
                            }

                            DispatchMessage(clientId, pubPacket.Topic, pubPacket.Payload, pubPacket.QoS, pubPacket.Retain);
                            break;

                        case MqttPacketType.PubAck:
                            if (brokerClient != null)
                                brokerClient.LastActivity = DateTime.UtcNow;
                            break;

                        case MqttPacketType.PubRec:
                            if (brokerClient != null)
                            {
                                brokerClient.LastActivity = DateTime.UtcNow;
                                ushort pubRecPacketId = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                                byte[] pubRelBytes = MqttPacket.EncodePubRel(pubRecPacketId);
                                await SafeWriteAsync(stream, pubRelBytes).ConfigureAwait(false);
                            }
                            break;

                        case MqttPacketType.PubRel:
                            if (brokerClient != null)
                            {
                                brokerClient.LastActivity = DateTime.UtcNow;
                                ushort pubRelPacketId = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                                byte[] pubCompBytes = MqttPacket.EncodePubComp(pubRelPacketId);
                                await SafeWriteAsync(stream, pubCompBytes).ConfigureAwait(false);
                            }
                            break;

                        case MqttPacketType.PubComp:
                            if (brokerClient != null)
                                brokerClient.LastActivity = DateTime.UtcNow;
                            break;

                        case MqttPacketType.Subscribe:
                            if (brokerClient == null) break;
                            brokerClient.LastActivity = DateTime.UtcNow;

                            var subPacket = MqttPacket.DecodeSubscribe(packet, offset, remainingLength);
                            var subAck = new MqttSubAckPacket { PacketId = subPacket.PacketId };

                            foreach (var (topicFilter, qos) in subPacket.Subscriptions)
                            {
                                byte grantedQos = (byte)qos;
                                if (qos > MqttQoS.AtLeastOnce)
                                    grantedQos = (byte)MqttQoS.AtLeastOnce;

                                AddSubscription(clientId, topicFilter, (MqttQoS)grantedQos);
                                subAck.ReturnCodes.Add(grantedQos);
                            }

                            byte[] subAckBytes = MqttPacket.EncodeSubAck(subAck);
                            await SafeWriteAsync(stream, subAckBytes).ConfigureAwait(false);

                            foreach (var (topicFilter, qos) in subPacket.Subscriptions)
                            {
                                SendRetainedMessages(brokerClient, topicFilter, qos);
                            }
                            break;

                        case MqttPacketType.Unsubscribe:
                            if (brokerClient == null) break;
                            brokerClient.LastActivity = DateTime.UtcNow;

                            var unsubPacket = MqttPacket.DecodeUnsubscribe(packet, offset, remainingLength);
                            foreach (string topicFilter in unsubPacket.TopicFilters)
                                RemoveSubscription(clientId, topicFilter);

                            byte[] unsubAckBytes = MqttPacket.EncodeUnsubAck(unsubPacket.PacketId);
                            await SafeWriteAsync(stream, unsubAckBytes).ConfigureAwait(false);
                            break;

                        case MqttPacketType.PingReq:
                            if (brokerClient != null)
                                brokerClient.LastActivity = DateTime.UtcNow;

                            byte[] pingRespBytes = MqttPacket.EncodePingResp();
                            await SafeWriteAsync(stream, pingRespBytes).ConfigureAwait(false);
                            break;

                        case MqttPacketType.Disconnect:
                            if (brokerClient != null)
                                brokerClient.HasWill = false;
                            break;

                        default:
                            break;
                    }

                    if (packetType == MqttPacketType.Disconnect)
                        break;
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                if (clientId != null)
                {
                    _clients.TryRemove(clientId, out var removed);

                    if (removed != null && removed.HasWill)
                    {
                        DispatchMessage(clientId, removed.WillTopic, removed.WillMessage, removed.WillQoS, removed.WillRetain);
                    }

                    RemoveAllSubscriptions(clientId);
                    OnClientDisconnected?.Invoke(this, clientId);
                }

                try { tcpClient.Close(); } catch { }
            }
        }

        private void DispatchMessage(string senderClientId, string topic, byte[] payload, MqttQoS qos, bool retain)
        {
            var matchingSubs = new List<BrokerSubscription>();

            _subscriptionLock.EnterReadLock();
            try
            {
                foreach (var kvp in _subscriptions)
                {
                    string filter = kvp.Key;
                    foreach (var sub in kvp.Value)
                    {
                        if (sub.ClientId != senderClientId && MqttTopicFilter.IsMatch(topic, filter))
                        {
                            matchingSubs.Add(sub);
                        }
                    }
                }
            }
            finally
            {
                _subscriptionLock.ExitReadLock();
            }

            foreach (var sub in matchingSubs)
            {
                if (_clients.TryGetValue(sub.ClientId, out var client))
                {
                    try
                    {
                        MqttQoS effectiveQos = qos < sub.QoS ? qos : sub.QoS;
                        ushort packetId = 0;
                        if (effectiveQos > MqttQoS.AtMostOnce)
                            packetId = (ushort)(Interlocked.Increment(ref _brokerPacketIdCounter) & 0xFFFF);

                        var publishPacket = new MqttPublishPacket
                        {
                            Topic = topic,
                            Payload = payload,
                            QoS = effectiveQos,
                            Retain = false,
                            PacketId = packetId
                        };

                        byte[] bytes = MqttPacket.EncodePublish(publishPacket);
                        lock (client.WriteLock)
                        {
                            client.Stream.Write(bytes, 0, bytes.Length);
                        }
                    }
                    catch { }
                }
            }
        }

        private void SendRetainedMessages(BrokerClient client, string topicFilter, MqttQoS qos)
        {
            foreach (var kvp in _retainedMessages)
            {
                if (MqttTopicFilter.IsMatch(kvp.Key, topicFilter))
                {
                    try
                    {
                        MqttQoS effectiveQos = kvp.Value.QoS < qos ? kvp.Value.QoS : qos;
                        ushort packetId = 0;
                        if (effectiveQos > MqttQoS.AtMostOnce)
                            packetId = (ushort)(Interlocked.Increment(ref _brokerPacketIdCounter) & 0xFFFF);

                        var publishPacket = new MqttPublishPacket
                        {
                            Topic = kvp.Value.Topic,
                            Payload = kvp.Value.Payload,
                            QoS = effectiveQos,
                            Retain = true,
                            PacketId = packetId
                        };

                        byte[] bytes = MqttPacket.EncodePublish(publishPacket);
                        lock (client.WriteLock)
                        {
                            client.Stream.Write(bytes, 0, bytes.Length);
                        }
                    }
                    catch { }
                }
            }
        }

        private void AddSubscription(string clientId, string topicFilter, MqttQoS qos)
        {
            _subscriptionLock.EnterWriteLock();
            try
            {
                var subs = _subscriptions.GetOrAdd(topicFilter, _ => new List<BrokerSubscription>());
                lock (subs)
                {
                    for (int i = 0; i < subs.Count; i++)
                    {
                        if (subs[i].ClientId == clientId)
                        {
                            subs[i] = new BrokerSubscription(clientId, topicFilter, qos);
                            return;
                        }
                    }
                    subs.Add(new BrokerSubscription(clientId, topicFilter, qos));
                }
            }
            finally
            {
                _subscriptionLock.ExitWriteLock();
            }
        }

        private void RemoveSubscription(string clientId, string topicFilter)
        {
            _subscriptionLock.EnterWriteLock();
            try
            {
                if (_subscriptions.TryGetValue(topicFilter, out var subs))
                {
                    lock (subs)
                    {
                        for (int i = subs.Count - 1; i >= 0; i--)
                        {
                            if (subs[i].ClientId == clientId)
                            {
                                subs.RemoveAt(i);
                                break;
                            }
                        }
                        if (subs.Count == 0)
                            _subscriptions.TryRemove(topicFilter, out _);
                    }
                }
            }
            finally
            {
                _subscriptionLock.ExitWriteLock();
            }
        }

        private void RemoveAllSubscriptions(string clientId)
        {
            _subscriptionLock.EnterWriteLock();
            try
            {
                var emptyFilters = new List<string>();
                foreach (var kvp in _subscriptions)
                {
                    lock (kvp.Value)
                    {
                        kvp.Value.RemoveAll(s => s.ClientId == clientId);
                        if (kvp.Value.Count == 0)
                            emptyFilters.Add(kvp.Key);
                    }
                }
                foreach (var filter in emptyFilters)
                    _subscriptions.TryRemove(filter, out _);
            }
            finally
            {
                _subscriptionLock.ExitWriteLock();
            }
        }

        private async Task KeepAliveCheckAsync(BrokerClient client, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && IsRunning)
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);

                    if (client.KeepAlive == 0) continue;

                    double elapsed = (DateTime.UtcNow - client.LastActivity).TotalSeconds;
                    if (elapsed > client.KeepAlive * 1.5)
                    {
                        try { client.TcpClient.Close(); } catch { }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken ct)
        {
            byte[] headerBuf = new byte[1];
            int read = await ReadExactAsync(stream, headerBuf, 0, 1, ct).ConfigureAwait(false);
            if (read == 0) return null;

            int multiplier = 1;
            int remainingLength = 0;
            int rlByteCount = 0;
            byte[] rlBytes = new byte[4];
            while (true)
            {
                if (rlByteCount >= 4)
                    throw new MqttProtocolException("Malformed remaining length");

                byte[] rlBuf = new byte[1];
                read = await ReadExactAsync(stream, rlBuf, 0, 1, ct).ConfigureAwait(false);
                if (read == 0) return null;

                rlBytes[rlByteCount] = rlBuf[0];
                rlByteCount++;

                byte encodedByte = rlBuf[0];
                remainingLength += (encodedByte & 0x7F) * multiplier;
                if ((encodedByte & 0x80) == 0) break;
                multiplier *= 128;
            }

            int totalLength = 1 + rlByteCount + remainingLength;
            byte[] packet = new byte[totalLength];
            packet[0] = headerBuf[0];
            Buffer.BlockCopy(rlBytes, 0, packet, 1, rlByteCount);

            if (remainingLength > 0)
            {
                read = await ReadExactAsync(stream, packet, 1 + rlByteCount, remainingLength, ct).ConfigureAwait(false);
                if (read < remainingLength) return null;
            }

            return packet;
        }

        private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                ct.ThrowIfCancellationRequested();
                int bytesRead = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead).ConfigureAwait(false);
                if (bytesRead == 0) return totalRead;
                totalRead += bytesRead;
            }
            return totalRead;
        }

        private static async Task SafeWriteAsync(Stream stream, byte[] data)
        {
            try
            {
                await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            }
            catch { }
        }

        private class BrokerClient
        {
            public string ClientId { get; set; }
            public TcpClient TcpClient { get; set; }
            public NetworkStream Stream { get; set; }
            public bool CleanSession { get; set; }
            public ushort KeepAlive { get; set; }
            public DateTime LastActivity { get; set; }
            public bool HasWill { get; set; }
            public string WillTopic { get; set; }
            public byte[] WillMessage { get; set; }
            public MqttQoS WillQoS { get; set; }
            public bool WillRetain { get; set; }
            public object WriteLock { get; } = new object();
        }

        private class BrokerSubscription
        {
            public string ClientId { get; }
            public string TopicFilter { get; }
            public MqttQoS QoS { get; }

            public BrokerSubscription(string clientId, string topicFilter, MqttQoS qos)
            {
                ClientId = clientId;
                TopicFilter = topicFilter;
                QoS = qos;
            }
        }

        private class RetainedMessage
        {
            public string Topic { get; set; }
            public byte[] Payload { get; set; }
            public MqttQoS QoS { get; set; }
        }
    }
}
