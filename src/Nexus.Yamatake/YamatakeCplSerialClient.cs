using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Yamatake
{
    public class YamatakeCplSerialClient : SerialDeviceBase, IBatchReadWrite
    {
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private readonly object _syncLock = new object();

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public byte Station { get; set; } = 1;

        public YamatakeCplSerialClient(ISerialPort serialPort, int timeout = 5000)
            : base(serialPort, timeout) { }

        // ═══════════════════════════════════════════
        //  读取
        // ═══════════════════════════════════════════

        public override OperateResult<short> ReadInt16(string address)
        {
            var parsed = YamatakeCplAddress.Parse(address, Station);
            var cmd = BuildReadCommand(parsed.Station, parsed.Address, 1);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult<short>.Failed(recv.Message);

            return ParseReadResponse(recv.Content, 1);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var parsed = YamatakeCplAddress.Parse(address, Station);
            var cmd = BuildReadCommand(parsed.Station, parsed.Address, 2);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult<int>.Failed(recv.Message);

            var vals = ParseReadResponseMultiple(recv.Content, 2);
            if (!vals.IsSuccess) return OperateResult<int>.Failed(vals.Message, vals.ErrorCode);

            int hi = vals.Content[0] & 0xFFFF;
            int lo = vals.Content[1] & 0xFFFF;
            return OperateResult<int>.Success((hi << 16) | lo);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(
                BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var parsed = YamatakeCplAddress.Parse(address, Station);
            var cmd = BuildReadCommand(parsed.Station, parsed.Address, 4);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult<double>.Failed(recv.Message);

            var vals = ParseReadResponseMultiple(recv.Content, 4);
            if (!vals.IsSuccess) return OperateResult<double>.Failed(vals.Message, vals.ErrorCode);

            long v = ((long)(vals.Content[0] & 0xFFFF) << 48)
                   | ((long)(vals.Content[1] & 0xFFFF) << 32)
                   | ((long)(vals.Content[2] & 0xFFFF) << 16)
                   | (long)(vals.Content[3] & 0xFFFF);
            return OperateResult<double>.Success(BitConverter.Int64BitsToDouble(v));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var parsed = YamatakeCplAddress.Parse(address, Station);
            int wordCount = (length + 1) / 2;
            var cmd = BuildReadCommand(parsed.Station, parsed.Address, wordCount);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            var vals = ParseReadResponseMultiple(recv.Content, wordCount);
            if (!vals.IsSuccess) return OperateResult<byte[]>.Failed(vals.Message, vals.ErrorCode);

            var bytes = new byte[wordCount * 2];
            for (int i = 0; i < wordCount; i++)
            {
                bytes[i * 2] = (byte)((vals.Content[i] >> 8) & 0xFF);
                bytes[i * 2 + 1] = (byte)(vals.Content[i] & 0xFF);
            }
            return OperateResult<byte[]>.Success(bytes);
        }

        // ═══════════════════════════════════════════
        //  写入
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, short value)
            => WriteRegisters(address, new[] { value });

        public override OperateResult Write(string address, ushort value)
            => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            short lo = (short)(value & 0xFFFF);
            short hi = (short)((value >> 16) & 0xFFFF);
            return WriteRegisters(address, new[] { hi, lo });
        }

        public override OperateResult Write(string address, uint value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, float value)
            => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public override OperateResult Write(string address, double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            short w0 = (short)((bits >> 48) & 0xFFFF);
            short w1 = (short)((bits >> 32) & 0xFFFF);
            short w2 = (short)((bits >> 16) & 0xFFFF);
            short w3 = (short)(bits & 0xFFFF);
            return WriteRegisters(address, new[] { w0, w1, w2, w3 });
        }

        private OperateResult WriteRegisters(string address, short[] values)
        {
            var parsed = YamatakeCplAddress.Parse(address, Station);
            var cmd = BuildWriteCommand(parsed.Station, parsed.Address, values);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            return ParseWriteResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  命令构建（公开供测试）
        // ═══════════════════════════════════════════

        public static byte[] BuildReadCommand(byte station, int address, int count)
        {
            string stationHex = station.ToString("X2");
            string addressHex = address.ToString("X4");
            string countHex = count.ToString("X2");
            string body = stationHex + 'R' + addressHex + countHex;
            return BuildFrame(body);
        }

        public static byte[] BuildWriteCommand(byte station, int address, short[] values)
        {
            string stationHex = station.ToString("X2");
            string addressHex = address.ToString("X4");
            string countHex = values.Length.ToString("X2");
            var sb = new StringBuilder(stationHex.Length + 1 + 4 + 2 + values.Length * 4);
            sb.Append(stationHex);
            sb.Append('W');
            sb.Append(addressHex);
            sb.Append(countHex);
            for (int i = 0; i < values.Length; i++)
                sb.Append(((ushort)values[i]).ToString("X4"));
            return BuildFrame(sb.ToString());
        }

        private static byte[] BuildFrame(string body)
        {
            byte[] bodyBytes = Encoding.ASCII.GetBytes(body);
            int frameLen = 1 + bodyBytes.Length + 1 + 2;
            var frame = new byte[frameLen];
            frame[0] = STX;
            Buffer.BlockCopy(bodyBytes, 0, frame, 1, bodyBytes.Length);
            frame[1 + bodyBytes.Length] = ETX;

            byte bcc = 0;
            for (int i = 1; i < 1 + bodyBytes.Length + 1; i++)
                bcc ^= frame[i];
            string bccHex = bcc.ToString("X2");
            frame[frameLen - 2] = (byte)bccHex[0];
            frame[frameLen - 1] = (byte)bccHex[1];
            return frame;
        }

        // ═══════════════════════════════════════════
        //  响应解析（公开供测试）
        // ═══════════════════════════════════════════

        public static OperateResult<short> ParseReadResponse(byte[] response, int count)
        {
            var multi = ParseReadResponseMultiple(response, count);
            if (!multi.IsSuccess) return OperateResult<short>.Failed(multi.Message, multi.ErrorCode);
            return OperateResult<short>.Success(multi.Content[0]);
        }

        public static OperateResult<short[]> ParseReadResponseMultiple(byte[] response, int count)
        {
            if (response == null || response.Length < 9)
                return OperateResult<short[]>.Failed("响应数据过短");
            if (response[0] != STX)
                return OperateResult<short[]>.Failed($"STX 校验失败: 0x{response[0]:X2}");

            int etxPos = Array.IndexOf(response, ETX, 1);
            if (etxPos < 0)
                return OperateResult<short[]>.Failed("未找到 ETX");

            if (!VerifyBcc(response, etxPos))
                return OperateResult<short[]>.Failed("BCC 校验失败");

            string stationStr = Encoding.ASCII.GetString(response, 1, 2);
            string cmdChar = Encoding.ASCII.GetString(response, 3, 1);
            string respCode = Encoding.ASCII.GetString(response, 4, 2);

            if (respCode != "00")
                return OperateResult<short[]>.Failed($"设备错误: {GetErrorMessage(respCode)}");

            int dataStart = 6;
            int dataLen = etxPos - dataStart;
            int expectedLen = count * 4;
            if (dataLen < expectedLen)
                return OperateResult<short[]>.Failed($"数据长度不足: 期望 {expectedLen}, 实际 {dataLen}");

            string dataHex = Encoding.ASCII.GetString(response, dataStart, expectedLen);
            var result = new short[count];
            for (int i = 0; i < count; i++)
            {
                string word = dataHex.Substring(i * 4, 4);
                result[i] = (short)ushort.Parse(word, System.Globalization.NumberStyles.HexNumber);
            }
            return OperateResult<short[]>.Success(result);
        }

        public static OperateResult ParseWriteResponse(byte[] response)
        {
            if (response == null || response.Length < 9)
                return OperateResult.Failed("响应数据过短");
            if (response[0] != STX)
                return OperateResult.Failed($"STX 校验失败: 0x{response[0]:X2}");

            int etxPos = Array.IndexOf(response, ETX, 1);
            if (etxPos < 0)
                return OperateResult.Failed("未找到 ETX");

            if (!VerifyBcc(response, etxPos))
                return OperateResult.Failed("BCC 校验失败");

            string respCode = Encoding.ASCII.GetString(response, 4, 2);
            if (respCode != "00")
                return OperateResult.Failed($"设备错误: {GetErrorMessage(respCode)}");

            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  串口收发
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendAndReceiveCpl(byte[] request)
        {
            lock (_syncLock)
            {
                try
                {
                    if (!Port.IsOpen)
                        return OperateResult<byte[]>.Failed("串口未打开");

                    RaiseMessageSent(DataConverter.ToHexString(request));
                    Port.Write(request, 0, request.Length);

                    if (InterFrameDelay > 0)
                        Thread.Sleep(InterFrameDelay);

                    var response = ReadUntilEtx();
                    if (response == null || response.Length == 0)
                        return OperateResult<byte[]>.Failed("响应超时");

                    RaiseMessageReceived(DataConverter.ToHexString(response));
                    return OperateResult<byte[]>.Success(response);
                }
                catch (Exception ex)
                {
                    RaiseError($"Yamatake CPL 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
                }
            }
        }

        private byte[]? ReadUntilEtx()
        {
            var buf = new List<byte>(64);
            int start = Environment.TickCount;
            byte[] readBuf = new byte[256];

            while (unchecked(Environment.TickCount - start) < Timeout)
            {
                try
                {
                    int read = Port.Read(readBuf, 0, readBuf.Length);
                    if (read > 0)
                    {
                        for (int i = 0; i < read; i++)
                            buf.Add(readBuf[i]);

                        int etxPos = Array.IndexOf(buf.ToArray(), ETX);
                        if (etxPos >= 0)
                        {
                            int needed = etxPos + 1 + 2;
                            while (buf.Count < needed && unchecked(Environment.TickCount - start) < Timeout)
                            {
                                Thread.Sleep(5);
                                if (Port.Read(readBuf, 0, 1) > 0)
                                    buf.Add(readBuf[0]);
                            }
                            return buf.ToArray();
                        }
                    }
                }
                catch (TimeoutException)
                {
                    if (buf.Count > 0) return buf.ToArray();
                    return null;
                }
                Thread.Sleep(5);
            }
            return buf.Count > 0 ? buf.ToArray() : null;
        }

        // ═══════════════════════════════════════════
        //  辅助
        // ═══════════════════════════════════════════

        private static bool VerifyBcc(byte[] response, int etxPos)
        {
            if (etxPos + 2 >= response.Length) return false;
            byte bcc = 0;
            for (int i = 1; i <= etxPos; i++)
                bcc ^= response[i];
            string expected = bcc.ToString("X2");
            return response[etxPos + 1] == (byte)expected[0]
                && response[etxPos + 2] == (byte)expected[1];
        }

        private static string GetErrorMessage(string code)
        {
            switch (code)
            {
                case "00": return "正常";
                case "01": return "BCC 错误";
                case "02": return "帧格式错误";
                case "03": return "溢出错误";
                case "04": return "校验位错误";
                default: return $"未知错误({code})";
            }
        }

        public override string ToString() => $"YamatakeCplSerialClient[Station={Station}]";

        // ═══════════════════════════════════════════
        //  IBatchReadWrite
        // ═══════════════════════════════════════════

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
                result[addr] = (object)r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
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
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
                    double d => Write(kv.Key, d),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));
    }
}
