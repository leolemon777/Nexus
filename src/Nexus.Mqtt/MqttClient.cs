using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mqtt
{
    public class MqttClient : IDisposable
    {
        private TcpClient _tcpClient;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private Task? _receiveLoop;
        private Task? _keepAliveLoop;
        private readonly object _writeLock = new object();
        private volatile bool _isConnected;
        private int _packetIdCounter;
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<MqttPubAckPacket>> _pendingPubAcks
            = new ConcurrentDictionary<ushort, TaskCompletionSource<MqttPubAckPacket>>();
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<MqttPubRecPacket>> _pendingPubRecs
            = new ConcurrentDictionary<ushort, TaskCompletionSource<MqttPubRecPacket>>();
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<MqttPubCompPacket>> _pendingPubComps
            = new ConcurrentDictionary<ushort, TaskCompletionSource<MqttPubCompPacket>>();
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<MqttSubAckPacket>> _pendingSubAcks
            = new ConcurrentDictionary<ushort, TaskCompletionSource<MqttSubAckPacket>>();
        private readonly ConcurrentDictionary<ushort, TaskCompletionSource<MqttUnsubAckPacket>> _pendingUnsubAcks
            = new ConcurrentDictionary<ushort, TaskCompletionSource<MqttUnsubAckPacket>>();
        private readonly ConcurrentDictionary<ushort, MqttPublishPacket> _inflightQoS2
            = new ConcurrentDictionary<ushort, MqttPublishPacket>();

        public string Host { get; private set; } = "";
        public int Port { get; private set; }
        public string ClientId { get; private set; } = "";
        public bool CleanSession { get; set; } = true;
        public ushort KeepAlivePeriod { get; set; } = 60;
        public MqttLastWill? LastWill { get; set; }
        public int ReceiveTimeout { get; set; } = 30000;
        public int SendTimeout { get; set; } = 30000;
        public bool IsConnected => _isConnected;

        public event EventHandler<MqttMessageEventArgs>? OnMessageReceived;
        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;

        public MqttClient()
        {
            _tcpClient = new TcpClient();
        }

        public async Task ConnectAsync(string host, int port = 1883, string? clientId = null,
            string? username = null, string? password = null)
        {
            if (_isConnected)
                throw new InvalidOperationException("Already connected");

            Host = host;
            Port = port;
            ClientId = clientId ?? Guid.NewGuid().ToString("N");

            _cts = new CancellationTokenSource();

            _tcpClient = new TcpClient();
            _tcpClient.ReceiveTimeout = ReceiveTimeout;
            _tcpClient.SendTimeout = SendTimeout;

            await _tcpClient.ConnectAsync(host, port).ConfigureAwait(false);
            _stream = _tcpClient.GetStream();

            var connectPacket = new MqttConnectPacket
            {
                CleanSession = CleanSession,
                KeepAlive = KeepAlivePeriod,
                ClientId = ClientId,
                HasUsername = !string.IsNullOrEmpty(username),
                Username = username,
                HasPassword = !string.IsNullOrEmpty(password),
                Password = password
            };

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

            var connAck = MqttPacket.DecodeConnAck(connAckBytes, 2);
            if (connAck.ReturnCode != MqttConnectReturnCode.Accepted)
                throw new MqttProtocolException($"Connection rejected: {connAck.ReturnCode}");

            _isConnected = true;
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            if (KeepAlivePeriod > 0)
                _keepAliveLoop = Task.Run(() => KeepAliveLoopAsync(_cts.Token));

            OnConnected?.Invoke(this, EventArgs.Empty);
        }

        public async Task PublishAsync(string topic, byte[] payload, MqttQoS qos = MqttQoS.AtMostOnce,
            bool retain = false)
        {
            EnsureConnected();

            ushort packetId = 0;
            if (qos > 0)
                packetId = NextPacketId();

            var publishPacket = new MqttPublishPacket
            {
                Topic = topic,
                Payload = payload ?? Array.Empty<byte>(),
                QoS = qos,
                Retain = retain,
                PacketId = packetId
            };

            if (qos == MqttQoS.AtLeastOnce)
            {
                var tcs = new TaskCompletionSource<MqttPubAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingPubAcks[packetId] = tcs;

                byte[] bytes = MqttPacket.EncodePublish(publishPacket);
                await WriteAsync(bytes).ConfigureAwait(false);

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                        .ConfigureAwait(false);
                    if (completedTask != tcs.Task)
                    {
                        _pendingPubAcks.TryRemove(packetId, out _);
                        throw new TimeoutException("PUBACK timeout");
                    }
                    timeoutCts.Cancel();
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            else if (qos == MqttQoS.ExactlyOnce)
            {
                var tcsRec = new TaskCompletionSource<MqttPubRecPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingPubRecs[packetId] = tcsRec;

                byte[] bytes = MqttPacket.EncodePublish(publishPacket);
                await WriteAsync(bytes).ConfigureAwait(false);

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var completedTask = await Task.WhenAny(tcsRec.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                        .ConfigureAwait(false);
                    if (completedTask != tcsRec.Task)
                    {
                        _pendingPubRecs.TryRemove(packetId, out _);
                        throw new TimeoutException("PUBREC timeout");
                    }
                    timeoutCts.Cancel();
                    await tcsRec.Task.ConfigureAwait(false);
                }

                var tcsComp = new TaskCompletionSource<MqttPubCompPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingPubComps[packetId] = tcsComp;

                byte[] pubRelBytes = MqttPacket.EncodePubRel(packetId);
                await WriteAsync(pubRelBytes).ConfigureAwait(false);

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var completedTask = await Task.WhenAny(tcsComp.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                        .ConfigureAwait(false);
                    if (completedTask != tcsComp.Task)
                    {
                        _pendingPubComps.TryRemove(packetId, out _);
                        throw new TimeoutException("PUBCOMP timeout");
                    }
                    timeoutCts.Cancel();
                    await tcsComp.Task.ConfigureAwait(false);
                }
            }
            else
            {
                byte[] bytes = MqttPacket.EncodePublish(publishPacket);
                await WriteAsync(bytes).ConfigureAwait(false);
            }
        }

        public async Task PublishAsync(string topic, string payload, MqttQoS qos = MqttQoS.AtMostOnce,
            bool retain = false)
        {
            await PublishAsync(topic, System.Text.Encoding.UTF8.GetBytes(payload ?? ""), qos, retain).ConfigureAwait(false);
        }

        public async Task<MqttSubAckPacket> SubscribeAsync(string topicFilter, MqttQoS qos = MqttQoS.AtMostOnce)
        {
            EnsureConnected();

            ushort packetId = NextPacketId();
            var subscribePacket = new MqttSubscribePacket
            {
                PacketId = packetId,
                Subscriptions = new List<(string, MqttQoS)> { (topicFilter, qos) }
            };

            var tcs = new TaskCompletionSource<MqttSubAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSubAcks[packetId] = tcs;

            byte[] bytes = MqttPacket.EncodeSubscribe(subscribePacket);
            await WriteAsync(bytes).ConfigureAwait(false);

            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);
                if (completedTask != tcs.Task)
                {
                    _pendingSubAcks.TryRemove(packetId, out _);
                    throw new TimeoutException("SUBACK timeout");
                }
                timeoutCts.Cancel();
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        public async Task<MqttUnsubAckPacket> UnsubscribeAsync(string topicFilter)
        {
            EnsureConnected();

            ushort packetId = NextPacketId();
            var unsubscribePacket = new MqttUnsubscribePacket
            {
                PacketId = packetId,
                TopicFilters = new List<string> { topicFilter }
            };

            var tcs = new TaskCompletionSource<MqttUnsubAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingUnsubAcks[packetId] = tcs;

            byte[] bytes = MqttPacket.EncodeUnsubscribe(unsubscribePacket);
            await WriteAsync(bytes).ConfigureAwait(false);

            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);
                if (completedTask != tcs.Task)
                {
                    _pendingUnsubAcks.TryRemove(packetId, out _);
                    throw new TimeoutException("UNSUBACK timeout");
                }
                timeoutCts.Cancel();
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        public void Disconnect()
        {
            if (!_isConnected) return;

            try
            {
                if (_stream != null)
                {
                    byte[] disconnectBytes = MqttPacket.EncodeDisconnect();
                    lock (_writeLock)
                    {
                        _stream.Write(disconnectBytes, 0, disconnectBytes.Length);
                    }
                }
            }
            catch { }

            Cleanup();
        }

        public void Dispose()
        {
            Disconnect();
            _tcpClient?.Dispose();
            _stream?.Dispose();
            _cts?.Dispose();
        }

        private void EnsureConnected()
        {
            if (!_isConnected)
                throw new InvalidOperationException("Not connected");
        }

        private ushort NextPacketId()
        {
            ushort id;
            do
            {
                id = (ushort)(Interlocked.Increment(ref _packetIdCounter) & 0xFFFF);
                if (id == 0) id = 1;
            } while (false);
            return id;
        }

        private Task WriteAsync(byte[] data)
        {
            if (_stream == null)
                throw new InvalidOperationException("Stream not available");

            lock (_writeLock)
            {
                _stream.Write(data, 0, data.Length);
            }
            return Task.CompletedTask;
        }

        private async Task<byte[]?> ReadPacketAsync(CancellationToken ct)
        {
            if (_stream == null) return null;

            byte[] headerBuf = new byte[1];
            int read = await ReadExactAsync(_stream, headerBuf, 0, 1, ct).ConfigureAwait(false);
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
                read = await ReadExactAsync(_stream, rlBuf, 0, 1, ct).ConfigureAwait(false);
                if (read == 0) return null;

                rlBytes[rlByteCount] = rlBuf[0];
                rlByteCount++;

                byte encodedByte = rlBuf[0];
                remainingLength += (encodedByte & 0x7F) * multiplier;
                if ((encodedByte & 0x80) == 0) break;
                multiplier *= 128;
            }

            byte[] packet = new byte[1 + rlByteCount + remainingLength];
            packet[0] = headerBuf[0];
            Buffer.BlockCopy(rlBytes, 0, packet, 1, rlByteCount);

            if (remainingLength > 0)
            {
                read = await ReadExactAsync(_stream, packet, 1 + rlByteCount, remainingLength, ct).ConfigureAwait(false);
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

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _isConnected)
                {
                    byte[]? packet;
                    try
                    {
                        packet = await ReadPacketAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }

                    if (packet == null || packet.Length < 2)
                        break;

                    try
                    {
                        ProcessReceivedPacket(packet);
                    }
                    catch (Exception) { }
                }
            }
            finally
            {
                Cleanup();
            }
        }

        private void ProcessReceivedPacket(byte[] packet)
        {
            int firstByte = packet[0];
            MqttPacketType packetType = (MqttPacketType)(firstByte >> 4);

            int offset = 1;
            int remainingLength = MqttPacket.DecodeRemainingLength(packet, ref offset);

            switch (packetType)
            {
                case MqttPacketType.Publish:
                    HandlePublish(firstByte, packet, offset, remainingLength);
                    break;

                case MqttPacketType.PubAck:
                    if (remainingLength >= 2)
                    {
                        ushort packetId = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                        if (_pendingPubAcks.TryRemove(packetId, out var tcs))
                            tcs.TrySetResult(new MqttPubAckPacket { PacketId = packetId });
                    }
                    break;

                case MqttPacketType.PubRec:
                    if (remainingLength >= 2)
                    {
                        ushort packetId = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                        if (_pendingPubRecs.TryRemove(packetId, out var tcs))
                            tcs.TrySetResult(new MqttPubRecPacket { PacketId = packetId });
                    }
                    break;

                case MqttPacketType.PubComp:
                    if (remainingLength >= 2)
                    {
                        ushort packetId = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                        if (_pendingPubComps.TryRemove(packetId, out var tcs))
                            tcs.TrySetResult(new MqttPubCompPacket { PacketId = packetId });
                    }
                    break;

                case MqttPacketType.PubRel:
                    if (remainingLength >= 2)
                    {
                        ushort packetId = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                        if (_inflightQoS2.TryRemove(packetId, out var pubPacket))
                        {
                            var args = new MqttMessageEventArgs
                            {
                                Topic = pubPacket.Topic,
                                Payload = pubPacket.Payload,
                                QoS = pubPacket.QoS,
                                Retain = pubPacket.Retain
                            };
                            OnMessageReceived?.Invoke(this, args);
                        }
                        byte[] pubCompBytes = MqttPacket.EncodePubComp(packetId);
                        lock (_writeLock)
                        {
                            _stream?.Write(pubCompBytes, 0, pubCompBytes.Length);
                        }
                    }
                    break;

                case MqttPacketType.SubAck:
                    if (remainingLength >= 2)
                    {
                        ushort packetId = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                        if (_pendingSubAcks.TryRemove(packetId, out var tcs))
                        {
                            var subAck = new MqttSubAckPacket { PacketId = packetId };
                            for (int i = offset + 2; i < offset + remainingLength; i++)
                                subAck.ReturnCodes.Add(packet[i]);
                            tcs.TrySetResult(subAck);
                        }
                    }
                    break;

                case MqttPacketType.UnsubAck:
                    if (remainingLength >= 2)
                    {
                        ushort packetId = (ushort)((packet[offset] << 8) | packet[offset + 1]);
                        if (_pendingUnsubAcks.TryRemove(packetId, out var tcs))
                            tcs.TrySetResult(new MqttUnsubAckPacket { PacketId = packetId });
                    }
                    break;

                case MqttPacketType.PingResp:
                    break;
            }
        }

        private void HandlePublish(int firstByte, byte[] data, int offset, int length)
        {
            byte flags = (byte)(firstByte & 0x0F);
            var pubPacket = MqttPacket.DecodePublish(flags, data, offset, length);

            if (pubPacket.QoS == MqttQoS.AtMostOnce)
            {
                var args = new MqttMessageEventArgs
                {
                    Topic = pubPacket.Topic,
                    Payload = pubPacket.Payload,
                    QoS = pubPacket.QoS,
                    Retain = pubPacket.Retain
                };
                OnMessageReceived?.Invoke(this, args);
            }
            else if (pubPacket.QoS == MqttQoS.AtLeastOnce)
            {
                var args = new MqttMessageEventArgs
                {
                    Topic = pubPacket.Topic,
                    Payload = pubPacket.Payload,
                    QoS = pubPacket.QoS,
                    Retain = pubPacket.Retain
                };
                OnMessageReceived?.Invoke(this, args);

                byte[] pubAckBytes = MqttPacket.EncodePubAck(pubPacket.PacketId);
                lock (_writeLock)
                {
                    _stream?.Write(pubAckBytes, 0, pubAckBytes.Length);
                }
            }
            else if (pubPacket.QoS == MqttQoS.ExactlyOnce)
            {
                _inflightQoS2[pubPacket.PacketId] = pubPacket;

                byte[] pubRecBytes = MqttPacket.EncodePubRec(pubPacket.PacketId);
                lock (_writeLock)
                {
                    _stream?.Write(pubRecBytes, 0, pubRecBytes.Length);
                }
            }
        }

        private async Task KeepAliveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _isConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(KeepAlivePeriod / 2.0), ct).ConfigureAwait(false);
                    if (!_isConnected) break;

                    try
                    {
                        byte[] pingBytes = MqttPacket.EncodePingReq();
                        lock (_writeLock)
                        {
                            _stream?.Write(pingBytes, 0, pingBytes.Length);
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        private void Cleanup()
        {
            if (!_isConnected) return;
            _isConnected = false;

            _cts?.Cancel();

            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }

            foreach (var tcs in _pendingPubAcks.Values)
                tcs.TrySetCanceled();
            _pendingPubAcks.Clear();

            foreach (var tcs in _pendingPubRecs.Values)
                tcs.TrySetCanceled();
            _pendingPubRecs.Clear();

            foreach (var tcs in _pendingPubComps.Values)
                tcs.TrySetCanceled();
            _pendingPubComps.Clear();

            foreach (var tcs in _pendingSubAcks.Values)
                tcs.TrySetCanceled();
            _pendingSubAcks.Clear();

            foreach (var tcs in _pendingUnsubAcks.Values)
                tcs.TrySetCanceled();
            _pendingUnsubAcks.Clear();

            _inflightQoS2.Clear();

            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }
}
