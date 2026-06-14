using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;

namespace Nexus.Robot.Ur
{
    /// <summary>
    /// Universal Robots URScript 客户端 — 支持 UR3e/UR5e/UR10e/UR16e。
    /// <para>通过 Secondary Interface (端口 30002) 发送 URScript 命令。</para>
    /// <para>通过 Dashboard Server (端口 29999) 进行状态控制。</para>
    /// <para>通过 Real-Time Interface (端口 30003) 读取实时数据。</para>
    /// </summary>
    public class UrClient : IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private System.Net.Sockets.TcpClient? _scriptClient;
        private System.Net.Sockets.TcpClient? _dashboardClient;
        private System.Net.Sockets.TcpClient? _rtClient;
        private bool _isConnected;
        protected ILogger Log { get; set; }

        /// <summary>机器人 IP 地址。</summary>
        public string IpAddress { get; }
        /// <summary>Secondary Interface 端口。</summary>
        public int ScriptPort { get; }
        /// <summary>Dashboard Server 端口。</summary>
        public int DashboardPort { get; }
        /// <summary>Real-Time Interface 端口。</summary>
        public int RealTimePort { get; }
        /// <summary>超时时间（毫秒）。</summary>
        public int Timeout { get; set; } = 5000;

        public bool IsConnected
        {
            get { lock (_lock) return _isConnected && _scriptClient?.Connected == true; }
        }

