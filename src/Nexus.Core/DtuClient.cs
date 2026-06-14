using System;
using System.IO;
using System.Net.Sockets;

namespace Nexus
{
    /// <summary>
    /// DTU 透传客户端 — 通过 TCP 连接到 DTU（数据传输单元）设备，实现远程串口透传。
    /// <para>DTU 将串口数据通过 4G/以太网转发到 TCP 服务器，本客户端作为 TCP 主动连接方。</para>
    /// <para>适用于 PLC 通过 DTU 连接远端服务器的场景。</para>
    /// </summary>
    public class DtuClient : IDisposable
    {
        // ── 属性 ─────────────────────────────────
        protected string Ip { get; }
        protected int Port { get; }
        protected int Timeout { get; set; }
        protected ILogger Log { get; set; }

        /// <summary>DTU 设备注册码/IMEI（用于身份验证，可选）。</summary>
        public string? DeviceId { get; set; }

        /// <summary>重连间隔（毫秒，默认 5000ms）。</summary>
        public int ReconnectInterval { get; set; } = 5000;

        /// <summary>是否自动重连（默认 true）。</summary>
        public bool AutoReconnect { get; set; } = true;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly object _lock = new object();

        // ── 事件 ──────────────────────────────────

        /// <summary>DTU 连接成功。</summary>
        public event EventHandler? OnConnected;

        /// <summary>DTU 连接断开。</summary>
        public event EventHandler? OnDisconnected;

        /// <summary>通讯错误。</summary>
        public event EventHandler<string>? OnError;

        /// <summary>收到原始数据（十六进制）。</summary>
        public event EventHandler<string>? OnDataReceived;

        /// <summary>发送原始数据（十六进制）。</summary>
        public event EventHandler<string>? OnDataSent;

        /// <summary>是否已连接。</summary>
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

        // ── 构造 ────────────────────────────────

        public DtuClient(string ip, int port = 8899, int timeout = 10000)
        {
            Ip = ip ?? throw new ArgumentNullException(nameof(ip));
            Port = port;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        /// <summary>连接到 DTU 服务器。</summary>
        public OperateResult Connect()
        {
            lock (_lock)
            {
                try
                {
                    DisconnectCore();

                    _client = new TcpClient { SendTimeout = Timeout, ReceiveTimeout = Timeout };
                    var result = _client.BeginConnect(Ip, Port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(Timeout, true))
                    {
                        DisconnectCore();
                        return OperateResult.Failed($"DTU 连接超时: {Ip}:{Port} ({Timeout}ms)");
                    }
                    _client.EndConnect(result);

                    _stream = _client.GetStream();
                    _stream.ReadTimeout = Timeout;
                    _stream.WriteTimeout = Timeout;

                    // 发送注册包（如果设置了 DeviceId）
                    if (!string.IsNullOrEmpty(DeviceId))
                    {
                        byte[] regPacket = BuildRegisterPacket(DeviceId!);
                        _stream.Write(regPacket, 0, regPacket.Length);
                        OnDataSent?.Invoke(this, DataConverter.ToHexString(regPacket));
                        Log.Info($"DTU 注册包已发送: {DeviceId}");

                        // 等待注册确认
                        var ackBuf = new byte[64];
                        int ackRead = _stream.Read(ackBuf, 0, ackBuf.Length);
                        if (ackRead > 0)
                        {
                            string ackHex = DataConverter.ToHexString(ackBuf, 0, ackRead);
                            OnDataReceived?.Invoke(this, ackHex);
                            Log.Info($"DTU 注册响应: {ackHex}");
                        }
                    }

                    Log.Info($"DTU 已连接: {Ip}:{Port}");
                    OnConnected?.Invoke(this, EventArgs.Empty);
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    DisconnectCore();
                    Log.Error($"DTU 连接失败: {ex.Message}");
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult.Failed($"DTU 连接失败: {ex.Message}");
                }
            }
        }

        /// <summary>断开 DTU 连接。</summary>
        public void Disconnect()
        {
            lock (_lock) DisconnectCore();
        }

        // ── 资源释放 ──────────────────────────────
        private bool _disposed;

        /// <summary>
        /// 释放 DTU 客户端占用的网络资源（socket/stream）。
        /// 忘记调用 Disconnect 时由 Dispose 兜底，避免 socket 泄漏。
        /// </summary>
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
                lock (_lock) DisconnectCore();
            }
        }

