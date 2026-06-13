using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Fatek
{
    /// <summary>
    /// 永宏 Fatek FBs Program Over TCP 客户端。
    /// <para>通过 TCP 连接发送 Fatek 串口帧格式报文，用于 Fatek 编程口 TCP 模式。</para>
    /// <para>帧格式: STX(0x02) + Station(2hex ASCII) + Command + Data + ETX(0x03) + Checksum(2hex ASCII)</para>
    /// <para>Checksum = Station 至 ETX 所有字节之和 mod 256。</para>
    /// </summary>
    public class FatekProgramOverTcpClient : TcpDeviceBase, IReadWriteDevice, IBatchReadWrite
    {
        private readonly object _sendLock = new object();
        public byte Station { get; set; }

        public FatekProgramOverTcpClient(string ipAddress, int port = 5000, byte station = 1, int timeout = 5000)
            : base(ipAddress, port, timeout)
        {
            Station = station;
            SetPersistentConnection();
        }

        protected override int ResponseHeaderLength => 0;

        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  帧收发
        // ═══════════════════════════════════════════

        private byte[] BuildCommand(string command)
        {
            string body = Station.ToString("D2") + command;
            return BuildFrame(body);
        }

        private static byte[] BuildFrame(string body)
        {
            var content = Encoding.ASCII.GetBytes(body);
            var frame = new byte[1 + content.Length + 1];
            frame[0] = 0x02;
            Buffer.BlockCopy(content, 0, frame, 1, content.Length);
            frame[frame.Length - 1] = 0x03;

            byte sum = 0;
            for (int i = 1; i < frame.Length; i++)
                sum += frame[i];
            var csBytes = Encoding.ASCII.GetBytes(sum.ToString("X2"));

            var result = new byte[frame.Length + csBytes.Length];
            Buffer.BlockCopy(frame, 0, result, 0, frame.Length);
            Buffer.BlockCopy(csBytes, 0, result, frame.Length, csBytes.Length);
            return result;
        }

        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            if (!IsConnected)
            {
                var cr = Connect();
                if (!cr.IsSuccess) return OperateResult<byte[]>.Failed(cr.Message);
            }

            lock (_sendLock)
            {
                if (_stream == null)
                    return OperateResult<byte[]>.Failed("未连接 / Not connected");

                try
                {
                    Log.Debug($"TX → {BitConverter.ToString(request)}");
                    _stream.Write(request, 0, request.Length);
                    _stream.Flush();

                    var response = ReadResponse();
                    if (response == null)
                        return OperateResult<byte[]>.Failed("无响应 / No response");

                    Log.Debug($"RX ← {BitConverter.ToString(response)}");
                    return OperateResult<byte[]>.Success(response);
                }
                catch (Exception ex)
                {
                    Disconnect();
                    return OperateResult<byte[]>.Failed(ex.Message);
                }
            }
        }

        private byte[]? ReadResponse()
        {
            try
            {
                var buf = new List<byte>();
                int maxRead = 512;
                while (buf.Count < maxRead)
                {
                    int b = _stream!.ReadByte();
                    if (b < 0) break;
                    buf.Add((byte)b);
                    if (b == 0x03 && buf.Count > 3)
                    {
                        int cs1 = _stream.ReadByte();
                        if (cs1 < 0) break;
                        buf.Add((byte)cs1);
                        if (buf.Count >= maxRead) break;
                        int cs2 = _stream.ReadByte();
                        if (cs2 < 0) break;
                        buf.Add((byte)cs2);
                        break;
                    }
                }
                return buf.Count == 0 ? null : buf.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private OperateResult<string> SendCommand(string command)
        {
            var frame = BuildCommand(command);
            var resp = SendAndReceive(frame);
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message);

            string text = Encoding.ASCII.GetString(resp.Content).Trim();
            if (text.StartsWith("\x02")) text = text.Substring(1);
            if (text.Length > 2) text = text.Substring(0, text.Length - 2);
            if (text.EndsWith("\x03")) text = text.Substring(0, text.Length - 1);

            return OperateResult<string>.Success(text);
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        private static (string area, int number, bool isBit) ParseAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
                throw new ArgumentException("Address is empty");

            string addr = address.Trim().ToUpperInvariant();
            char prefix = addr[0];
            string numStr = addr.Substring(1);
            if (!int.TryParse(numStr, out int num))
                throw new FormatException($"Invalid address number: {numStr}");

            switch (prefix)
            {
                case 'R': return ("R", num, true);
                case 'X': return ("X", num, true);
                case 'Y': return ("Y", num, true);
                case 'M': return ("M", num, true);
                case 'D': return ("D", num, false);
                case 'T': return ("T", num, false);
                case 'C': return ("C", num, false);
                default:
                    throw new ArgumentException(
                        $"Unknown area prefix '{prefix}'. Valid: R/X/Y/M/D/T/C");
            }
        }

        private static string IncrementAddress(string address, int offset = 1)
        {
            var (area, num, _) = ParseAddress(address);
            return $"{area}{num + offset}";
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 读取
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            try
            {
                var (area, num, _) = ParseAddress(address);
                var resp = SendCommand($"R{area}{num:D4}");
                if (!resp.IsSuccess) return OperateResult<bool>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<bool>.Failed("Fatek error: " + resp.Content);

                string data = resp.Content.Substring(2).Trim();
                bool value = data == "1" || data.Equals("ON", StringComparison.OrdinalIgnoreCase);
                return OperateResult<bool>.Success(value);
            }
            catch (Exception ex) { return OperateResult<bool>.Failed(ex.Message); }
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            try
            {
                var (area, num, _) = ParseAddress(address);
                var resp = SendCommand($"R{area}{num:D4}");
                if (!resp.IsSuccess) return OperateResult<short>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<short>.Failed("Fatek error: " + resp.Content);

                string data = resp.Content.Substring(2).Trim();
                if (short.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out short val))
                    return OperateResult<short>.Success(val);
                return OperateResult<short>.Failed($"Cannot parse '{data}' as Int16");
            }
            catch (Exception ex) { return OperateResult<short>.Failed(ex.Message); }
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            try
            {
                var (area, num, _) = ParseAddress(address);
                var resp = SendCommand($"R{area}{num:D4}");
                if (!resp.IsSuccess) return OperateResult<ushort>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<ushort>.Failed("Fatek error: " + resp.Content);

                string data = resp.Content.Substring(2).Trim();
                if (ushort.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort val))
                    return OperateResult<ushort>.Success(val);
                return OperateResult<ushort>.Failed($"Cannot parse '{data}' as UInt16");
            }
            catch (Exception ex) { return OperateResult<ushort>.Failed(ex.Message); }
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            try
            {
                var lo = ReadInt16(address);
                if (!lo.IsSuccess) return OperateResult<int>.Failed(lo.Message);
                var hi = ReadInt16(IncrementAddress(address));
                if (!hi.IsSuccess) return OperateResult<int>.Failed(hi.Message);
                return OperateResult<int>.Success((hi.Content << 16) | (lo.Content & 0xFFFF));
            }
            catch (Exception ex) { return OperateResult<int>.Failed(ex.Message); }
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            try
            {
                var lo = ReadInt32(address);
                if (!lo.IsSuccess) return OperateResult<long>.Failed(lo.Message);
                var hi = ReadInt32(IncrementAddress(IncrementAddress(address)));
                if (!hi.IsSuccess) return OperateResult<long>.Failed(hi.Message);
                return OperateResult<long>.Success(((long)hi.Content << 32) | (uint)lo.Content);
            }
            catch (Exception ex) { return OperateResult<long>.Failed(ex.Message); }
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success((ulong)r.Content);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success(
                BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success(BitConverter.Int64BitsToDouble(r.Content));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, (ushort)(length * 2));
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            try
            {
                var result = new List<byte>();
                string currentAddr = address;
                for (int i = 0; i < (length + 1) / 2; i++)
                {
                    var r = ReadInt16(currentAddr);
                    if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
                    result.AddRange(BitConverter.GetBytes(r.Content));
                    currentAddr = IncrementAddress(currentAddr);
                }
                return OperateResult<byte[]>.Success(result.ToArray());
            }
            catch (Exception ex) { return OperateResult<byte[]>.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 写入
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, bool value)
        {
            try
            {
                var (area, num, _) = ParseAddress(address);
                var resp = SendCommand($"W{area}{num:D4}{(value ? "1" : "0")}");
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek write error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        public override OperateResult Write(string address, short value)
            => WriteRegister(address, value.ToString("D5"));

        public override OperateResult Write(string address, ushort value)
            => WriteRegister(address, value.ToString("D5"));

        public override OperateResult Write(string address, int value)
        {
            var lo = (short)(value & 0xFFFF);
            var hi = (short)((value >> 16) & 0xFFFF);
            var r1 = Write(address, lo);
            if (!r1.IsSuccess) return r1;
            return Write(IncrementAddress(address), hi);
        }

        public override OperateResult Write(string address, uint value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var r1 = Write(address, (int)(value & 0xFFFFFFFF));
            if (!r1.IsSuccess) return r1;
            return Write(IncrementAddress(IncrementAddress(address)), (int)(value >> 32));
        }

        public override OperateResult Write(string address, ulong value)
            => Write(address, (long)value);

        public override OperateResult Write(string address, float value)
            => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public override OperateResult Write(string address, double value)
            => Write(address, BitConverter.ToInt64(BitConverter.GetBytes(value), 0));

        public override OperateResult Write(string address, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            return Write(address, bytes);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            for (int i = 0; i < data.Length; i += 2)
            {
                short val = data.Length > i + 1
                    ? (short)(data[i] | (data[i + 1] << 8))
                    : data[i];
                string addr = IncrementAddress(address, i / 2);
                var r = Write(addr, val);
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        private OperateResult WriteRegister(string address, string valueStr)
        {
            try
            {
                var (area, num, _) = ParseAddress(address);
                var resp = SendCommand($"W{area}{num:D4}{valueStr}");
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek write error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  批量位操作
        // ═══════════════════════════════════════════

        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            if (count == 0) return OperateResult<bool[]>.Success(Array.Empty<bool>());
            if (count == 1)
            {
                var r = ReadBool(address);
                return r.IsSuccess
                    ? OperateResult<bool[]>.Success(new[] { r.Content })
                    : OperateResult<bool[]>.Failed(r.Message);
            }

            try
            {
                var (area, num, _) = ParseAddress(address);
                string countHex = count.ToString("X2");
                var resp = SendCommand($"44{countHex}{area}{num:D4}");
                if (!resp.IsSuccess) return OperateResult<bool[]>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<bool[]>.Failed("Fatek read error: " + resp.Content);

                string data = resp.Content.Substring(2);
                var result = new bool[count];
                for (int i = 0; i < count && i < data.Length; i++)
                    result[i] = data[i] == '1';

                return OperateResult<bool[]>.Success(result);
            }
            catch (Exception ex) { return OperateResult<bool[]>.Failed(ex.Message); }
        }

        public OperateResult WriteBools(string address, bool[] values)
        {
            if (values == null || values.Length == 0) return OperateResult.Success();
            if (values.Length == 1) return Write(address, values[0]);

            try
            {
                var (area, num, _) = ParseAddress(address);
                string countHex = values.Length.ToString("X2");
                var dataChars = new char[values.Length];
                for (int i = 0; i < values.Length; i++)
                    dataChars[i] = values[i] ? '1' : '0';

                var resp = SendCommand($"45{countHex}{area}{num:D4}{new string(dataChars)}");
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek write error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadPlcStatus()
        {
            try
            {
                var resp = SendCommand("40");
                if (!resp.IsSuccess) return OperateResult<bool>.Failed(resp.Message);
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult<bool>.Failed("Fatek error: " + resp.Content);

                string data = resp.Content.Substring(2).Trim();
                bool isRunning = data.Length > 0 && data[0] == '1';
                return OperateResult<bool>.Success(isRunning);
            }
            catch (Exception ex) { return OperateResult<bool>.Failed(ex.Message); }
        }

        public OperateResult Run()
        {
            try
            {
                var resp = SendCommand("411");
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek RUN error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        public OperateResult Stop()
        {
            try
            {
                var resp = SendCommand("410");
                if (!resp.IsSuccess) return resp;
                if (!resp.Content.StartsWith("!0"))
                    return OperateResult.Failed("Fatek STOP error: " + resp.Content);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

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
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
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
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
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

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);
    }
}
