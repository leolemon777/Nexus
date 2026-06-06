using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// TCP 设备基类 — 封装短连接/长连接管理、超时、网络异常处理、日志、事件。
    /// </summary>
    public abstract class TcpDeviceBase : IReadWriteDevice
    {
        protected string Ip { get; }
        protected int Port { get; }
        protected int Timeout { get; set; }
        protected ILogger Log { get; set; }

        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly object _lock = new object();
        private bool _persistentMode;

        // ── 事件 ──────────────────────────────────

        /// <summary>连接成功事件。</summary>
        public event EventHandler? OnConnected;

        /// <summary>连接断开事件。</summary>
        public event EventHandler? OnDisconnected;

        /// <summary>通讯错误事件。</summary>
        public event EventHandler<string>? OnError;

        /// <summary>原始报文发送事件（十六进制字符串）。</summary>
        public event EventHandler<string>? OnMessageSent;

        /// <summary>原始报文接收事件（十六进制字符串）。</summary>
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                    return _client?.Connected == true &&
                           (_client.Client?.Poll(0, SelectMode.SelectRead) != true ||
                            _client.Available > 0);
            }
        }

        protected TcpDeviceBase(string ip, int port, int timeout = 5000)
        {
            Ip = ip ?? throw new ArgumentNullException(nameof(ip));
            Port = port;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        /// <summary>注入日志记录器。</summary>
        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        /// <summary>启用长连接模式（不会在每次操作后断开）。</summary>
        public void SetPersistentConnection() => _persistentMode = true;

        // ── 连接管理 ──────────────────────────────

        public virtual OperateResult Connect()
        {
            try
            {
                lock (_lock)
                {
                    DisconnectCore();
                    _client = new TcpClient { SendTimeout = Timeout, ReceiveTimeout = Timeout };
                    var result = _client.BeginConnect(Ip, Port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(Timeout, true))
                    {
                        DisconnectCore();
                        return OperateResult.Failed($"连接超时: {Ip}:{Port} ({Timeout}ms)");
                    }
                    _client.EndConnect(result);
                    _stream = _client.GetStream();
                    _stream.ReadTimeout = Timeout;
                    _stream.WriteTimeout = Timeout;
                }
                Log.Info($"已连接 {Ip}:{Port}");
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"连接失败 {Ip}:{Port} — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
        }

        public virtual async Task<OperateResult> ConnectAsync()
        {
            try
            {
                lock (_lock) DisconnectCore();
                _client = new TcpClient { SendTimeout = Timeout, ReceiveTimeout = Timeout };
                await _client.ConnectAsync(Ip, Port).ConfigureAwait(false);
                lock (_lock) { _stream = _client.GetStream(); }
                Log.Info($"已连接 {Ip}:{Port}");
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"连接失败 {Ip}:{Port} — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            lock (_lock) DisconnectCore();
        }

        private void DisconnectCore()
        {
            bool wasConnected = _client?.Connected == true;
            _stream?.Close();
            _stream = null;
            _client?.Close();
            _client = null;
            if (wasConnected)
            {
                Log.Info($"已断开 {Ip}:{Port}");
                OnDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        // ── 网络收发 ──────────────────────────────

        protected OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                bool wasConnected;
                lock (_lock) { wasConnected = IsConnected; }

                if (!wasConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                var headerBuf = ReadExact(ns, ResponseHeaderLength);
                if (headerBuf == null) return OperateResult<byte[]>.Failed("读取响应头失败");

                int payloadLen = GetResponsePayloadLength(headerBuf);
                byte[] payload = payloadLen > 0 ? ReadExact(ns, payloadLen) ?? new byte[0] : new byte[0];

                byte[] full = new byte[headerBuf.Length + payload.Length];
                Buffer.BlockCopy(headerBuf, 0, full, 0, headerBuf.Length);
                Buffer.BlockCopy(payload, 0, full, headerBuf.Length, payload.Length);

                Log.Debug($"RX ← {DataConverter.ToHexString(full)}");
                OnMessageReceived?.Invoke(this, DataConverter.ToHexString(full));

                if (!_persistentMode) lock (_lock) DisconnectCore();

                return OperateResult<byte[]>.Success(full);
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        /// <summary>发送原始报文并接收响应（自定义功能码场景）。</summary>
        public OperateResult<byte[]> SendCustomMessage(byte[] request)
            => SendAndReceive(request);

        private byte[]? ReadExact(NetworkStream ns, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = ns.Read(buf, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        /// <summary>子类实现：响应头长度（用于分包读取）。</summary>
        protected abstract int ResponseHeaderLength { get; }

        /// <summary>子类实现：从响应头解析后续载荷长度。</summary>
        protected abstract int GetResponsePayloadLength(byte[] header);

        // ── IReadWriteDevice 占位（子类 override）────────

        public virtual OperateResult<bool> ReadBool(string address) => throw new NotImplementedException();
        public virtual OperateResult<short> ReadInt16(string address) => throw new NotImplementedException();
        public virtual OperateResult<ushort> ReadUInt16(string address) => throw new NotImplementedException();
        public virtual OperateResult<int> ReadInt32(string address) => throw new NotImplementedException();
        public virtual OperateResult<uint> ReadUInt32(string address) => throw new NotImplementedException();
        public virtual OperateResult<long> ReadInt64(string address) => throw new NotImplementedException();
        public virtual OperateResult<ulong> ReadUInt64(string address) => throw new NotImplementedException();
        public virtual OperateResult<float> ReadFloat(string address) => throw new NotImplementedException();
        public virtual OperateResult<double> ReadDouble(string address) => throw new NotImplementedException();
        public virtual OperateResult<string> ReadString(string address, ushort length) => throw new NotImplementedException();
        public virtual OperateResult<byte[]> ReadBytes(string address, ushort length) => throw new NotImplementedException();

        public virtual OperateResult Write(string address, bool value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, short value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, ushort value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, int value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, uint value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, long value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, ulong value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, float value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, double value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, string value) => throw new NotImplementedException();
        public virtual OperateResult Write(string address, byte[] data) => throw new NotImplementedException();

        // Async 默认实现
        public virtual Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public virtual Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public virtual Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public virtual Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public virtual Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public virtual Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public virtual Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public virtual Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public virtual Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public virtual Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public virtual Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));

        public virtual Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (disposing) Disconnect(); }
    }
}