        private void DisconnectCore()
        {
            try
            {
                if (_stream != null) { _stream.Dispose(); _stream = null; }
                if (_client != null) { _client.Close(); _client = null; }
            }
            catch { }

            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        // ═══════════════════════════════════════════
        //  数据收发（透传）
        // ═══════════════════════════════════════════

        /// <summary>发送原始数据到 DTU（透传到远端串口）。</summary>
        public OperateResult Send(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    _stream!.Write(data, 0, data.Length);
                    OnDataSent?.Invoke(this, DataConverter.ToHexString(data));
                    Log.Debug($"DTU 发送 {data.Length} 字节");
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    HandleDisconnect(ex);
                    return OperateResult.Failed($"DTU 发送失败: {ex.Message}");
                }
            }
        }

        /// <summary>从 DTU 接收指定长度的数据。</summary>
        public OperateResult<byte[]> Receive(int expectedLength)
        {
            if (expectedLength <= 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));

            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    byte[] buffer = new byte[expectedLength];
                    int totalRead = 0;
                    // unchecked 差值比较，避免 Environment.TickCount 在连续运行约 24.8 天后 int 溢出导致超时失效。
                    int start = Environment.TickCount;

                    while (totalRead < expectedLength && unchecked(Environment.TickCount - start) < Timeout)
                    {
                        int remaining = expectedLength - totalRead;
                        int read = _stream!.Read(buffer, totalRead, remaining);
                        if (read == 0)
                            return OperateResult<byte[]>.Failed("DTU 连接已关闭");

                        totalRead += read;
                    }

                    if (totalRead < expectedLength)
                        return OperateResult<byte[]>.Failed($"DTU 接收超时: 收到 {totalRead}/{expectedLength} 字节");

                    OnDataReceived?.Invoke(this, DataConverter.ToHexString(buffer));
                    Log.Debug($"DTU 接收 {totalRead} 字节");
                    return OperateResult<byte[]>.Success(buffer);
                }
                catch (Exception ex)
                {
                    HandleDisconnect(ex);
                    return OperateResult<byte[]>.Failed($"DTU 接收失败: {ex.Message}");
                }
            }
        }

        /// <summary>发送数据并等待响应（请求-响应模式）。</summary>
        public OperateResult<byte[]> SendAndReceive(byte[] data, int responseLength)
        {
            var sendResult = Send(data);
            if (!sendResult.IsSuccess) return OperateResult<byte[]>.Failed(sendResult.Message);
            return Receive(responseLength);
        }

        /// <summary>发送数据并读取直到遇到指定结束标志。</summary>
        public OperateResult<byte[]> SendAndReadUntil(byte[] data, byte delimiter, int maxBytes = 4096)
        {
            var sendResult = Send(data);
            if (!sendResult.IsSuccess) return OperateResult<byte[]>.Failed(sendResult.Message);

            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    var result = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[256];
                    // unchecked 差值比较，避免 TickCount 在连续运行约 24.8 天后 int 溢出导致超时失效。
                    int start = Environment.TickCount;

                    while (unchecked(Environment.TickCount - start) < Timeout && result.Count < maxBytes)
                    {
                        int read = _stream!.Read(buf, 0, buf.Length);
                        if (read == 0)
                            break;

                        for (int i = 0; i < read; i++)
                        {
                            result.Add(buf[i]);
                            if (buf[i] == delimiter)
                            {
                                byte[] final = result.ToArray();
                                OnDataReceived?.Invoke(this, DataConverter.ToHexString(final));
                                return OperateResult<byte[]>.Success(final);
                            }
                        }
                    }

                    return result.Count > 0
                        ? OperateResult<byte[]>.Success(result.ToArray())
                        : OperateResult<byte[]>.Failed($"DTU 读取超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    HandleDisconnect(ex);
                    return OperateResult<byte[]>.Failed($"DTU 读取失败: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  DTU 设备管理
        // ═══════════════════════════════════════════

        /// <summary>获取在线 DTU 列表（部分 DTU 服务器支持）。</summary>
        public OperateResult<string[]> GetOnlineDevices()
        {
            try
            {
                lock (_lock)
                {
                    EnsureConnected();
                    byte[] cmd = System.Text.Encoding.ASCII.GetBytes("LIST\r\n");
                    _stream!.Write(cmd, 0, cmd.Length);
                    OnDataSent?.Invoke(this, System.Text.Encoding.ASCII.GetString(cmd));

                    var buf = new byte[4096];
                    int read = _stream.Read(buf, 0, buf.Length);
                    if (read <= 0)
                        return OperateResult<string[]>.Failed("无响应");

                    string response = System.Text.Encoding.ASCII.GetString(buf, 0, read);
                    OnDataReceived?.Invoke(this, response);

                    return OperateResult<string[]>.Success(
                        response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries));
                }
            }
            catch (Exception ex)
            {
                return OperateResult<string[]>.Failed($"获取设备列表失败: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  透传协议客户端工厂
        // ═══════════════════════════════════════════

        // ═══════════════════════════════════════════
        //  内部方法
        // ═══════════════════════════════════════════

        private byte[] BuildRegisterPacket(string deviceId)
        {
            // 常见 DTU 注册包格式: IMEI + "\r\n" 或自定义协议
            byte[] idBytes = System.Text.Encoding.ASCII.GetBytes(deviceId + "\r\n");
            return idBytes;
        }

        private void EnsureConnected()
        {
            if (_stream == null || _client == null || !_client.Connected)
                throw new InvalidOperationException("DTU 未连接，请先调用 Connect()");
        }

        private void HandleDisconnect(Exception ex)
        {
            Log.Error($"DTU 通讯异常: {ex.Message}");
            OnError?.Invoke(this, ex.Message);
            DisconnectCore();
        }

        public override string ToString() => $"DtuClient[{Ip}:{Port}, Id={DeviceId ?? "N/A"}]";
    }
}
