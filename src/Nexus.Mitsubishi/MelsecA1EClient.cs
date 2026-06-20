using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 A1E 兼容帧协议客户端 — 二进制通讯。
    /// <para>适用于 FX 系列 PLC (FX3U 等)，默认端口 5551。</para>
    /// <para>地址格式：D100, M100, X0, Y10, S100, B1A, TS100, TC100, TN100, CS100, CC100, CN100, R100, W100, F100。</para>
    /// <para>X/Y 地址：以 "0" 开头为八进制，否则为十六进制。</para>
    /// </summary>
    public class MelsecA1EClient : TcpDeviceBase, IBatchReadWrite
    {
        // ── A1E 数据类型码（小端序低位在前）────────
        private const ushort CodeX  = 0x5820;   // X 输入（位，hex/oct）
        private const ushort CodeY  = 0x5920;   // Y 输出（位，hex/oct）
        private const ushort CodeM  = 0x4D20;   // M 中间继电器（位，dec）
        private const ushort CodeS  = 0x5320;   // S 状态（位，dec）
        private const ushort CodeF  = 0x4620;   // F 报警器（位，dec）
        private const ushort CodeB  = 0x4220;   // B 连接继电器（位，hex）
        private const ushort CodeTS = 0x5453;   // TS 定时器触点（位，dec）
        private const ushort CodeTC = 0x5443;   // TC 定时器线圈（位，dec）
        private const ushort CodeTN = 0x544E;   // TN 定时器当前值（字，dec）
        private const ushort CodeCS = 0x4353;   // CS 计数器触点（位，dec）
        private const ushort CodeCC = 0x4343;   // CC 计数器线圈（位，dec）
        private const ushort CodeCN = 0x434E;   // CN 计数器当前值（字，dec）
        private const ushort CodeD  = 0x4440;   // D 数据寄存器（字，dec）
        private const ushort CodeW  = 0x5740;   // W 链接寄存器（字，hex）
        private const ushort CodeR  = 0x5220;   // R 文件寄存器（字，dec）

        private const byte TypeBit  = 1;
        private const byte TypeWord = 0;

        /// <summary>字读取最大数量（单次命令）。</summary>
        public const int MaxWordReadCount = 64;
        /// <summary>位读取最大数量（单次命令）。</summary>
        public const int MaxBitReadCount = 256;

        /// <summary>PLC 编号（默认 0xFF）。</summary>
        public byte PLCNumber { get; set; } = 0xFF;

        // ── TcpDeviceBase 抽象实现 ───────────────

        /// <summary>A1E 响应头固定 2 字节（子命令 + 错误码）。</summary>
        protected override int ResponseHeaderLength => 2;

        private int _expectedPayloadLen;

        /// <summary>
        /// A1E 响应头不含长度字段，通过 <see cref="_expectedPayloadLen"/> 传递期望长度。
        /// </summary>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 2) return 0;
            if (header[1] != 0) return 0; // 错误响应无额外数据
            return _expectedPayloadLen;
        }

        // ── 构造 ────────────────────────────────

        /// <summary>
        /// 初始化 A1E 协议客户端。
        /// </summary>
        public MelsecA1EClient(string ip, int port = 5551, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  原始字节读写
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addrResult = AnalysisAddress(address);
            if (!addrResult.Success) return OperateResult<byte[]>.Failed(addrResult.Message);

            ushort wordCount = (ushort)((length + 1) / 2);
            int currentAddr = addrResult.Address;
            var result = new List<byte>();
            int remaining = wordCount;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, MaxWordReadCount);
                var cmd = BuildReadCommandCore(addrResult.DataCode, currentAddr, chunk, false, PLCNumber);

                _expectedPayloadLen = chunk * 2;
                var recv = SendAndReceive(cmd);
                if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

                var check = CheckResponse(recv.Content);
                if (!check.IsSuccess) return OperateResult<byte[]>.Failed(check.Message);

                var data = ExtractActualData(recv.Content, false);
                result.AddRange(data);

                // 地址递增：位类型 ×16，字类型 ×1
                currentAddr += addrResult.DataType == TypeBit ? chunk * 16 : chunk;
                remaining -= chunk;
            }

            byte[] final = result.ToArray();
            if (final.Length > length)
            {
                var trimmed = new byte[length];
                Array.Copy(final, 0, trimmed, 0, length);
                final = trimmed;
            }
            return OperateResult<byte[]>.Success(final);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var cmd = BuildWriteWordCommand(address, data, PLCNumber);
            if (!cmd.IsSuccess) return cmd;

            _expectedPayloadLen = 0;
            var recv = SendAndReceive(cmd.Content);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            return CheckResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  位操作
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBools(address, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content[0]);
        }

        public override OperateResult Write(string address, bool value)
            => WriteBools(address, new[] { value });

        /// <summary>批量读取 bool 数组（A1E 位批量读取）。</summary>
        public OperateResult<bool[]> ReadBools(string address, ushort length)
        {
            var addrResult = AnalysisAddress(address);
            if (!addrResult.Success) return OperateResult<bool[]>.Failed(addrResult.Message);

            int currentAddr = addrResult.Address;
            var result = new List<bool>();
            int remaining = length;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, MaxBitReadCount);
                var cmd = BuildReadCommandCore(addrResult.DataCode, currentAddr, chunk, true, PLCNumber);

                _expectedPayloadLen = (chunk + 1) / 2;
                var recv = SendAndReceive(cmd);
                if (!recv.IsSuccess) return OperateResult<bool[]>.Failed(recv.Message);

                var check = CheckResponse(recv.Content);
                if (!check.IsSuccess) return OperateResult<bool[]>.Failed(check.Message);

                var rawBits = ExtractActualData(recv.Content, true);
                int take = Math.Min(remaining, rawBits.Length);
                for (int i = 0; i < take; i++)
                    result.Add(rawBits[i] != 0);

                currentAddr += chunk;
                remaining -= chunk;
            }

            return OperateResult<bool[]>.Success(result.ToArray());
        }

        /// <summary>批量写入 bool 数组（A1E 位批量写入）。</summary>
        public OperateResult WriteBools(string address, bool[] values)
        {
            var cmd = BuildWriteBoolCommand(address, values, PLCNumber);
            if (!cmd.IsSuccess) return cmd;

            _expectedPayloadLen = 0;
            var recv = SendAndReceive(cmd.Content);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            return CheckResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  标准类型读取（大端序）
        // ═══════════════════════════════════════════

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success(
                BitConverter.ToSingle(BitConverter.GetBytes(DataConverter.ToInt32(r.Content, 0)), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success(
                BitConverter.ToDouble(BitConverter.GetBytes(DataConverter.ToInt64(r.Content, 0)), 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        // ═══════════════════════════════════════════
        //  标准类型写入（大端序）
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, short value)
            => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, ushort value)
            => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
            => Write(address, new byte[] {
                (byte)(value >> 24), (byte)(value >> 16),
                (byte)(value >> 8),  (byte)(value & 0xFF) });

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, ulong value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, float value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, string value)
            => Write(address, System.Text.Encoding.ASCII.GetBytes(value ?? string.Empty));

        // ═══════════════════════════════════════════
        //  协议帧构建（公开静态方法，便于测试）
        // ═══════════════════════════════════════════

        /// <summary>构建读取命令帧（12 字节）。</summary>
        public static OperateResult<byte[]> BuildReadCommand(string address, ushort length, bool isBit, byte plcNumber)
        {
            var addr = AnalysisAddress(address);
            if (!addr.Success) return OperateResult<byte[]>.Failed(addr.Message);
            return OperateResult<byte[]>.Success(
                BuildReadCommandCore(addr.DataCode, addr.Address, length, isBit, plcNumber));
        }

        /// <summary>构建写字命令帧（12 字节头 + 数据）。</summary>
        public static OperateResult<byte[]> BuildWriteWordCommand(string address, byte[] value, byte plcNumber)
        {
            var addr = AnalysisAddress(address);
            if (!addr.Success) return OperateResult<byte[]>.Failed(addr.Message);

            var cmd = new byte[12 + value.Length];
            cmd[0] = 3; // 字写入子命令
            cmd[1] = plcNumber;
            cmd[2] = 10;
            cmd[3] = 0;
            cmd[4] = (byte)(addr.Address & 0xFF);
            cmd[5] = (byte)((addr.Address >> 8) & 0xFF);
            cmd[6] = (byte)((addr.Address >> 16) & 0xFF);
            cmd[7] = (byte)((addr.Address >> 24) & 0xFF);
            cmd[8] = (byte)(addr.DataCode & 0xFF);
            cmd[9] = (byte)((addr.DataCode >> 8) & 0xFF);
            cmd[10] = (byte)((value.Length / 2) & 0xFF);
            cmd[11] = (byte)((value.Length / 2) >> 8);
            Array.Copy(value, 0, cmd, 12, value.Length);
            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>构建写位命令帧（12 字节头 + 打包数据）。</summary>
        public static OperateResult<byte[]> BuildWriteBoolCommand(string address, bool[] value, byte plcNumber)
        {
            var addr = AnalysisAddress(address);
            if (!addr.Success) return OperateResult<byte[]>.Failed(addr.Message);

            byte[] packed = PackBools(value);
            var cmd = new byte[12 + packed.Length];
            cmd[0] = 2; // 位写入子命令
            cmd[1] = plcNumber;
            cmd[2] = 10;
            cmd[3] = 0;
            cmd[4] = (byte)(addr.Address & 0xFF);
            cmd[5] = (byte)((addr.Address >> 8) & 0xFF);
            cmd[6] = (byte)((addr.Address >> 16) & 0xFF);
            cmd[7] = (byte)((addr.Address >> 24) & 0xFF);
            cmd[8] = (byte)(addr.DataCode & 0xFF);
            cmd[9] = (byte)((addr.DataCode >> 8) & 0xFF);
            cmd[10] = (byte)(value.Length & 0xFF);
            cmd[11] = (byte)(value.Length >> 8);
            Array.Copy(packed, 0, cmd, 12, packed.Length);
            return OperateResult<byte[]>.Success(cmd);
        }

        /// <summary>校验 A1E 响应。</summary>
        public static OperateResult CheckResponse(byte[] response)
        {
            if (response == null || response.Length < 2)
                return OperateResult.Failed($"A1E 响应过短 ({response?.Length ?? 0} 字节)");
            if (response[1] == 0)
                return OperateResult.Success();
            if (response[1] == 0x5B && response.Length > 2)
                return OperateResult.Failed($"A1E 错误码: 0x{response[2]:X2}，请参考三菱手册");
            return OperateResult.Failed($"A1E 错误码: 0x{response[1]:X2}，请参考三菱手册");
        }

        /// <summary>从响应中提取实际数据。</summary>
        public static byte[] ExtractActualData(byte[] response, bool isBit)
        {
            if (response == null || response.Length <= 2)
                return new byte[0];

            if (isBit)
            {
                // 每个响应字节包含 2 个位（高半字节 bit4=第一位，低半字节 bit0=第二位）
                var result = new byte[(response.Length - 2) * 2];
                for (int i = 2; i < response.Length; i++)
                {
                    if ((response[i] & 0x10) != 0)
                        result[(i - 2) * 2] = 1;
                    if ((response[i] & 0x01) != 0)
                        result[(i - 2) * 2 + 1] = 1;
                }
                return result;
            }

            var data = new byte[response.Length - 2];
            Array.Copy(response, 2, data, 0, data.Length);
            return data;
        }

        // ═══════════════════════════════════════════
        //  内部方法
        // ═══════════════════════════════════════════

        private static byte[] BuildReadCommandCore(ushort dataCode, int address, int length, bool isBit, byte plcNumber)
        {
            byte subCmd = isBit ? (byte)0 : (byte)1;
            int encodedLen = length == 256 ? 0 : length; // 协议规定：256 编码为 0
            var cmd = new byte[12];
            cmd[0] = subCmd;
            cmd[1] = plcNumber;
            cmd[2] = 10;  // 看门狗低字节
            cmd[3] = 0;   // 看门狗高字节
            cmd[4] = (byte)(address & 0xFF);
            cmd[5] = (byte)((address >> 8) & 0xFF);
            cmd[6] = (byte)((address >> 16) & 0xFF);
            cmd[7] = (byte)((address >> 24) & 0xFF);
            cmd[8] = (byte)(dataCode & 0xFF);
            cmd[9] = (byte)((dataCode >> 8) & 0xFF);
            cmd[10] = (byte)(encodedLen & 0xFF);
            cmd[11] = (byte)((encodedLen >> 8) & 0xFF);
            return cmd;
        }

        /// <summary>将 bool 数组打包为 A1E 位数据（每字节 2 个位）。</summary>
        private static byte[] PackBools(bool[] values)
        {
            int byteCount = (values.Length + 1) / 2;
            var result = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
            {
                if (values[i * 2])
                    result[i] |= 0x10;
                if (i * 2 + 1 < values.Length && values[i * 2 + 1])
                    result[i] |= 0x01;
            }
            return result;
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        /// <summary>地址解析结果。</summary>
        public readonly struct AddressResult
        {
            public bool Success { get; }
            public string Message { get; }
            public ushort DataCode { get; }
            public byte DataType { get; }
            public int Address { get; }

            private AddressResult(bool success, string message, ushort dataCode, byte dataType, int address)
            {
                Success = success; Message = message;
                DataCode = dataCode; DataType = dataType; Address = address;
            }

            public static AddressResult Ok(ushort dataCode, byte dataType, int address)
                => new AddressResult(true, string.Empty, dataCode, dataType, address);

            public static AddressResult Fail(string message)
                => new AddressResult(false, message, 0, 0, 0);
        }

        /// <summary>解析 A1E 地址字符串。</summary>
        public static AddressResult AnalysisAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
                return AddressResult.Fail("地址不能为空");

            try
            {
                char c0 = char.ToUpper(address[0]);

                // 双字符前缀: TS, TC, TN, CS, CC, CN
                if (address.Length >= 2)
                {
                    char c1 = char.ToUpper(address[1]);

                    if (c0 == 'T')
                    {
                        if (c1 == 'S') return AddressResult.Ok(CodeTS, TypeBit, ParseInt(address.Substring(2), 10));
                        if (c1 == 'C') return AddressResult.Ok(CodeTC, TypeBit, ParseInt(address.Substring(2), 10));
                        if (c1 == 'N') return AddressResult.Ok(CodeTN, TypeWord, ParseInt(address.Substring(2), 10));
                    }
                    if (c0 == 'C')
                    {
                        if (c1 == 'S') return AddressResult.Ok(CodeCS, TypeBit, ParseInt(address.Substring(2), 10));
                        if (c1 == 'C') return AddressResult.Ok(CodeCC, TypeBit, ParseInt(address.Substring(2), 10));
                        if (c1 == 'N') return AddressResult.Ok(CodeCN, TypeWord, ParseInt(address.Substring(2), 10));
                    }
                }

                string rest = address.Substring(1);

                switch (c0)
                {
                    case 'X': return AddressResult.Ok(CodeX, TypeBit, ParseOctalOrHex(rest));
                    case 'Y': return AddressResult.Ok(CodeY, TypeBit, ParseOctalOrHex(rest));
                    case 'M': return AddressResult.Ok(CodeM, TypeBit, ParseInt(rest, 10));
                    case 'S': return AddressResult.Ok(CodeS, TypeBit, ParseInt(rest, 10));
                    case 'F': return AddressResult.Ok(CodeF, TypeBit, ParseInt(rest, 10));
                    case 'B': return AddressResult.Ok(CodeB, TypeBit, ParseInt(rest, 16));
                    case 'D': return AddressResult.Ok(CodeD, TypeWord, ParseInt(rest, 10));
                    case 'W': return AddressResult.Ok(CodeW, TypeWord, ParseInt(rest, 16));
                    case 'R': return AddressResult.Ok(CodeR, TypeWord, ParseInt(rest, 10));
                    default:  return AddressResult.Fail($"不支持的地址类型: {address}");
                }
            }
            catch (Exception ex)
            {
                return AddressResult.Fail($"地址解析失败: {ex.Message}");
            }
        }

        /// <summary>解析 X/Y 地址（"0" 开头为八进制，否则为十六进制）。</summary>
        private static int ParseOctalOrHex(string text)
        {
            if (text.StartsWith("0") && text.Length > 1)
                return Convert.ToInt32(text, 8);
            return Convert.ToInt32(text, 16);
        }

        private static int ParseInt(string text, int fromBase)
            => fromBase == 10 ? int.Parse(text) : Convert.ToInt32(text, fromBase);

        public override string ToString() => $"MelsecA1EClient[{Ip}:{Port}]";

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            foreach (string addr in addresses)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = (object?)r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addresses)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                OperateResult r = kv.Value switch
                {
                    bool v => Write(kv.Key, v),
                    short v => Write(kv.Key, v),
                    ushort v => Write(kv.Key, v),
                    int v => Write(kv.Key, v),
                    uint v => Write(kv.Key, v),
                    long v => Write(kv.Key, v),
                    ulong v => Write(kv.Key, v),
                    float v => Write(kv.Key, v),
                    double v => Write(kv.Key, v),
                    string v => Write(kv.Key, v),
                    byte[] v => Write(kv.Key, v),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <inheritdoc/>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

        /// <inheritdoc/>
        protected override byte[]? BuildHeartbeat()
        {
            try { return BuildReadCommand("D0", 1, false, PLCNumber).Content; }
            catch { return null; }
        }
    }
}
