using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Fuji
{
    /// <summary>
    /// 富士 SPB 系列 PLC 串口通讯客户端 — SPB 协议。
    /// <para>帧格式: ':' + Station(2hex) + ByteCount(2hex) + Cmd(4hex) + SubCmd(4hex) + TypeCode(2hex) + Addr(4hex) + Count(4hex) + [Data] + LRC(2hex) + CR+LF</para>
    /// <para>支持位区域: X/Y/M/L/TC/CC 和字区域: TN/CN/D/R/W</para>
    /// </summary>
    public class FujiSpbClient : SerialDeviceBase, IBatchReadWrite
    {
        private const byte FrameStart = (byte)':';
        private const byte CR = 0x0D;
        private const byte LF = 0x0A;
        private readonly object _serialLock = new object();

        public byte Station { get; set; }

        public FujiSpbClient(ISerialPort serialPort, byte station = 1, int timeout = 5000)
            : base(serialPort, timeout)
        {
            Station = station;
        }

        // ── SerialDeviceBase 抽象实现（自定义 ASCII 收发）──
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  SPB 帧构建
        // ═══════════════════════════════════════════

        private static string BuildReadCommand(byte station, FujiSpbTypeCode typeCode, int address, int wordCount)
        {
            string frame = station.ToString("X2")
                + "09"
                + "FF00"
                + "0000"
                + ((byte)typeCode).ToString("X2")
                + SwapBytes(address.ToString("X4"))
                + SwapBytes(wordCount.ToString("X4"));
            return ":" + frame + ComputeLrc(frame) + "\r\n";
        }

        private static string BuildWriteCommand(byte station, FujiSpbTypeCode typeCode, int address, int wordCount, byte[] data)
        {
            int byteCount = 4 + data.Length;
            string frame = station.ToString("X2")
                + byteCount.ToString("X2")
                + "FF00"
                + "0100"
                + ((byte)typeCode).ToString("X2")
                + SwapBytes(address.ToString("X4"))
                + SwapBytes(wordCount.ToString("X4"))
                + BytesToHex(data);
            return ":" + frame + ComputeLrc(frame) + "\r\n";
        }

        private static string BuildBitWriteCommand(byte station, FujiSpbTypeCode typeCode, int bitAddress, bool value)
        {
            string frame = station.ToString("X2")
                + "07"
                + "FF00"
                + "0200"
                + ((byte)typeCode).ToString("X2")
                + SwapBytes(bitAddress.ToString("X4"))
                + (value ? "01" : "00");
            return ":" + frame + ComputeLrc(frame) + "\r\n";
        }

        private static string SwapBytes(string hex4)
        {
            if (hex4.Length < 4) hex4 = hex4.PadLeft(4, '0');
            return hex4.Substring(2, 2) + hex4.Substring(0, 2);
        }

        private static string ComputeLrc(string data)
        {
            byte lrc = 0;
            for (int i = 0; i < data.Length; i += 2)
                lrc ^= (byte)Convert.ToByte(data.Substring(i, 2), 16);
            return lrc.ToString("X2");
        }

        // ═══════════════════════════════════════════
        //  串口通讯
        // ═══════════════════════════════════════════

        private OperateResult<string> SendReceiveAscii(string frame)
        {
            lock (_serialLock)
            {
                try
                {
                    byte[] tx = Encoding.ASCII.GetBytes(frame);
                    RaiseMessageSent(frame.Replace("\r", "\\r").Replace("\n", "\\n"));

                    Port.Write(tx, 0, tx.Length);

                    var response = new List<byte>();
                    byte[] buf = new byte[256];
                    int start = Environment.TickCount;

                    while (unchecked(Environment.TickCount - start) < Timeout)
                    {
                        int read = Port.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                response.Add(buf[i]);
                                if (buf[i] == LF)
                                {
                                    string resp = Encoding.ASCII.GetString(response.ToArray());
                                    RaiseMessageReceived(resp.Replace("\r", "\\r").Replace("\n", "\\n"));
                                    return OperateResult<string>.Success(resp);
                                }
                            }
                        }
                    }

                    return OperateResult<string>.Failed($"SPB 响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"SPB 串口通讯异常: {ex.Message}");
                    return OperateResult<string>.Failed($"SPB 串口通讯异常: {ex.Message}");
                }
            }
        }

        private OperateResult<byte[]> ExecuteRead(FujiSpbTypeCode typeCode, int address, int wordCount)
        {
            string frame = BuildReadCommand(Station, typeCode, address, wordCount);
            var recv = SendReceiveAscii(frame);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            return ParseReadResponse(recv.Content, wordCount);
        }

        private OperateResult ExecuteWrite(FujiSpbTypeCode typeCode, int address, int wordCount, byte[] data)
        {
            string frame = BuildWriteCommand(Station, typeCode, address, wordCount, data);
            var recv = SendReceiveAscii(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            return ParseWriteResponse(recv.Content);
        }

        private OperateResult ExecuteBitWrite(FujiSpbTypeCode typeCode, int bitAddress, bool value)
        {
            string frame = BuildBitWriteCommand(Station, typeCode, bitAddress, value);
            var recv = SendReceiveAscii(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            return ParseWriteResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  响应解析
        // ═══════════════════════════════════════════

        private static OperateResult<byte[]> ParseReadResponse(string response, int wordCount)
        {
            response = response.Trim();
            if (!response.StartsWith(":"))
                return OperateResult<byte[]>.Failed("SPB 响应格式错误: 缺少起始符");

            string hex = response.Substring(1);
            if (hex.Length < 16)
                return OperateResult<byte[]>.Failed("SPB 响应太短");

            string lrcStr = hex.Substring(hex.Length - 2);
            string body = hex.Substring(0, hex.Length - 2);
            string computedLrc = ComputeLrc(body);
            if (lrcStr != computedLrc)
                return OperateResult<byte[]>.Failed("SPB LRC 校验失败");

            string byteCountStr = body.Substring(2, 2);
            int byteCount = Convert.ToByte(byteCountStr, 16);

            string cmdStr = body.Substring(4, 4);
            if (cmdStr == "FF80")
            {
                string errCode = body.Length > 12 ? body.Substring(12, 2) : "??";
                return OperateResult<byte[]>.Failed($"SPB 错误响应: 错误码 {errCode}");
            }

            string dataHex = body.Substring(12, byteCount * 2);
            return OperateResult<byte[]>.Success(HexToBytes(dataHex));
        }

        private static OperateResult ParseWriteResponse(string response)
        {
            response = response.Trim();
            if (!response.StartsWith(":"))
                return OperateResult.Failed("SPB 响应格式错误: 缺少起始符");

            string hex = response.Substring(1);
            if (hex.Length < 16)
                return OperateResult.Failed("SPB 响应太短");

            string lrcStr = hex.Substring(hex.Length - 2);
            string body = hex.Substring(0, hex.Length - 2);
            string computedLrc = ComputeLrc(body);
            if (lrcStr != computedLrc)
                return OperateResult.Failed("SPB LRC 校验失败");

            string cmdStr = body.Substring(4, 4);
            if (cmdStr == "FF80")
            {
                string errCode = body.Length > 12 ? body.Substring(12, 2) : "??";
                return OperateResult.Failed($"SPB 错误响应: 错误码 {errCode}");
            }

            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  地址解析辅助
        // ═══════════════════════════════════════════

        private static FujiSpbAddress ParseAddress(string address)
        {
            var parsed = FujiSpbAddress.TryParse(address);
            if (parsed == null)
                throw new ArgumentException($"无效的 SPB 地址: {address}");
            return parsed;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddress(address);
            if (addr.IsBitArea)
            {
                int bitAddr = addr.WordAddress * 16 + addr.BitIndex;
                var r = ExecuteRead(addr.TypeCode, bitAddr, 1);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
                return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
            }
            else
            {
                var r = ExecuteRead(addr.TypeCode, addr.WordAddress, 1);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
                ushort val = r.Content.Length >= 2 ? (ushort)((r.Content[1] << 8) | r.Content[0]) : (ushort)0;
                return OperateResult<bool>.Success(val != 0);
            }
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddress(address);
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            ushort val = r.Content.Length >= 2 ? (ushort)((r.Content[1] << 8) | r.Content[0]) : (ushort)0;
            return OperateResult<short>.Success((short)val);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddress(address);
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据长度不足");
            uint val = (uint)(r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24));
            return OperateResult<int>.Success((int)val);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadUInt64(address);
            return r.IsSuccess ? OperateResult<long>.Success(unchecked((long)r.Content)) : OperateResult<long>.Failed(r.Message);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var addr = ParseAddress(address);
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, 4);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            if (r.Content.Length < 8) return OperateResult<ulong>.Failed("数据长度不足");
            ulong val = 0;
            for (int i = 0; i < 8; i++)
                val |= (ulong)r.Content[i] << (i * 8);
            return OperateResult<ulong>.Success(val);
        }

        public override unsafe OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            int v = r.Content;
            return OperateResult<float>.Success(*(float*)&v);
        }

        public override unsafe OperateResult<double> ReadDouble(string address)
        {
            var r = ReadUInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            ulong v = r.Content;
            return OperateResult<double>.Success(*(double*)&v);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = ParseAddress(address);
            int wordCount = (length + 1) / 2;
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, wordCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            byte[] data = new byte[length];
            Array.Copy(r.Content, 0, data, 0, Math.Min(r.Content.Length, length));
            return OperateResult<byte[]>.Success(data);
        }

        // ── 写入 ────────────────────────────────

        public override OperateResult Write(string address, bool value)
        {
            var addr = ParseAddress(address);
            if (addr.IsBitArea)
            {
                int bitAddr = addr.WordAddress * 16 + (addr.BitIndex >= 0 ? addr.BitIndex : 0);
                return ExecuteBitWrite(addr.TypeCode, bitAddr, value);
            }
            return Write(address, (short)(value ? 1 : 0));
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = ParseAddress(address);
            byte[] data = new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, 1, data);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = ParseAddress(address);
            byte[] data = new byte[] {
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 24) & 0xFF)
            };
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, 2, data);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value) => Write(address, unchecked((ulong)value));

        public override OperateResult Write(string address, ulong value)
        {
            var addr = ParseAddress(address);
            byte[] data = new byte[8];
            for (int i = 0; i < 8; i++)
                data[i] = (byte)((value >> (i * 8)) & 0xFF);
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, 4, data);
        }

        public override unsafe OperateResult Write(string address, float value) => Write(address, *(int*)&value);
        public override unsafe OperateResult Write(string address, double value) => Write(address, *(ulong*)&value);

        public override OperateResult Write(string address, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? "");
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            var addr = ParseAddress(address);
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, bytes.Length / 2, bytes);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null || data.Length == 0) return OperateResult.Failed("写入数据不能为空");
            byte[] padded = data;
            if (padded.Length % 2 != 0) { padded = new byte[data.Length + 1]; Array.Copy(data, padded, data.Length); }
            var addr = ParseAddress(address);
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, padded.Length / 2, padded);
        }

        // ═══════════════════════════════════════════
        //  批量读写 — IBatchReadWrite
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = new List<string>(addresses);
            if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = new List<string>(addresses);
            if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = new List<KeyValuePair<string, object>>(items);
            if (itemList.Count == 0) return OperateResult.Failed("写入列表不能为空");
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

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        // ═══════════════════════════════════════════
        //  Hex 工具
        // ═══════════════════════════════════════════

        private static byte[] HexToBytes(string hex)
        {
            hex = hex.Trim();
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return result;
        }

        private static string BytesToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (byte b in data)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }
}
