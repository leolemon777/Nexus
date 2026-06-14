using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus;

namespace Nexus.Panasonic
{
    /// <summary>
    /// 松下 Mewtocol 协议 TCP 客户端 — 将 Mewtocol 串口帧封装在 TCP 传输层上。
    /// <para>帧格式: % [Station(2)] [Command(2)] [Data] [BCC(2)] CR</para>
    /// <para>与 PanasonicMewtocolClient 共享相同的协议格式，仅传输层不同。</para>
    /// <para>对标 HSL: PanasonicMewtocolNet</para>
    /// </summary>
    public class PanasonicMewtocolOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        /// <summary>站号 (01-99)。</summary>
        public byte Station { get; set; }

        public PanasonicMewtocolOverTcpClient(string ip, int port = 9094, byte station = 1, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
        }

        // TcpDeviceBase abstract members — not used (we override SendAndReceive entirely)
        protected override int ResponseHeaderLength => 1;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  BCC 校验
        // ═══════════════════════════════════════════

        private static byte ComputeBcc(string data)
        {
            byte bcc = 0;
            foreach (char c in data)
                bcc ^= (byte)c;
            return bcc;
        }

        // ═══════════════════════════════════════════
        //  帧收发 (override for text-based Mewtocol)
        // ═══════════════════════════════════════════

        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                if (!IsConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                Log.Debug($"TX → {Encoding.ASCII.GetString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                // Mewtocol 响应以 \r 结尾，逐字节读取
                int start = Environment.TickCount;
                using var ms = new MemoryStream();
                while (unchecked(Environment.TickCount - start) <= Timeout)
                {
                    int remaining = Timeout - unchecked(Environment.TickCount - start);
                    if (remaining < 0) return OperateResult<byte[]>.Failed("读取响应超时");
                    int b = ReadByteWithTimeout(ns, remaining);
                    if (b < 0) return OperateResult<byte[]>.Failed("读取响应超时");
                    ms.WriteByte((byte)b);
                    if (b == '\r') break;
                }

                byte[] response = ms.ToArray();
                Log.Debug($"RX ← {Encoding.ASCII.GetString(response)}");
                RaiseMessageReceived(DataConverter.ToHexString(response));

                if (!_persistentMode)
                {
                    lock (_lock) { DisconnectCore(); }
                }

                return OperateResult<byte[]>.Success(response);
            }
            catch (Exception ex)
            {
                Log.Error($"Mewtocol TCP 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode)
                {
                    lock (_lock) { DisconnectCore(); }
                }
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private static int ReadByteWithTimeout(NetworkStream ns, int remainingMs)
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) <= remainingMs)
            {
                try { return ns.ReadByte(); }
                catch (TimeoutException) { /* retry until timeout */ }
            }
            return -1;
        }

        // ═══════════════════════════════════════════
        //  Mewtocol 命令收发 (ASCII 层)
        // ═══════════════════════════════════════════

        private OperateResult<string> SendMewtocol(string command, string data)
        {
            string stationStr = Station.ToString("D2");
            string body = stationStr + command + data;
            byte bcc = ComputeBcc(body);
            string frame = "%" + body + bcc.ToString("X2") + "\r";

            byte[] txBytes = Encoding.ASCII.GetBytes(frame);
            var result = SendAndReceive(txBytes);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);

            string response = Encoding.ASCII.GetString(result.Content);

            if (response.Length < 6 || !response.StartsWith("%"))
                return OperateResult<string>.Failed("响应格式错误");

            string respStation = response.Substring(1, 2);
            if (respStation != stationStr)
                return OperateResult<string>.Failed($"响应站号不匹配: 期望={stationStr}, 实际={respStation}");

            string respCmd = response.Substring(3, 2);
            if (respCmd == "!")
            {
                string errCode = response.Length > 5 ? response.Substring(5) : "??";
                return OperateResult<string>.Failed($"PLC 错误: {ParseErrorCode(errCode)}");
            }

            string respBody = response.Substring(1, response.Length - 4);
            byte expectedBcc = ComputeBcc(respBody);
            string respBcc = response.Substring(response.Length - 3, 2);
            if (expectedBcc.ToString("X2") != respBcc)
                return OperateResult<string>.Failed($"BCC 校验失败: 期望={expectedBcc:X2}, 实际={respBcc}");

