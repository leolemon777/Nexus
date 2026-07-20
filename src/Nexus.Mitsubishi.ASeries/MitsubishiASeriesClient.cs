using Nexus;

namespace Nexus.Mitsubishi.ASeries
{
    /// <summary>
    /// 三菱 A 系列(AnA / AnS / Q02AS)RS-232 计算机链接协议客户端。
    /// </summary>
    /// <remarks>
    /// <b>实现状态</b>(Phase C-3):本类提供基础命令字符串构建 + 通过基类
    /// <see cref="SerialDeviceBase.SendCustomMessage(byte[])"/> 直接收发的能力,
    /// 允许用户与 A 系列 PLC 通讯。完整的高级 ReadInt16/ReadBool API 仍是占位
    /// (返回 Failed),需要协议手册逐字段确认(AnA/AnS 已停产多年,优先级低)。
    /// <para>
    /// <b>协议格式</b>(三菱 A 系列计算机链接协议,RS-232 接口):
    /// <code>
    /// 请求: ENQ(0x05) 站号2 PC号2 命令2 起始地址4 终止地址4 数据N *(0x2A) CR(0x0D)
    /// 响应: STX(0x02) 站号2 PC号2 数据N ETX(0x03) 校验和2
    /// 异常: NAK(0x15) 错误码2
    /// </code>
    /// </para>
    /// <para><b>推荐替代</b>:新项目请用 <c>Nexus.Mitsubishi.Mc3EBinaryClient</c>(TCP)
    /// 或 <c>Nexus.Mitsubishi.FxLinkClient</c>(FX 系列串口)。</para>
    /// </remarks>
    public class MitsubishiASeriesClient : SerialDeviceBase, IBatchReadWrite
    {
        /// <summary>PLC 站号(0-31,默认 0)。</summary>
        public byte StationNumber { get; set; } = 0x30;

        /// <summary>PC 号(0-F,默认 FF 表示任意 PC)。</summary>
        public string PcNumber { get; set; } = "FF";

        /// <summary>是否在帧末尾追加校验和(2 字符 hex)。默认 true。</summary>
        public bool AppendChecksum { get; set; } = true;

        public MitsubishiASeriesClient(ISerialPort port, int timeout = 5000) : base(port, timeout) { }

        // AnA 协议响应头:站号(2) + PC号(2) = 4 字节(在 STX 之后)。
        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            return (header[2] << 8) | header[3];
        }

        // ── 命令字符串构建(基于公开规范)────────

        /// <summary>构建"读字寄存器"命令字符串(WR - Word Read)。</summary>
        /// <param name="device">设备类型: "D"(数据寄存器)、"R"(文件寄存器)、"ZN"(链路寄存器)等。</param>
        /// <param name="startAddress">起始地址(6 位字符串)。</param>
        /// <param name="endAddress">终止地址(6 位字符串)。</param>
        /// <example>
        /// BuildReadWordCommand("D", "000000", "000003")  // 读 D0..D3 共 4 个字。
        /// </example>
        public string BuildReadWordCommand(string device, string startAddress, string endAddress)
            => BuildAsciiCommand("WR", device, startAddress, endAddress, null);

        /// <summary>构建"写字寄存器"命令字符串(WW - Word Write)。</summary>
        public string BuildWriteWordCommand(string device, string startAddress, string endAddress, string data)
            => BuildAsciiCommand("WW", device, startAddress, endAddress, data);

        /// <summary>构建"读位"命令字符串(BR - Bit Read)。</summary>
        public string BuildReadBitCommand(string device, string startAddress, string endAddress)
            => BuildAsciiCommand("BR", device, startAddress, endAddress, null);

        /// <summary>构建"写位"命令字符串(BW - Bit Write)。</summary>
        public string BuildWriteBitCommand(string device, string startAddress, string endAddress, string data)
            => BuildAsciiCommand("BW", device, startAddress, endAddress, data);

