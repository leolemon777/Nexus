using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Nexus.Robot.Staubli
{
    /// <summary>
    /// Stäubli VAL3 协议客户端 — 支持 TX2/TS2 系列机器人。
    /// <para>通过 CS8/CS9 控制器的命令端口发送 VAL3 命令。</para>
    /// <para>支持运动控制、I/O 操作、程序管理。</para>
    /// </summary>
    public class StaubliClient : IBatchReadWrite
    {
        private readonly object _lock = new object();
        private System.Net.Sockets.TcpClient? _client;
        private bool _isConnected;
        protected ILogger Log { get; set; }

        /// <summary>控制器 IP 地址。</summary>
        public string IpAddress { get; }
        /// <summary>命令端口。</summary>
        public int Port { get; }
        /// <summary>超时（毫秒）。</summary>
        public int Timeout { get; set; } = 5000;

        public bool IsConnected
        {
            get { lock (_lock) return _isConnected && _client?.Connected == true; }
        }

        public StaubliClient(string ipAddress, int port = 59000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
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
                    _client = new System.Net.Sockets.TcpClient();
                    var ar = _client.BeginConnect(IpAddress, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(Timeout, false))
                    {
                        _client.Close();
                        _client = null;
                        return OperateResult.Failed("连接超时");
                    }
                    _client.EndConnect(ar);
                    _client.SendTimeout = Timeout;
                    _client.ReceiveTimeout = Timeout;
                    _isConnected = true;
                }
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
        }

        public Task<OperateResult> ConnectAsync() => Task.Run(() => Connect());

        public void Disconnect()
        {
            lock (_lock)
            {
                _isConnected = false;
                try { _client?.Close(); } catch { }
                _client = null;
            }
        }

        // ═══════════════════════════════════════════
        //  VAL3 命令发送
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送 VAL3 命令并返回响应。
        /// 命令以换行符结尾，响应也以换行符结束。
        /// </summary>
        public OperateResult<string> SendCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return OperateResult<string>.Failed("命令不能为空");

            lock (_lock)
            {
                if (_client == null || !_isConnected)
                    return OperateResult<string>.Failed("未连接");

                try
                {
                    var stream = _client.GetStream();
                    byte[] data = Encoding.ASCII.GetBytes(command + "\n");
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                    Log.Debug($"Staubli TX: {command}");

                    // 读取响应
                    var buffer = new byte[4096];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        return OperateResult<string>.Failed("无响应");

                    string response = Encoding.ASCII.GetString(buffer, 0, read).Trim();
                    Log.Debug($"Staubli RX: {response}");
                    return OperateResult<string>.Success(response);
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    return OperateResult<string>.Failed($"通信错误: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  运动控制
        // ═══════════════════════════════════════════

        /// <summary>MoveJ — 关节运动到目标位姿。</summary>
        public OperateResult MoveJ(double[] joints, double speed = 100, string? frame = null)
        {
            if (joints == null || joints.Length != 6)
                return OperateResult.Failed("关节角度数组必须包含 6 个元素");

            string frameStr = frame != null ? $"\"{frame}\"" : "here";
            string cmd = $"movej({FormatArray(joints)}, {speed.ToString(CultureInfo.InvariantCulture)})";
            return CheckResponse(SendCommand(cmd));
        }

        /// <summary>MoveL — 线性运动到目标位姿。</summary>
        public OperateResult MoveL(double[] pose, double speed = 1000)
        {
            if (pose == null || pose.Length != 6)
                return OperateResult.Failed("位姿数组必须包含 6 个元素 (X,Y,Z,Rx,Ry,Rz)");

            string cmd = $"movel({FormatArray(pose)}, {speed.ToString(CultureInfo.InvariantCulture)})";
            return CheckResponse(SendCommand(cmd));
        }

        /// <summary>停止运动。</summary>
        public OperateResult StopMotion()
            => CheckResponse(SendCommand("stop()"));

        /// <summary>延时（毫秒）。</summary>
        public OperateResult Delay(int milliseconds)
            => CheckResponse(SendCommand($"delay({milliseconds})"));

        // ═══════════════════════════════════════════
        //  I/O 操作
        // ═══════════════════════════════════════════

        /// <summary>设置数字输出。</summary>
        public OperateResult SetDigitalOutput(int ioId, bool value)
            => CheckResponse(SendCommand($"set(dio[{ioId}], {(value ? 1 : 0)})"));

        /// <summary>读取数字输入。</summary>
        public OperateResult<bool> GetDigitalInput(int ioId)
        {
            var result = SendCommand($"get(dio[{ioId}])");
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message);
            bool val = result.Content.Contains("1") || result.Content.ToUpperInvariant().Contains("TRUE");
            return OperateResult<bool>.Success(val);
        }

        /// <summary>读取机器人当前位姿。</summary>
        public OperateResult<string> GetRobotPose()
            => SendCommand("get(robot[0].pose)");

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
            => OperateResult<byte[]>.Failed("Stäubli 使用 VAL3 命令，请使用 SendCommand/运动/I/O 方法");

        public OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("Stäubli 使用 VAL3 命令，请使用 SendCommand/运动/I/O 方法");

        public OperateResult<bool> ReadBool(string address) => GetDigitalInput(0);
        public OperateResult<short> ReadInt16(string address) => OperateResult<short>.Failed("请使用 GetDigitalInput");
        public OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Failed("请使用 GetDigitalInput");
        public OperateResult<int> ReadInt32(string address) => OperateResult<int>.Failed("请使用 SendCommand");
        public OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Failed("请使用 SendCommand");
        public OperateResult<long> ReadInt64(string address) => OperateResult<long>.Failed("请使用 SendCommand");
        public OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Failed("请使用 SendCommand");
        public OperateResult<float> ReadFloat(string address) => OperateResult<float>.Failed("请使用 SendCommand");
        public OperateResult<double> ReadDouble(string address) => OperateResult<double>.Failed("请使用 SendCommand");
        public OperateResult<string> ReadString(string address, ushort length) => SendCommand(address);

        public OperateResult Write(string address, bool value) => SetDigitalOutput(0, value);
        public OperateResult Write(string address, short value) => SendCommand($"set(dio[0], {value})").IsSuccess ? OperateResult.Success() : OperateResult.Failed("写入失败");
        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) => SendCommand($"set(dio[0], {value})").IsSuccess ? OperateResult.Success() : OperateResult.Failed("写入失败");
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, (int)value);
        public OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public OperateResult Write(string address, float value) => SendCommand(address).IsSuccess ? OperateResult.Success() : OperateResult.Failed("写入失败");
        public OperateResult Write(string address, double value) => Write(address, (float)value);
        public OperateResult Write(string address, string value) => CheckResponse(SendCommand(value));

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

        // ── 辅助 ──

        private static OperateResult CheckResponse(OperateResult<string> result)
        {
            if (!result.IsSuccess) return result;
            if (result.Content.ToUpperInvariant().StartsWith("ERROR"))
                return OperateResult.Failed(StaubliErrorCodes.GetDescription(result.Content));
            return OperateResult.Success();
        }

        private static string FormatArray(double[] values)
        {
            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            return "{" + string.Join(", ", parts) + "}";
        }

        public void Dispose() { Disconnect(); GC.SuppressFinalize(this); }

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
    }
}
