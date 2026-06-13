using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Yamatake
{
    public class YamatakeCplOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private readonly object _sendLock = new object();

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public byte Station { get; set; } = 1;

        public YamatakeCplOverTcpClient(string ip, int port = 5000, int timeout = 5000)
            : base(ip, port, timeout)
        {
            SetPersistentConnection();
        }

        // ═══════════════════════════════════════════
        //  读取
        // ═══════════════════════════════════════════

        public override OperateResult<short> ReadInt16(string address)
        {
            var parsed = YamatakeCplAddress.Parse(address, Station);
            var cmd = YamatakeCplSerialClient.BuildReadCommand(parsed.Station, parsed.Address, 1);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult<short>.Failed(recv.Message);

            return YamatakeCplSerialClient.ParseReadResponse(recv.Content, 1);
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
            var cmd = YamatakeCplSerialClient.BuildReadCommand(parsed.Station, parsed.Address, 2);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult<int>.Failed(recv.Message);

            var vals = YamatakeCplSerialClient.ParseReadResponseMultiple(recv.Content, 2);
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
            var cmd = YamatakeCplSerialClient.BuildReadCommand(parsed.Station, parsed.Address, 4);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult<double>.Failed(recv.Message);

            var vals = YamatakeCplSerialClient.ParseReadResponseMultiple(recv.Content, 4);
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
            var cmd = YamatakeCplSerialClient.BuildReadCommand(parsed.Station, parsed.Address, wordCount);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            var vals = YamatakeCplSerialClient.ParseReadResponseMultiple(recv.Content, wordCount);
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
            var cmd = YamatakeCplSerialClient.BuildWriteCommand(parsed.Station, parsed.Address, values);
            var recv = SendAndReceiveCpl(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            return YamatakeCplSerialClient.ParseWriteResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  TCP 收发
        // ═══════════════════════════════════════════

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
                    return OperateResult<byte[]>.Failed("未连接");

                try
                {
                    RaiseMessageSent(DataConverter.ToHexString(request));
                    _stream.Write(request, 0, request.Length);
                    _stream.Flush();

                    var response = ReadResponseFromStream();
                    if (response == null || response.Length == 0)
                        return OperateResult<byte[]>.Failed("响应超时");

                    RaiseMessageReceived(DataConverter.ToHexString(response));
                    return OperateResult<byte[]>.Success(response);
                }
                catch (Exception ex)
                {
                    Disconnect();
                    return OperateResult<byte[]>.Failed(ex.Message);
                }
            }
        }

        private OperateResult<byte[]> SendAndReceiveCpl(byte[] request)
            => SendAndReceive(request);

        private byte[]? ReadResponseFromStream()
        {
            try
            {
                var buf = new List<byte>(64);
                byte[] readBuf = new byte[256];
                int deadline = Environment.TickCount + Timeout;

                while (Environment.TickCount < deadline)
                {
                    if (_stream!.DataAvailable)
                    {
                        int read = _stream.Read(readBuf, 0, readBuf.Length);
                        if (read > 0)
                        {
                            for (int i = 0; i < read; i++)
                                buf.Add(readBuf[i]);

                            int etxPos = Array.IndexOf(buf.ToArray(), ETX);
                            if (etxPos >= 0)
                            {
                                int needed = etxPos + 1 + 2;
                                while (buf.Count < needed && Environment.TickCount < deadline)
                                {
                                    Thread.Sleep(5);
                                    if (_stream.DataAvailable && _stream.Read(readBuf, 0, 1) > 0)
                                        buf.Add(readBuf[0]);
                                }
                                return buf.ToArray();
                            }
                        }
                    }
                    Thread.Sleep(5);
                }
                return buf.Count > 0 ? buf.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        public override string ToString() => $"YamatakeCplOverTcpClient[{Ip}:{Port}, Station={Station}]";

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