        public UrClient(string ipAddress, int scriptPort = 30002, int dashboardPort = 29999, int rtPort = 30003)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            ScriptPort = scriptPort;
            DashboardPort = dashboardPort;
            RealTimePort = rtPort;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        public OperateResult Connect()
        {
            try
            {
                lock (_lock)
                {
                    if (_isConnected) return OperateResult.Success();

                    _scriptClient = new System.Net.Sockets.TcpClient();
                    _scriptClient.Connect(IpAddress, ScriptPort);
                    _scriptClient.SendTimeout = Timeout;
                    _scriptClient.ReceiveTimeout = Timeout;

                    _dashboardClient = new System.Net.Sockets.TcpClient();
                    _dashboardClient.Connect(IpAddress, DashboardPort);
                    _dashboardClient.SendTimeout = Timeout;
                    _dashboardClient.ReceiveTimeout = Timeout;

                    _isConnected = true;
                }
                Log.Debug($"UR Connected to {IpAddress}");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
        }

        public Task<OperateResult> ConnectAsync()
            => Task.Run(() => Connect());

        public void Disconnect()
        {
            lock (_lock)
            {
                _isConnected = false;
                try { _scriptClient?.Close(); } catch { }
                try { _dashboardClient?.Close(); } catch { }
                try { _rtClient?.Close(); } catch { }
                _scriptClient = null;
                _dashboardClient = null;
                _rtClient = null;
            }
        }

        // ═══════════════════════════════════════════
        //  URScript 命令发送
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送 URScript 命令到 Secondary Interface。
        /// URScript 命令以换行符结尾，机器人执行后无响应（开环命令）。
        /// </summary>
        public OperateResult SendScript(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return OperateResult.Failed("URScript 命令不能为空");

            lock (_lock)
            {
                if (_scriptClient == null || !_isConnected)
                    return OperateResult.Failed("未连接");

                try
                {
                    byte[] data = Encoding.ASCII.GetBytes(script + "\n");
                    var stream = _scriptClient.GetStream();
                    stream.Write(data, 0, data.Length);
                    Log.Debug($"UR Script TX: {script}");
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    return OperateResult.Failed($"发送失败: {ex.Message}");
                }
            }
        }

        // ── 运动命令 ──

        /// <summary>MoveL — 线性运动到目标位姿。</summary>
        public OperateResult MoveL(double[] pose, double acceleration = 1.2, double velocity = 0.25, double time = 0, double radius = 0)
        {
            if (pose == null || pose.Length != 6)
                return OperateResult.Failed("位姿数组必须包含 6 个元素 (X,Y,Z,Rx,Ry,Rz)");

            string script = $"movel(p[{FormatPose(pose)}], a={acceleration}, v={velocity}, t={time}, r={radius})";
            return SendScript(script);
        }

        /// <summary>MoveJ — 关节运动到目标位姿。</summary>
        public OperateResult MoveJ(double[] pose, double acceleration = 1.4, double velocity = 1.05, double time = 0, double radius = 0)
        {
            if (pose == null || pose.Length != 6)
                return OperateResult.Failed("位姿数组必须包含 6 个元素");

            string script = $"movej(p[{FormatPose(pose)}], a={acceleration}, v={velocity}, t={time}, r={radius})";
            return SendScript(script);
        }

        /// <summary>MoveP — 过渡线性运动。</summary>
        public OperateResult MoveP(double[] pose, double acceleration = 1.2, double velocity = 0.25, double radius = 0.01)
        {
            if (pose == null || pose.Length != 6)
                return OperateResult.Failed("位姿数组必须包含 6 个元素");

            return SendScript($"movep(p[{FormatPose(pose)}], a={acceleration}, v={velocity}, r={radius})");
        }

        /// <summary>Servoj — 伺服关节控制（实时控制）。</summary>
        public OperateResult ServoJ(double[] jointPositions, double acceleration = 1.2, double velocity = 0.25, double lookaheadTime = 0.1, double servoGain = 200)
        {
            if (jointPositions == null || jointPositions.Length != 6)
                return OperateResult.Failed("关节位置数组必须包含 6 个元素");

            return SendScript($"servoj([{FormatPose(jointPositions)}], a={acceleration}, v={velocity}, t=0.008, lookahead_time={lookaheadTime}, gain={servoGain})");
        }

        // ── I/O 控制 ──

        /// <summary>设置标准数字输出。</summary>
        public OperateResult SetDigitalOut(int id, bool value)
            => SendScript($"set_digital_out({id}, {(value ? "True" : "False")})");

        /// <summary>设置标准模拟输出。</summary>
        public OperateResult SetAnalogOut(int id, double value)
            => SendScript($"set_analog_out({id}, {value.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

        /// <summary>设置工具数字输出。</summary>
        public OperateResult SetToolDigitalOut(int id, bool value)
            => SendScript($"set_tool_digital_out({id}, {(value ? "True" : "False")})");

        // ── 速度控制 ──

        /// <summary>SpeedL — 工具速度控制。</summary>
        public OperateResult SpeedL(double[] velocity, double acceleration = 1.2, double time = 0)
        {
            if (velocity == null || velocity.Length != 6)
                return OperateResult.Failed("速度数组必须包含 6 个元素");

            return SendScript($"speedl([{FormatPose(velocity)}], a={acceleration}, t={time})");
        }

        /// <summary>停止运动。</summary>
        public OperateResult StopL(double acceleration = 5.0)
            => SendScript($"stopl({acceleration})");

        // ═══════════════════════════════════════════
        //  Dashboard 命令
        // ═══════════════════════════════════════════

        /// <summary>发送 Dashboard 命令并返回响应。</summary>
        public OperateResult<string> SendDashboardCommand(string command)
        {
            lock (_lock)
            {
                if (_dashboardClient == null || !_isConnected)
                    return OperateResult<string>.Failed("未连接到 Dashboard");

                try
                {
                    byte[] data = Encoding.ASCII.GetBytes(command);
                    var stream = _dashboardClient.GetStream();
                    stream.Write(data, 0, data.Length);

                    // 读取响应（以换行符结束）
                    var buffer = new byte[1024];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        return OperateResult<string>.Failed("Dashboard 无响应");

                    string response = Encoding.ASCII.GetString(buffer, 0, read).Trim();
                    Log.Debug($"UR Dashboard: cmd={command.Trim()} resp={response}");
                    return OperateResult<string>.Success(response);
                }
                catch (Exception ex)
                {
                    return OperateResult<string>.Failed($"Dashboard 命令失败: {ex.Message}");
                }
            }
        }

        /// <summary>运行程序。</summary>
        public OperateResult<string> Play() => SendDashboardCommand(UrConstants.CmdPlay);

        /// <summary>暂停程序。</summary>
        public OperateResult<string> Pause() => SendDashboardCommand(UrConstants.CmdPause);

        /// <summary>停止程序。</summary>
        public OperateResult<string> Stop() => SendDashboardCommand(UrConstants.CmdStop);

        /// <summary>检查是否正在运行。</summary>
        public OperateResult<bool> IsRunning()
        {
            var result = SendDashboardCommand(UrConstants.CmdRunning);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message);
            return OperateResult<bool>.Success(result.Content.ToUpperInvariant().Contains("TRUE"));
        }

        /// <summary>获取机器人模式。</summary>
        public OperateResult<string> GetRobotMode() => SendDashboardCommand(UrConstants.CmdRobotMode);

        /// <summary>加载程序。</summary>
        public OperateResult<string> LoadProgram(string programPath)
            => SendDashboardCommand($"load {programPath}\n");

        /// <summary>释放刹车。</summary>
        public OperateResult<string> BrakeRelease() => SendDashboardCommand(UrConstants.CmdBrakeRelease);

        // ═══════════════════════════════════════════
        //  IReadWriteDevice（通过 UR 寄存器）
        // ═══════════════════════════════════════════

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
            => OperateResult<byte[]>.Failed("UR 使用寄存器读写，请使用 ReadInt32/ReadFloat 等方法");

        public OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("UR 使用寄存器写入，请使用 Write 方法");

        /// <summary>读取浮点寄存器 (read_input_float_register)。</summary>
        public OperateResult<double> ReadFloatRegister(int registerId)
        {
            return SendScript($"read_input_float_register({registerId})")
                .IsSuccess
                ? OperateResult<double>.Failed("UR 寄存器读取需要 Real-Time Interface")
                : OperateResult<double>.Failed("UR 不支持通过脚本直接读取寄存器值");
        }

        /// <summary>写入浮点寄存器 (write_float_register)。</summary>
        public OperateResult WriteFloatRegister(int registerId, double value)
        {
            string formatted = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return SendScript($"write_float_register({registerId}, {formatted})");
        }

        /// <summary>写入整数寄存器。</summary>
        public OperateResult WriteIntRegister(int registerId, int value)
            => SendScript($"write_int_register({registerId}, {value})");

        // ── IReadWriteDevice 占位 ──

        public OperateResult<bool> ReadBool(string address) => OperateResult<bool>.Failed("请使用 UR Dashboard 或 I/O 方法");
        public OperateResult<short> ReadInt16(string address) => OperateResult<short>.Failed("请使用 ReadFloatRegister");
        public OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Failed("请使用 ReadFloatRegister");
        public OperateResult<int> ReadInt32(string address) => OperateResult<int>.Failed("请使用 ReadFloatRegister");
        public OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Failed("请使用 ReadFloatRegister");
        public OperateResult<long> ReadInt64(string address) => OperateResult<long>.Failed("请使用 ReadFloatRegister");
        public OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Failed("请使用 ReadFloatRegister");
        public OperateResult<float> ReadFloat(string address) => OperateResult<float>.Failed("请使用 ReadFloatRegister");
        public OperateResult<double> ReadDouble(string address) => OperateResult<double>.Failed("请使用 ReadFloatRegister");
        public OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Failed("请使用 UR Script 方法");
        public OperateResult Write(string address, bool value) => SetDigitalOut(0, value);
        public OperateResult Write(string address, short value) => WriteIntRegister(0, value);
        public OperateResult Write(string address, ushort value) => WriteIntRegister(0, value);
        public OperateResult Write(string address, int value) => WriteIntRegister(0, value);
        public OperateResult Write(string address, uint value) => WriteIntRegister(0, (int)value);
        public OperateResult Write(string address, long value) => WriteIntRegister(0, (int)value);
        public OperateResult Write(string address, ulong value) => WriteIntRegister(0, (int)value);
        public OperateResult Write(string address, float value) => WriteFloatRegister(0, value);
        public OperateResult Write(string address, double value) => WriteFloatRegister(0, value);
        public OperateResult Write(string address, string value) => SendScript(value);

        public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.FromResult(ReadBool(address));
        public Task<OperateResult<short>> ReadInt16Async(string address) => Task.FromResult(ReadInt16(address));
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.FromResult(ReadUInt16(address));
        public Task<OperateResult<int>> ReadInt32Async(string address) => Task.FromResult(ReadInt32(address));
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.FromResult(ReadUInt32(address));
        public Task<OperateResult<long>> ReadInt64Async(string address) => Task.FromResult(ReadInt64(address));
        public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.FromResult(ReadUInt64(address));
        public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.FromResult(ReadFloat(address));
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.FromResult(ReadDouble(address));
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.FromResult(ReadString(address, length));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.FromResult(ReadBytes(address, length));
        public Task<OperateResult> WriteAsync(string address, bool value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, short value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, int value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, float value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, string value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.FromResult(Write(address, data));
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.FromResult(Write(address, value));

        // ── 辅助 ──

        private static string FormatPose(double[] pose)
        {
            var parts = new string[pose.Length];
            for (int i = 0; i < pose.Length; i++)
                parts[i] = pose[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(", ", parts);
        }

        public void Dispose()
        {
            Disconnect();
            GC.SuppressFinalize(this);
        }

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

        // ═══════════════════════════════════════════
        //  ISubscribeDevice — 数据订阅接口
        // ═══════════════════════════════════════════

        private readonly object _monitorLock = new object();
        private readonly Dictionary<string, MonitorEntry> _monitors = new Dictionary<string, MonitorEntry>();
        private bool _monitoring;
        private Timer? _monitorTimer;

        private class MonitorEntry
        {
            public string Address = "";
            public string DataType = "Int16";
            public int IntervalMs = 1000;
            public object? LastValue;
        }

        /// <summary>数据变化事件。</summary>
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        /// <summary>订阅指定地址的数据变化。</summary>
        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        {
            lock (_monitorLock)
            {
                _monitors[address] = new MonitorEntry
                {
                    Address = address,
                    DataType = dataType,
                    IntervalMs = intervalMs,
                    LastValue = null
                };
            }
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        /// <summary>启动所有订阅。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

        /// <summary>停止所有订阅。</summary>
        public void StopSubscriptions()
        {
            _monitoring = false;
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        private void PollMonitors(object? state)
        {
            if (!_monitoring) return;
            try
            {
                List<MonitorEntry> entries;
                lock (_monitorLock) { entries = new List<MonitorEntry>(_monitors.Values); }

                foreach (var entry in entries)
                {
                    try
                    {
                        object? current = entry.DataType switch
                        {
                            "Int16" => ReadInt16(entry.Address).Content,
                            "UInt16" => ReadUInt16(entry.Address).Content,
                            "Int32" => ReadInt32(entry.Address).Content,
                            "Float" => ReadFloat(entry.Address).Content,
                            "Bool" => ReadBool(entry.Address).Content,
                            "String" => ReadString(entry.Address, 10).Content,
                            _ => null
                        };

                        if (current != null && !Equals(current, entry.LastValue))
                        {
                            if (entry.LastValue == null) { entry.LastValue = current; continue; }
                            var args = new DataChangeEventArgs
                            {
                                Address = entry.Address,
                                OldValue = entry.LastValue,
                                NewValue = current,
                                Timestamp = DateTime.Now,
                                Quality = "Good"
                            };
                            entry.LastValue = current;
                            OnDataChanged?.Invoke(this, args);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
