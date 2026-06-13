using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Yokogawa
{
    /// <summary>
    /// 横河 PLC 二进制链接协议客户端。
    /// 帧格式: [ICF(1), CpuNum(1), PayloadLenHi(1), PayloadLenLo(1), Payload...]
    /// 响应头: 4 字节，后续长度 = header[2]*256 + header[3]
    /// 响应 Payload: [cmdEcho, errorCode, reserved, reserved, data...]
    /// 字节序: CDAB（16 位大端，32/64 位高低字交换）
    /// </summary>
    public class YokogawaClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        #region 常量

        /// <summary>读取继电器（位）。</summary>
        private const byte CMD_READ_RELAY = 0x01;

        /// <summary>写入继电器（位）。</summary>
        private const byte CMD_WRITE_RELAY = 0x02;

        /// <summary>随机读取继电器。</summary>
        private const byte CMD_RANDOM_READ_RELAY = 0x04;

        /// <summary>随机写入继电器。</summary>
        private const byte CMD_RANDOM_WRITE_RELAY = 0x05;

        /// <summary>读取字。</summary>
        private const byte CMD_READ_WORD = 0x11;

        /// <summary>写入字。</summary>
        private const byte CMD_WRITE_WORD = 0x12;

        /// <summary>随机读取字。</summary>
        private const byte CMD_RANDOM_READ_WORD = 0x14;

        /// <summary>随机写入字。</summary>
        private const byte CMD_RANDOM_WRITE_WORD = 0x15;

        /// <summary>启动 PLC。</summary>
        private const byte CMD_START = 0x45;

        /// <summary>停止 PLC。</summary>
        private const byte CMD_STOP = 0x46;

        /// <summary>随机操作最大地址数。</summary>
        private const int MAX_RANDOM_ADDRESSES = 32;

        #endregion

        #region 属性

        /// <summary>CPU 编号（默认 1）。</summary>
        public byte CpuNumber { get; set; } = 1;

        #endregion

        #region TcpDeviceBase 实现

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 4;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 4) return 0;
            return header[2] * 256 + header[3];
        }

        #endregion

        #region 构造

        /// <summary>
        /// 创建横河 PLC 二进制链接客户端。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">端口号（默认 8000）。</param>
        /// <param name="timeout">超时时间（毫秒）。</param>
        public YokogawaClient(string ip, int port = 8000, int timeout = 5000)
            : base(ip, port, timeout)
        {
        }

        #endregion

        #region 命令构建

        /// <summary>
        /// 构建读取命令（字或继电器）。
        /// 命令: [ICF, cpu, payloadLenHi, payloadLenLo, dataCode(6), countHi, countLo]
        /// </summary>
        private OperateResult<byte[]> BuildReadCommand(string address, ushort count, bool isBit)
        {
            var addrResult = YokogawaAddress.ParseFrom(address, count);
            if (!addrResult.IsSuccess)
                return OperateResult<byte[]>.Failed(addrResult.Message, addrResult.ErrorCode);

            byte icf = isBit ? CMD_READ_RELAY : CMD_READ_WORD;
            byte[] addrBytes = addrResult.Content.GetAddressBinaryContent();
            int payloadLen = 8; // 6 地址 + 2 计数

            byte[] cmd = new byte[4 + payloadLen];
            cmd[0] = icf;
            cmd[1] = CpuNumber;
            cmd[2] = (byte)((payloadLen >> 8) & 0xFF);
            cmd[3] = (byte)(payloadLen & 0xFF);
            Buffer.BlockCopy(addrBytes, 0, cmd, 4, 6);
            cmd[10] = (byte)((count >> 8) & 0xFF);
            cmd[11] = (byte)(count & 0xFF);

            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>
        /// 构建写字命令。
        /// 命令: [ICF, cpu, payloadLenHi, payloadLenLo, dataCode(6), wordCountHi, wordCountLo, data...]
        /// </summary>
        private OperateResult<byte[]> BuildWriteWordCommand(string address, byte[] data)
        {
            if (data == null || data.Length == 0)
                return OperateResult<byte[]>.Failed("写入数据不能为空");
            if (data.Length % 2 != 0)
                return OperateResult<byte[]>.Failed("写入数据长度必须是 2 的倍数");

            ushort wordCount = (ushort)(data.Length / 2);

            var addrResult = YokogawaAddress.ParseFrom(address, wordCount);
            if (!addrResult.IsSuccess)
                return OperateResult<byte[]>.Failed(addrResult.Message, addrResult.ErrorCode);

            byte[] addrBytes = addrResult.Content.GetAddressBinaryContent();
            int payloadLen = 8 + data.Length;

            byte[] cmd = new byte[4 + payloadLen];
            cmd[0] = CMD_WRITE_WORD;
            cmd[1] = CpuNumber;
            cmd[2] = (byte)((payloadLen >> 8) & 0xFF);
            cmd[3] = (byte)(payloadLen & 0xFF);
            Buffer.BlockCopy(addrBytes, 0, cmd, 4, 6);
            cmd[10] = (byte)((wordCount >> 8) & 0xFF);
            cmd[11] = (byte)(wordCount & 0xFF);
            Buffer.BlockCopy(data, 0, cmd, 12, data.Length);

            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>
        /// 构建写继电器命令。
        /// 命令: [ICF, cpu, payloadLenHi, payloadLenLo, dataCode(6), countHi, countLo, bit0, bit1, ...]
        /// </summary>
        private OperateResult<byte[]> BuildWriteRelayCommand(string address, bool[] values)
        {
            if (values == null || values.Length == 0)
                return OperateResult<byte[]>.Failed("写入数据不能为空");

            ushort count = (ushort)values.Length;

            var addrResult = YokogawaAddress.ParseFrom(address, count);
            if (!addrResult.IsSuccess)
                return OperateResult<byte[]>.Failed(addrResult.Message, addrResult.ErrorCode);

            byte[] addrBytes = addrResult.Content.GetAddressBinaryContent();
            int payloadLen = 8 + values.Length;

            byte[] cmd = new byte[4 + payloadLen];
            cmd[0] = CMD_WRITE_RELAY;
            cmd[1] = CpuNumber;
            cmd[2] = (byte)((payloadLen >> 8) & 0xFF);
            cmd[3] = (byte)(payloadLen & 0xFF);
            Buffer.BlockCopy(addrBytes, 0, cmd, 4, 6);
            cmd[10] = (byte)((count >> 8) & 0xFF);
            cmd[11] = (byte)(count & 0xFF);
            for (int i = 0; i < values.Length; i++)
                cmd[12 + i] = values[i] ? (byte)0x01 : (byte)0x00;

            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>
        /// 构建随机读字命令。
        /// 命令: [ICF, cpu, payloadLenHi, payloadLenLo, countHi, countLo, addr1(6), addr2(6), ...]
        /// </summary>
        private OperateResult<byte[]> BuildReadRandomWordCommand(string[] addresses)
        {
            if (addresses == null || addresses.Length == 0)
                return OperateResult<byte[]>.Failed("地址列表不能为空");
            if (addresses.Length > MAX_RANDOM_ADDRESSES)
                return OperateResult<byte[]>.Failed($"随机读取最多支持 {MAX_RANDOM_ADDRESSES} 个地址");

            ushort count = (ushort)addresses.Length;
            int payloadLen = 2 + count * 6;

            byte[] cmd = new byte[4 + payloadLen];
            cmd[0] = CMD_RANDOM_READ_WORD;
            cmd[1] = CpuNumber;
            cmd[2] = (byte)((payloadLen >> 8) & 0xFF);
            cmd[3] = (byte)(payloadLen & 0xFF);
            cmd[4] = (byte)((count >> 8) & 0xFF);
            cmd[5] = (byte)(count & 0xFF);

            for (int i = 0; i < addresses.Length; i++)
            {
                var addrResult = YokogawaAddress.ParseFrom(addresses[i], 1);
                if (!addrResult.IsSuccess)
                    return OperateResult<byte[]>.Failed($"地址 {addresses[i]} 解析失败: {addrResult.Message}");

                byte[] addrBytes = addrResult.Content.GetAddressBinaryContent();
                Buffer.BlockCopy(addrBytes, 0, cmd, 6 + i * 6, 6);
            }

            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>
        /// 构建随机写字命令。
        /// 命令: [ICF, cpu, payloadLenHi, payloadLenLo, countHi, countLo, [addr(6)+data(2)]...]
        /// </summary>
        private OperateResult<byte[]> BuildWriteRandomWordCommand(string[] addresses, byte[][] data)
        {
            if (addresses == null || data == null || addresses.Length != data.Length)
                return OperateResult<byte[]>.Failed("地址和数据数量不匹配");
            if (addresses.Length == 0 || addresses.Length > MAX_RANDOM_ADDRESSES)
                return OperateResult<byte[]>.Failed($"地址数量必须在 1-{MAX_RANDOM_ADDRESSES} 之间");

            ushort count = (ushort)addresses.Length;
            int payloadLen = 2 + count * 8; // 2 计数 + 每项 8 (6 地址 + 2 数据)

            byte[] cmd = new byte[4 + payloadLen];
            cmd[0] = CMD_RANDOM_WRITE_WORD;
            cmd[1] = CpuNumber;
            cmd[2] = (byte)((payloadLen >> 8) & 0xFF);
            cmd[3] = (byte)(payloadLen & 0xFF);
            cmd[4] = (byte)((count >> 8) & 0xFF);
            cmd[5] = (byte)(count & 0xFF);

            int offset = 6;
            for (int i = 0; i < addresses.Length; i++)
            {
                if (data[i] == null || data[i].Length != 2)
                    return OperateResult<byte[]>.Failed($"第 {i} 个数据必须是 2 字节");

                var addrResult = YokogawaAddress.ParseFrom(addresses[i], 1);
                if (!addrResult.IsSuccess)
                    return OperateResult<byte[]>.Failed($"地址 {addresses[i]} 解析失败: {addrResult.Message}");

                byte[] addrBytes = addrResult.Content.GetAddressBinaryContent();
                Buffer.BlockCopy(addrBytes, 0, cmd, offset, 6);
                Buffer.BlockCopy(data[i], 0, cmd, offset + 6, 2);
                offset += 8;
            }

            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>
        /// 构建 PLC 控制命令（启动/停止）。
        /// </summary>
        private byte[] BuildControlCommand(byte icf)
        {
            return new byte[] { icf, CpuNumber, 0x00, 0x00 };
        }

        #endregion

        #region 响应校验

        /// <summary>
        /// 校验响应并提取数据。
        /// 响应: [ICF, cpu, payloadLenHi, payloadLenLo, cmdEcho, errorCode, reserved, reserved, data...]
        /// payload 部分（offset 4 起）: [cmdEcho, errorCode, 0x00, 0x00, data...]
        /// </summary>
        private OperateResult<byte[]> CheckResponse(byte[] response)
        {
            if (response == null || response.Length < 8)
                return OperateResult<byte[]>.Failed($"响应数据长度不足: {response?.Length ?? 0}");

            byte errorCode = response[5];
            if (errorCode != 0)
                return OperateResult<byte[]>.Failed(GetErrorText(errorCode), errorCode);

            if (response.Length > 8)
            {
                byte[] data = new byte[response.Length - 8];
                Buffer.BlockCopy(response, 8, data, 0, data.Length);
                return OperateResult<byte[]>.Success(data);
            }

            return OperateResult<byte[]>.Success(new byte[0]);
        }

        #endregion

        #region 核心读写

        /// <summary>
        /// 读取字数据。length 为字数量，返回 length*2 字节。
        /// </summary>
        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var cmdResult = BuildReadCommand(address, length, false);
            if (!cmdResult.IsSuccess)
                return cmdResult;

            var response = SendAndReceive(cmdResult.Content);
            if (!response.IsSuccess)
                return OperateResult<byte[]>.Failed(response.Message, response.ErrorCode);

            return CheckResponse(response.Content);
        }

        /// <summary>
        /// 写入字数据。
        /// </summary>
        public override OperateResult Write(string address, byte[] data)
        {
            var cmdResult = BuildWriteWordCommand(address, data);
            if (!cmdResult.IsSuccess)
                return OperateResult.Failed(cmdResult.Message, cmdResult.ErrorCode);

            var response = SendAndReceive(cmdResult.Content);
            if (!response.IsSuccess)
                return OperateResult.Failed(response.Message, response.ErrorCode);

            var check = CheckResponse(response.Content);
            if (!check.IsSuccess)
                return OperateResult.Failed(check.Message, check.ErrorCode);

            return OperateResult.Success();
        }

        /// <summary>
        /// 读取单个布尔值（继电器地址：X/Y/I/E/M/T/C/L）。
        /// </summary>
        public override OperateResult<bool> ReadBool(string address)
        {
            var cmdResult = BuildReadCommand(address, 1, true);
            if (!cmdResult.IsSuccess)
                return OperateResult<bool>.Failed(cmdResult.Message, cmdResult.ErrorCode);

            var response = SendAndReceive(cmdResult.Content);
            if (!response.IsSuccess)
                return OperateResult<bool>.Failed(response.Message, response.ErrorCode);

            var check = CheckResponse(response.Content);
            if (!check.IsSuccess)
                return OperateResult<bool>.Failed(check.Message, check.ErrorCode);

            if (check.Content == null || check.Content.Length == 0)
                return OperateResult<bool>.Failed("响应数据为空");

            return OperateResult<bool>.Success(check.Content[0] != 0x00);
        }

        /// <summary>
        /// 写入单个布尔值（继电器地址）。
        /// </summary>
        public override OperateResult Write(string address, bool value)
        {
            var cmdResult = BuildWriteRelayCommand(address, new[] { value });
            if (!cmdResult.IsSuccess)
                return OperateResult.Failed(cmdResult.Message, cmdResult.ErrorCode);

            var response = SendAndReceive(cmdResult.Content);
            if (!response.IsSuccess)
                return OperateResult.Failed(response.Message, response.ErrorCode);

            var check = CheckResponse(response.Content);
            if (!check.IsSuccess)
                return OperateResult.Failed(check.Message, check.ErrorCode);

            return OperateResult.Success();
        }

        #endregion

        #region PLC 控制

        /// <summary>启动 PLC。</summary>
        public OperateResult Start()
        {
            var response = SendAndReceive(BuildControlCommand(CMD_START));
            if (!response.IsSuccess)
                return OperateResult.Failed(response.Message, response.ErrorCode);

            var check = CheckResponse(response.Content);
            if (!check.IsSuccess)
                return OperateResult.Failed(check.Message, check.ErrorCode);

            return OperateResult.Success();
        }

        /// <summary>停止 PLC。</summary>
        public OperateResult Stop()
        {
            var response = SendAndReceive(BuildControlCommand(CMD_STOP));
            if (!response.IsSuccess)
                return OperateResult.Failed(response.Message, response.ErrorCode);

            var check = CheckResponse(response.Content);
            if (!check.IsSuccess)
                return OperateResult.Failed(check.Message, check.ErrorCode);

            return OperateResult.Success();
        }

        #endregion

        #region 随机读写

        /// <summary>
        /// 随机读取多个地址的字数据。每个地址读取 1 个字（2 字节），返回连接数据。
        /// </summary>
        public OperateResult<byte[]> ReadRandomWords(string[] addresses)
        {
            var cmdResult = BuildReadRandomWordCommand(addresses);
            if (!cmdResult.IsSuccess)
                return cmdResult;

            var response = SendAndReceive(cmdResult.Content);
            if (!response.IsSuccess)
                return OperateResult<byte[]>.Failed(response.Message, response.ErrorCode);

            return CheckResponse(response.Content);
        }

        /// <summary>
        /// 随机写入多个地址的字数据。每个地址写入 1 个字（2 字节）。
        /// </summary>
        public OperateResult WriteRandomWords(string[] addresses, byte[][] data)
        {
            var cmdResult = BuildWriteRandomWordCommand(addresses, data);
            if (!cmdResult.IsSuccess)
                return cmdResult;

            var response = SendAndReceive(cmdResult.Content);
            if (!response.IsSuccess)
                return OperateResult.Failed(response.Message, response.ErrorCode);

            var check = CheckResponse(response.Content);
            if (!check.IsSuccess)
                return OperateResult.Failed(check.Message, check.ErrorCode);

            return OperateResult.Success();
        }

        /// <summary>
        /// 随机读取多个 Int16 值。
        /// </summary>
        public OperateResult<short[]> ReadRandomInt16(string[] addresses)
        {
            var readResult = ReadRandomWords(addresses);
            if (!readResult.IsSuccess)
                return OperateResult<short[]>.Failed(readResult.Message, readResult.ErrorCode);

            short[] values = new short[addresses.Length];
            for (int i = 0; i < addresses.Length; i++)
                values[i] = ToInt16BE(readResult.Content, i * 2);

            return OperateResult<short[]>.Success(values);
        }

        /// <summary>
        /// 随机读取多个 UInt16 值。
        /// </summary>
        public OperateResult<ushort[]> ReadRandomUInt16(string[] addresses)
        {
            var readResult = ReadRandomWords(addresses);
            if (!readResult.IsSuccess)
                return OperateResult<ushort[]>.Failed(readResult.Message, readResult.ErrorCode);

            ushort[] values = new ushort[addresses.Length];
            for (int i = 0; i < addresses.Length; i++)
                values[i] = (ushort)ToInt16BE(readResult.Content, i * 2);

            return OperateResult<ushort[]>.Success(values);
        }

        #endregion

        #region IReadWriteDevice — 类型化读取

        public override OperateResult<short> ReadInt16(string address)
        {
            var read = ReadBytes(address, 1);
            if (!read.IsSuccess) return OperateResult<short>.Failed(read.Message, read.ErrorCode);
            return OperateResult<short>.Success(ToInt16BE(read.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var read = ReadBytes(address, 1);
            if (!read.IsSuccess) return OperateResult<ushort>.Failed(read.Message, read.ErrorCode);
            return OperateResult<ushort>.Success((ushort)ToInt16BE(read.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var read = ReadBytes(address, 2);
            if (!read.IsSuccess) return OperateResult<int>.Failed(read.Message, read.ErrorCode);
            return OperateResult<int>.Success(ToInt32CDAB(read.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var read = ReadBytes(address, 2);
            if (!read.IsSuccess) return OperateResult<uint>.Failed(read.Message, read.ErrorCode);
            return OperateResult<uint>.Success((uint)ToInt32CDAB(read.Content, 0));
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var read = ReadBytes(address, 4);
            if (!read.IsSuccess) return OperateResult<long>.Failed(read.Message, read.ErrorCode);
            return OperateResult<long>.Success(ToInt64CDAB(read.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var read = ReadBytes(address, 4);
            if (!read.IsSuccess) return OperateResult<ulong>.Failed(read.Message, read.ErrorCode);
            return OperateResult<ulong>.Success((ulong)ToInt64CDAB(read.Content, 0));
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var read = ReadBytes(address, 2);
            if (!read.IsSuccess) return OperateResult<float>.Failed(read.Message, read.ErrorCode);
            return OperateResult<float>.Success(ToFloatCDAB(read.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var read = ReadBytes(address, 4);
            if (!read.IsSuccess) return OperateResult<double>.Failed(read.Message, read.ErrorCode);
            return OperateResult<double>.Success(ToDoubleCDAB(read.Content, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var read = ReadBytes(address, length);
            if (!read.IsSuccess) return OperateResult<string>.Failed(read.Message, read.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(read.Content));
        }

        #endregion

        #region IReadWriteDevice — 类型化写入

        public override OperateResult Write(string address, short value)
        {
            return Write(address, GetBytesBE(value));
        }

        public override OperateResult Write(string address, ushort value)
        {
            return Write(address, GetBytesBE((short)value));
        }

        public override OperateResult Write(string address, int value)
        {
            return Write(address, GetBytesCDAB(value));
        }

        public override OperateResult Write(string address, uint value)
        {
            return Write(address, GetBytesCDAB((int)value));
        }

        public override OperateResult Write(string address, long value)
        {
            return Write(address, GetBytesCDAB(value));
        }

        public override OperateResult Write(string address, ulong value)
        {
            return Write(address, GetBytesCDAB((long)value));
        }

        public override OperateResult Write(string address, float value)
        {
            byte[] intBytes = BitConverter.GetBytes(value);
            int intValue = BitConverter.ToInt32(intBytes, 0);
            return Write(address, GetBytesCDAB(intValue));
        }

        public override OperateResult Write(string address, double value)
        {
            long longValue = BitConverter.DoubleToInt64Bits(value);
            return Write(address, GetBytesCDAB(longValue));
        }

        public override OperateResult Write(string address, string value)
        {
            if (value == null) value = string.Empty;
            int byteCount = Encoding.ASCII.GetByteCount(value);
            int wordCount = (byteCount + 1) / 2;
            byte[] data = new byte[wordCount * 2];
            Encoding.ASCII.GetBytes(value, 0, value.Length, data, 0);
            return Write(address, data);
        }

        #endregion

        #region CDAB 字节序转换

        /// <summary>16 位大端序 → Int16。</summary>
        private static short ToInt16BE(byte[] data, int offset)
        {
            return (short)((data[offset] << 8) | data[offset + 1]);
        }

        /// <summary>
        /// CDAB 32 位 → Int32。
        /// CDAB: [C,D,A,B] → 交换高低字 → [A,B,C,D] → 大端序解析。
        /// </summary>
        private static int ToInt32CDAB(byte[] data, int offset)
        {
            return (data[offset + 2] << 24) | (data[offset + 3] << 16) |
                   (data[offset] << 8) | data[offset + 1];
        }

        /// <summary>
        /// CDAB 64 位 → Int64。
        /// 4 个 16 位字按相邻对交换: [W1,W0,W3,W2] → [W0,W1,W2,W3]。
        /// </summary>
        private static long ToInt64CDAB(byte[] data, int offset)
        {
            uint hi = (uint)((data[offset + 2] << 24) | (data[offset + 3] << 16) |
                             (data[offset] << 8) | data[offset + 1]);
            uint lo = (uint)((data[offset + 6] << 24) | (data[offset + 7] << 16) |
                             (data[offset + 4] << 8) | data[offset + 5]);
            return ((long)hi << 32) | lo;
        }

        /// <summary>CDAB 32 位 → Float。</summary>
        private static float ToFloatCDAB(byte[] data, int offset)
        {
            int intValue = ToInt32CDAB(data, offset);
            byte[] bytes = BitConverter.GetBytes(intValue);
            return BitConverter.ToSingle(bytes, 0);
        }

        /// <summary>CDAB 64 位 → Double。</summary>
        private static double ToDoubleCDAB(byte[] data, int offset)
        {
            long longValue = ToInt64CDAB(data, offset);
            return BitConverter.Int64BitsToDouble(longValue);
        }

        /// <summary>Int16 → 大端序字节。</summary>
        private static byte[] GetBytesBE(short value)
        {
            return new byte[] { (byte)(value >> 8), (byte)value };
        }

        /// <summary>
        /// Int32 → CDAB 字节。
        /// 大端序 [A,B,C,D] → CDAB: [C,D,A,B]。
        /// </summary>
        private static byte[] GetBytesCDAB(int value)
        {
            return new byte[]
            {
                (byte)(value >> 8),   (byte)value,           // C, D
                (byte)(value >> 24),  (byte)(value >> 16)    // A, B
            };
        }

        /// <summary>
        /// Int64 → CDAB 字节。
        /// 大端序 [W0,W1,W2,W3] → CDAB: [W1,W0,W3,W2]。
        /// </summary>
        private static byte[] GetBytesCDAB(long value)
        {
            return new byte[]
            {
                (byte)(value >> 40), (byte)(value >> 32),  // W1
                (byte)(value >> 56), (byte)(value >> 48),  // W0
                (byte)(value >> 8),  (byte)value,           // W3
                (byte)(value >> 24), (byte)(value >> 16)    // W2
            };
        }

        #endregion

        #region IBatchReadWrite — 批量读写接口

        /// <summary>批量读取多个地址的值（利用协议原生随机读命令）。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToArray();
            if (addrList.Length == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            var readResult = ReadRandomWords(addrList);
            if (!readResult.IsSuccess)
                return OperateResult<Dictionary<string, object?>>.Failed(readResult.Message, readResult.ErrorCode);

            var result = new Dictionary<string, object?>();
            for (int i = 0; i < addrList.Length; i++)
            {
                int offset = i * 2;
                if (offset + 2 <= readResult.Content.Length)
                    result[addrList[i]] = ToInt16BE(readResult.Content, offset);
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
            var addrList = addresses.ToArray();
            if (addrList.Length == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");

            var readResult = ReadRandomWords(addrList);
            if (!readResult.IsSuccess)
                return OperateResult<Dictionary<string, byte[]>>.Failed(readResult.Message, readResult.ErrorCode);

            var result = new Dictionary<string, byte[]>();
            for (int i = 0; i < addrList.Length; i++)
            {
                int offset = i * 2;
                if (offset + 2 <= readResult.Content.Length)
                {
                    byte[] wordBytes = new byte[2];
                    wordBytes[0] = readResult.Content[offset];
                    wordBytes[1] = readResult.Content[offset + 1];
                    result[addrList[i]] = wordBytes;
                }
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

            var addresses = new string[itemList.Count];
            var data = new byte[itemList.Count][];

            for (int i = 0; i < itemList.Count; i++)
            {
                addresses[i] = itemList[i].Key;
                short value = itemList[i].Value switch
                {
                    short s => s,
                    ushort us => (short)us,
                    int n => (short)n,
                    uint u => (short)u,
                    bool b => (short)(b ? 1 : 0),
                    _ => (short)0
                };
                data[i] = GetBytesBE(value);
            }

            return WriteRandomWords(addresses, data);
        }

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        #endregion

        #region 错误码

        /// <summary>获取错误码描述文本。</summary>
        public static string GetErrorText(byte errorCode)
        {
            switch (errorCode)
            {
                case 1: return "不支持该命令";
                case 2: return "命令长度错误";
                case 3: return "地址长度错误";
                case 4: return "数据长度错误";
                case 5: return "地址范围错误";
                case 6: return "数据错误";
                case 7: return "系统错误";
                case 8: return "CPU 错误";
                case 0x41: return "看门狗超时";
                case 0x42: return "链路错误";
                case 0x43: return "站号错误";
                case 0x44: return "目标错误";
                case 0x51: return "单元错误";
                case 0x52: return "硬件错误";
                case 0xF1: return "命令错误";
                default: return $"未知错误 (0x{errorCode:X2})";
            }
        }

        #endregion

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
        protected override byte[] BuildHeartbeat()
        {
            try { return BuildReadCommand("HR0", 1, false).Content; }
            catch { return null; }
        }
    }
}
