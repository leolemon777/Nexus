using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Robot.Yaskawa
{
    /// <summary>
    /// YASKAWA YRC1000 高速以太网通讯客户端。
    /// <para>用于安川机器人控制器 YRC1000 / DX200 的以太网通讯。</para>
    /// <para>帧格式: Header(16) + Data</para>
    /// <para>Header: RequestId(2) + BlockId(1) + Reserved(1) + Cmd(2) + SubCmd(2) + DataLen(4) + Reserved(4)</para>
    /// <para>支持读写 IO、读写变量、读取位置、状态查询、伺服控制。</para>
    /// </summary>
    public class Yrc1000Client : TcpDeviceBase
    {
        // ── 命令码 ──────────────────────────────
        private const ushort CMD_READ_IO_INPUT = 0x0101;
        private const ushort CMD_READ_IO_OUTPUT = 0x0102;
        private const ushort CMD_WRITE_IO = 0x0103;
        private const ushort CMD_READ_REGISTER = 0x0201;
        private const ushort CMD_WRITE_REGISTER = 0x0202;
        private const ushort CMD_READ_VARIABLE = 0x0301;
        private const ushort CMD_WRITE_VARIABLE = 0x0302;
        private const ushort CMD_READ_POSITION = 0x0401;
        private const ushort CMD_READ_STATUS = 0x0501;
        private const ushort CMD_SERVO_ON = 0x0601;
        private const ushort CMD_SERVO_OFF = 0x0602;
        private const ushort CMD_JOB_START = 0x0701;
        private const ushort CMD_JOB_STOP = 0x0702;

        // ── 变量类型 ─────────────────────────────
        private const byte VAR_TYPE_BYTE = 0x01;
        private const byte VAR_TYPE_INTEGER = 0x02;
        private const byte VAR_TYPE_DOUBLE = 0x03;
        private const byte VAR_TYPE_REAL = 0x04;

        // ── 属性 ─────────────────────────────────

        /// <summary>块 ID（控制器编号，多控制器时使用）。</summary>
        public byte BlockId { get; set; } = 0x00;

        private ushort _requestId;
        private readonly object _idLock = new object();

        // ── TcpDeviceBase 抽象实现 ───────────────

        protected override int ResponseHeaderLength => 16;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 16) return 0;
            // 数据长度在 header[8..11]（小端序）
            return BitConverter.ToInt32(header, 8);
        }

        // ── 构造 ────────────────────────────────

        public Yrc1000Client(string ip, int port = 80, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  IO 读写
        // ═══════════════════════════════════════════

        /// <summary>读取通用输入信号（CN00000-CN00255）。</summary>
        /// <param name="address">输入地址（0-255）。</param>
        public OperateResult<bool> ReadInput(int address)
        {
            var cmd = BuildReadCommand(CMD_READ_IO_INPUT, address, 1);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<bool>.Failed(recv.Message);

            return ParseBoolResponse(recv.Content);
        }

        /// <summary>批量读取通用输入信号。</summary>
        public OperateResult<bool[]> ReadInputs(int startAddress, int count)
        {
            var cmd = BuildReadCommand(CMD_READ_IO_INPUT, startAddress, count);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<bool[]>.Failed(recv.Message);

            return ParseBoolArrayResponse(recv.Content, count);
        }

        /// <summary>读取通用输出信号。</summary>
        public OperateResult<bool> ReadOutput(int address)
        {
            var cmd = BuildReadCommand(CMD_READ_IO_OUTPUT, address, 1);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<bool>.Failed(recv.Message);

            return ParseBoolResponse(recv.Content);
        }

        /// <summary>写入通用输出信号。</summary>
        public OperateResult WriteOutput(int address, bool value)
        {
            byte[] data = new byte[] { (byte)(value ? 1 : 0) };
            var cmd = BuildWriteCommand(CMD_WRITE_IO, address, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckResponse(recv.Content);
        }

        /// <summary>批量写入通用输出信号。</summary>
        public OperateResult WriteOutputs(int startAddress, bool[] values)
        {
            byte[] data = new byte[values.Length];
            for (int i = 0; i < values.Length; i++)
                data[i] = (byte)(values[i] ? 1 : 0);

            var cmd = BuildWriteCommand(CMD_WRITE_IO, startAddress, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  寄存器读写
        // ═══════════════════════════════════════════

        /// <summary>读取整数寄存器。</summary>
        public OperateResult<int> ReadRegister(int index)
        {
            var cmd = BuildReadCommand(CMD_READ_REGISTER, index, 1);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<int>.Failed(recv.Message);
            return ParseIntResponse(recv.Content);
        }

        /// <summary>写入整数寄存器。</summary>
        public OperateResult WriteRegister(int index, int value)
        {
            byte[] data = BitConverter.GetBytes(value);
            var cmd = BuildWriteCommand(CMD_WRITE_REGISTER, index, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckResponse(recv.Content);
        }

        /// <summary>读取浮点寄存器。</summary>
        public OperateResult<double> ReadRegisterDouble(int index)
        {
            var cmd = BuildReadCommand(CMD_READ_REGISTER, index, 1);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<double>.Failed(recv.Message);
            return ParseDoubleResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  变量读写
        // ═══════════════════════════════════════════

        /// <summary>读取用户变量（字节类型）。</summary>
        public OperateResult<byte> ReadVariableByte(int index)
        {
            var subData = new byte[] { VAR_TYPE_BYTE, (byte)(index >> 8), (byte)(index & 0xFF) };
            var cmd = BuildWriteCommand(CMD_READ_VARIABLE, 0, subData);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<byte>.Failed(recv.Message);

            var resp = ParseResponseData(recv.Content);
            if (!resp.IsSuccess) return OperateResult<byte>.Failed(resp.Message);
            return resp.Content.Length > 0
                ? OperateResult<byte>.Success(resp.Content[0])
                : OperateResult<byte>.Failed("变量数据为空");
        }

        /// <summary>读取用户变量（整数类型）。</summary>
        public OperateResult<int> ReadVariableInt(int index)
        {
            var subData = new byte[] { VAR_TYPE_INTEGER, (byte)(index >> 8), (byte)(index & 0xFF) };
            var cmd = BuildWriteCommand(CMD_READ_VARIABLE, 0, subData);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<int>.Failed(recv.Message);

            var resp = ParseResponseData(recv.Content);
            if (!resp.IsSuccess) return OperateResult<int>.Failed(resp.Message);
            if (resp.Content.Length < 4)
                return OperateResult<int>.Failed("变量数据不足 4 字节");
            return OperateResult<int>.Success(BitConverter.ToInt32(resp.Content, 0));
        }

        /// <summary>写入用户变量（整数类型）。</summary>
        public OperateResult WriteVariableInt(int index, int value)
        {
            byte[] data = new byte[7];
            data[0] = VAR_TYPE_INTEGER;
            data[1] = (byte)(index >> 8);
            data[2] = (byte)(index & 0xFF);
            BitConverter.GetBytes(value).CopyTo(data, 3);

            var cmd = BuildWriteCommand(CMD_WRITE_VARIABLE, 0, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  机器人状态与位置
        // ═══════════════════════════════════════════

        /// <summary>读取当前机械臂关节角度（度）。</summary>
        public OperateResult<double[]> ReadJointPosition()
        {
            var cmd = BuildReadCommand(CMD_READ_POSITION, 0, 6); // 6 轴
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<double[]>.Failed(recv.Message);
            return ParseDoubleArrayResponse(recv.Content, 6);
        }

        /// <summary>读取机器人运行状态。</summary>
        public OperateResult<YrcRobotStatus> ReadRobotStatus()
        {
            var cmd = BuildReadCommand(CMD_READ_STATUS, 0, 8);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<YrcRobotStatus>.Failed(recv.Message);

            var data = ParseResponseData(recv.Content);
            if (!data.IsSuccess) return OperateResult<YrcRobotStatus>.Failed(data.Message);

            var status = new YrcRobotStatus();
            if (data.Content.Length >= 2)
                status.ServoState = data.Content[0];
            if (data.Content.Length >= 4)
                status.RunState = data.Content[1];
            if (data.Content.Length >= 6)
                status.AlarmCode = BitConverter.ToUInt16(data.Content, 2);
            if (data.Content.Length >= 8)
                status.ErrorCode = BitConverter.ToUInt16(data.Content, 4);

            return OperateResult<YrcRobotStatus>.Success(status);
        }

        // ═══════════════════════════════════════════
        //  控制命令
        // ═══════════════════════════════════════════

        /// <summary>伺服上电。</summary>
        public OperateResult ServoOn()
        {
            var cmd = BuildWriteCommand(CMD_SERVO_ON, 0, new byte[] { 0x01 });
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckResponse(recv.Content);
        }

        /// <summary>伺服下电。</summary>
        public OperateResult ServoOff()
        {
            var cmd = BuildWriteCommand(CMD_SERVO_OFF, 0, new byte[] { 0x01 });
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckResponse(recv.Content);
        }

        /// <summary>启动 JOB 执行。</summary>
        public OperateResult JobStart(string jobName)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(jobName ?? string.Empty);
            byte[] data = new byte[1 + nameBytes.Length];
            data[0] = (byte)nameBytes.Length;
            nameBytes.CopyTo(data, 1);

            var cmd = BuildWriteCommand(CMD_JOB_START, 0, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckResponse(recv.Content);
        }

        /// <summary>停止 JOB 执行。</summary>
        public OperateResult JobStop()
        {
            var cmd = BuildWriteCommand(CMD_JOB_STOP, 0, new byte[] { 0x01 });
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 基础实现
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            if (int.TryParse(address, out int addr))
            {
                var cmd = BuildReadCommand(CMD_READ_REGISTER, addr, length);
                var recv = SendAndReceive(cmd);
                if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);
                return ParseResponseData(recv.Content);
            }
            return OperateResult<byte[]>.Failed($"地址格式错误: {address}");
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (int.TryParse(address, out int addr))
            {
                var cmd = BuildWriteCommand(CMD_WRITE_REGISTER, addr, data);
                var recv = SendAndReceive(cmd);
                if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
                return CheckResponse(recv.Content);
            }
            return OperateResult.Failed($"地址格式错误: {address}");
        }

        // ═══════════════════════════════════════════
        //  命令构建
        // ═══════════════════════════════════════════

        /// <summary>构建 YRC1000 读取命令帧。</summary>
        /// <para>帧: ReqId(2) + BlockId(1) + Reserved(1) + Cmd(2) + SubCmd(2) + DataLen(4) + Addr(4) + Count(4)</para>
        public byte[] BuildReadCommand(ushort cmdCode, int address, int count)
        {
            ushort reqId;
            lock (_idLock) { reqId = ++_requestId; }

            byte[] frame = new byte[24]; // 16 header + 8 data (addr+count)
            frame[0] = (byte)(reqId >> 8);
            frame[1] = (byte)(reqId & 0xFF);
            frame[2] = BlockId;
            frame[3] = 0x00;
            frame[4] = (byte)(cmdCode >> 8);
            frame[5] = (byte)(cmdCode & 0xFF);
            frame[6] = 0x00;
            frame[7] = 0x00;

            int dataLen = 8; // address(4) + count(4)
            frame[8] = (byte)(dataLen >> 24);
            frame[9] = (byte)(dataLen >> 16);
            frame[10] = (byte)(dataLen >> 8);
            frame[11] = (byte)(dataLen & 0xFF);

            // reserved
            frame[12] = 0; frame[13] = 0; frame[14] = 0; frame[15] = 0;

            // address (big endian)
            frame[16] = (byte)(address >> 24);
            frame[17] = (byte)(address >> 16);
            frame[18] = (byte)(address >> 8);
            frame[19] = (byte)(address & 0xFF);

            // count (big endian)
            frame[20] = (byte)(count >> 24);
            frame[21] = (byte)(count >> 16);
            frame[22] = (byte)(count >> 8);
            frame[23] = (byte)(count & 0xFF);

            return frame;
        }

        /// <summary>构建 YRC1000 写入命令帧。</summary>
        public byte[] BuildWriteCommand(ushort cmdCode, int address, byte[] data)
        {
            ushort reqId;
            lock (_idLock) { reqId = ++_requestId; }

            int dataLen = 4 + data.Length; // address(4) + data
            byte[] frame = new byte[16 + dataLen];

            frame[0] = (byte)(reqId >> 8);
            frame[1] = (byte)(reqId & 0xFF);
            frame[2] = BlockId;
            frame[3] = 0x00;
            frame[4] = (byte)(cmdCode >> 8);
            frame[5] = (byte)(cmdCode & 0xFF);
            frame[6] = 0x00;
            frame[7] = 0x00;

            frame[8] = (byte)(dataLen >> 24);
            frame[9] = (byte)(dataLen >> 16);
            frame[10] = (byte)(dataLen >> 8);
            frame[11] = (byte)(dataLen & 0xFF);

            frame[12] = 0; frame[13] = 0; frame[14] = 0; frame[15] = 0;

            // address (big endian)
            frame[16] = (byte)(address >> 24);
            frame[17] = (byte)(address >> 16);
            frame[18] = (byte)(address >> 8);
            frame[19] = (byte)(address & 0xFF);

            // data
            data.CopyTo(frame, 20);

            return frame;
        }

        // ═══════════════════════════════════════════
        //  响应解析
        // ═══════════════════════════════════════════

        private static OperateResult CheckResponse(byte[] raw)
        {
            if (raw == null || raw.Length < 16)
                return OperateResult.Failed($"响应数据过短 ({raw?.Length ?? 0})");

            // 检查响应码（header[4..5] 为状态码）
            ushort status = (ushort)((raw[4] << 8) | raw[5]);
            if (status != 0)
                return OperateResult.Failed($"YRC1000 错误码: 0x{status:X4}");

            return OperateResult.Success();
        }

        private static OperateResult<byte[]> ParseResponseData(byte[] raw)
        {
            if (raw == null || raw.Length <= 16)
                return OperateResult<byte[]>.Failed($"响应数据过短 ({raw?.Length ?? 0})");

            var check = CheckResponse(raw);
            if (!check.IsSuccess) return OperateResult<byte[]>.Failed(check.Message);

            int dataLen = raw.Length - 16;
            byte[] data = new byte[dataLen];
            Array.Copy(raw, 16, data, 0, dataLen);
            return OperateResult<byte[]>.Success(data);
        }

        private static OperateResult<bool> ParseBoolResponse(byte[] raw)
        {
            var data = ParseResponseData(raw);
            if (!data.IsSuccess) return OperateResult<bool>.Failed(data.Message);
            return data.Content.Length > 0
                ? OperateResult<bool>.Success(data.Content[0] != 0)
                : OperateResult<bool>.Failed("IO 数据为空");
        }

        private static OperateResult<bool[]> ParseBoolArrayResponse(byte[] raw, int count)
        {
            var data = ParseResponseData(raw);
            if (!data.IsSuccess) return OperateResult<bool[]>.Failed(data.Message);

            var result = new bool[Math.Min(count, data.Content.Length)];
            for (int i = 0; i < result.Length; i++)
                result[i] = data.Content[i] != 0;
            return OperateResult<bool[]>.Success(result);
        }

        private static OperateResult<int> ParseIntResponse(byte[] raw)
        {
            var data = ParseResponseData(raw);
            if (!data.IsSuccess) return OperateResult<int>.Failed(data.Message);
            if (data.Content.Length < 4)
                return OperateResult<int>.Failed("数据不足 4 字节");
            return OperateResult<int>.Success(BitConverter.ToInt32(data.Content, 0));
        }

        private static OperateResult<double> ParseDoubleResponse(byte[] raw)
        {
            var data = ParseResponseData(raw);
            if (!data.IsSuccess) return OperateResult<double>.Failed(data.Message);
            if (data.Content.Length < 8)
                return OperateResult<double>.Failed("数据不足 8 字节");
            return OperateResult<double>.Success(BitConverter.ToDouble(data.Content, 0));
        }

        private static OperateResult<double[]> ParseDoubleArrayResponse(byte[] raw, int expectedAxes)
        {
            var data = ParseResponseData(raw);
            if (!data.IsSuccess) return OperateResult<double[]>.Failed(data.Message);

            int axes = Math.Min(expectedAxes, data.Content.Length / 8);
            if (axes == 0) return OperateResult<double[]>.Failed("位置数据为空");

            var result = new double[axes];
            for (int i = 0; i < axes; i++)
                result[i] = BitConverter.ToDouble(data.Content, i * 8);
            return OperateResult<double[]>.Success(result);
        }

        public override string ToString() => $"Yrc1000Client[{Ip}:{Port}]";
    }

    // ── 辅助类型 ──────────────────────────────

    /// <summary>YRC1000 机器人状态。</summary>
    public class YrcRobotStatus
    {
        /// <summary>伺服状态（0=OFF, 1=ON）。</summary>
        public byte ServoState { get; set; }

        /// <summary>运行状态（0=停止, 1=运行, 2=暂停, 3=急停）。</summary>
        public byte RunState { get; set; }

        /// <summary>报警码。</summary>
        public ushort AlarmCode { get; set; }

        /// <summary>错误码。</summary>
        public ushort ErrorCode { get; set; }

        public bool IsServoOn => ServoState != 0;

        public string RunStateDescription => RunState switch
        {
            0 => "停止",
            1 => "运行",
            2 => "暂停",
            3 => "急停",
            _ => $"未知({RunState})"
        };

        public override string ToString() =>
            $"Servo={ServoState}, Run={RunStateDescription}, Alarm=0x{AlarmCode:X4}, Err=0x{ErrorCode:X4}";
    }
}
