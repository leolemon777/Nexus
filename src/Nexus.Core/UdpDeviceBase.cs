using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Nexus
{
    /// <summary>
    /// UDP 设备基类 — 封装无连接 UDP 通讯、超时、网络异常处理、日志、事件、广播支持。
    /// </summary>
    public abstract class UdpDeviceBase : IReadWriteDevice
    {
        protected string Ip { get; }
        protected int Port { get; }
        protected int Timeout { get; set; }
        protected ILogger Log { get; set; }

        private UdpClient? _client;
        protected readonly object _lock = new object();
        /// <summary>
        /// 收发互斥信号量 — A3 修复:UDP 虽然是无连接的,但同一 UdpClient 实例的 Send+Receive
        /// 必须串行化,否则两个并发 SendAndReceive 会把对方响应错配到自己的请求上。
        /// 同步路径用 <see cref="SemaphoreSlim.Wait()"/>,异步路径用 <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>,
        /// 两者共用此 SemaphoreSlim(避免 SerialDeviceBase 曾经的双锁不一致 bug)。
        /// </summary>
        protected readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1, 1);
        private bool _connected;
        private volatile bool _disposed;

        // ── 事件 ──────────────────────────────────

        /// <summary>Socket 创建成功事件。</summary>
        public event EventHandler? OnConnected;

        /// <summary>Socket 关闭事件。</summary>
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
                    return _client != null && _connected;
            }
        }

        protected UdpDeviceBase(string ip, int port, int timeout = 5000)
        {
            Ip = ip ?? throw new ArgumentNullException(nameof(ip));
            Port = port;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        /// <summary>注入日志记录器。</summary>
        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ── 连接管理 ──────────────────────────────

        public virtual OperateResult Connect()
        {
            if (_disposed) return OperateResult.Failed("UDP 设备已释放");
            try
            {
                lock (_lock)
                {
                    DisconnectCore();
                    _client = new UdpClient();
                    _client.Client.SendTimeout = Timeout;
                    _client.Client.ReceiveTimeout = Timeout;
                    _client.Client.EnableBroadcast = true;
                    _client.Connect(Ip, Port);
                    _connected = true;
                }
                Log.Info($"UDP 已创建 {Ip}:{Port}");
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"UDP 创建失败 {Ip}:{Port} — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult.Failed($"UDP 创建失败: {ex.Message}");
            }
        }

        public virtual Task<OperateResult> ConnectAsync()
            => ConnectAsync(CancellationToken.None);

        public virtual Task<OperateResult> ConnectAsync(CancellationToken cancellationToken)
        {
            // UdpClient.Connect 只是设置默认远端,不真发包(无连接),所以同步执行即可。
            // 不再使用 Task.Run 包装(避免线程池盗窃)。
            if (_disposed) return Task.FromResult(OperateResult.Failed("UDP 设备已释放"));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Connect());
        }

        public void Disconnect()
        {
            lock (_lock) DisconnectCore();
        }

        protected void DisconnectCore()
        {
            bool wasConnected = _client != null && _connected;
            _client?.Close();
            _client?.Dispose();
            _client = null;
            _connected = false;
            if (wasConnected)
            {
                Log.Info($"UDP 已关闭 {Ip}:{Port}");
                OnDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        // ── 事件触发器 ───────────────────────────

        /// <summary>触发原始报文发送事件。</summary>
        protected void RaiseMessageSent(string hex) => OnMessageSent?.Invoke(this, hex);

        /// <summary>触发原始报文接收事件。</summary>
        protected void RaiseMessageReceived(string hex) => OnMessageReceived?.Invoke(this, hex);

        /// <summary>触发通讯错误事件。</summary>
        protected void RaiseError(string message) => OnError?.Invoke(this, message);

        // ── UDP 收发 ──────────────────────────────

        protected OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            // A3 修复:整个 Send+Receive 必须在 _asyncLock 内串行执行。
            // 原实现:lock(_lock) 内只取 client 引用就释放,Send/Receive 在锁外。
            // 两个并发 SendAndReceive 都拿到同一 client,各自 Send 后 Receive 任意一方响应,
            // 导致响应错配到错误的请求(UDP 无序、无连接,出错尤其严重)。
            _asyncLock.Wait();
            try
            {
                UdpClient? client;
                lock (_lock)
                {
                    if (_client == null || !_connected)
                    {
                        var conn = Connect();
                        if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                    }
                    client = _client;
                }

                if (client == null) return OperateResult<byte[]>.Failed("UDP 未创建");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                client.Send(request, request.Length);

                var remoteEP = new IPEndPoint(IPAddress.Any, 0);
                byte[] response = client.Receive(ref remoteEP);

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                OnMessageReceived?.Invoke(this, DataConverter.ToHexString(response));

                return OperateResult<byte[]>.Success(response);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                Log.Error($"UDP 接收超时 — {Ip}:{Port}");
                OnError?.Invoke(this, $"接收超时: {ex.Message}");
                return OperateResult<byte[]>.Failed($"接收超时: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"UDP 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
            finally
            {
                // Release 必须放在 finally:catch 块提前 return 后仍要释放锁。
                // 若 Release 抛 ObjectDisposedException(Dispose 已销毁信号量),吞掉避免掩盖业务异常。
                try { _asyncLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>异步发送请求并接收响应。</summary>
        protected async Task<OperateResult<byte[]>> SendAndReceiveAsync(
            byte[] request, CancellationToken cancellationToken = default)
        {
            // A3 修复:与同步路径同样的并发保护,使用 _asyncLock.WaitAsync。
            await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                UdpClient? client;
                lock (_lock)
                {
                    if (_client == null || !_connected)
                    {
                        var conn = Connect();
                        if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                    }
                    client = _client;
                }

                if (client == null) return OperateResult<byte[]>.Failed("UDP 未创建");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                await client.SendAsync(request, request.Length).ConfigureAwait(false);

                var receiveTask = client.ReceiveAsync();
                var timeoutTask = Task.Delay(Timeout, cancellationToken);
                var completed = await Task.WhenAny(receiveTask, timeoutTask).ConfigureAwait(false);

                if (completed != receiveTask)
                {
                    // 超时后孤儿 receiveTask 仍在后台运行,持有旧 UdpClient 并可能偷吃下一个响应包。
                    // 重建 socket(Close 旧 client 让 receiveTask 终止),下次收发会重新连接。
                    // A3: 现已持 _asyncLock,只需 DisconnectCore 清理 socket 即可。
                    Log.Error($"UDP 接收超时 — {Ip}:{Port}");
                    OnError?.Invoke(this, "接收超时");
                    lock (_lock) DisconnectCore();
                    return OperateResult<byte[]>.Failed("接收超时");
                }

                var result = await receiveTask.ConfigureAwait(false);
                byte[] response = result.Buffer;

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                OnMessageReceived?.Invoke(this, DataConverter.ToHexString(response));

                return OperateResult<byte[]>.Success(response);
            }
            catch (OperationCanceledException)
            {
                return OperateResult<byte[]>.Failed("操作已取消");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                Log.Error($"UDP 接收超时 — {Ip}:{Port}");
                OnError?.Invoke(this, $"接收超时: {ex.Message}");
                return OperateResult<byte[]>.Failed($"接收超时: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"UDP 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
            finally
            {
                try { _asyncLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>发送原始报文并接收响应（自定义功能码场景）。</summary>
        public OperateResult<byte[]> SendCustomMessage(byte[] request)
            => SendAndReceive(request);

        /// <summary>异步发送自定义报文并接收响应。</summary>
        public Task<OperateResult<byte[]>> SendCustomMessageAsync(byte[] request, CancellationToken cancellationToken = default)
            => SendAndReceiveAsync(request, cancellationToken);

        // ── 广播支持 ──────────────────────────────

        /// <summary>
        /// 发送广播报文并接收第一个响应。
        /// </summary>
        /// <param name="request">广播请求报文。</param>
        /// <param name="broadcastIp">广播地址，如 255.255.255.255 或子网广播地址。</param>
        protected OperateResult<byte[]> SendBroadcast(byte[] request, string broadcastIp)
        {
            UdpClient? broadcastClient = null;
            try
            {
                var broadcastEP = new IPEndPoint(IPAddress.Parse(broadcastIp), Port);

                broadcastClient = new UdpClient();
                broadcastClient.Client.SendTimeout = Timeout;
                broadcastClient.Client.ReceiveTimeout = Timeout;
                broadcastClient.Client.EnableBroadcast = true;
                broadcastClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

                Log.Debug($"TX → [BROADCAST {broadcastIp}] {DataConverter.ToHexString(request)}");
                OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                broadcastClient.Send(request, request.Length, broadcastEP);

                var remoteEP = new IPEndPoint(IPAddress.Any, 0);
                byte[] response = broadcastClient.Receive(ref remoteEP);

                Log.Debug($"RX ← [{remoteEP}] {DataConverter.ToHexString(response)}");
                OnMessageReceived?.Invoke(this, DataConverter.ToHexString(response));

                return OperateResult<byte[]>.Success(response);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                Log.Error($"UDP 广播接收超时 — {broadcastIp}:{Port}");
                OnError?.Invoke(this, $"广播接收超时: {ex.Message}");
                return OperateResult<byte[]>.Failed($"广播接收超时: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"UDP 广播异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<byte[]>.Failed($"广播异常: {ex.Message}");
            }
            finally
            {
                broadcastClient?.Close();
                broadcastClient?.Dispose();
            }
        }

        /// <summary>子类实现：响应头长度（用于验证/解析）。</summary>
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
        public virtual Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                // A3 修复:Dispose 时先尝试取得 _asyncLock 等待进行中的 IO 结束(最多 5 秒),
                // 再 Disconnect,最后释放 SemaphoreSlim 自身。
                try
                {
                    if (!_asyncLock.Wait(TimeSpan.FromSeconds(5)))
                        Log.Warn("Dispose: 5 秒内未能取得 UDP IO 锁,强制释放");
                }
                catch (ObjectDisposedException) { }

                Disconnect();

                try { _asyncLock.Dispose(); } catch (ObjectDisposedException) { }
            }
        }
    }
}