        /// <summary>核心命令字符串构造: ENQ + 站号 + PC号 + cmd + 设备类型 + 起+终 + 数据 + * + CR [+ 校验和]。</summary>
        private string BuildAsciiCommand(string cmd, string device, string startAddress, string endAddress, string? data)
        {
            // 站号: 2 字符 hex(00-1F),从 StationNumber 数字转 hex
            string stationHex = StationNumber.ToString("X2");
            // 命令字符串(无 ENQ / CR / 校验和,后续统一追加)
            string body = stationHex + PcNumber + cmd + device + startAddress + endAddress;
            if (!string.IsNullOrEmpty(data))
                body += data;
            body += "*";

            // 计算校验和(从站号到 * 的所有字节累加取低 8 位,转 2 字符 hex)
            if (AppendChecksum)
            {
                int sum = 0;
                foreach (char c in body) sum += c;
                body += (sum & 0xFF).ToString("X2");
            }

            return body;
        }

        /// <summary>把命令字符串(无 ENQ/CR 前后缀)编码为完整字节帧:ENQ + body + CR。</summary>
        public static byte[] EncodeCommandFrame(string body)
        {
            // ENQ(0x05) + body + CR(0x0D)
            byte[] result = new byte[body.Length + 2];
            result[0] = 0x05;
            for (int i = 0; i < body.Length; i++)
                result[i + 1] = (byte)body[i];
            result[result.Length - 1] = 0x0D;
            return result;
        }

        // ── 高级 API 仍是占位(完整实现需要协议手册逐字段验证)────────

        public override OperateResult<bool> ReadBool(string address)
            => OperateResult<bool>.Failed("A 系列高级 ReadBool 未实现。请用 BuildReadBitCommand + SendCustomMessage。");
        public override OperateResult<short> ReadInt16(string address)
            => OperateResult<short>.Failed("A 系列高级 ReadInt16 未实现。请用 BuildReadWordCommand + SendCustomMessage。");
        public override OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Failed("A 系列高级 ReadUInt16 未实现");
        public override OperateResult<int> ReadInt32(string address) => OperateResult<int>.Failed("A 系列高级 ReadInt32 未实现");
        public override OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Failed("A 系列高级 ReadUInt32 未实现");
        public override OperateResult<long> ReadInt64(string address) => OperateResult<long>.Failed("A 系列高级 ReadInt64 未实现");
        public override OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Failed("A 系列高级 ReadUInt64 未实现");
        public override OperateResult<float> ReadFloat(string address) => OperateResult<float>.Failed("A 系列高级 ReadFloat 未实现");
        public override OperateResult<double> ReadDouble(string address) => OperateResult<double>.Failed("A 系列高级 ReadDouble 未实现");
        public override OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Failed("A 系列高级 ReadString 未实现");
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) => OperateResult<byte[]>.Failed("A 系列高级 ReadBytes 未实现");

        public override OperateResult Write(string address, bool value)
            => OperateResult.Failed("A 系列高级 Write(bool)未实现。请用 BuildWriteBitCommand + SendCustomMessage。");
        public override OperateResult Write(string address, short value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, ushort value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, int value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, uint value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, long value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, ulong value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, float value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, double value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, string value) => OperateResult.Failed("A 系列高级 Write 未实现");
        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("A 系列高级 Write(byte[])未实现。请用 BuildWriteWordCommand + SendCustomMessage。");

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public override Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public override Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public override Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public override Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public override Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public override Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public override Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public override Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public override Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));

        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
            => OperateResult<Dictionary<string, object?>>.Failed("A 系列 BatchRead 未实现");
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default)
            => Task.FromResult(BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
            => OperateResult<Dictionary<string, byte[]>>.Failed("A 系列 RandomRead 未实现");
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default)
            => Task.FromResult(RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
            => OperateResult.Failed("A 系列 BatchWrite 未实现");
        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default)
            => Task.FromResult(BatchWrite(items));
    }
}
