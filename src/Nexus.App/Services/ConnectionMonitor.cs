using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.App.Services
{
    /// <summary>
    /// 连接监控服务 — 自动重连 + 心跳检测。
    /// <para>每个设备注册后，后台线程定期 ping，断线自动重连。</para>
    /// </summary>
    public sealed class ConnectionMonitorService : IDisposable
    {
        private readonly ConcurrentDictionary<string, MonitoredConnection> _connections = new();
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private bool _started;

        /// <summary>健康检查间隔（毫秒），默认 3000</summary>
        public int HeartbeatIntervalMs { get; set; } = 3000;

        /// <summary>最大自动重连次数，默认 10</summary>
        public int MaxRetryCount { get; set; } = 10;

        /// <summary>重连间隔（毫秒），默认 5000</summary>
        public int RetryIntervalMs { get; set; } = 5000;

        public event EventHandler<ConnectionMonitorEventArgs>? ConnectionLost;
        public event EventHandler<ConnectionMonitorEventArgs>? ConnectionRestored;
        public event EventHandler<ConnectionMonitorEventArgs>? HeartbeatOk;
        public event EventHandler<ConnectionMonitorEventArgs>? RetryFailed;

        /// <summary>
        /// 注册一个需要监控的设备连接。
        /// </summary>
        public void Register(string name, IReadWriteDevice device, Func<OperateResult> connectFunc, Action disconnectAction)
        {
            _connections[name] = new MonitoredConnection
            {
                Name = name,
                Device = device,
                ConnectFunc = connectFunc,
                DisconnectAction = disconnectAction,
                RetryCount = 0,
                IsHealthy = true
            };
        }

        public void Unregister(string name)
        {
            _connections.TryRemove(name, out _);
        }

        /// <summary>启动后台心跳循环</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => MonitorLoop(_cts.Token));
        }

        /// <summary>停止监控</summary>
        public void Stop()
        {
            _cts?.Cancel();
            _started = false;
            _loopTask = null;
        }

        private async Task MonitorLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var kvp in _connections)
                    {
                        var conn = kvp.Value;
                        try
                        {
                            if (!conn.Device.IsConnected)
                            {
                                // 连接丢失
                                if (conn.IsHealthy)
                                {
                                    conn.IsHealthy = false;
                                    ConnectionLost?.Invoke(this, new ConnectionMonitorEventArgs(conn.Name, "连接丢失"));
                                }

                                // 尝试重连
                                if (conn.RetryCount < MaxRetryCount)
                                {
                                    conn.RetryCount++;
                                    var result = await Task.Run(() => conn.ConnectFunc()).ConfigureAwait(false);
                                    if (result.IsSuccess)
                                    {
                                        conn.IsHealthy = true;
                                        conn.RetryCount = 0;
                                        ConnectionRestored?.Invoke(this, new ConnectionMonitorEventArgs(conn.Name, $"重连成功（第 {conn.RetryCount} 次）"));
                                    }
                                    else
                                    {
                                        RetryFailed?.Invoke(this, new ConnectionMonitorEventArgs(conn.Name, $"重连失败 ({conn.RetryCount}/{MaxRetryCount}): {result.Message}"));
                                    }
                                }
                            }
                            else
                            {
                                // 心跳: 读取设备第一个寄存器
                                try
                                {
                                    await Task.Run(() => conn.Device.ReadInt16("D0")).ConfigureAwait(false);
                                    if (!conn.IsHealthy)
                                    {
                                        conn.IsHealthy = true;
                                        conn.RetryCount = 0;
                                    }
                                    HeartbeatOk?.Invoke(this, new ConnectionMonitorEventArgs(conn.Name, "心跳正常"));
                                }
                                catch
                                {
                                    // 心跳失败但 TCP 仍连接 → 状态不确定
                                    ConnectionLost?.Invoke(this, new ConnectionMonitorEventArgs(conn.Name, "心跳超时"));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            RetryFailed?.Invoke(this, new ConnectionMonitorEventArgs(conn.Name, $"监控异常: {ex.Message}"));
                        }
                    }
                }
                catch { }

                await Task.Delay(HeartbeatIntervalMs, ct).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            Stop();
            _connections.Clear();
            GC.SuppressFinalize(this);
        }
    }

    public sealed class MonitoredConnection
    {
        public string Name { get; set; } = string.Empty;
        public IReadWriteDevice Device { get; set; } = null!;
        public Func<OperateResult> ConnectFunc { get; set; } = null!;
        public Action DisconnectAction { get; set; } = null!;
        public int RetryCount { get; set; }
        public bool IsHealthy { get; set; }
    }

    public sealed class ConnectionMonitorEventArgs : EventArgs
    {
        public string DeviceName { get; }
        public string Message { get; }
        public DateTime Timestamp { get; } = DateTime.Now;

        public ConnectionMonitorEventArgs(string deviceName, string message)
        {
            DeviceName = deviceName;
            Message = message;
        }
    }
}
