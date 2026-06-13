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
        protected bool _persistentMode;

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
        protected OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                lock (_lock)
                {
                    if (!Port.IsOpen) return OperateResult<byte[]>.Failed("串口未打开");

                    Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                    OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                    Port.Write(request, 0, request.Length);

                    if (InterFrameDelay > 0)
                        Thread.Sleep(InterFrameDelay);

                    // 读取响应头
                    byte[] header = new byte[ResponseHeaderLength];
                    int headerRead = ReadExactSerial(header, 0, ResponseHeaderLength);
                    if (headerRead < ResponseHeaderLength)
                        return OperateResult<byte[]>.Failed("读取串口响应头失败");

                    int payloadLen = GetResponsePayloadLength(header);
                    byte[] payload = new byte[payloadLen];
                    if (payloadLen > 0)
                    {
                        int payloadRead = ReadExactSerial(payload, 0, payloadLen);
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
            }
            catch (Exception ex)
            {
                Log.Error($"串口通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);

                var retry = TryReconnectAndRetry(request);
                if (retry != null) return retry;

                return OperateResult<byte[]>.Failed($"串口通讯异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步发送请求并接收响应。
        /// </summary>
        protected async Task<OperateResult<byte[]>> SendAndReceiveAsync(
            byte[] request, CancellationToken cancellationToken = default)
        {
            try
            {
                lock (_lock)
                {
                    if (!Port.IsOpen) return OperateResult<byte[]>.Failed("串口未打开");

                    Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                    OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                    Port.Write(request, 0, request.Length);
                }

                if (InterFrameDelay > 0)
                    await Task.Delay(InterFrameDelay, cancellationToken).ConfigureAwait(false);

                byte[] header = new byte[ResponseHeaderLength];
                int headerRead = await ReadExactSerialAsync(header, 0, ResponseHeaderLength, cancellationToken).ConfigureAwait(false);
                if (headerRead < ResponseHeaderLength)
                    return OperateResult<byte[]>.Failed("读取串口响应头失败");

                int payloadLen = GetResponsePayloadLength(header);
                byte[] payload = new byte[payloadLen];
                if (payloadLen > 0)
                {
                    int payloadRead = await ReadExactSerialAsync(payload, 0, payloadLen, cancellationToken).ConfigureAwait(false);
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

                var retry = await TryReconnectAndRetryAsync(request, cancellationToken).ConfigureAwait(false);
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
        /// 异步重连并重试发送请求。
        /// </summary>
        private async Task<OperateResult<byte[]>?> TryReconnectAndRetryAsync(byte[] request, CancellationToken cancellationToken)
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

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                Port.Write(request, 0, request.Length);

                if (InterFrameDelay > 0)
                    await Task.Delay(InterFrameDelay, cancellationToken).ConfigureAwait(false);

                byte[] header = new byte[ResponseHeaderLength];
                int headerRead = await ReadExactSerialAsync(header, 0, ResponseHeaderLength, cancellationToken).ConfigureAwait(false);
                if (headerRead < ResponseHeaderLength)
                    return OperateResult<byte[]>.Failed("重连后读取响应头失败");

                int payloadLen = GetResponsePayloadLength(header);
                byte[] payload = new byte[payloadLen];
                if (payloadLen > 0)
                {
                    int payloadRead = await ReadExactSerialAsync(payload, 0, payloadLen, cancellationToken).ConfigureAwait(false);
                    if (payloadRead < payloadLen)
                        return OperateResult<byte[]>.Failed("重连后读取响应数据失败");
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
                return null;
            }
            catch (Exception retryEx)
            {
                Log.Error($"重连重试失败 — {retryEx.Message}");
                OnError?.Invoke(this, retryEx.Message);
                return null;
            }
        }

        /// <summary>发送原始报文并接收响应（自定义功能码场景）。</summary>
        public OperateResult<byte[]> SendCustomMessage(byte[] request)
            => SendAndReceive(request);

        /// <summary>异步发送原始报文并接收响应。</summary>
        public Task<OperateResult<byte[]>> SendCustomMessageAsync(
            byte[] request, CancellationToken cancellationToken = default)
            => SendAndReceiveAsync(request, cancellationToken);

        private OperateResult<byte[]>? TryReconnectAndRetry(byte[] request)
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

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                Port.Write(request, 0, request.Length);

                if (InterFrameDelay > 0)
                    Thread.Sleep(InterFrameDelay);

                byte[] header = new byte[ResponseHeaderLength];
                int headerRead = ReadExactSerial(header, 0, ResponseHeaderLength);
                if (headerRead < ResponseHeaderLength)
                    return OperateResult<byte[]>.Failed("重连后读取响应头失败");

                int payloadLen = GetResponsePayloadLength(header);
                byte[] payload = new byte[payloadLen];
                if (payloadLen > 0)
                {
                    int payloadRead = ReadExactSerial(payload, 0, payloadLen);
                    if (payloadRead < payloadLen)
                        return OperateResult<byte[]>.Failed("重连后读取响应数据失败");
                }

                byte[] full = new byte[header.Length + payload.Length];
                Buffer.BlockCopy(header, 0, full, 0, header.Length);
                if (payload.Length > 0)
                    Buffer.BlockCopy(payload, 0, full, header.Length, payload.Length);

                Log.Debug($"RX ← {DataConverter.ToHexString(full)}");
                OnMessageReceived?.Invoke(this, DataConverter.ToHexString(full));

                return OperateResult<byte[]>.Success(full);
            }
            catch (Exception retryEx)
            {
                Log.Error($"重连重试失败 — {retryEx.Message}");
                OnError?.Invoke(this, retryEx.Message);
                return null;
            }
        }

        private int ReadExactSerial(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            int deadline = Environment.TickCount + Timeout;
            while (totalRead < count)
            {
                if (Environment.TickCount > deadline)
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
        public virtual Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public virtual Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (disposing) Disconnect(); }
    }
}
