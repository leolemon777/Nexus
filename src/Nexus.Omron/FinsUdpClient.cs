using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// 欧姆龙 FINS-UDP 协议客户端 — 通过 UDP 传输 FINS 报文。
    /// <para>FINS over UDP 帧结构: 无 TCP 帧头，直接发送/接收 FINS 帧。</para>
    /// <para>FINS Header(10) + CommandCode(2) + Data(N)</para>
    /// <para>支持所有内存区域: CIO, WR, HR, AR, DM, EM, TC</para>
    /// <para>支持数据类型: Bool, Int16, UInt16, Int32, Float, Double, String, Bytes</para>
    /// </summary>
    public class FinsUdpClient : UdpDeviceBase, IBatchReadWrite
    {
        /// <summary>目标网络地址。</summary>
        public byte DNA { get; set; } = 0x00;

        /// <summary>目标节点号。</summary>
        public byte DA1 { get; set; } = 0x01;

        /// <summary>目标单元地址（CPU = 0x00）。</summary>
        public byte DA2 { get; set; } = 0x00;

        /// <summary>源网络地址。</summary>
        public byte SNA { get; set; } = 0x00;

        /// <summary>源节点号。</summary>
        public byte SA1 { get; set; } = 0x01;

        /// <summary>源单元地址。</summary>
        public byte SA2 { get; set; } = 0x00;

        /// <summary>多寄存器值的字节序（默认大端）。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        /// <summary>字符串编码选项（默认 ASCII）。</summary>
        public FinsStringEncoding StringEncoding { get; set; } = FinsStringEncoding.Ascii;

        private int _sid;
        private readonly FinsAddressParser _addressParser = new FinsAddressParser();

        protected override int ResponseHeaderLength => 14;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            return 0;
        }

        public FinsUdpClient(string ip, int port = 9600, int timeout = 5000)
            : base(ip, port, timeout)
        {
        }

        // ── FINS-UDP 帧收发 ────────────────────────

        private OperateResult<byte[]> SendFinsCommand(ushort commandCode, byte[] commandData)
        {
            try
            {
                bool wasConnected;
                lock (_lock) { wasConnected = IsConnected; }

                if (!wasConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                byte sid = (byte)(Interlocked.Increment(ref _sid) & 0xFF);

                byte[] finsHeader = new byte[10];
                finsHeader[0] = 0x80;
                finsHeader[1] = 0x00;
                finsHeader[2] = 0x02;
                finsHeader[3] = DNA;
                finsHeader[4] = DA1;
                finsHeader[5] = DA2;
                finsHeader[6] = SNA;
                finsHeader[7] = SA1;
                finsHeader[8] = SA2;
                finsHeader[9] = sid;

                byte[] cmdBytes = new byte[] { (byte)(commandCode >> 8), (byte)(commandCode & 0xFF) };

                byte[] frame = new byte[10 + 2 + commandData.Length];
                Buffer.BlockCopy(finsHeader, 0, frame, 0, 10);
                Buffer.BlockCopy(cmdBytes, 0, frame, 10, 2);
                if (commandData.Length > 0)
                    Buffer.BlockCopy(commandData, 0, frame, 12, commandData.Length);

                var result = SendAndReceive(frame);
                if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

                byte[] response = result.Content;
                if (response.Length < 14)
                    return OperateResult<byte[]>.Failed("FINS-UDP 响应帧不完整");

                ushort endCode = (ushort)((response[12] << 8) | response[13]);
                if (endCode != 0x0000)
                {
                    return OperateResult<byte[]>.Failed(
                        $"FINS 错误: {FinsEndCode.ToMessage(endCode)} (0x{endCode:X4})",
                        (int)endCode);
                }

                return OperateResult<byte[]>.Success(response);
            }
            catch (Exception ex)
            {
                Log.Error($"FINS-UDP 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<byte[]>.Failed($"FINS-UDP 通讯异常: {ex.Message}");
            }
        }

        // ── 内存区域读取 ──────────────────────────

        private OperateResult<byte[]> ReadMemoryArea(FinsAddress addr, ushort readLength)
        {
            byte[] cmdDataFull = new byte[7];
            cmdDataFull[0] = (byte)addr.Area;

            if (addr.Area == FinsMemoryArea.EM)
            {
                // B4 修复：EM 地址字段 bank(1)+address(2,big-endian)。
                cmdDataFull[1] = addr.EmBank;
                cmdDataFull[2] = (byte)(addr.WordAddress >> 8);
                cmdDataFull[3] = (byte)(addr.WordAddress & 0xFF);
            }
            else
            {
                cmdDataFull[1] = (byte)(addr.BitOffset >= 0 ? 0x01 : 0x00);
                cmdDataFull[2] = (byte)(addr.WordAddress >> 8);
                cmdDataFull[3] = (byte)(addr.WordAddress & 0xFF);
            }

            cmdDataFull[4] = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            cmdDataFull[5] = (byte)(readLength >> 8);
            cmdDataFull[6] = (byte)(readLength & 0xFF);

            return SendFinsCommand(FinsCommandCode.MemoryAreaRead, cmdDataFull);
        }

        // ── 字节序转换 ──────────────────────────────

        private short ToInt16Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.LittleEndian => (short)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToInt16(data, offset)
        };

        private ushort ToUInt16Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.LittleEndian => (ushort)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToUInt16(data, offset)
        };

        private int ToInt32Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.BigEndian => DataConverter.ToInt32(data, offset),
            Endianness.LittleEndian => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24),
            Endianness.MidBigEndian => (data[offset + 1] << 24) | (data[offset] << 16) | (data[offset + 3] << 8) | data[offset + 2],
            Endianness.MidLittleEndian => (data[offset + 2] << 24) | (data[offset + 3] << 16) | (data[offset] << 8) | data[offset + 1],
            _ => DataConverter.ToInt32(data, offset)
        };

        private long ToInt64Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.BigEndian => DataConverter.ToInt64(data, offset),
            Endianness.LittleEndian => (long)(
                (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24)) |
                ((long)(uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24)) << 32)),
            _ => DataConverter.ToInt64(data, offset)
        };

        private float ToFloatOrdered(byte[] data, int offset)
        {
            int v = ToInt32Ordered(data, offset);
            unsafe { return *(float*)&v; }
        }

        private double ToDoubleOrdered(byte[] data, int offset)
        {
            long v = ToInt64Ordered(data, offset);
            unsafe { return *(double*)&v; }
        }

        private byte[] GetBytesOrdered(short value) => ByteOrder == Endianness.LittleEndian
            ? new byte[] { (byte)value, (byte)(value >> 8) }
            : DataConverter.GetBytes(value);

        private byte[] GetBytesOrdered(ushort value) => GetBytesOrdered((short)value);

        private byte[] GetBytesOrdered(int value)
        {
            if (ByteOrder == Endianness.BigEndian) return DataConverter.GetBytes(value);
            if (ByteOrder == Endianness.LittleEndian) return new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
            if (ByteOrder == Endianness.MidBigEndian) return new byte[] { (byte)(value >> 16), (byte)(value >> 24), (byte)value, (byte)(value >> 8) };
            return new byte[] { (byte)(value >> 8), (byte)value, (byte)(value >> 24), (byte)(value >> 16) };
        }

        private byte[] GetBytesOrdered(float value)
        {
            unsafe { int v = *(int*)&value; return GetBytesOrdered(v); }
        }

        private byte[] GetBytesOrdered(long value)
        {
            if (ByteOrder == Endianness.BigEndian) return DataConverter.GetBytes(value);
            if (ByteOrder == Endianness.LittleEndian)
            {
                return new byte[] {
                    (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
                    (byte)(value >> 32), (byte)(value >> 40), (byte)(value >> 48), (byte)(value >> 56)
                };
            }
            return DataConverter.GetBytes(value);
        }

        private byte[] GetBytesOrdered(double value)
        {
            unsafe { long v = *(long*)&value; return GetBytesOrdered(v); }
        }

        // ── IReadWriteDevice 实现 ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult<bool>.Failed("读取 Bool 需要指定位偏移，例如 D100.03");

            var result = ReadMemoryArea(addr, 1);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 15)
                return OperateResult<bool>.Failed("FINS Bool 响应数据不足");

            byte data = result.Content[14];
            return OperateResult<bool>.Success((data & (1 << addr.BitOffset)) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 1);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 16)
                return OperateResult<short>.Failed("FINS Int16 响应数据不足");

            return OperateResult<short>.Success(ToInt16Ordered(result.Content, 14));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 1);
            if (!result.IsSuccess) return OperateResult<ushort>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 16)
                return OperateResult<ushort>.Failed("FINS UInt16 响应数据不足");

            return OperateResult<ushort>.Success(ToUInt16Ordered(result.Content, 14));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 2);
            if (!result.IsSuccess) return OperateResult<int>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 18)
                return OperateResult<int>.Failed("FINS Int32 响应数据不足");

            return OperateResult<int>.Success(ToInt32Ordered(result.Content, 14));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 4);
            if (!result.IsSuccess) return OperateResult<long>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 22)
                return OperateResult<long>.Failed("FINS Int64 响应数据不足");

            return OperateResult<long>.Success(ToInt64Ordered(result.Content, 14));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 2);
            if (!result.IsSuccess) return OperateResult<float>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 18)
                return OperateResult<float>.Failed("FINS Float 响应数据不足");

            return OperateResult<float>.Success(ToFloatOrdered(result.Content, 14));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 4);
            if (!result.IsSuccess) return OperateResult<double>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 22)
                return OperateResult<double>.Failed("FINS Double 响应数据不足");

            return OperateResult<double>.Success(ToDoubleOrdered(result.Content, 14));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var result = ReadMemoryArea(addr, wordCount);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);

            int available = result.Content.Length - 14;
            if (available <= 0)
                return OperateResult<string>.Failed("FINS String 响应数据不足");

            return OperateResult<string>.Success(DataConverter.ToString(result.Content, 14, Math.Min(length, available)));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var result = ReadMemoryArea(addr, wordCount);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            int available = result.Content.Length - 14;
            if (available <= 0)
                return OperateResult<byte[]>.Failed("FINS Bytes 响应数据不足");

            byte[] data = new byte[Math.Min(length, available)];
            Buffer.BlockCopy(result.Content, 14, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        // ── 字符串编码读写 ────────────────────────

        public OperateResult<string> ReadStringEncoded(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var result = ReadMemoryArea(addr, wordCount);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);

            int available = result.Content.Length - 14;
            if (available <= 0)
                return OperateResult<string>.Failed("FINS String 响应数据不足");

            int byteLen = Math.Min(length, available);
            string text = StringEncoding switch
            {
                FinsStringEncoding.Utf8 => Encoding.UTF8.GetString(result.Content, 14, byteLen).TrimEnd('\0', ' '),
                FinsStringEncoding.Unicode => Encoding.Unicode.GetString(result.Content, 14, byteLen).TrimEnd('\0', ' '),
                _ => DataConverter.ToString(result.Content, 14, byteLen)
            };
            return OperateResult<string>.Success(text);
        }

        public OperateResult WriteStringEncoded(string address, string value)
        {
            var addr = _addressParser.Parse(address);
            byte[] data = StringEncoding switch
            {
                FinsStringEncoding.Utf8 => Encoding.UTF8.GetBytes(value),
                FinsStringEncoding.Unicode => Encoding.Unicode.GetBytes(value),
                _ => DataConverter.GetBytes(value)
            };
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0)
                Array.Resize(ref data, data.Length + 1);
            return WriteMemoryArea(addr, wordCount, data);
        }

        // ── 写入实现 ──────────────────────────────

        private OperateResult WriteMemoryArea(FinsAddress addr, ushort wordCount, byte[] data)
        {
            byte[] cmdData = new byte[7 + data.Length];
            cmdData[0] = (byte)addr.Area;

            if (addr.Area == FinsMemoryArea.EM)
            {
                // B4 修复：EM 地址字段 bank(1)+address(2,big-endian)。
                cmdData[1] = addr.EmBank;
                cmdData[2] = (byte)(addr.WordAddress >> 8);
                cmdData[3] = (byte)(addr.WordAddress & 0xFF);
            }
            else
            {
                cmdData[1] = (byte)(addr.BitOffset >= 0 ? 0x01 : 0x00);
                cmdData[2] = (byte)(addr.WordAddress >> 8);
                cmdData[3] = (byte)(addr.WordAddress & 0xFF);
            }

            cmdData[4] = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            cmdData[5] = (byte)(wordCount >> 8);
            cmdData[6] = (byte)(wordCount & 0xFF);
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, cmdData, 7, data.Length);

            var result = SendFinsCommand(FinsCommandCode.MemoryAreaWrite, cmdData);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, bool value)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult.Failed("写入 Bool 需要指定位偏移，例如 D100.03");

            var readResult = ReadMemoryArea(new FinsAddress(addr.Original, addr.Area, addr.WordAddress, -1), 1);
            if (!readResult.IsSuccess) return OperateResult.Failed("写入 Bool 前读取失败: " + readResult.Message);

            byte currentWord = readResult.Content.Length > 15 ? readResult.Content[14] : (byte)0;
            if (value)
                currentWord |= (byte)(1 << addr.BitOffset);
            else
                currentWord &= (byte)~(1 << addr.BitOffset);

            var writeAddr = new FinsAddress(addr.Original, addr.Area, addr.WordAddress, -1);
            return WriteMemoryArea(writeAddr, 1, new byte[] { 0x00, currentWord });
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 1, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, ushort value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 1, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, int value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 4, GetBytesOrdered(value));
        }
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);

        public override OperateResult Write(string address, float value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, double value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 4, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, string value)
        {
            var addr = _addressParser.Parse(address);
            byte[] data = DataConverter.GetBytes(value);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0)
                Array.Resize(ref data, data.Length + 1);
            return WriteMemoryArea(addr, wordCount, data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0)
                Array.Resize(ref data, data.Length + 1);
            return WriteMemoryArea(addr, wordCount, data);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            var addrList = addresses.ToList();

            var groups = addrList.GroupBy(a =>
            {
                var parsed = _addressParser.Parse(a);
                return (parsed.Area, parsed.EmBank);
            });

            foreach (var group in groups)
            {
                var sorted = group.Select(a => _addressParser.Parse(a))
                                  .OrderBy(a => a.WordAddress).ToList();

                ushort minAddr = sorted.Min(a => a.WordAddress);
                ushort maxAddr = sorted.Max(a => a.WordAddress);
                ushort range = (ushort)(maxAddr - minAddr + 1);

                var sampleAddr = sorted[0];
                var readAddr = new FinsAddress("", sampleAddr.Area, minAddr, -1, sampleAddr.EmBank);
                var raw = ReadMemoryArea(readAddr, range);
                if (!raw.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(raw.Message, raw.ErrorCode);

                foreach (var addrStr in group)
                {
                    var parsed = _addressParser.Parse(addrStr);
                    int byteOffset = (parsed.WordAddress - minAddr) * 2 + 14;

                    if (parsed.BitOffset >= 0)
                    {
                        if (byteOffset < raw.Content.Length)
                        {
                            byte word = raw.Content[byteOffset];
                            result[addrStr] = (word & (1 << parsed.BitOffset)) != 0;
                        }
                    }
                    else
                    {
                        if (byteOffset + 2 <= raw.Content.Length)
                            result[addrStr] = ToInt16Ordered(raw.Content, byteOffset);
                    }
                }
            }

            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, byte[]>();

            foreach (var addrStr in addresses)
            {
                var parsed = _addressParser.Parse(addrStr);
                var raw = ReadMemoryArea(parsed, 1);
                if (!raw.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(raw.Message, raw.ErrorCode);

                int available = raw.Content.Length - 14;
                if (available <= 0)
                    return OperateResult<Dictionary<string, byte[]>>.Failed("FINS 响应数据不足");

                byte[] data = new byte[available];
                Buffer.BlockCopy(raw.Content, 14, data, 0, available);
                result[addrStr] = data;
            }

            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    long l => Write(kv.Key, l),
                    ulong ul => Write(kv.Key, ul),
                    float f => Write(kv.Key, f),
                    double d => Write(kv.Key, d),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

        // ═══════════════════════════════════════════
        //  FINS 设备发现（UDP 广播）
        // ═══════════════════════════════════════════

        /// <summary>
        /// 通过 UDP 广播发现 FINS 网络 devices。
        /// 发送 FINS Controller Read 广播命令，收集响应中的设备信息。
        /// </summary>
        /// <param name="broadcastPort">广播端口（默认 9600）。</param>
        /// <param name="timeoutMs">等待响应超时（毫秒）。</param>
        /// <returns>发现的设备列表。</returns>
        public OperateResult<FinsDiscoveredDevice[]> DiscoverDevices(string broadcastIp = "255.255.255.255", int timeoutMs = 3000)
        {
            try
            {
                // FINS 广播帧: ICF=0xC0(广播) + RSV=0 + GCT=2 + DNA=0 + DA1=0xFF(广播) + DA2=0
                byte sid = unchecked(++ServiceId);
                byte[] discoveryFrame =
                {
                    0xC0, // ICF: 广播帧
                    0x00, // RSV
                    0x02, // GCT
                    0x00, // DNA: 本地网络
                    0xFF, // DA1: 广播地址
                    0x00, // DA2: CPU
                    SNA,  // SNA
                    SA1,  // SA1
                    SA2,  // SA2
                    sid,  // SID
                    0x05, 0x01 // MRC=0x05, SRC=0x01 (Controller Read)
                };

                var respResult = SendBroadcast(discoveryFrame, broadcastIp);

                if (!respResult.IsSuccess)
                    return OperateResult<FinsDiscoveredDevice[]>.Failed(respResult.Message);

                var devices = new List<FinsDiscoveredDevice>();
                byte[] resp = respResult.Content;

                if (resp.Length >= 14 && (resp[0] & 0x40) != 0)
                {
                    var device = new FinsDiscoveredDevice
                    {
                        NetworkAddress = resp[6],
                        NodeNumber = resp[7],
                        UnitNumber = resp[8],
                    };

                    if (resp.Length >= 36)
                    {
                        device.ControllerModel = Encoding.ASCII.GetString(resp, 14, Math.Min(20, resp.Length - 14)).TrimEnd('\0', ' ');
                    }
                    else if (resp.Length > 12)
                    {
                        device.ControllerModel = Encoding.ASCII.GetString(resp, 12, Math.Min(20, resp.Length - 12)).TrimEnd('\0', ' ');
                    }

                    devices.Add(device);
                }

                return OperateResult<FinsDiscoveredDevice[]>.Success(devices.ToArray());
            }
            catch (Exception ex)
            {
                return OperateResult<FinsDiscoveredDevice[]>.Failed(ex.Message);
            }
        }

        /// <summary>递增服务 ID。</summary>
        private byte ServiceId;
    }
}
