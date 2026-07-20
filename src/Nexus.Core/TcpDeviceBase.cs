using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Nexus
{
    /// <summary>
    /// TCP 设备基类 — 封装短连接/长连接管理、超时、网络异常处理、日志、事件。
    /// 提供 SemaphoreSlim（_asyncLock）用于 async-safe 互斥；保留 _lock 供子类向后兼容。
    /// </summary>
    /// <remarks>
    /// <b>B8: 已过时</b>。新代码请使用 <c>Nexus.Device.DeviceTcpNet</c>(组合 <c>PipeTcpNet</c> +
    /// <c>INetMessage</c> + <c>IByteTransform</c>)。本类保留是为了渐进迁移现有协议子类,
    /// 计划在所有协议迁移完成后删除。详见 Phase B 重构说明。
    /// </remarks>
    [Obsolete("新代码请使用 Nexus.Device.DeviceTcpNet(Phase B 重构)。本类保留仅为渐进迁移。")]
    public abstract class TcpDeviceBase : IReadWriteDevice
    {
        protected string Ip { get; }
        protected int Port { get; }
        protected int Timeout { get; set; }
        protected ILogger Log { get; set; }

        private TcpClient? _client;
        protected NetworkStream? _stream;

        /// <summary>同步锁 — 保留供子类使用（如 lock(_lock)）。TcpDeviceBase 自身方法已改用 _asyncLock。</summary>
        protected readonly object _lock = new object();

        /// <summary>异步互斥信号量 — 支持 async/await 的互斥操作，TcpDeviceBase 内部使用。</summary>
        protected readonly SemaphoreSlim _asyncLock = new SemaphoreSlim(1, 1);

        protected volatile bool _persistentMode;
        private bool _disposed;
        private AutoReconnectGuard? _autoReconnectGuard;
        private HeartbeatGuard? _heartbeatGuard;
        private Func<Task<OperateResult>>? _heartbeatCallback;
        private bool _autoReconnect;
        private bool _heartbeatEnabled;

        // ── 事件 ──────────────────────────────────

        /// <summary>连接成功事件。</summary>
        public event EventHandler? OnConnected;

        /// <summary>连接断开事件。</summary>
        public event EventHandler? OnDisconnected;

        /// <summary>通讯错误事件。</summary>
        public event EventHandler<string>? OnError;

        /// <summary>正在尝试自动重连。</summary>
        public event Action<int>? OnReconnecting;

        /// <summary>自动重连成功。</summary>
        public event Action? OnReconnected;

        /// <summary>自动重连最终失败。</summary>
        public event Action<string>? OnReconnectFailed;

        /// <summary>原始报文发送事件（十六进制字符串）。</summary>
        public event EventHandler<string>? OnMessageSent;

        /// <summary>原始报文接收事件（十六进制字符串）。</summary>
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected
        {
            get
            {
                // 仅检查 TCP 层连接状态（M1 修复）。原 Poll(0) 探测对刚收完数据、
                // 缓冲区空的健康连接会误判为断开，导致连接池复用时误触发重连、
                // 第二次操作连接超时。对端正常关闭由下次收发失败处理（OperateResult 模式）。
                var client = _client;
                return client?.Connected == true;
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

        /// <summary>
        /// 启用自动重连守护。自动重连只在长连接模式下响应断开事件，
        /// 避免短连接请求完成后的正常断开触发重连。
        /// </summary>
        public bool AutoReconnect
        {
            get => _autoReconnect;
            set
            {
                if (_autoReconnect == value) return;
                _autoReconnect = value;
                if (value) StartAutoReconnectGuard();
                else StopAutoReconnectGuard();
            }
        }

        /// <summary>自动重连基础间隔（毫秒，默认 1000ms）。</summary>
        public int ReconnectInterval { get; set; } = 1000;

        /// <summary>自动重连最大间隔（毫秒，默认 30000ms）。</summary>
        public int MaxReconnectInterval { get; set; } = 30000;

        /// <summary>最大重连次数（默认 10 次，0 = 无限重试）。</summary>
        public int MaxReconnectAttempts { get; set; } = 10;

        /// <summary>重连退避倍数（默认 2.0）。</summary>
        public double ReconnectBackoffMultiplier { get; set; } = 2.0;

        /// <summary>启用心跳守护。默认心跳使用 <see cref="BuildHeartbeat"/> 构造原始报文。</summary>
        public bool HeartbeatEnabled
        {
            get => _heartbeatEnabled;
            set
            {
                if (_heartbeatEnabled == value) return;
                _heartbeatEnabled = value;
                if (value) StartHeartbeatGuard();
                else StopHeartbeatGuard();
            }
        }

        /// <summary>心跳间隔（毫秒，默认 30000ms）。</summary>
        public int HeartbeatInterval { get; set; } = 30000;

        /// <summary>单次心跳超时（毫秒，默认 5000ms）。</summary>
        public int HeartbeatTimeout { get; set; } = 5000;

        /// <summary>最大连续心跳失败次数（默认 3 次）。</summary>
        public int MaxHeartbeatFailures { get; set; } = 3;

        /// <summary>设置自定义心跳回调，适用于不方便用原始报文表达的协议。</summary>
        public void SetHeartbeatCallback(Func<Task<OperateResult>> heartbeatCallback)
        {
            _heartbeatCallback = heartbeatCallback ?? throw new ArgumentNullException(nameof(heartbeatCallback));
            if (_heartbeatEnabled)
            {
                StopHeartbeatGuard();
                StartHeartbeatGuard();
            }
        }

        /// <summary>构造心跳原始报文。子类可重写；默认表示未配置心跳。</summary>
        protected virtual byte[]? BuildHeartbeat() => null;

        // ── IO 互斥锁钩子 ──────────────────────────
        // 设计意图：SendAndReceive/SendAndReceiveAsync 必须在一次完整收发（Write+Read）期间
        // 独占 _stream，否则长连接多线程并发会报文串台。基类默认用 _asyncLock（SemaphoreSlim）
        // 串行化整段 IO；子类若用 new 隐藏 SendAndReceive 自行实现收发，也应使用同一把锁
        // （直接调本钩子），保证 base 路径与子类路径走同一临界区。详见 C1/C5 修复。
        // 注意：此锁不得与 Connect/Disconnect 的 _asyncLock 持有嵌套——两者关注点不同
        // （连接生命周期 vs 单次收发），且 SemaphoreSlim 不可重入。
        protected virtual void AcquireIoLock() => _asyncLock.Wait();
        protected virtual void ReleaseIoLock() => _asyncLock.Release();
        protected virtual Task AcquireIoLockAsync(CancellationToken cancellationToken)
            => _asyncLock.WaitAsync(cancellationToken);
        protected virtual void ReleaseIoLockAsync() => _asyncLock.Release();

        // ── 连接管理 ──────────────────────────────

        public virtual OperateResult Connect()
        {
            try
            {
                _asyncLock.Wait();
                try
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
                finally
                {
                    _asyncLock.Release();
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

        public virtual Task<OperateResult> ConnectAsync()
            => ConnectAsync(CancellationToken.None);

        public virtual async Task<OperateResult> ConnectAsync(CancellationToken ct)
        {
            // A2 修复：整个 Connect 流程在 _asyncLock 内完成。
            // 原实现分段持锁（先 DisconnectCore 释放、再裸创建 _client 调 ConnectAsync、再取锁赋 _stream），
            // 中间窗口里并发 SendAndReceive 会读到 _client != null 但 _stream == null 的半初始化状态，
            // 进入临界区后 ns=null 走 "连接已断开" 路径，或更糟地 _client 被另一个 Connect 重置。
            // SemaphoreSlim 支持持锁期间 await，所以一次性持锁即可，无需分段释放。
            try
            {
                await _asyncLock.WaitAsync(ct).ConfigureAwait(false);
                bool connected = false;
                try
                {
                    DisconnectCore();

                    _client = new TcpClient { SendTimeout = Timeout, ReceiveTimeout = Timeout };
                    // ct 取消时关闭 _client 以解除 await _client.ConnectAsync 的阻塞。
                    using (ct.Register(() => { try { _client?.Close(); } catch { } }))
                    {
                        await _client.ConnectAsync(Ip, Port).ConfigureAwait(false);
                    }
                    ct.ThrowIfCancellationRequested();

                    _stream = _client.GetStream();
                    _stream.ReadTimeout = Timeout;
                    _stream.WriteTimeout = Timeout;
                    connected = true;
                }
                finally
                {
                    // 若中途异常（含取消）—— _client/_stream 已被 DisconnectCore 兜底清理；
                    // 成功路径下 finally 仅释放锁，不影响赋值结果。
                    if (!connected) DisconnectCore();
                    _asyncLock.Release();
                }

                Log.Info($"已连接 {Ip}:{Port}");
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (OperationCanceledException)
            {
                return OperateResult.Failed($"连接已取消: {Ip}:{Port}");
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
            _asyncLock.Wait();
            try { DisconnectCore(); }
            finally { _asyncLock.Release(); }
        }

        // ── 重试配置 ──────────────────────────────

        /// <summary>通讯重试次数（默认 0 = 不重试）。</summary>
        public int RetryCount { get; set; }

        /// <summary>重试间隔（毫秒，默认 1000ms）。</summary>
        public int RetryInterval { get; set; } = 1000;

        protected void DisconnectCore(bool forceDisconnectedEvent = false)  // accessible to subclasses (Siemens S7 needs to call it under lock from its own SendAndReceive override)
        {
            bool wasConnected = _client?.Connected == true;
            _stream?.Close();
            _stream = null;
            _client?.Close();
            _client = null;
            if (wasConnected || forceDisconnectedEvent)
            {
                Log.Info($"已断开 {Ip}:{Port}");
                OnDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private void StartAutoReconnectGuard()
        {
            StopAutoReconnectGuard();
            var guard = new AutoReconnectGuard(this, Log, () => _autoReconnect && _persistentMode)
            {
                MaxRetries = MaxReconnectAttempts,
                BaseDelayMs = ReconnectInterval,
                MaxDelayMs = MaxReconnectInterval,
                BackoffMultiplier = ReconnectBackoffMultiplier
            };
            guard.OnReconnecting += HandleReconnecting;
            guard.OnReconnected += HandleReconnected;
            guard.OnReconnectFailed += HandleReconnectFailed;
            guard.Start();
            _autoReconnectGuard = guard;
        }

        private void StopAutoReconnectGuard()
        {
            if (_autoReconnectGuard != null)
            {
                _autoReconnectGuard.OnReconnecting -= HandleReconnecting;
                _autoReconnectGuard.OnReconnected -= HandleReconnected;
                _autoReconnectGuard.OnReconnectFailed -= HandleReconnectFailed;
                _autoReconnectGuard.Dispose();
            }
            _autoReconnectGuard = null;
        }

        private void HandleReconnecting(int attempt) => OnReconnecting?.Invoke(attempt);
        private void HandleReconnected() => OnReconnected?.Invoke();
        private void HandleReconnectFailed(string message) => OnReconnectFailed?.Invoke(message);

        private void StartHeartbeatGuard()
        {
            StopHeartbeatGuard();
            var guard = new HeartbeatGuard(this, SendHeartbeatAsync, Log)
            {
                IntervalMs = HeartbeatInterval,
                TimeoutMs = HeartbeatTimeout,
                MaxConsecutiveFailures = MaxHeartbeatFailures
            };
            guard.OnHeartbeatFailed += OnHeartbeatFailed;
            guard.Start();
            _heartbeatGuard = guard;
        }

        private void StopHeartbeatGuard()
        {
            if (_heartbeatGuard != null)
            {
                _heartbeatGuard.OnHeartbeatFailed -= OnHeartbeatFailed;
                _heartbeatGuard.Dispose();
                _heartbeatGuard = null;
            }
        }

        private async Task<OperateResult> SendHeartbeatAsync()
        {
            if (_heartbeatCallback != null)
                return await _heartbeatCallback().ConfigureAwait(false);

            byte[]? request = BuildHeartbeat();
            if (request == null || request.Length == 0)
                return OperateResult.Failed("未配置心跳报文");

            var result = await SendAndReceiveAsync(request).ConfigureAwait(false);
            return result.IsSuccess
                ? OperateResult.Success()
                : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        private void OnHeartbeatFailed(int failureCount, string message)
        {
            string error = $"心跳连续失败 {failureCount} 次: {message}";
            Log.Error(error);
            RaiseError(error);
            _asyncLock.Wait();
            try { DisconnectCore(forceDisconnectedEvent: true); }
            finally { _asyncLock.Release(); }
        }

        // ── 事件触发器（供派生类 override SendAndReceive 时调用）──

        /// <summary>触发原始报文发送事件。</summary>
        protected void RaiseMessageSent(string hex) => OnMessageSent?.Invoke(this, hex);

        /// <summary>触发原始报文接收事件。</summary>
        protected void RaiseMessageReceived(string hex) => OnMessageReceived?.Invoke(this, hex);

        /// <summary>触发通讯错误事件。</summary>
        protected void RaiseError(string message) => OnError?.Invoke(this, message);

        /// <summary>触发连接断开事件（供子类在外部检测到断开时调用）。</summary>
        protected void RaiseDisconnected() => OnDisconnected?.Invoke(this, EventArgs.Empty);

        /// <summary>触发连接成功事件（供子类在自定义握手成功后调用）。</summary>
        protected void RaiseConnected() => OnConnected?.Invoke(this, EventArgs.Empty);

        // ── 网络收发 ──────────────────────────────

        protected virtual OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                if (!IsConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                // IO 锁覆盖整段 Write+Read，保证单次收发原子性（C1 根治）。
                // 内部 DisconnectCore 直调：IO 锁已保证单线程进入，无需再取连接锁，
                // 且 SemaphoreSlim 不可重入——若此处再 Wait 会自死锁（C5 根治）。
                AcquireIoLock();
                try
                {
                    NetworkStream? ns = _stream;
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

                    if (!_persistentMode)
                        DisconnectCore();

                    return OperateResult<byte[]>.Success(full);
                }
                finally
                {
                    ReleaseIoLock();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                if (!_persistentMode)
                {
                    AcquireIoLock();
                    try { DisconnectCore(); }
                    finally { ReleaseIoLock(); }
                }
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        /// <summary>发送原始报文并接收响应（自定义功能码场景）。</summary>
        public OperateResult<byte[]> SendCustomMessage(byte[] request)
            => SendAndReceive(request);

        // ── 真正的异步收发 ────────────────────────────

        /// <summary>
        /// 异步发送请求并接收响应。使用 CancellationToken 控制超时和取消。
        /// 子类应优先使用此方法实现真正的 async 路径。
        /// </summary>
        protected virtual async Task<OperateResult<byte[]>> SendAndReceiveAsync(
            byte[] request, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!IsConnected)
                {
                    var conn = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                // IO 锁覆盖整段 WriteAsync+ReadAsync（C1 根治）。
                await AcquireIoLockAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    NetworkStream? ns = _stream;
                    if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                    Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                    OnMessageSent?.Invoke(this, DataConverter.ToHexString(request));

                    await ns.WriteAsync(request, 0, request.Length, cancellationToken).ConfigureAwait(false);

                    var headerBuf = await ReadExactAsync(ns, ResponseHeaderLength, cancellationToken).ConfigureAwait(false);
                    if (headerBuf == null) return OperateResult<byte[]>.Failed("读取响应头失败");

                    int payloadLen = GetResponsePayloadLength(headerBuf);
                    byte[] payload = payloadLen > 0
                        ? await ReadExactAsync(ns, payloadLen, cancellationToken).ConfigureAwait(false) ?? Array.Empty<byte>()
                        : Array.Empty<byte>();

                    byte[] full = new byte[headerBuf.Length + payload.Length];
                    Buffer.BlockCopy(headerBuf, 0, full, 0, headerBuf.Length);
                    Buffer.BlockCopy(payload, 0, full, headerBuf.Length, payload.Length);

                    Log.Debug($"RX ← {DataConverter.ToHexString(full)}");
                    OnMessageReceived?.Invoke(this, DataConverter.ToHexString(full));

                    if (!_persistentMode)
                        DisconnectCore();

                    return OperateResult<byte[]>.Success(full);
                }
                finally
                {
                    ReleaseIoLockAsync();
                }
            }
            catch (OperationCanceledException)
            {
                if (!_persistentMode)
                {
                    await AcquireIoLockAsync(CancellationToken.None).ConfigureAwait(false);
                    try { DisconnectCore(); }
                    finally { ReleaseIoLockAsync(); }
                }
                return OperateResult<byte[]>.Failed("操作已取消");
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                if (!_persistentMode)
                {
                    await AcquireIoLockAsync(CancellationToken.None).ConfigureAwait(false);
                    try { DisconnectCore(); }
                    finally { ReleaseIoLockAsync(); }
                }
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        /// <summary>异步发送自定义报文并接收响应。</summary>
        public Task<OperateResult<byte[]>> SendCustomMessageAsync(byte[] request, CancellationToken cancellationToken = default)
            => SendAndReceiveAsync(request, cancellationToken);

        /// <summary>
        /// 带重试的异步发送和接收。当通讯失败时自动重试，适用于瞬态故障。
        /// 使用 <see cref="RetryCount"/> 和 <see cref="RetryInterval"/> 配置重试策略。
        /// </summary>
        protected async Task<OperateResult<byte[]>> SendAndReceiveWithRetryAsync(
            byte[] request, CancellationToken cancellationToken = default)
        {
            OperateResult<byte[]>? lastResult = null;
            for (int i = 0; i <= RetryCount; i++)
            {
                if (i > 0)
                {
                    Log.Info($"通讯重试 ({i}/{RetryCount})，等待 {RetryInterval}ms...");
                    await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
                }
                lastResult = await SendAndReceiveAsync(request, cancellationToken).ConfigureAwait(false);
                if (lastResult.IsSuccess) return lastResult;
            }
            return lastResult ?? OperateResult<byte[]>.Failed("通讯失败");
        }

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

        private static async Task<byte[]?> ReadExactAsync(NetworkStream ns, int count, CancellationToken ct)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await ns.ReadAsync(buf, offset, count - offset, ct).ConfigureAwait(false);
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

        // ── Async 默认实现（true async via CoreAsync）──

        // Protected virtual core async methods — 子类 override 以实现真正异步

        protected virtual Task<OperateResult<bool>> ReadBoolCoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadBool(address));
        protected virtual Task<OperateResult<short>> ReadInt16CoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadInt16(address));
        protected virtual Task<OperateResult<ushort>> ReadUInt16CoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadUInt16(address));
        protected virtual Task<OperateResult<int>> ReadInt32CoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadInt32(address));
        protected virtual Task<OperateResult<uint>> ReadUInt32CoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadUInt32(address));
        protected virtual Task<OperateResult<long>> ReadInt64CoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadInt64(address));
        protected virtual Task<OperateResult<ulong>> ReadUInt64CoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadUInt64(address));
        protected virtual Task<OperateResult<float>> ReadFloatCoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadFloat(address));
        protected virtual Task<OperateResult<double>> ReadDoubleCoreAsync(string address, CancellationToken ct)
            => Task.FromResult(ReadDouble(address));
        protected virtual Task<OperateResult<string>> ReadStringCoreAsync(string address, ushort length, CancellationToken ct)
            => Task.FromResult(ReadString(address, length));
        protected virtual Task<OperateResult<byte[]>> ReadBytesCoreAsync(string address, ushort length, CancellationToken ct)
            => Task.FromResult(ReadBytes(address, length));

        protected virtual Task<OperateResult> WriteBoolCoreAsync(string address, bool value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteInt16CoreAsync(string address, short value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteUInt16CoreAsync(string address, ushort value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteInt32CoreAsync(string address, int value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteUInt32CoreAsync(string address, uint value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteInt64CoreAsync(string address, long value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteUInt64CoreAsync(string address, ulong value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteFloatCoreAsync(string address, float value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteDoubleCoreAsync(string address, double value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteStringCoreAsync(string address, string value, CancellationToken ct)
            => Task.FromResult(Write(address, value));
        protected virtual Task<OperateResult> WriteBytesCoreAsync(string address, byte[] data, CancellationToken ct)
            => Task.FromResult(Write(address, data));

        // Public async methods — delegate to CoreAsync

        public virtual Task<OperateResult<bool>> ReadBoolAsync(string address)
            => ReadBoolCoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<short>> ReadInt16Async(string address)
            => ReadInt16CoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<ushort>> ReadUInt16Async(string address)
            => ReadUInt16CoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<int>> ReadInt32Async(string address)
            => ReadInt32CoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<uint>> ReadUInt32Async(string address)
            => ReadUInt32CoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<long>> ReadInt64Async(string address)
            => ReadInt64CoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<ulong>> ReadUInt64Async(string address)
            => ReadUInt64CoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<float>> ReadFloatAsync(string address)
            => ReadFloatCoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<double>> ReadDoubleAsync(string address)
            => ReadDoubleCoreAsync(address, CancellationToken.None);
        public virtual Task<OperateResult<string>> ReadStringAsync(string address, ushort length)
            => ReadStringCoreAsync(address, length, CancellationToken.None);
        public virtual Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length)
            => ReadBytesCoreAsync(address, length, CancellationToken.None);

        public virtual Task<OperateResult> WriteAsync(string address, bool value)
            => WriteBoolCoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, short value)
            => WriteInt16CoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, ushort value)
            => WriteUInt16CoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, int value)
            => WriteInt32CoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, uint value)
            => WriteUInt32CoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, long value)
            => WriteInt64CoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, ulong value)
            => WriteUInt64CoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, float value)
            => WriteFloatCoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, double value)
            => WriteDoubleCoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, string value)
            => WriteStringCoreAsync(address, value, CancellationToken.None);
        public virtual Task<OperateResult> WriteAsync(string address, byte[] data)
            => WriteBytesCoreAsync(address, data, CancellationToken.None);

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;   // 防重复 dispose：_asyncLock.Dispose() 二次调用会抛 ODE
            _disposed = true;

            if (disposing)
            {
                StopHeartbeatGuard();
                StopAutoReconnectGuard();
                try { Disconnect(); } catch { /* 关闭期异常不应阻断 Dispose */ }
                _asyncLock.Dispose();
            }
        }

        public override string ToString() => $"{Ip}:{Port}";
    }
}
