#nullable disable warnings
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.OpcUa
{
    public class OpcUaDataChangeEventArgs : EventArgs
    {
        public uint ClientHandle { get; set; }
        public string NodeId { get; set; }
        public object Value { get; set; }
        public uint StatusCode { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class OpcUaClient : IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private TcpClient _tcp;
        private NetworkStream _stream;
        private bool _isConnected;
        private readonly OpcUaSession _session;
        private readonly string _endpointUrl;
        private readonly ConcurrentDictionary<uint, string> _monitoredNodeMap = new ConcurrentDictionary<uint, string>();
        private readonly ConcurrentDictionary<string, object> _lastValues = new ConcurrentDictionary<string, object>();
        private CancellationTokenSource _publishCts;
        private Task _publishTask;
        private int _nextClientHandle;
        private uint _subscriptionId;
        private Timer _keepaliveTimer;
        private bool _disposed;

        protected ILogger Log { get; set; }

        public string IpAddress { get; }
        public int Port { get; }
        public int Timeout { get; set; }
        public int SessionTimeout { get; set; } = 60000;
        public int KeepaliveIntervalMs { get; set; } = 10000;
        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event EventHandler<string> OnError;
        public event EventHandler<string> OnMessageSent;
        public event EventHandler<string> OnMessageReceived;
        public event EventHandler<OpcUaDataChangeEventArgs> OnDataChanged;
        event EventHandler<DataChangeEventArgs> ISubscribeDevice.OnDataChanged
        {
            add => _dataChangedBridge += value;
            remove => _dataChangedBridge -= value;
        }
        private event EventHandler<DataChangeEventArgs> _dataChangedBridge;

        public bool IsConnected
        {
            get { lock (_lock) return _isConnected && _tcp?.Connected == true; }
        }

        public OpcUaClient(string ipAddress, int port = 4840, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
            Timeout = timeout;
            _endpointUrl = $"opc.tcp://{ipAddress}:{port}";
            _session = new OpcUaSession();
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        #region Connect / Disconnect

        public OperateResult Connect()
        {
            try
            {
                lock (_lock)
                {
                    if (_isConnected) return OperateResult.Success();
                    DisconnectCore();
                    _tcp = new TcpClient();
                    var ar = _tcp.BeginConnect(IpAddress, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(Timeout, false))
                    {
                        _tcp.Close();
                        _tcp = null;
                        return OperateResult.Failed("连接超时");
                    }
                    _tcp.EndConnect(ar);
                    _stream = _tcp.GetStream();
                    _stream.ReadTimeout = Timeout;
                    _stream.WriteTimeout = Timeout;
                }

                var helloResult = PerformHelloHandshake();
                if (!helloResult.IsSuccess) { Disconnect(); return helloResult; }

                var channelResult = PerformOpenSecureChannel();
                if (!channelResult.IsSuccess) { Disconnect(); return channelResult; }

                var sessionResult = PerformCreateSession();
                if (!sessionResult.IsSuccess) { Disconnect(); return sessionResult; }

                var activateResult = PerformActivateSession();
                if (!activateResult.IsSuccess) { Disconnect(); return activateResult; }

                lock (_lock) { _isConnected = true; }
                StartKeepaliveTimer();
                OnConnected?.Invoke(this, EventArgs.Empty);
                Log.Info($"OPC UA 会话已建立: {_endpointUrl}");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"连接失败: {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                Disconnect();
                return OperateResult.Failed(ex.Message);
            }
        }

        public async Task<OperateResult> ConnectAsync()
        {
            try
            {
                lock (_lock) DisconnectCore();
                _tcp = new TcpClient();
                await _tcp.ConnectAsync(IpAddress, Port).ConfigureAwait(false);
                lock (_lock)
                {
                    _stream = _tcp.GetStream();
                    _stream.ReadTimeout = Timeout;
                    _stream.WriteTimeout = Timeout;
                }

                var helloResult = PerformHelloHandshake();
                if (!helloResult.IsSuccess) { Disconnect(); return helloResult; }

                var channelResult = PerformOpenSecureChannel();
                if (!channelResult.IsSuccess) { Disconnect(); return channelResult; }

                var sessionResult = PerformCreateSession();
                if (!sessionResult.IsSuccess) { Disconnect(); return sessionResult; }

                var activateResult = PerformActivateSession();
                if (!activateResult.IsSuccess) { Disconnect(); return activateResult; }

                lock (_lock) { _isConnected = true; }
                StartKeepaliveTimer();
                OnConnected?.Invoke(this, EventArgs.Empty);
                Log.Info($"OPC UA 会话已建立: {_endpointUrl}");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"连接失败: {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                Disconnect();
                return OperateResult.Failed(ex.Message);
            }
        }

        public void Disconnect()
        {
            StopKeepaliveTimer();
            try
            {
                if (_isConnected && _stream != null)
                {
                    try { PerformCloseSession(); } catch { }
                    try { PerformCloseSecureChannel(); } catch { }
                }
            }
            finally
            {
                lock (_lock) DisconnectCore();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopSubscriptions();
            StopKeepaliveTimer();
            Disconnect();
            GC.SuppressFinalize(this);
        }

        private void DisconnectCore()
        {
            _publishCts?.Cancel();
            _isConnected = false;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null;
            _tcp = null;
            _session.Reset();
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region Session Keepalive

        private void StartKeepaliveTimer()
        {
            StopKeepaliveTimer();
            _keepaliveTimer = new Timer(OnKeepaliveTick, null, KeepaliveIntervalMs, KeepaliveIntervalMs);
        }

        private void StopKeepaliveTimer()
        {
            _keepaliveTimer?.Dispose();
            _keepaliveTimer = null;
        }

        private void OnKeepaliveTick(object state)
        {
            try
            {
                if (!IsConnected) return;

                if (_session.IsTokenRenewalDue())
                {
                    Log.Debug("安全令牌即将过期，执行续期...");
                    var result = PerformOpenSecureChannel(renew: true);
                    if (!result.IsSuccess)
                    {
                        Log.Warn($"令牌续期失败: {result.Message}");
                        OnError?.Invoke(this, "令牌续期失败: " + result.Message);
                    }
                    else
                    {
                        Log.Debug("安全令牌续期成功");
                    }
                }

                if (_session.NeedsAcknowledgement)
                {
                    SendPublishWithAcknowledgement();
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Keepalive 异常: {ex.Message}");
            }
        }

        #endregion

        #region OPC UA TCP Hello/Acknowledge

        private OperateResult PerformHelloHandshake()
        {
            var helloMsg = BuildHelloMessage();
            SendMessage(helloMsg);
            OnMessageSent?.Invoke(this, $"HEL → {_endpointUrl}");

            var ackHeader = ReadBytes(8);
            if (ackHeader == null) return OperateResult.Failed("未收到 ACK 响应");

            if (ackHeader[0] != 0x41 || ackHeader[1] != 0x43 || ackHeader[2] != 0x4B)
                return OperateResult.Failed("无效的 ACK 响应: " + BitConverter.ToString(ackHeader, 0, 3));

            int ackSize = BitConverter.ToInt32(ackHeader, 4);
            int remaining = ackSize - 8;
            if (remaining > 0) ReadBytes(remaining);

            OnMessageReceived?.Invoke(this, "← ACK");
            Log.Debug("OPC UA TCP 握手完成");
            return OperateResult.Success();
        }

        private byte[] BuildHelloMessage()
        {
            var urlBytes = Encoding.UTF8.GetBytes(_endpointUrl);
            int msgSize = 8 + 24 + 4 + urlBytes.Length;
            var msg = new byte[msgSize];
            msg[0] = 0x48; msg[1] = 0x45; msg[2] = 0x4C; msg[3] = 0x46;
            WriteInt32(msg, 4, msgSize);
            WriteInt32(msg, 8, 65535);
            WriteInt32(msg, 12, 65535);
            WriteInt32(msg, 16, 65535);
            WriteInt32(msg, 20, 0);
            WriteInt32(msg, 24, 0);
            WriteInt32(msg, 28, urlBytes.Length);
            Buffer.BlockCopy(urlBytes, 0, msg, 32, urlBytes.Length);
            return msg;
        }

        #endregion

        #region OpenSecureChannel

        private OperateResult PerformOpenSecureChannel(bool renew = false)
        {
            int reqHandle = _session.NextRequestHandle();
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);

            WriteRequestHeader(w, new DateTime(0));
            w.Write((uint)(renew ? 1 : 0));
            w.Write((uint)0);
            w.Write((uint)(renew ? (uint)(_session.SecurityTokenLifetime > 0 ? _session.SecurityTokenLifetime : 600000) : 600000));
            w.Write(-1);

            var payloadBytes = payload.ToArray();
            int msgSize = 12 + 4 + payloadBytes.Length;
            var msg = new byte[msgSize];
            msg[0] = 0x4F; msg[1] = 0x50; msg[2] = 0x4E; msg[3] = 0x46;
            WriteInt32(msg, 4, msgSize);
            WriteInt32(msg, 8, (int)_session.SecureChannelId);
            Buffer.BlockCopy(payloadBytes, 0, msg, 12, payloadBytes.Length);
            SendMessage(msg);
            OnMessageSent?.Invoke(this, renew ? "OPN → RenewSecureChannel" : "OPN → OpenSecureChannel");

            var respHeader = ReadBytes(8);
            if (respHeader == null) return OperateResult.Failed("未收到 OpenSecureChannel 响应");

            if (respHeader[0] == 0x45 && respHeader[1] == 0x52 && respHeader[2] == 0x52)
            {
                int errSize = BitConverter.ToInt32(respHeader, 4);
                ReadBytes(errSize - 8);
                return OperateResult.Failed("OPC UA 错误响应");
            }

            if (respHeader[0] != 0x4F || respHeader[1] != 0x50 || respHeader[2] != 0x4E)
                return OperateResult.Failed("无效的 OPN 响应");

            int respSize = BitConverter.ToInt32(respHeader, 4);
            var respPayload = ReadBytes(respSize - 8);
            if (respPayload == null) return OperateResult.Failed("读取 OPN 响应失败");

            int offset = 0;
            _session.SecureChannelId = ReadUInt32LE(respPayload, ref offset);
            offset += ReadStringAt(respPayload, ref offset);
            ReadByteStringAt(respPayload, ref offset);
            ReadByteStringAt(respPayload, ref offset);
            offset += 4;
            offset += 4;
            offset += 4;
            _session.SecurityTokenId = ReadUInt32LE(respPayload, ref offset);
            _session.SecurityTokenCreatedAt = ReadDateTimeLE(respPayload, ref offset);
            _session.SecurityTokenLifetime = ReadUInt32LE(respPayload, ref offset);
            int nonceLen = ReadInt32LE(respPayload, ref offset);
            _session.ServerNonce = nonceLen > 0 ? ReadBytesAt(respPayload, ref offset, nonceLen) : new byte[0];

            OnMessageReceived?.Invoke(this, $"← OPN SecureChannelId={_session.SecureChannelId}");
            Log.Debug($"OpenSecureChannel 完成: ChannelId={_session.SecureChannelId}");
            return OperateResult.Success();
        }

        private void PerformCloseSecureChannel()
        {
            try
            {
                int msgSize = 12;
                var msg = new byte[msgSize];
                msg[0] = 0x43; msg[1] = 0x4C; msg[2] = 0x4F; msg[3] = 0x46;
                WriteInt32(msg, 4, msgSize);
                WriteInt32(msg, 8, (int)_session.SecureChannelId);
                SendMessage(msg);
                OnMessageSent?.Invoke(this, "CLO → CloseSecureChannel");
            }
            catch { }
        }

        #endregion

        #region CreateSession / ActivateSession

        private OperateResult PerformCreateSession()
        {
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);

            var appUri = Encoding.UTF8.GetBytes("urn:Nexus:OpcUaClient");
            var endpointBytes = Encoding.UTF8.GetBytes(_endpointUrl);
            var prodUri = Encoding.UTF8.GetBytes("urn:Nexus");
            var sessionName = Encoding.UTF8.GetBytes("NexusSession");

            w.Write(endpointBytes.Length);
            w.Write(endpointBytes);
            w.Write(appUri.Length);
            w.Write(appUri);
            w.Write(sessionName.Length);
            w.Write(sessionName);
            w.Write((ulong)0);
            w.Write(appUri.Length);
            w.Write(appUri);
            w.Write(prodUri.Length);
            w.Write(prodUri);
            w.Write(sessionName.Length);
            w.Write(sessionName);
            w.Write(-1);
            w.Write((uint)SessionTimeout);
            w.Write(-1);
            w.Write(-1);

            var payloadBytes = payload.ToArray();
            int msgSize = 12 + payloadBytes.Length;
            var msg = new byte[msgSize];
            msg[0] = 0x4D; msg[1] = 0x53; msg[2] = 0x47; msg[3] = 0x46;
            WriteInt32(msg, 4, msgSize);
            WriteInt32(msg, 8, (int)_session.SecureChannelId);
            Buffer.BlockCopy(payloadBytes, 0, msg, 12, payloadBytes.Length);
            SendMessage(msg);
            OnMessageSent?.Invoke(this, "MSG → CreateSession");

            return ReadCreateSessionResponse();
        }

        private OperateResult ReadCreateSessionResponse()
        {
            var respHeader = ReadBytes(8);
            if (respHeader == null) return OperateResult.Failed("未收到 CreateSession 响应");
            if (respHeader[0] != 0x4D || respHeader[1] != 0x53 || respHeader[2] != 0x47)
                return OperateResult.Failed("无效的 MSG 响应");

            int respSize = BitConverter.ToInt32(respHeader, 4);
            var respPayload = ReadBytes(respSize - 8);
            if (respPayload == null) return OperateResult.Failed("读取 CreateSession 响应失败");

            int offset = 4;
            int policyLen = ReadInt32LE(respPayload, ref offset);
            offset += policyLen;
            int certLen = ReadInt32LE(respPayload, ref offset);
            if (certLen > 0) offset += certLen;
            int thumbLen = ReadInt32LE(respPayload, ref offset);
            if (thumbLen > 0) offset += thumbLen;
            offset += 4;
            offset += 4;

            ReadDateTimeLE(respPayload, ref offset);
            offset += 4;
            int diagLen = ReadInt32LE(respPayload, ref offset);
            if (diagLen > 0) offset += diagLen;
            int strTableLen = ReadInt32LE(respPayload, ref offset);
            for (int i = 0; i < strTableLen; i++) { int sl = ReadInt32LE(respPayload, ref offset); offset += sl; }

            byte sessionNodeIdType = respPayload[offset]; offset++;
            if (sessionNodeIdType == 0x01)
            {
                _session.SessionNamespace = respPayload[offset]; offset++;
                _session.SessionId = ReadUInt16LE(respPayload, ref offset);
            }
            else if (sessionNodeIdType == 0x02)
            {
                _session.SessionNamespace = ReadUInt16LE(respPayload, ref offset);
                _session.SessionId = ReadUInt32LE(respPayload, ref offset);
            }
            else
            {
                _session.SessionId = ReadUInt32LE(respPayload, ref offset);
            }

            byte authNodeIdType = respPayload[offset]; offset++;
            ushort authNs = 0;
            if (authNodeIdType == 0x01)
            {
                authNs = respPayload[offset]; offset++;
                _session.AuthenticationToken = new OpcUaNodeId(authNs, (uint)ReadUInt16LE(respPayload, ref offset));
            }
            else if (authNodeIdType == 0x02)
            {
                authNs = ReadUInt16LE(respPayload, ref offset);
                _session.AuthenticationToken = new OpcUaNodeId(authNs, ReadUInt32LE(respPayload, ref offset));
            }
            else if (authNodeIdType == 0x03)
            {
                authNs = ReadUInt16LE(respPayload, ref offset);
                string authStr = ReadOpcString(respPayload, ref offset);
                _session.AuthenticationToken = new OpcUaNodeId(authNs, authStr);
            }
            else
            {
                _session.AuthenticationToken = new OpcUaNodeId(0, (uint)respPayload[offset]); offset++;
            }

            ReadDateTimeLE(respPayload, ref offset);

            OnMessageReceived?.Invoke(this, $"← CreateSession SessionId={_session.SessionId}");
            Log.Debug($"CreateSession 完成: SessionId={_session.SessionId}");
            return OperateResult.Success();
        }

        private OperateResult PerformActivateSession()
        {
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);
            WriteRequestHeader(w, null);

            w.Write(-1);

            w.Write((byte)0x01);
            OpcUaNodeId.WriteString(w, "Anonymous");
            w.Write(-1);
            w.Write(-1);
            w.Write(-1);

            var payloadBytes = payload.ToArray();
            var result = SendServiceRequest(0x01E0, payloadBytes);
            if (!result.IsSuccess) return result;

            int offset = 4;
            ReadDateTimeLE(result.Content, ref offset);
            offset += 4;
            int diagLen = ReadInt32LE(result.Content, ref offset);
            if (diagLen > 0) offset += diagLen;

            OnMessageReceived?.Invoke(this, "← ActivateSession 成功");
            Log.Debug("ActivateSession 完成");
            return OperateResult.Success();
        }

        private void PerformCloseSession()
        {
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);
            WriteRequestHeader(w, null);
            w.Write(true);
            SendServiceRequest(0x01E6, payload.ToArray());
            OnMessageSent?.Invoke(this, "MSG → CloseSession");
        }

        #endregion

        #region Read Service

        public OperateResult<object> ReadValue(string nodeIdString)
        {
            var nodeId = OpcUaNodeId.Parse(nodeIdString);
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);
            WriteRequestHeader(w, null);
            w.Write((double)0);
            w.Write((uint)0);
            w.Write(1);
            nodeId.EncodeTo(w);
            w.Write((uint)13);
            OpcUaNodeId.WriteString(w, null);
            OpcUaNodeId.WriteString(w, null);

            var result = SendServiceRequest(0x077D, payload.ToArray());
            if (!result.IsSuccess) return OperateResult<object>.Failed(result.Message);

            int offset = 4;
            int count = ReadInt32LE(result.Content, ref offset);
            if (count < 1) return OperateResult<object>.Failed("空响应");

            return ReadDataValue(result.Content, ref offset);
        }

        public OperateResult<T> ReadValue<T>(string nodeIdString)
        {
            var result = ReadValue(nodeIdString);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message);
            try
            {
                return OperateResult<T>.Success((T)Convert.ChangeType(result.Content, typeof(T), CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"类型转换失败: {ex.Message}");
            }
        }

        public OperateResult WriteValue(string nodeIdString, object value)
        {
            var nodeId = OpcUaNodeId.Parse(nodeIdString);
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);
            WriteRequestHeader(w, null);
            w.Write(1);
            nodeId.EncodeTo(w);
            w.Write((uint)13);
            OpcUaNodeId.WriteString(w, null);
            OpcUaNodeId.WriteString(w, null);
            WriteDataValue(w, value, 0x01);

            var result = SendServiceRequest(0x07A5, payload.ToArray());
            if (!result.IsSuccess) return result;

            int offset = 4;
            int count = ReadInt32LE(result.Content, ref offset);
            if (count < 1) return OperateResult.Failed("空响应");

            int statusCode = ReadInt32LE(result.Content, ref offset);
            if (statusCode != 0) return OperateResult.Failed($"Write failed: 0x{statusCode:X8}");

            return OperateResult.Success();
        }

        #endregion

        #region Browse Service

        public OperateResult<List<OpcUaReferenceDescription>> Browse(string nodeIdString, uint maxReferences = 100)
        {
            var nodeId = OpcUaNodeId.Parse(nodeIdString);
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);
            WriteRequestHeader(w, null);
            w.Write((uint)0);
            w.Write((uint)0);
            w.Write((long)0);
            w.Write((long)0);
            w.Write(maxReferences);
            w.Write(1);
            nodeId.EncodeTo(w);
            w.Write((uint)3);
            new OpcUaNodeId(0, 33u).EncodeTo(w);
            w.Write((byte)0);
            w.Write((uint)0);
            w.Write((uint)0x3F);

            var result = SendServiceRequest(0x07F5, payload.ToArray());
            if (!result.IsSuccess) return OperateResult<List<OpcUaReferenceDescription>>.Failed(result.Message);

            var references = new List<OpcUaReferenceDescription>();
            int offset = 4;
            int diagLen = ReadInt32LE(result.Content, ref offset);
            if (diagLen > 0) offset += diagLen;
            int strTableLen = ReadInt32LE(result.Content, ref offset);
            for (int i = 0; i < strTableLen; i++) { int sl = ReadInt32LE(result.Content, ref offset); offset += sl; }
            int resultCount = ReadInt32LE(result.Content, ref offset);

            for (int r = 0; r < resultCount; r++)
            {
                int statusCode = ReadInt32LE(result.Content, ref offset);
                int cpLen = ReadInt32LE(result.Content, ref offset);
                if (cpLen > 0) offset += cpLen;
                int refCount = ReadInt32LE(result.Content, ref offset);

                for (int j = 0; j < refCount; j++)
                {
                    byte refTypeEnc = result.Content[offset]; offset++;
                    if (refTypeEnc == 0x03)
                    {
                        ushort ns = ReadUInt16LE(result.Content, ref offset);
                        int sLen = ReadInt32LE(result.Content, ref offset);
                        offset += sLen;
                    }
                    else if (refTypeEnc == 0x02)
                    {
                        ushort ns = ReadUInt16LE(result.Content, ref offset);
                        offset += 4;
                    }
                    else if (refTypeEnc == 0x01)
                    {
                        offset += 2;
                    }
                    else if (refTypeEnc == 0x04)
                    {
                        ushort ns = ReadUInt16LE(result.Content, ref offset);
                        offset += 16;
                    }
                    else
                    {
                        offset++;
                    }

                    bool isForward = result.Content[offset] != 0; offset++;
                    string targetNodeId = ReadExpandedNodeId(result.Content, ref offset);

                    int browseNameNs = ReadUInt16LE(result.Content, ref offset);
                    int browseNameLen = ReadInt32LE(result.Content, ref offset);
                    string browseName = Encoding.UTF8.GetString(result.Content, offset, browseNameLen);
                    offset += browseNameLen;

                    int displayNameEnc = result.Content[offset]; offset++;
                    int localeLen = ReadInt32LE(result.Content, ref offset);
                    offset += localeLen;
                    int textLen = ReadInt32LE(result.Content, ref offset);
                    string displayName = Encoding.UTF8.GetString(result.Content, offset, textLen);
                    offset += textLen;

                    int nodeClass = ReadInt32LE(result.Content, ref offset);

                    string typeDefNodeId = ReadExpandedNodeId(result.Content, ref offset);

                    references.Add(new OpcUaReferenceDescription
                    {
                        ReferenceTypeId = "",
                        IsForward = isForward,
                        NodeId = targetNodeId,
                        BrowseName = browseName,
                        DisplayName = displayName,
                        NodeClass = nodeClass,
                        TypeDefinition = typeDefNodeId
                    });
                }
            }

            return OperateResult<List<OpcUaReferenceDescription>>.Success(references);
        }

        #endregion

        #region Subscription / MonitoredItems

        public OperateResult<uint> CreateSubscription(double publishingIntervalMs = 1000, uint maxNotificationsPerPublish = 1000)
        {
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);
            WriteRequestHeader(w, null);
            w.Write((double)0);
            w.Write(10000);
            w.Write(10000);
            w.Write(maxNotificationsPerPublish);
            w.Write(1);
            w.Write(true);

            var result = SendServiceRequest(0x07E9, payload.ToArray());
            if (!result.IsSuccess) return OperateResult<uint>.Failed(result.Message);

            int offset = 4;
            _subscriptionId = ReadUInt32LE(result.Content, ref offset);
            double revisedInterval = BitConverter.ToDouble(result.Content, offset); offset += 8;
            uint revisedLifetime = ReadUInt32LE(result.Content, ref offset);
            uint revisedKeepAlive = ReadUInt32LE(result.Content, ref offset);
            uint revisedMaxNotif = ReadUInt32LE(result.Content, ref offset);

            OnMessageReceived?.Invoke(this, $"← CreateSubscription Id={_subscriptionId} Interval={revisedInterval}ms");
            Log.Info($"创建订阅: Id={_subscriptionId}, Interval={revisedInterval}ms");
            return OperateResult<uint>.Success(_subscriptionId);
        }

        public OperateResult<uint> CreateMonitoredItem(string nodeIdString, double samplingIntervalMs = 1000)
        {
            uint clientHandle = (uint)Interlocked.Increment(ref _nextClientHandle);
            _monitoredNodeMap[clientHandle] = nodeIdString;

            var nodeId = OpcUaNodeId.Parse(nodeIdString);
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);
            WriteRequestHeader(w, null);
            w.Write(_subscriptionId);
            w.Write((uint)2);
            w.Write(1);
            nodeId.EncodeTo(w);
            w.Write((uint)13);
            OpcUaNodeId.WriteString(w, null);
            OpcUaNodeId.WriteString(w, null);
            w.Write((uint)2);
            w.Write(samplingIntervalMs);
            w.Write(clientHandle);
            w.Write((uint)1);
            w.Write(true);

            var result = SendServiceRequest(0x07F1, payload.ToArray());
            if (!result.IsSuccess) return OperateResult<uint>.Failed(result.Message);

            int offset = 4;
            int diagLen = ReadInt32LE(result.Content, ref offset);
            if (diagLen > 0) offset += diagLen;
            int strTableLen = ReadInt32LE(result.Content, ref offset);
            for (int i = 0; i < strTableLen; i++) { int sl = ReadInt32LE(result.Content, ref offset); offset += sl; }
            int count = ReadInt32LE(result.Content, ref offset);

            if (count < 1) return OperateResult<uint>.Failed("CreateMonitoredItems 返回空结果");

            int sc = ReadInt32LE(result.Content, ref offset);
            uint serverHandle = ReadUInt32LE(result.Content, ref offset);
            double revisedInterval = BitConverter.ToDouble(result.Content, offset); offset += 8;
            uint revisedQueueSize = ReadUInt32LE(result.Content, ref offset);
            offset++;

            OnMessageReceived?.Invoke(this, $"← CreateMonitoredItem NodeId={nodeIdString} ServerHandle={serverHandle}");
            Log.Debug($"创建监控项: {nodeIdString}, ClientHandle={clientHandle}, ServerHandle={serverHandle}");
            return OperateResult<uint>.Success(clientHandle);
        }

        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        {
            if (_subscriptionId == 0)
            {
                var subResult = CreateSubscription(intervalMs);
                if (!subResult.IsSuccess) { Log.Error($"创建订阅失败: {subResult.Message}"); return; }
            }
            CreateMonitoredItem(address, intervalMs);
        }

        public void Unsubscribe(string address)
        {
            foreach (var kvp in _monitoredNodeMap)
            {
                if (kvp.Value == address)
                {
                    _monitoredNodeMap.TryRemove(kvp.Key, out _);
                    break;
                }
            }
        }

        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            StopSubscriptions();
            _publishCts = new CancellationTokenSource();
            var ct = _publishCts.Token;
            _publishTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var result = SendPublishRequest();
                        if (result.IsSuccess) ProcessPublishResponse(result.Content);
                        await Task.Delay(globalIntervalMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Log.Error($"Publish 异常: {ex.Message}"); }
                }
            }, ct);
        }

        public void StopSubscriptions()
        {
            _publishCts?.Cancel();
            try { _publishTask?.Wait(2000); } catch { }
            _publishCts?.Dispose();
            _publishCts = null;
            _publishTask = null;
        }

        private OperateResult<byte[]> SendPublishRequest()
        {
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);
            WriteRequestHeader(w, null);

            w.Write(1);
            if (_session.NeedsAcknowledgement)
            {
                w.Write(_subscriptionId);
                w.Write(_session.LastReceivedSequence);
                _session.NeedsAcknowledgement = false;
                _session.LastAcknowledgedSequence = _session.LastReceivedSequence;
            }
            else
            {
                w.Write((uint)0);
                w.Write((uint)0);
            }

            var result = SendServiceRequest(0x07F7, payload.ToArray());
            return result;
        }

        private void SendPublishWithAcknowledgement()
        {
            try
            {
                var result = SendPublishRequest();
                if (result.IsSuccess) ProcessPublishResponse(result.Content);
            }
            catch (Exception ex)
            {
                Log.Debug($"Publish ack 异常: {ex.Message}");
            }
        }

        private void ProcessPublishResponse(byte[] response)
        {
            try
            {
                int offset = 4;
                ReadDateTimeLE(response, ref offset);
                offset += 4;
                int diagLen = ReadInt32LE(response, ref offset);
                if (diagLen > 0) offset += diagLen;
                int strTableLen = ReadInt32LE(response, ref offset);
                for (int i = 0; i < strTableLen; i++) { int sl = ReadInt32LE(response, ref offset); offset += sl; }

                uint subscriptionId = ReadUInt32LE(response, ref offset);
                uint sequenceNumber = ReadUInt32LE(response, ref offset);
                ReadDateTimeLE(response, ref offset);

                _session.LastReceivedSequence = sequenceNumber;
                _session.NeedsAcknowledgement = true;

                int notifCount = ReadInt32LE(response, ref offset);

                for (int n = 0; n < notifCount; n++)
                {
                    byte typeEnc = response[offset]; offset++;
                    if (typeEnc == 0x02)
                    {
                        ushort typeNs = ReadUInt16LE(response, ref offset);
                        uint typeId = ReadUInt32LE(response, ref offset);
                    }
                    else if (typeEnc == 0x03)
                    {
                        ushort typeNs = ReadUInt16LE(response, ref offset);
                        int typeStrLen = ReadInt32LE(response, ref offset);
                        offset += typeStrLen;
                    }
                    else
                    {
                        offset++;
                    }

                    int encodingMask = ReadInt32LE(response, ref offset);
                    int monItemCount = ReadInt32LE(response, ref offset);

                    for (int m = 0; m < monItemCount; m++)
                    {
                        uint clientHandle = ReadUInt32LE(response, ref offset);
                        byte valueEnc = response[offset]; offset++;
                        object value = null;
                        uint statusCode = 0;
                        if ((valueEnc & 0x01) != 0)
                        {
                            value = ReadVariant(response, ref offset);
                        }
                        if ((valueEnc & 0x02) != 0)
                        {
                            statusCode = ReadUInt32LE(response, ref offset);
                        }
                        if ((valueEnc & 0x04) != 0)
                        {
                            ReadDateTimeLE(response, ref offset);
                        }
                        if ((valueEnc & 0x08) != 0)
                        {
                            ReadDateTimeLE(response, ref offset);
                        }

                        string nodeId = _monitoredNodeMap.ContainsKey(clientHandle) ? _monitoredNodeMap[clientHandle] : $"Handle={clientHandle}";
                        object prevValue = null;
                        _lastValues.TryGetValue(nodeId, out prevValue);

                        if (value != null)
                        {
                            _lastValues[nodeId] = value;
                            OnDataChanged?.Invoke(this, new OpcUaDataChangeEventArgs
                            {
                                ClientHandle = clientHandle,
                                NodeId = nodeId,
                                Value = value,
                                StatusCode = statusCode,
                                Timestamp = DateTime.Now
                            });
                            _dataChangedBridge?.Invoke(this, new DataChangeEventArgs
                            {
                                Address = nodeId,
                                OldValue = prevValue,
                                NewValue = value,
                                Timestamp = DateTime.Now,
                                Quality = statusCode == 0 ? "Good" : $"0x{statusCode:X8}"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"处理 Publish 响应异常: {ex.Message}");
            }
        }

        #endregion

        #region IReadWriteDevice Implementation

        public OperateResult<bool> ReadBool(string address) => ReadTypedValue<bool>(address);
        public OperateResult<short> ReadInt16(string address) => ReadTypedValue<short>(address);
        public OperateResult<ushort> ReadUInt16(string address) => ReadTypedValue<ushort>(address);
        public OperateResult<int> ReadInt32(string address) => ReadTypedValue<int>(address);
        public OperateResult<uint> ReadUInt32(string address) => ReadTypedValue<uint>(address);
        public OperateResult<long> ReadInt64(string address) => ReadTypedValue<long>(address);
        public OperateResult<ulong> ReadUInt64(string address) => ReadTypedValue<ulong>(address);
        public OperateResult<float> ReadFloat(string address) => ReadTypedValue<float>(address);
        public OperateResult<double> ReadDouble(string address) => ReadTypedValue<double>(address);
        public OperateResult<string> ReadString(string address, ushort length) => ReadTypedValue<string>(address);
        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var result = ReadValue(address);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);
            if (result.Content is byte[] bytes) return OperateResult<byte[]>.Success(bytes);
            return OperateResult<byte[]>.Success(Encoding.UTF8.GetBytes(result.Content?.ToString() ?? ""));
        }

        private OperateResult<T> ReadTypedValue<T>(string address)
        {
            var result = ReadValue(address);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message);
            try
            {
                return OperateResult<T>.Success((T)Convert.ChangeType(result.Content, typeof(T), CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"类型转换失败: {ex.Message}");
            }
        }

        public OperateResult Write(string address, bool value) => WriteValueInternal(address, value);
        public OperateResult Write(string address, short value) => WriteValueInternal(address, value);
        public OperateResult Write(string address, ushort value) => WriteValueInternal(address, (short)value);
        public OperateResult Write(string address, int value) => WriteValueInternal(address, value);
        public OperateResult Write(string address, uint value) => WriteValueInternal(address, (int)value);
        public OperateResult Write(string address, long value) => WriteValueInternal(address, value);
        public OperateResult Write(string address, ulong value) => WriteValueInternal(address, (long)value);
        public OperateResult Write(string address, float value) => WriteValueInternal(address, value);
        public OperateResult Write(string address, double value) => WriteValueInternal(address, value);
        public OperateResult Write(string address, string value) => WriteValueInternal(address, value);
        public OperateResult Write(string address, byte[] data) => WriteValueInternal(address, data);

        private OperateResult WriteValueInternal(string address, object value)
        {
            return WriteValue(address, value);
        }

        public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        #endregion

        #region Low-Level Protocol Helpers

        private OperateResult<byte[]> SendServiceRequest(uint serviceNodeId, byte[] requestBody)
        {
            int reqHandle = _session.NextRequestHandle();
            var payload = new MemoryStream();
            var w = new BinaryWriter(payload);

            var authBytes = _session.AuthenticationToken.Encode();
            w.Write(authBytes);
            OpcUaNodeId.WriteDateTime(w, DateTime.UtcNow);
            w.Write(reqHandle);
            w.Write((uint)0);
            OpcUaNodeId.WriteString(w, null);
            w.Write((uint)Timeout);
            w.Write(requestBody, 0, requestBody.Length);

            int msgSize = 12 + (int)payload.Length;
            var msg = new byte[msgSize];
            msg[0] = 0x4D; msg[1] = 0x53; msg[2] = 0x47; msg[3] = 0x46;
            WriteInt32(msg, 4, msgSize);
            WriteInt32(msg, 8, (int)_session.SecureChannelId);
            Buffer.BlockCopy(payload.ToArray(), 0, msg, 12, (int)payload.Length);

            lock (_lock)
            {
                if (!_isConnected) return OperateResult<byte[]>.Failed("未连接");
                try
                {
                    SendMessage(msg);
                    OnMessageSent?.Invoke(this, $"MSG → Service 0x{serviceNodeId:X4}");

                    var respHeader = ReadBytes(8);
                    if (respHeader == null) return OperateResult<byte[]>.Failed("未收到响应");

                    if (respHeader[0] == 0x45 && respHeader[1] == 0x52 && respHeader[2] == 0x52)
                    {
                        int errSize = BitConverter.ToInt32(respHeader, 4);
                        ReadBytes(errSize - 8);
                        return OperateResult<byte[]>.Failed("OPC UA 错误响应");
                    }

                    if (respHeader[0] != 0x4D || respHeader[1] != 0x53 || respHeader[2] != 0x47)
                        return OperateResult<byte[]>.Failed("无效的 MSG 响应");

                    int respSize = BitConverter.ToInt32(respHeader, 4);
                    var respPayload = ReadBytes(respSize - 8);
                    if (respPayload == null) return OperateResult<byte[]>.Failed("读取响应失败");

                    if (respPayload.Length < 4) return OperateResult<byte[]>.Failed("响应过短");

                    int respOffset = 0;
                    int serviceResult = ReadInt32LE(respPayload, ref respOffset);
                    if (serviceResult != 0)
                    {
                        return OperateResult<byte[]>.Failed($"服务错误: 0x{serviceResult:X8}");
                    }

                    return OperateResult<byte[]>.Success(respPayload);
                }
                catch (Exception ex)
                {
                    Log.Error($"服务请求异常: {ex.Message}");
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult<byte[]>.Failed(ex.Message);
                }
            }
        }

        private void WriteRequestHeader(BinaryWriter w, DateTime? timestamp)
        {
            var authBytes = _session.AuthenticationToken?.Encode() ?? new byte[] { 0x00, 0x00 };
            w.Write(authBytes);
            OpcUaNodeId.WriteDateTime(w, timestamp ?? DateTime.UtcNow);
            w.Write(_session.NextRequestHandle());
            w.Write((uint)0);
            OpcUaNodeId.WriteString(w, null);
            w.Write((uint)Timeout);
        }

        private void WriteDataValue(BinaryWriter w, object value, byte encodingMask)
        {
            w.Write(encodingMask);
            WriteVariant(w, value);
        }

        private OperateResult<object> ReadDataValue(byte[] data, ref int offset)
        {
            byte encodingMask = data[offset]; offset++;
            object value = null;
            uint statusCode = 0;

            if ((encodingMask & 0x01) != 0)
            {
                value = ReadVariant(data, ref offset);
            }
            if ((encodingMask & 0x02) != 0)
            {
                statusCode = ReadUInt32LE(data, ref offset);
            }
            if ((encodingMask & 0x04) != 0)
            {
                ReadDateTimeLE(data, ref offset);
            }
            if ((encodingMask & 0x08) != 0)
            {
                ReadDateTimeLE(data, ref offset);
            }

            if (statusCode != 0 && statusCode != 0x80000000)
            {
                return OperateResult<object>.Failed($"Bad StatusCode: 0x{statusCode:X8}");
            }
            return OperateResult<object>.Success(value);
        }

        private void WriteVariant(BinaryWriter w, object value)
        {
            if (value == null) { w.Write((byte)0x00); return; }
            if (value is bool bv) { w.Write((byte)0x01); w.Write(bv); }
            else if (value is sbyte sbv) { w.Write((byte)0x02); w.Write(sbv); }
            else if (value is byte byv) { w.Write((byte)0x03); w.Write(byv); }
            else if (value is short i16v) { w.Write((byte)0x04); w.Write(i16v); }
            else if (value is ushort u16v) { w.Write((byte)0x05); w.Write(u16v); }
            else if (value is int i32v) { w.Write((byte)0x06); w.Write(i32v); }
            else if (value is uint u32v) { w.Write((byte)0x07); w.Write(u32v); }
            else if (value is long i64v) { w.Write((byte)0x08); w.Write(i64v); }
            else if (value is ulong u64v) { w.Write((byte)0x09); w.Write(u64v); }
            else if (value is float fv) { w.Write((byte)0x0A); w.Write(fv); }
            else if (value is double dv) { w.Write((byte)0x0B); w.Write(dv); }
            else if (value is string sv) { w.Write((byte)0x0C); OpcUaNodeId.WriteString(w, sv); }
            else if (value is DateTime dtv) { w.Write((byte)0x0D); OpcUaNodeId.WriteDateTime(w, dtv); }
            else if (value is Guid gv) { w.Write((byte)0x0E); w.Write(gv.ToByteArray()); }
            else if (value is byte[] bav) { w.Write((byte)0x0F); w.Write(bav.Length); w.Write(bav); }
            else { w.Write((byte)0x0C); OpcUaNodeId.WriteString(w, value.ToString()); }
        }

        private object ReadVariant(byte[] data, ref int offset)
        {
            byte encoding = data[offset]; offset++;
            if (encoding == 0x00) return null;
            byte variantType = (byte)(encoding & 0x3F);
            bool isArray = (encoding & 0x80) != 0;

            if (isArray)
            {
                int count = ReadInt32LE(data, ref offset);
                if (count < 0) return null;
                var array = new object[count];
                for (int i = 0; i < count; i++)
                    array[i] = ReadSingleVariant(data, ref offset, variantType);
                return array;
            }
            return ReadSingleVariant(data, ref offset, variantType);
        }

        private object ReadSingleVariant(byte[] data, ref int offset, byte variantType)
        {
            switch (variantType)
            {
                case 0x01: bool v = data[offset] != 0; offset++; return v;
                case 0x02: sbyte sb = (sbyte)data[offset]; offset++; return sb;
                case 0x03: byte by = data[offset]; offset++; return by;
                case 0x04: short i16 = (short)(data[offset] | (data[offset + 1] << 8)); offset += 2; return i16;
                case 0x05: ushort u16 = (ushort)(data[offset] | (data[offset + 1] << 8)); offset += 2; return u16;
                case 0x06: int i32 = ReadInt32LE(data, ref offset); return i32;
                case 0x07: uint u32 = ReadUInt32LE(data, ref offset); return u32;
                case 0x08: long i64 = ReadInt64LE(data, ref offset); return i64;
                case 0x09: ulong u64 = (ulong)ReadInt64LE(data, ref offset); return u64;
                case 0x0A: float f = BitConverter.ToSingle(data, offset); offset += 4; return f;
                case 0x0B: double d = BitConverter.ToDouble(data, offset); offset += 8; return d;
                case 0x0C: return ReadOpcString(data, ref offset);
                case 0x0D: return ReadDateTimeLE(data, ref offset);
                case 0x0E: var gBytes = new byte[16]; Buffer.BlockCopy(data, offset, gBytes, 0, 16); offset += 16; return new Guid(gBytes);
                case 0x0F: int baLen = ReadInt32LE(data, ref offset); var ba = new byte[baLen]; Buffer.BlockCopy(data, offset, ba, 0, baLen); offset += baLen; return ba;
                default: return null;
            }
        }

        private string ReadExpandedNodeId(byte[] data, ref int offset)
        {
            byte enc = data[offset]; offset++;
            bool hasNsUri = (enc & 0x80) != 0;
            bool hasServerIdx = (enc & 0x40) != 0;
            byte nodeIdType = (byte)(enc & 0x0F);

            if (hasNsUri)
            {
                int uriLen = ReadInt32LE(data, ref offset);
                offset += uriLen;
            }

            ushort ns = 0;
            if (nodeIdType >= 0x02)
            {
                ns = ReadUInt16LE(data, ref offset);
            }
            else if (nodeIdType == 0x01)
            {
                ns = data[offset]; offset++;
            }

            string id;
            switch (nodeIdType)
            {
                case 0x00:
                    id = data[offset].ToString(); offset++;
                    break;
                case 0x01:
                    id = ReadUInt16LE(data, ref offset).ToString();
                    break;
                case 0x02:
                    id = ReadUInt32LE(data, ref offset).ToString();
                    break;
                case 0x03:
                    int sLen = ReadInt32LE(data, ref offset);
                    id = Encoding.UTF8.GetString(data, offset, sLen);
                    offset += sLen;
                    break;
                case 0x04:
                    var gb = new byte[16];
                    Buffer.BlockCopy(data, offset, gb, 0, 16);
                    offset += 16;
                    id = new Guid(gb).ToString();
                    break;
                case 0x05:
                    int oLen = ReadInt32LE(data, ref offset);
                    offset += oLen;
                    id = "Opaque";
                    break;
                default:
                    id = "?";
                    break;
            }

            if (hasServerIdx)
            {
                ReadUInt32LE(data, ref offset);
            }

            return $"ns={ns};{(nodeIdType == 0x03 ? "s=" : "i=")}{id}";
        }

        private void SendMessage(byte[] msg)
        {
            lock (_lock)
            {
                if (_stream == null) throw new InvalidOperationException("未连接");
                _stream.Write(msg, 0, msg.Length);
                _stream.Flush();
            }
        }

        private byte[] ReadBytes(int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadInt32LE(byte[] data, ref int offset)
        {
            int v = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
            offset += 4;
            return v;
        }

        private static uint ReadUInt32LE(byte[] data, ref int offset)
        {
            uint v = (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            offset += 4;
            return v;
        }

        private static ushort ReadUInt16LE(byte[] data, ref int offset)
        {
            ushort v = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            return v;
        }

        private static long ReadInt64LE(byte[] data, ref int offset)
        {
            uint lo = ReadUInt32LE(data, ref offset);
            uint hi = ReadUInt32LE(data, ref offset);
            return ((long)hi << 32) | lo;
        }

        private static DateTime ReadDateTimeLE(byte[] data, ref int offset)
        {
            long ticks = ReadInt64LE(data, ref offset);
            return OpcUaNodeId.FromOpcUaTimestamp(ticks);
        }

        private static string ReadOpcString(byte[] data, ref int offset)
        {
            int len = ReadInt32LE(data, ref offset);
            if (len < 0) return null;
            if (len == 0) return string.Empty;
            string s = Encoding.UTF8.GetString(data, offset, len);
            offset += len;
            return s;
        }

        private static int ReadStringAt(byte[] data, ref int offset)
        {
            int len = ReadInt32LE(data, ref offset);
            if (len > 0) offset += len;
            return len;
        }

        private static void ReadByteStringAt(byte[] data, ref int offset)
        {
            int len = ReadInt32LE(data, ref offset);
            if (len > 0) offset += len;
        }

        private static byte[] ReadBytesAt(byte[] data, ref int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(data, offset, result, 0, count);
            offset += count;
            return result;
        }

        #endregion

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <summary>批量读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        /// <summary>随机读取多个不连续地址（返回原始字节）。</summary>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 1);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <summary>随机读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        /// <summary>批量写入多个地址的值。</summary>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));
    }

    public class OpcUaReferenceDescription
    {
        public string ReferenceTypeId { get; set; }
        public bool IsForward { get; set; }
        public string NodeId { get; set; }
        public string BrowseName { get; set; }
        public string DisplayName { get; set; }
        public int NodeClass { get; set; }
        public string TypeDefinition { get; set; }

        public override string ToString() => $"{BrowseName} [{NodeId}] ({NodeClass})";
    }
}
