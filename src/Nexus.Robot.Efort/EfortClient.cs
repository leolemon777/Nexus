using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Robot.Efort
{
    /// <summary>
    /// 埃夫特（EFORT）机器人 ER7BC10 通讯客户端。
    /// <para>基于 KEBA 控制器，端口 8008。</para>
    /// <para>请求帧: "MessageHead"(16B) + 总长度(2B) + 命令码(2B, 1001) + 心跳(2B) + "MessageTail"(16B) = 38 字节</para>
    /// <para>响应帧: 788 字节固定长度，包含轴位置/速度/IO/状态等完整数据。</para>
    /// </summary>
    public class EfortClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        // ── TcpDeviceBase 抽象实现 ───────────────
        protected override int ResponseHeaderLength => 0;

        protected override int GetResponsePayloadLength(byte[] header) => 0;

        private ushort _heartbeat;
        private readonly object _hbLock = new object();

        // ── 常量 ────────────────────────────────
        private const string MSG_HEAD = "MessageHead";
        private const string MSG_TAIL = "MessageTail";
        private const ushort CMD_READ = 1001;
        private const int RESPONSE_LENGTH = 788;

        // ── 构造 ────────────────────────────────

        public EfortClient(string ip, int port = 8008, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  读取机器人数据
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取机器人完整状态数据。
        /// </summary>
        public OperateResult<EfortData> ReadRobotData()
        {
            var recv = SendCommand();
            if (!recv.IsSuccess) return OperateResult<EfortData>.Failed(recv.Message);
            return EfortData.ParseFrom(recv.Content);
        }

        /// <summary>
        /// 读取原始响应字节。
        /// </summary>
        public OperateResult<byte[]> ReadRaw()
        {
            return SendCommand();
        }

        // ═══════════════════════════════════════════
        //  命令构建（公开供测试）
        // ═══════════════════════════════════════════

        /// <summary>构建读取命令（38 字节固定帧）。</summary>
        public byte[] BuildReadCommand()
        {
            byte[] cmd = new byte[38];

            // "MessageHead" — 16 字节 ASCII
            byte[] headBytes = Encoding.ASCII.GetBytes(MSG_HEAD);
            Array.Copy(headBytes, 0, cmd, 0, headBytes.Length);

            // 总长度 (offset 16, 2B, LE)
            cmd[16] = (byte)(cmd.Length & 0xFF);
            cmd[17] = (byte)(cmd.Length >> 8);

            // 命令码 (offset 18, 2B, LE) = 1001
            cmd[18] = (byte)(CMD_READ & 0xFF);
            cmd[19] = (byte)(CMD_READ >> 8);

            // 心跳计数 (offset 20, 2B, LE)
            ushort hb;
            lock (_hbLock) { hb = _heartbeat++; }
            cmd[20] = (byte)(hb & 0xFF);
            cmd[21] = (byte)(hb >> 8);

            // "MessageTail" — 16 字节 ASCII (offset 22)
            byte[] tailBytes = Encoding.ASCII.GetBytes(MSG_TAIL);
            Array.Copy(tailBytes, 0, cmd, 22, tailBytes.Length);

            return cmd;
        }

        // ═══════════════════════════════════════════
        //  内部实现
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendCommand()
        {
            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    byte[] cmd = BuildReadCommand();
                    RaiseMessageSent(DataConverter.ToHexString(cmd));

                    _stream!.Write(cmd, 0, cmd.Length);

                    // 读取 788 字节固定响应
                    byte[] response = ReadExact(_stream, RESPONSE_LENGTH);
                    RaiseMessageReceived(DataConverter.ToHexString(response));

                    return OperateResult<byte[]>.Success(response);
                }
                catch (Exception ex)
                {
                    RaiseError($"EFORT 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"EFORT 通讯异常: {ex.Message}");
                }
            }
        }

        /// <summary>从 NetworkStream 读取精确字节数。</summary>
        private static byte[] ReadExact(System.Net.Sockets.NetworkStream ns, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = ns.Read(buffer, offset, count - offset);
                if (read == 0)
                    throw new System.IO.IOException($"连接关闭，已读取 {offset}/{count} 字节");
                offset += read;
            }
            return buffer;
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                var conn = Connect();
                if (!conn.IsSuccess) throw new InvalidOperationException($"EFORT 连接失败: {conn.Message}");
            }
        }

        public override string ToString() => $"EfortClient[{Ip}:{Port}]";

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 类型化读写
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var recv = SendCommand();
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            if (!int.TryParse(address, out int offset))
                return OperateResult<byte[]>.Failed($"地址格式错误: {address}，需为字节偏移量");
            if (offset < 0 || offset + length > recv.Content.Length)
                return OperateResult<byte[]>.Failed($"读取范围超出: offset={offset}, length={length}, 总长={recv.Content.Length}");

            byte[] result = new byte[length];
            Buffer.BlockCopy(recv.Content, offset, result, 0, length);
            return OperateResult<byte[]>.Success(result);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 1);
            return r.IsSuccess ? OperateResult<bool>.Success(r.Content[0] != 0) : OperateResult<bool>.Failed(r.Message);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 2);
            return r.IsSuccess ? OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0)) : OperateResult<short>.Failed(r.Message);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadBytes(address, 2);
            return r.IsSuccess ? OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0)) : OperateResult<ushort>.Failed(r.Message);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 4);
            return r.IsSuccess ? OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0)) : OperateResult<int>.Failed(r.Message);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadBytes(address, 4);
            return r.IsSuccess ? OperateResult<uint>.Success(DataConverter.ToUInt32(r.Content, 0)) : OperateResult<uint>.Failed(r.Message);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 8);
            return r.IsSuccess ? OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0)) : OperateResult<long>.Failed(r.Message);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadBytes(address, 8);
            return r.IsSuccess ? OperateResult<ulong>.Success(DataConverter.ToUInt64(r.Content, 0)) : OperateResult<ulong>.Failed(r.Message);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 4);
            return r.IsSuccess ? OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0)) : OperateResult<float>.Failed(r.Message);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadBytes(address, 8);
            return r.IsSuccess ? OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0)) : OperateResult<double>.Failed(r.Message);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            return r.IsSuccess ? OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length)) : OperateResult<string>.Failed(r.Message);
        }

        public override OperateResult Write(string address, bool value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, short value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, ushort value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, int value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, uint value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, long value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, ulong value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, float value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, double value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("EFORT 机器人不支持写入操作");

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

        /// <inheritdoc/>
        protected override byte[]? BuildHeartbeat()
        {
            try { return BuildReadCommand(); }
            catch { return null; }
        }
    }

    /// <summary>
    /// 埃夫特机器人状态数据结构（ER7BC10 新版格式，788 字节响应）。
    /// </summary>
    public class EfortData
    {
        /// <summary>报文开始标记。</summary>
        public string PacketStart { get; set; } = "";

        /// <summary>数据命令码。</summary>
        public ushort PacketOrders { get; set; }

        /// <summary>数据心跳。</summary>
        public ushort PacketHeartbeat { get; set; }

        /// <summary>报警状态（1=有报警，0=无报警）。</summary>
        public byte ErrorStatus { get; set; }

        /// <summary>急停状态（1=无急停，0=有急停）。</summary>
        public byte EmergencyStopStatus { get; set; }

        /// <summary>权限状态（1=有权限，0=无权限）。</summary>
        public byte AuthorityStatus { get; set; }

        /// <summary>伺服状态（1=使能，0=未使能）。</summary>
        public byte ServoStatus { get; set; }

        /// <summary>轴运动状态（1=运动中，0=停止）。</summary>
        public byte AxisMoveStatus { get; set; }

        /// <summary>程序运行状态（1=运行中，0=未运行）。</summary>
        public byte ProgramRunStatus { get; set; }

        /// <summary>程序加载状态（1=已加载，0=未加载）。</summary>
        public byte ProgramLoadStatus { get; set; }

        /// <summary>程序暂停状态（1=暂停，0=未暂停）。</summary>
        public byte ProgramHoldStatus { get; set; }

        /// <summary>模式状态（1=手动，2=自动，3=远程）。</summary>
        public ushort ModeStatus { get; set; }

        /// <summary>速度百分比。</summary>
        public ushort SpeedStatus { get; set; }

        /// <summary>数字输出状态（32字节）。</summary>
        public byte[] DigitalOutputs { get; set; } = new byte[32];

        /// <summary>数字输入状态（32字节）。</summary>
        public byte[] DigitalInputs { get; set; } = new byte[32];

        /// <summary>整数输出（32个int）。</summary>
        public int[] IntegerOutputs { get; set; } = new int[32];

        /// <summary>整数输入（32个int）。</summary>
        public int[] IntegerInputs { get; set; } = new int[32];

        /// <summary>加载工程名。</summary>
        public string ProjectName { get; set; } = "";

        /// <summary>加载程序名。</summary>
        public string ProgramName { get; set; } = "";

        /// <summary>错误信息。</summary>
        public string ErrorText { get; set; } = "";

        /// <summary>一到七轴角度（7个float）。</summary>
        public float[] AxisPositions { get; set; } = new float[7];

        /// <summary>笛卡尔坐标 X,Y,Z,A,B,C（6个float）。</summary>
        public float[] CartesianPositions { get; set; } = new float[6];

        /// <summary>一到七轴速度（7个float）。</summary>
        public float[] AxisSpeeds { get; set; } = new float[7];

        /// <summary>一到七轴加速度（7个float）。</summary>
        public float[] AxisAccelerations { get; set; } = new float[7];

        /// <summary>一到七轴加加速度（7个float）。</summary>
        public float[] AxisJerk { get; set; } = new float[7];

        /// <summary>一到七轴力矩（7个float）。</summary>
        public float[] AxisTorques { get; set; } = new float[7];

        /// <summary>轴反向计数（7个int）。</summary>
        public int[] AxisDirectionCounts { get; set; } = new int[7];

        /// <summary>轴工作总时长（7个int）。</summary>
        public int[] AxisWorkTimes { get; set; } = new int[7];

        /// <summary>设备开机总时长。</summary>
        public int DeviceUptime { get; set; }

        /// <summary>报文结束标记。</summary>
        public string PacketEnd { get; set; } = "";

        /// <summary>
        /// 从原始字节数组解析 EFORT 机器人数据（新版 788 字节格式）。
        /// </summary>
        public static OperateResult<EfortData> ParseFrom(byte[] data)
        {
            if (data == null || data.Length < RESPONSE_LENGTH)
                return OperateResult<EfortData>.Failed($"数据长度不足，需要 {RESPONSE_LENGTH} 字节，实际 {data?.Length ?? 0} 字节");

            var d = new EfortData();

            // PacketStart: 16 字节 ASCII
            d.PacketStart = Encoding.ASCII.GetString(data, 0, 16).Trim('\0', ' ');
            // 跳过 padding byte at 16
            // PacketOrders: offset 18, ushort LE
            d.PacketOrders = BitConverter.ToUInt16(data, 18);
            // PacketHeartbeat: offset 20, ushort LE
            d.PacketHeartbeat = BitConverter.ToUInt16(data, 20);

            // 状态字节: offset 22-29
            d.ErrorStatus = data[22];
            d.EmergencyStopStatus = data[23];
            d.AuthorityStatus = data[24];
            d.ServoStatus = data[25];
            d.AxisMoveStatus = data[26];
            d.ProgramRunStatus = data[27];
            d.ProgramLoadStatus = data[28];
            d.ProgramHoldStatus = data[29];

            // ModeStatus: offset 30, ushort LE
            d.ModeStatus = BitConverter.ToUInt16(data, 30);
            // SpeedStatus: offset 32, ushort LE
            d.SpeedStatus = BitConverter.ToUInt16(data, 32);

            // DigitalOutputs: 32 字节 at offset 34
            Array.Copy(data, 34, d.DigitalOutputs, 0, 32);
            // DigitalInputs: 32 字节 at offset 66
            Array.Copy(data, 66, d.DigitalInputs, 0, 32);

            // IntegerOutputs: 32 × int at offset 100
            for (int i = 0; i < 32; i++)
                d.IntegerOutputs[i] = BitConverter.ToInt32(data, 100 + 4 * i);

            // IntegerInputs: 32 × int at offset 228
            for (int i = 0; i < 32; i++)
                d.IntegerInputs[i] = BitConverter.ToInt32(data, 228 + 4 * i);

            // ProjectName: 32 字节 ASCII at offset 356
            d.ProjectName = Encoding.ASCII.GetString(data, 356, 32).Trim('\0', ' ');
            // ProgramName: 32 字节 ASCII at offset 388
            d.ProgramName = Encoding.ASCII.GetString(data, 388, 32).Trim('\0', ' ');
            // ErrorText: 128 字节 ASCII at offset 420
            d.ErrorText = Encoding.ASCII.GetString(data, 420, 128).Trim('\0', ' ');

            // AxisPositions: 7 × float at offset 548
            for (int i = 0; i < 7; i++)
                d.AxisPositions[i] = BitConverter.ToSingle(data, 548 + 4 * i);

            // CartesianPositions: 6 × float at offset 576
            for (int i = 0; i < 6; i++)
                d.CartesianPositions[i] = BitConverter.ToSingle(data, 576 + 4 * i);

            // AxisSpeeds: 7 × float at offset 600
            for (int i = 0; i < 7; i++)
                d.AxisSpeeds[i] = BitConverter.ToSingle(data, 600 + 4 * i);

            // AxisAccelerations: 7 × float at offset 628
            for (int i = 0; i < 7; i++)
                d.AxisAccelerations[i] = BitConverter.ToSingle(data, 628 + 4 * i);

            // AxisJerk: 7 × float at offset 656
            for (int i = 0; i < 7; i++)
                d.AxisJerk[i] = BitConverter.ToSingle(data, 656 + 4 * i);

            // AxisTorques: 7 × float at offset 684
            for (int i = 0; i < 7; i++)
                d.AxisTorques[i] = BitConverter.ToSingle(data, 684 + 4 * i);

            // AxisDirectionCounts: 7 × int at offset 712
            for (int i = 0; i < 7; i++)
                d.AxisDirectionCounts[i] = BitConverter.ToInt32(data, 712 + 4 * i);

            // AxisWorkTimes: 7 × int at offset 740
            for (int i = 0; i < 7; i++)
                d.AxisWorkTimes[i] = BitConverter.ToInt32(data, 740 + 4 * i);

            // DeviceUptime: int at offset 768
            d.DeviceUptime = BitConverter.ToInt32(data, 768);

            // PacketEnd: 16 字节 ASCII at offset 772
            d.PacketEnd = Encoding.ASCII.GetString(data, 772, 16).Trim('\0', ' ');

            return OperateResult<EfortData>.Success(d);
        }

        private const int RESPONSE_LENGTH = 788;
    }
}
