using System;
using System.Text;

namespace Nexus.Robot.Efort
{
    /// <summary>
    /// 埃夫特（EFORT）机器人 ER7BC10 通讯客户端。
    /// <para>基于 KEBA 控制器，端口 8008。</para>
    /// <para>请求帧: "MessageHead"(16B) + 总长度(2B) + 命令码(2B, 1001) + 心跳(2B) + "MessageTail"(16B) = 38 字节</para>
    /// <para>响应帧: 788 字节固定长度，包含轴位置/速度/IO/状态等完整数据。</para>
    /// </summary>
    public class EfortClient : TcpDeviceBase
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
            int offset = 0;

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
