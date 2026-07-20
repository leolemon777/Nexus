using System;
using System.Threading;
using System.Threading.Tasks;

// B8: AutoReconnectGuard 是旧 TcpDeviceBase 的伴生组件,自身也需要随 Phase B 重构迁出。
// 在迁移完成前,我们屏蔽 CS0618 警告以避免污染 build 输出。
#pragma warning disable CS0618

namespace Nexus
{
    /// <summary>
    /// 自动重连守护 — 监听 TcpDeviceBase 连接断开事件，按指数退避策略自动重连。
    /// 作为外部组件使用，不修改 TcpDeviceBase 本身。
    /// </summary>
    public class AutoReconnectGuard : IDisposable
    {
        private readonly TcpDeviceBase _device;
        private readonly ILogger _log;
        private readonly object _stateLock = new object();
        private Timer? _retryTimer;
        private CancellationTokenSource? _cts;
        private readonly Func<bool> _shouldReconnect;
        private int _attempt;
        private bool _started;
        private bool _disposed;
        private string? _lastError;

        // ── 配置属性 ──────────────────────────────

        /// <summary>最大重试次数（默认 10 次，0 = 无限重试）。</summary>
        public int MaxRetries { get; set; } = 10;

        /// <summary>基础重试间隔（毫秒，默认 1000ms）。</summary>
        public int BaseDelayMs { get; set; } = 1000;

        /// <summary>最大重试间隔（毫秒，默认 30000ms）。</summary>
        public int MaxDelayMs { get; set; } = 30000;

        /// <summary>退避倍数（默认 2.0）。</summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        // ── 事件 ──────────────────────────────────

        /// <summary>正在尝试重连。</summary>
        public event Action<int>? OnReconnecting;

        /// <summary>重连成功。</summary>
        public event Action? OnReconnected;

        /// <summary>重连最终失败（达到最大重试次数）。</summary>
        public event Action<string>? OnReconnectFailed;

        // ── 状态 ──────────────────────────────────

        /// <summary>是否正在重连中。</summary>
        public bool IsReconnecting
        {
            get { lock (_stateLock) return _started && _attempt > 0 && !_disposed; }
        }

        /// <param name="device">要守护的 TCP 设备。</param>
        /// <param name="log">日志记录器（可选，默认 NullLogger）。</param>
        public AutoReconnectGuard(TcpDeviceBase device, ILogger? log = null, Func<bool>? shouldReconnect = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _log = log ?? NullLogger.Instance;
            _shouldReconnect = shouldReconnect ?? (() => true);
        }

        /// <summary>启动自动重连守护（订阅断开事件）。</summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_started || _disposed) return;
                _started = true;
            }

            _device.OnDisconnected += OnDeviceDisconnected;
            _device.OnError += OnDeviceError;
        }

        /// <summary>停止自动重连守护（取消进行中的重连）。</summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_started) return;
                _started = false;
            }

            _device.OnDisconnected -= OnDeviceDisconnected;
            _device.OnError -= OnDeviceError;
            CancelRetry();
        }

        /// <summary>取消正在进行的重连尝试。</summary>
        public void CancelRetry()
        {
            lock (_stateLock)
            {
                _retryTimer?.Dispose();
                _retryTimer = null;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                _attempt = 0;
            }
        }

        // ── 内部方法 ──────────────────────────────

        private void OnDeviceDisconnected(object? sender, EventArgs e)
        {
            TriggerReconnect();
        }

        private void OnDeviceError(object? sender, string error)
        {
            // 连接断开类错误也可能触发重连（某些子类不触发 OnDisconnected）
            _lastError = error;
        }

        private void TriggerReconnect()
        {
            lock (_stateLock)
            {
                if (!_started || _disposed) return;
                if (!_shouldReconnect()) return;

                // 如果已在重连中，不重复触发
                if (_retryTimer != null) return;

                _attempt = 0;
                _lastError = null;
                _cts = new CancellationTokenSource();

                // 立即开始第一次重试
                ScheduleRetry(0);
            }
        }

        private void ScheduleRetry(int delayMs)
        {
            lock (_stateLock)
            {
                if (!_started || _disposed) return;

                _retryTimer?.Dispose();
                _retryTimer = new Timer(RetryCallback, null, delayMs, Timeout.Infinite);
            }
        }

        private void RetryCallback(object? state)
        {
            int currentAttempt;
            CancellationToken ct;

            lock (_stateLock)
            {
                if (!_started || _disposed) return;
                currentAttempt = ++_attempt;
                ct = _cts?.Token ?? CancellationToken.None;
            }

            // 检查是否超过最大重试次数
            if (MaxRetries > 0 && currentAttempt > MaxRetries)
            {
                string error = _lastError ?? "达到最大重试次数";
                _log.Warn($"自动重连放弃 ({IpPort()}) — {error}");
                OnReconnectFailed?.Invoke(error);

                lock (_stateLock)
                {
                    _retryTimer?.Dispose();
                    _retryTimer = null;
                }
                return;
            }

            string maxLabel = MaxRetries > 0 ? MaxRetries.ToString() : "inf";
            _log.Info($"自动重连中 ({currentAttempt}/{maxLabel}) {IpPort()}…");
            OnReconnecting?.Invoke(currentAttempt);

            try
            {
                var result = _device.Connect();
                if (result.IsSuccess && _device.IsConnected)
                {
                    _log.Info($"自动重连成功 {IpPort()}");
                    OnReconnected?.Invoke();

                    lock (_stateLock)
                    {
                        _retryTimer?.Dispose();
                        _retryTimer = null;
                        _attempt = 0;
                    }
                    return;
                }

                _lastError = result.Message;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }

            // 计算下次退避时间
            double delay = BaseDelayMs * Math.Pow(BackoffMultiplier, currentAttempt - 1);
            int nextDelay = (int)Math.Min(delay, MaxDelayMs);

            _log.Debug($"自动重连下次尝试 {nextDelay}ms 后…");
            ScheduleRetry(nextDelay);
        }

        private string IpPort()
        {
            // TcpDeviceBase.Ip 和 Port 是 protected，无法直接访问
            // 使用 ToString 或日志中的信息
            return _device.ToString();
        }

        /// <summary>释放资源并停止重连。</summary>
        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed) return;
                _disposed = true;
            }

            Stop();
        }
    }
}
