using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// 串口抽象接口 — 解耦 System.IO.Ports 依赖，便于测试和跨平台。
    /// </summary>
    public interface ISerialPort : IDisposable
    {
        string PortName { get; set; }
        int BaudRate { get; set; }
        int DataBits { get; set; }
        StopBits StopBits { get; set; }
        Parity Parity { get; set; }
        int ReadTimeout { get; set; }
        int WriteTimeout { get; set; }
        bool IsOpen { get; }
        bool DtrEnable { get; set; }
        bool RtsEnable { get; set; }

        void Open();
        void Close();
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] buffer, int offset, int count);
    }

    /// <summary>
    /// 串口停止位枚举。
    /// </summary>
    public enum StopBits
    {
        None = 0,
        One = 1,
        Two = 2,
        OnePointFive = 3,
    }

    /// <summary>
    /// 串口校验位枚举。
    /// </summary>
    public enum Parity
    {
        None = 0,
        Odd = 1,
        Even = 2,
        Mark = 3,
        Space = 4,
    }

    /// <summary>
    /// 串口设备基类 — 封装串口连接管理、超时、日志、事件、自动重连。
    /// 通过 ISerialPort 抽象串口操作，不直接依赖 System.IO.Ports。
    /// </summary>
    public abstract class SerialDeviceBase : IReadWriteDevice
    {
        protected ISerialPort Port { get; }
        protected int Timeout { get; set; }
        protected ILogger Log { get; set; }
        protected readonly object _lock = new object();
        /// <summary>
        /// 异步收发互斥信号量 — 串口半双工，Write 与 Read 必须在一次完整收发期间独占端口。
        /// <b>A1 修复</b>：同步路径 (<see cref="SendAndReceive"/>) 与异步路径
        /// (<see cref="SendAndReceiveAsync"/>) 共用此 SemaphoreSlim。原实现同步用
        /// <c>lock(_lock)</c>、异步用此 SemaphoreSlim，是<b>两把不同的锁</b>，
        /// 同步+异步并发会破坏半双工。
        /// </summary>
        protected readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1, 1);
        protected volatile bool _persistentMode;
        private volatile bool _disposed;

        // ── 可配置属性 ──────────────────────────────

        /// <summary>RS485 帧间延时（毫秒），默认 50ms。</summary>
        public int InterFrameDelay { get; set; } = 50;

        /// <summary>DTR 硬件流控。</summary>
        public bool DtrEnable
        {
            get { lock (_lock) return Port.DtrEnable; }
            set { lock (_lock) Port.DtrEnable = value; }
        }

        /// <summary>RTS 硬件流控。</summary>
        public bool RtsEnable
        {
            get { lock (_lock) return Port.RtsEnable; }
            set { lock (_lock) Port.RtsEnable = value; }
        }

        // ── 事件 ──────────────────────────────────

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected
        {
            get { lock (_lock) return Port?.IsOpen == true; }
        }

        protected SerialDeviceBase(ISerialPort serialPort, int timeout = 5000)
        {
            Port = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ── 事件触发辅助方法（供子类调用）──────────

        /// <summary>触发消息已发送事件。</summary>
        protected void RaiseMessageSent(string hex) => OnMessageSent?.Invoke(this, hex);

        /// <summary>触发消息已接收事件。</summary>
        protected void RaiseMessageReceived(string hex) => OnMessageReceived?.Invoke(this, hex);

        /// <summary>触发错误事件。</summary>
        protected void RaiseError(string message) => OnError?.Invoke(this, message);

        /// <summary>触发已连接事件。</summary>
        protected void RaiseConnected() => OnConnected?.Invoke(this, EventArgs.Empty);

        /// <summary>触发已断开事件。</summary>
        protected void RaiseDisconnected() => OnDisconnected?.Invoke(this, EventArgs.Empty);

        /// <summary>启用长连接模式（串口默认保持打开，此方法与 TcpDeviceBase 对齐）。</summary>
        public void SetPersistentConnection() => _persistentMode = true;

        // ── 连接管理 ──────────────────────────────

        public virtual OperateResult Connect()
        {
            if (_disposed) return OperateResult.Failed("串口对象已释放");
            try
            {
                lock (_lock)
                {
                    DisconnectCore();
                    Port.ReadTimeout = Timeout;
                    Port.WriteTimeout = Timeout;
                    Port.Open();
                }
                Log.Info($"串口已打开 {Port.PortName} ({Port.BaudRate}/{Port.DataBits}/{Port.Parity}/{Port.StopBits})");
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"串口打开失败 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult.Failed($"串口打开失败: {ex.Message}");
            }
        }

        public virtual Task<OperateResult> ConnectAsync()
        {
            return Task.FromResult(Connect());
        }

        public void Disconnect()
        {
            lock (_lock) DisconnectCore();
        }

        protected void DisconnectCore()
        {
            bool wasOpen = Port?.IsOpen == true;
            try { Port?.Close(); } catch { }
            if (wasOpen)
            {
                Log.Info($"串口已关闭 {Port?.PortName}");
                OnDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        // ── 网络收发 ──────────────────────────────

        /// <summary>
        /// 串口收发 — 发送请求，等待指定长度响应。
        /// 失败时自动尝试重连一次。
        /// </summary>
        /// <remarks>
        /// <b>并发模型（A1 修复）</b>：同步路径用 <c>_asyncLock.Wait()</c>，异步路径用
        /// <c>_asyncLock.WaitAsync()</c>，<b>两者共享同一把 SemaphoreSlim</b>。原实现同步路径用
        /// <c>lock(_lock)</c>、异步路径用 <c>_asyncLock</c>，是<b>两把互不相干的锁</b>，导致
        /// 同步+异步并发调用时半双工串口的 Write/Read 会交错，响应被偷吃。统一到 _asyncLock 后，
        /// 所有路径（同步、异步、重连重试）串行化，符合半双工语义。<see cref="_lock"/> 仅保留给
        /// 轻量属性 (<see cref="IsConnected"/>/<see cref="DtrEnable"/>/<see cref="RtsEnable"/>) 同步。
        /// </remarks>
        protected OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            _asyncLock.Wait();
            try
            {
                return SendAndReceiveCore(request, isAsync: false, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        /// <summary>
        /// 异步发送请求并接收响应。
        /// </summary>
        protected async Task<OperateResult<byte[]>> SendAndReceiveAsync(
            byte[] request, CancellationToken cancellationToken = default)
        {
            await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await SendAndReceiveCore(request, isAsync: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _asyncLock.Release();
            }
        }

        /// <summary>
        /// 共享的收发核心 — 在调用方已持有 <c>_asyncLock</c> 的前提下执行一次完整事务。
        /// 同步/异步路径统一走这里，消除 4 处复制粘贴。
        /// </summary>
        private async Task<OperateResult<byte[]>> SendAndReceiveCore(
            byte[] request, bool isAsync, CancellationToken cancellationToken)
        {
            try
            {
                if (!Port.IsOpen) return OperateResult<byte[]>.Failed("串口未打开");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                Port.Write(request, 0, request.Length);

                if (InterFrameDelay > 0)
                {
                    if (isAsync)
                        await Task.Delay(InterFrameDelay, cancellationToken).ConfigureAwait(false);
                    else
                        Thread.Sleep(InterFrameDelay);
                }

                byte[] header = new byte[ResponseHeaderLength];
                int headerRead = isAsync
                    ? await ReadExactSerialAsync(header, 0, ResponseHeaderLength, cancellationToken).ConfigureAwait(false)
                    : ReadExactSerial(header, 0, ResponseHeaderLength);
                if (headerRead < ResponseHeaderLength)
                    return OperateResult<byte[]>.Failed("读取串口响应头失败");

                int payloadLen = GetResponsePayloadLength(header);
                byte[] payload = new byte[payloadLen];
                if (payloadLen > 0)
                {
                    int payloadRead = isAsync
                        ? await ReadExactSerialAsync(payload, 0, payloadLen, cancellationToken).ConfigureAwait(false)
                        : ReadExactSerial(payload, 0, payloadLen);
                    if (payloadRead < payloadLen)
                        return OperateResult<byte[]>.Failed("读取串口响应数据失败");
                }

                byte[] full = new byte[header.Length + payload.Length];
                Buffer.BlockCopy(header, 0, full, 0, header.Length);
                if (payload.Length > 0)
                    Buffer.BlockCopy(payload, 0, full, header.Length, payload.Length);

                Log.Debug($"RX ← {DataConverter.ToHexString(full)}");
                OnMessageReceived?.Invoke(this, DataConverter.ToHexString(full));

                return OperateResult<byte[]>.Success(full);
            }
            catch (OperationCanceledException)
            {
                return OperateResult<byte[]>.Failed("串口通讯已取消");
            }
            catch (Exception ex)
            {
                Log.Error($"串口通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);

                // 重连重试仍在已持有的 _asyncLock 内执行，保证重试期间端口独占。
                var retry = isAsync
                    ? await TryReconnectAndRetryAsync(request, cancellationToken).ConfigureAwait(false)
                    : TryReconnectAndRetry(request);
                if (retry != null) return retry;

                return OperateResult<byte[]>.Failed($"串口通讯异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步精确读取指定长度的字节，使用 CancellationToken 替代 busy-waiting。
        /// </summary>
        private async Task<int> ReadExactSerialAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;
            using var timeoutToken = new CancellationTokenSource(Timeout);
            using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutToken.Token);

            while (totalRead < count)
            {
                try
                {
                    // Note: ISerialPort currently only exposes synchronous Read. 
                    // We wrap only the Read call in Task.Run to avoid blocking the caller, 
                    // which is significantly better than wrapping the entire transaction.
                    int read = await Task.Run(() => Port.Read(buffer, offset + totalRead, count - totalRead), linkedToken.Token).ConfigureAwait(false);
                    if (read == 0) return totalRead;
                    totalRead += read;
                }
                catch (OperationCanceledException)
                {
                    return totalRead;
                }
                catch (TimeoutException)
                {
                    return totalRead;
                }
            }
            return totalRead;
        }

        /// <summary>
        /// 异步重连并重试发送请求（调用方已持有 <c>_asyncLock</c>）。
        /// </summary>
        private async Task<OperateResult<byte[]>?> TryReconnectAndRetryAsync(byte[] request, CancellationToken cancellationToken)
        {
            if (!await ReconnectAsync().ConfigureAwait(false)) return null;
            // 重连成功后，复用 SendAndReceiveCore 完成事务（仍在 _asyncLock 内）。
            return await SendAndReceiveCore(request, isAsync: true, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>发送原始报文并接收响应（自定义功能码场景）。</summary>
        public OperateResult<byte[]> SendCustomMessage(byte[] request)
            => SendAndReceive(request);

        /// <summary>异步发送原始报文并接收响应。</summary>
        public Task<OperateResult<byte[]>> SendCustomMessageAsync(
            byte[] request, CancellationToken cancellationToken = default)
            => SendAndReceiveAsync(request, cancellationToken);

        /// <summary>同步重连并重试（调用方已持有 <c>_asyncLock</c>）。</summary>
        private OperateResult<byte[]>? TryReconnectAndRetry(byte[] request)
        {
            if (!ReconnectSync()) return null;
            return SendAndReceiveCore(request, isAsync: false, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>重连核心逻辑（同步版）。返回 true 表示重连成功可继续重试。</summary>
        private bool ReconnectSync()
        {
            try
            {
                Log.Warn("尝试重连串口…");
                DisconnectCore();
                Port.ReadTimeout = Timeout;
                Port.WriteTimeout = Timeout;
                Port.Open();
                Log.Info($"串口重连成功 {Port.PortName}");
                OnConnected?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception retryEx)
            {
                Log.Error($"重连重试失败 — {retryEx.Message}");
                OnError?.Invoke(this, retryEx.Message);
                return false;
            }
        }

        /// <summary>重连核心逻辑（异步版，语义与同步版一致）。</summary>
        private Task<bool> ReconnectAsync()
        {
            // ISerialPort.Open() 本身是同步阻塞操作，且不提供异步 API；
            // 串口打开通常很快（< 100ms），不值得为此引入 Task.Run。
            try
            {
                return Task.FromResult(ReconnectSync());
            }
            catch (Exception retryEx)
            {
                Log.Error($"重连重试失败 — {retryEx.Message}");
                OnError?.Invoke(this, retryEx.Message);
                return Task.FromResult(false);
            }
        }

        private int ReadExactSerial(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            // 用 unchecked 差值比较避免 Environment.TickCount 在连续运行约 24.8 天后 int 溢出导致超时失效。
            // netstandard2.0 无 TickCount64；unchecked(TickCount - start) 在回绕时仍给出正确有符号差值。
            int start = Environment.TickCount;
            while (totalRead < count)
            {
                if (unchecked(Environment.TickCount - start) > Timeout)
                    return totalRead;

                try
                {
                    int read = Port.Read(buffer, offset + totalRead, count - totalRead);
                    if (read == 0) return totalRead;
                    totalRead += read;
                }
                catch (TimeoutException)
                {
                    return totalRead;
                }
            }
            return totalRead;
        }

        /// <summary>子类实现：响应头长度。</summary>
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
                // 先标记 disposed，再尝试持有 _asyncLock 以确保没有正在进行的 IO；
                // 若等不到锁（持锁方正在执行），最长等 5 秒后强制继续释放。
                try
                {
                    if (!_asyncLock.Wait(TimeSpan.FromSeconds(5)))
                        Log.Warn("Dispose: 5 秒内未能取得串口 IO 锁，强制释放");
                }
                catch (ObjectDisposedException) { }

                Disconnect();

                // _asyncLock 自身是 IDisposable — 显式释放避免 SemaphoreSlim 内部资源泄漏。
                try { _asyncLock.Dispose(); } catch (ObjectDisposedException) { }
            }
        }
    }
}