            int dataStart = 5;
            int dataEnd = response.Length - 3;
            if (dataEnd <= dataStart)
                return OperateResult<string>.Success("");
            return OperateResult<string>.Success(response.Substring(dataStart, dataEnd - dataStart));
        }

        private static string ParseErrorCode(string code) => code switch
        {
            "00" => "无错误",
            "01" => "未定义命令",
            "02" => "非法地址",
            "03" => "非法数据",
            "04" => "操作失败",
            "05" => "忙碌",
            "20" => "通讯错误",
            "21" => "帧错误",
            "22" => "BCC 错误",
            "23" => "站号不匹配",
            "24" => "超时",
            "40" => "PLC 运行中不允许写入",
            "41" => "地址写保护",
            _ => $"未知错误代码 {code}"
        };

        // ═══════════════════════════════════════════
        //  寄存器地址映射
        // ═══════════════════════════════════════════

        private static (string areaCode, int address) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            if (address.StartsWith("DT")) return ("D", int.Parse(address.Substring(2)));
            if (address.StartsWith("DD")) return ("D", int.Parse(address.Substring(2)));
            if (address.StartsWith("WR")) return ("R", int.Parse(address.Substring(2)));
            if (address.StartsWith("WL")) return ("L", int.Parse(address.Substring(2)));
            if (address.StartsWith("FL")) return ("F", int.Parse(address.Substring(2)));
            if (address.StartsWith("SV")) return ("J", int.Parse(address.Substring(2)));
            if (address.StartsWith("EV")) return ("K", int.Parse(address.Substring(2)));
            if (address.StartsWith("IX")) return ("X", int.Parse(address.Substring(2)));
            if (address.StartsWith("IY")) return ("Y", int.Parse(address.Substring(2)));

            if (address.StartsWith("R")) return ("R", int.Parse(address.Substring(1)));
            if (address.StartsWith("L")) return ("L", int.Parse(address.Substring(1)));
            if (address.StartsWith("F")) return ("F", int.Parse(address.Substring(1)));
            if (address.StartsWith("X")) return ("X", int.Parse(address.Substring(1)));
            if (address.StartsWith("Y")) return ("Y", int.Parse(address.Substring(1)));

            return ("D", int.Parse(address));
        }

        private static (string type, int address) ParseContactAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            char prefix = address[0];
            int num = int.Parse(address.Substring(1));

            return prefix switch
            {
                'X' => ("X", num),
                'Y' => ("Y", num),
                'R' => ("R", num),
                'T' => ("T", num),
                'C' => ("C", num),
                'L' => ("L", num),
                _ => throw new ArgumentException($"不支持的触点类型: {prefix}")
            };
        }

        // ═══════════════════════════════════════════
        //  读写命令
        // ═══════════════════════════════════════════

        private OperateResult<string> ReadRegisters(string area, int startAddress, int count)
        {
            int endAddr = startAddress + count - 1;
            string data = area + startAddress.ToString("D5") + endAddr.ToString("D5");
            return SendMewtocol("RD", data);
        }

        private OperateResult<string> ReadSingleRegister(string area, int address)
        {
            return ReadRegisters(area, address, 1);
        }

        private OperateResult WriteRegisters(string area, int startAddress, string hexData)
        {
            string data = area + startAddress.ToString("D5") + hexData;
            var result = SendMewtocol("WD", data);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        public OperateResult<string> ReadPlcModel()
        {
            var r = SendMewtocol("RT", "");
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content.Trim());
        }

        public OperateResult Run()
        {
            var r = SendMewtocol("MS", "01");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        public OperateResult Stop()
        {
            var r = SendMewtocol("MS", "02");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  触点（位）读写
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadContact(string address)
        {
            var (type, addr) = ParseContactAddress(address);
            var r = SendMewtocol("RCS", type + addr.ToString("D4"));
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() == "1");
        }

        public OperateResult WriteContact(string address, bool value)
        {
            var (type, addr) = ParseContactAddress(address);
            string data = type + addr.ToString("D4") + (value ? "1" : "0");
            var r = SendMewtocol("WCS", data);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        public OperateResult<bool[]> ReadContacts(string address, ushort count)
        {
            var (type, addr) = ParseContactAddress(address);
            string data = type + addr.ToString("D4") + count.ToString("D4");
            var r = SendMewtocol("RLS", data);
            if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message, r.ErrorCode);

            bool[] bits = new bool[r.Content.Length];
            for (int i = 0; i < r.Content.Length; i++)
                bits[i] = r.Content[i] == '1';
            return OperateResult<bool[]>.Success(bits);
        }

        public OperateResult WriteContacts(string address, bool[] values)
        {
            var (type, addr) = ParseContactAddress(address);
            var sb = new StringBuilder();
            sb.Append(type);
            sb.Append(addr.ToString("D4"));
            sb.Append(values.Length.ToString("D4"));
            foreach (var v in values)
                sb.Append(v ? "1" : "0");

            var r = SendMewtocol("WLS", sb.ToString());
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 数据类型读写
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadSingleRegister(area, addr);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success(r.Content != "0000");
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadSingleRegister(area, addr);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success((short)HexToUInt16(r.Content));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success((int)HexToUInt32(r.Content));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 16) return OperateResult<long>.Failed("响应数据不足");
            return OperateResult<long>.Success(HexToInt64(r.Content));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override unsafe OperateResult<float> ReadFloat(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<float>.Failed("响应数据不足");
            int v = (int)HexToUInt32(r.Content);
            return OperateResult<float>.Success(*(float*)&v);
        }

        public override unsafe OperateResult<double> ReadDouble(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 4);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 16) return OperateResult<double>.Failed("响应数据不足");
            long v = HexToInt64(r.Content);
            return OperateResult<double>.Success(*(double*)&v);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var (area, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(area, addr, regCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            byte[] bytes = HexToBytes(r.Content);
            string text = Encoding.ASCII.GetString(bytes, 0, Math.Min(length, bytes.Length));
            return OperateResult<string>.Success(text.TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (area, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(area, addr, regCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = HexToBytes(r.Content);
            if (data.Length < length)
                return OperateResult<byte[]>.Failed($"响应数据不足: 期望 {length} 字节，实际 {data.Length} 字节");

            byte[] result = new byte[length];
            Array.Copy(data, result, length);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 写入 ──

        public override OperateResult Write(string address, bool value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = value ? "0001" : "0000";
            return WriteRegisters(area, addr, hex);
        }

        public override OperateResult Write(string address, short value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = ((ushort)value).ToString("X4");
            return WriteRegisters(area, addr, hex);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = ((uint)value).ToString("X8");
            return WriteRegisters(area, addr, hex);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = unchecked((ulong)value).ToString("X16");
            return WriteRegisters(area, addr, hex);
        }

        public override OperateResult Write(string address, ulong value)
        {
            var (area, addr) = ParseAddress(address);
            string hex = value.ToString("X16");
            return WriteRegisters(area, addr, hex);
        }

        public override unsafe OperateResult Write(string address, float value)
        {
            int v = *(int*)&value;
            return Write(address, v);
        }

        public override unsafe OperateResult Write(string address, double value)
        {
            ulong v = *(ulong*)&value;
            return Write(address, v);
        }

        public override OperateResult Write(string address, string value)
        {
            var (area, addr) = ParseAddress(address);
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            string hex = BytesToHex(bytes);
            return WriteRegisters(area, addr, hex);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var (area, addr) = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            string hex = BytesToHex(data);
            return WriteRegisters(area, addr, hex);
        }

        // ═══════════════════════════════════════════
        //  Hex 转换辅助
        // ═══════════════════════════════════════════

        private static ushort HexToUInt16(string hex) => ushort.Parse(hex.Substring(0, 4), NumberStyles.HexNumber);
        private static uint HexToUInt32(string hex) => uint.Parse(hex.Substring(0, 8), NumberStyles.HexNumber);
        private static long HexToInt64(string hex) => unchecked((long)ulong.Parse(hex.Substring(0, 16), NumberStyles.HexNumber));

        private static byte[] HexToBytes(string hex)
        {
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = (byte)(HexVal(hex[i * 2]) << 4 | HexVal(hex[i * 2 + 1]));
            return result;
        }

        private static string BytesToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (byte b in data) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 :
            c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            var result = new Dictionary<string, object?>();
            foreach (string addr in addressList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = (object?)r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");

            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addressList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");

            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool v => Write(kv.Key, v),
                    short v => Write(kv.Key, v),
                    ushort v => Write(kv.Key, (short)v),
                    int v => Write(kv.Key, v),
                    uint v => Write(kv.Key, (int)v),
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

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);
    }
}
