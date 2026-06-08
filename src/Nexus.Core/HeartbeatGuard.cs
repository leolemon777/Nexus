using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// 心跳保活守护 — 定期发送心跳包检测连接有效性。
    /// 使用外部回调函数发送心跳，适用于任何 IReadWriteDevice。
    /// </summary>
    public class HeartbeatGuard : IDisposable
    {
        private readonly IReadWriteDevice _device;
        private readonly Func<Task<OperateResult>> _heartbeatCallback;
        private readonly ILogger _log;
        private readonly object _stateLock = new object();
        private Timer? _heartbeatTimer;
        private int _consecutiveFailures;
        private bool _running;
        private bool _disposed;

        // ── 配置属性 ──────────────────────────────

        /// <summary>心跳间隔（毫秒，默认 30000ms）。</summary>
        public int IntervalMs { get; set; } = 30000;

        /// <summary>最大连续失败次数（默认 3 次，超过后触发 OnHeartbeatFailed 并停止）。</summary>
        public int MaxConsecutiveFailures { get; set; } = 3;

        /// <summary>单次心跳超时（毫秒，默认 5000ms）。</summary>
        public int TimeoutMs { get; set; } = 5000;

        // ── 事件 ──────────────────────────────────

        /// <summary>心跳成功。</summary>
        public event Action? OnHeartbeatOk;

        /// <summary>心跳连续失败达到上限。</summary>
        public event Action<int, string>? OnHeartbeatFailed;

        /// <summary>心跳超时（单次）。</summary>
        public event Action? OnHeartbeatTimeout;

        // ── 状态 ──────────────────────────────────

        /// <summary>是否正在运行。</summary>
        public bool IsRunning
        {
            get { lock (_stateLock) return _running && !_disposed; }
        }

        /// <summary>当前连续失败次数。</summary>
        public int ConsecutiveFailures
        {
            get { lock (_stateLock) return _consecutiveFailures; }
        }

        /// <param name="device">要监控的设备。</param>
        /// <param name="heartbeatCallback">心跳发送回调（如读取已知寄存器）。</param>
        /// <param name="log">日志记录器（可选，默认 NullLogger）。</param>
        public HeartbeatGuard(
            IReadWriteDevice device,
            Func<Task<OperateResult>> heartbeatCallback,
            ILogger? log = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _heartbeatCallback = heartbeatCallback ?? throw new ArgumentNullException(nameof(heartbeatCallback));
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>启动心跳守护。</summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_running || _disposed) return;
                _running = true;
                _consecutiveFailures = 0;

                _heartbeatTimer = new Timer(
                    HeartbeatCallback,
                    null,
                    IntervalMs,
                    IntervalMs);
            }

            _log.Info($"心跳守护已启动 (间隔 {IntervalMs}ms, 最大失败 {MaxConsecutiveFailures} 次)");
        }

        /// <summary>停止心跳守护。</summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_running) return;
                _running = false;

                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
            }

            _log.Info("心跳守护已停止");
        }

        // ── 内部方法 ──────────────────────────────

        private async void HeartbeatCallback(object? state)
        {
            // 防止重入：Timer 可能在上一次回调完成前再次触发
            lock (_stateLock)
            {
                if (!_running || _disposed) return;
            }

            string errorMsg = string.Empty;
            bool success = false;
            bool timedOut = false;

            try
            {
                using var cts = new CancellationTokenSource(TimeoutMs);

                // 使用 Task.Run + WhenAny 实现超时控制
                // （netstandard2.0 无法使用 Task.WhenAsync 等高版本 API）
                var heartbeatTask = _heartbeatCallback();
                var completed = await Task.WhenAny(
                    heartbeatTask,
                    Task.Delay(TimeoutMs, cts.Token)).ConfigureAwait(false);

                if (completed == heartbeatTask)
                {
                    var result = heartbeatTask.IsCompletedSuccessfully()
                        ? heartbeatTask.Result
                        : OperateResult.Failed("心跳任务异常");

                    if (result.IsSuccess)
                    {
                        success = true;
                    }
                    else
                    {
                        errorMsg = result.Message;
                    }
                }
                else
                {
                    timedOut = true;
                    errorMsg = $"心跳超时 ({TimeoutMs}ms)";
                }
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                errorMsg = $"心跳超时 ({TimeoutMs}ms)";
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }

            lock (_stateLock)
            {
                if (!_running || _disposed) return;

                if (success)
                {
                    _consecutiveFailures = 0;
                    OnHeartbeatOk?.Invoke();
                    _log.Debug("心跳成功");
                }
                else
                {
                    _consecutiveFailures++;
                    _log.Warn($"心跳失败 ({_consecutiveFailures}/{MaxConsecutiveFailures}) — {errorMsg}");

                    if (timedOut)
                        OnHeartbeatTimeout?.Invoke();

                    if (_consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _log.Error($"心跳连续失败 {MaxConsecutiveFailures} 次，停止心跳守护");
                        OnHeartbeatFailed?.Invoke(_consecutiveFailures, errorMsg);

                        // 停止定时器
                        _running = false;
                        _heartbeatTimer?.Dispose();
                        _heartbeatTimer = null;
                    }
                }
            }
        }

        /// <summary>释放资源并停止心跳。</summary>
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

    /// <summary>
    /// netstandard2.0 下 Task 扩展方法 — 检查任务是否成功完成。
    /// </summary>
    internal static class TaskExtensions
    {
        /// <summary>判断 Task 是否成功完成（RanToCompletion 且未取消/异常）。</summary>
        public static bool IsCompletedSuccessfully(this Task task)
        {
            return task.Status == TaskStatus.RanToCompletion;
        }
    }
}
