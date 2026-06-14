using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Vigor
{
    public class VigorOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        public byte Station { get; set; }

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public VigorOverTcpClient(string ip, int port = 5000, byte station = 1, int timeout = 3000)
            : base(ip, port, timeout)
        {
            Station = station;
            SetPersistentConnection();
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            try
            {
                var addr = VigorAddress.Parse(address);
                if (!addr.IsBit)
                    return OperateResult<bool>.Failed($"Address {address} is not a bit area");

                var cmd = VigorProtocol.BuildReadCommand(Station, VigorCommand.ReadBit, addr.DataCode, addr.Number, 1);
                var resp = SendVigorFrame(cmd);
                if (!resp.IsSuccess) return OperateResult<bool>.Failed(resp.Message);

                var parsed = VigorProtocol.ParseResponse(resp.Content, VigorCommand.ReadBit);
                if (!parsed.IsSuccess) return OperateResult<bool>.Failed(parsed.Message);

                bool value = parsed.Content.Length > 0 && parsed.Content[0] != 0;
                return OperateResult<bool>.Success(value);
            }
            catch (Exception ex) { return OperateResult<bool>.Failed(ex.Message); }
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            try
            {
                var addr = VigorAddress.Parse(address);
                if (addr.IsBit)
                    return OperateResult<short>.Failed($"Address {address} is a bit area, not a word area");

                var cmd = VigorProtocol.BuildReadCommand(Station, VigorCommand.ReadWord, addr.DataCode, addr.Number, 1);
                var resp = SendVigorFrame(cmd);
                if (!resp.IsSuccess) return OperateResult<short>.Failed(resp.Message);

                var parsed = VigorProtocol.ParseResponse(resp.Content, VigorCommand.ReadWord);
                if (!parsed.IsSuccess) return OperateResult<short>.Failed(parsed.Message);

                if (parsed.Content.Length < 2)
                    return OperateResult<short>.Failed("Response data too short");

                return OperateResult<short>.Success((short)(parsed.Content[0] | (parsed.Content[1] << 8)));
            }
            catch (Exception ex) { return OperateResult<short>.Failed(ex.Message); }
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            try
            {
                var addr = VigorAddress.Parse(address);
                if (addr.IsBit)
                    return OperateResult<int>.Failed($"Address {address} is a bit area");

                var cmd = VigorProtocol.BuildReadCommand(Station, VigorCommand.ReadWord, addr.DataCode, addr.Number, 2);
                var resp = SendVigorFrame(cmd);
                if (!resp.IsSuccess) return OperateResult<int>.Failed(resp.Message);

                var parsed = VigorProtocol.ParseResponse(resp.Content, VigorCommand.ReadWord);
                if (!parsed.IsSuccess) return OperateResult<int>.Failed(parsed.Message);

                if (parsed.Content.Length < 4)
                    return OperateResult<int>.Failed("Response data too short for Int32");

                return OperateResult<int>.Success(
                    parsed.Content[0] | (parsed.Content[1] << 8) |
                    (parsed.Content[2] << 16) | (parsed.Content[3] << 24));
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
                var addr = VigorAddress.Parse(address);
                if (addr.IsBit)
                    return OperateResult<long>.Failed($"Address {address} is a bit area");

                var cmd = VigorProtocol.BuildReadCommand(Station, VigorCommand.ReadWord, addr.DataCode, addr.Number, 4);
                var resp = SendVigorFrame(cmd);
                if (!resp.IsSuccess) return OperateResult<long>.Failed(resp.Message);

                var parsed = VigorProtocol.ParseResponse(resp.Content, VigorCommand.ReadWord);
                if (!parsed.IsSuccess) return OperateResult<long>.Failed(parsed.Message);

                if (parsed.Content.Length < 8)
                    return OperateResult<long>.Failed("Response data too short for Int64");

                return OperateResult<long>.Success(
                    (long)parsed.Content[0] | ((long)parsed.Content[1] << 8) |
                    ((long)parsed.Content[2] << 16) | ((long)parsed.Content[3] << 24) |
                    ((long)parsed.Content[4] << 32) | ((long)parsed.Content[5] << 40) |
                    ((long)parsed.Content[6] << 48) | ((long)parsed.Content[7] << 56));
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
            return OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0));
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
                int wordCount = (length + 1) / 2;
                var addr = VigorAddress.Parse(address);
                if (addr.IsBit)
                    return OperateResult<byte[]>.Failed($"Address {address} is a bit area");

                var cmd = VigorProtocol.BuildReadCommand(Station, VigorCommand.ReadWord, addr.DataCode, addr.Number, wordCount);
                var resp = SendVigorFrame(cmd);
                if (!resp.IsSuccess) return OperateResult<byte[]>.Failed(resp.Message);

                var parsed = VigorProtocol.ParseResponse(resp.Content, VigorCommand.ReadWord);
                if (!parsed.IsSuccess) return OperateResult<byte[]>.Failed(parsed.Message);

                return OperateResult<byte[]>.Success(parsed.Content);
            }
            catch (Exception ex) { return OperateResult<byte[]>.Failed(ex.Message); }
        }

        public override OperateResult Write(string address, bool value)
        {
            try
            {
                var addr = VigorAddress.Parse(address);
                if (!addr.IsBit)
                    return OperateResult.Failed($"Address {address} is not a bit area");

                byte[] data = new byte[] { (byte)(value ? 0xFF : 0x00) };
                var cmd = VigorProtocol.BuildWriteCommand(Station, VigorCommand.WriteBit, addr.DataCode, addr.Number, 1, data);
                var resp = SendVigorFrame(cmd);
                if (!resp.IsSuccess) return OperateResult.Failed(resp.Message);

                var parsed = VigorProtocol.ParseResponse(resp.Content, VigorCommand.WriteBit);
                if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        public override OperateResult Write(string address, short value)
            => WriteWord(address, BitConverter.GetBytes(value));

        public override OperateResult Write(string address, ushort value)
            => WriteWord(address, BitConverter.GetBytes(value));

        public override OperateResult Write(string address, int value)
            => WriteWord(address, BitConverter.GetBytes(value), 2);

        public override OperateResult Write(string address, uint value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
            => WriteWord(address, BitConverter.GetBytes(value), 4);

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
            try
            {
                int wordCount = (data.Length + 1) / 2;
                byte[] padded = new byte[wordCount * 2];
                Buffer.BlockCopy(data, 0, padded, 0, data.Length);
                return WriteWord(address, padded, wordCount);
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        private OperateResult WriteWord(string address, byte[] leData, int wordCount = 1)
        {
            try
            {
                var addr = VigorAddress.Parse(address);
                if (addr.IsBit)
                    return OperateResult.Failed($"Address {address} is a bit area, use Write(address, bool)");

                var cmd = VigorProtocol.BuildWriteCommand(Station, VigorCommand.WriteWord, addr.DataCode, addr.Number, wordCount, leData);
                var resp = SendVigorFrame(cmd);
                if (!resp.IsSuccess) return OperateResult.Failed(resp.Message);

                var parsed = VigorProtocol.ParseResponse(resp.Content, VigorCommand.WriteWord);
                if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        private OperateResult<byte[]> SendVigorFrame(byte[] frame)
        {
            try
            {
                if (!IsConnected)
                {
                    var cr = Connect();
                    if (!cr.IsSuccess) return OperateResult<byte[]>.Failed(cr.Message);
                }

                if (_stream == null)
                    return OperateResult<byte[]>.Failed("Not connected");

                lock (_lock)
                {
                    Log.Debug($"TX → {DataConverter.ToHexString(frame)}");
                    RaiseMessageSent(DataConverter.ToHexString(frame));

                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();

                    var response = ReadVigorResponseTcp();
                    if (response == null)
                        return OperateResult<byte[]>.Failed("No response from Vigor PLC (timeout)");

                    Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                    RaiseMessageReceived(DataConverter.ToHexString(response));

                    return OperateResult<byte[]>.Success(response);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Vigor TCP communication error: {ex.Message}");
                RaiseError($"Vigor TCP communication error: {ex.Message}");
                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }
                return OperateResult<byte[]>.Failed($"Vigor TCP communication error: {ex.Message}");
            }
        }

        private byte[]? ReadVigorResponseTcp()
        {
            if (_stream == null) return null;

            var dataBytes = new List<byte>();
            int start = Environment.TickCount;
            bool headerRead = false;
            int dataLen = 0;
            int headerIndex = 0;
            byte[] headerBuf = new byte[4];

            while (unchecked(Environment.TickCount - start) < Timeout)
            {
                int b = _stream.ReadByte();
                if (b < 0) return null;
                byte bt = (byte)b;

                if (!headerRead)
                {
                    headerBuf[headerIndex++] = bt;
                    if (headerIndex == 4)
                    {
                        headerRead = true;
                        dataLen = headerBuf[2] | (headerBuf[3] << 8);
                        if (dataLen < VigorConstants.FixedDataLen)
                            return null;
                    }
                    continue;
                }

                if (bt == VigorConstants.STX)
                {
                    int next = _stream.ReadByte();
                    if (next < 0) return null;
                    if ((byte)next == VigorConstants.ETX)
                    {
                        int bcc1 = _stream.ReadByte();
                        int bcc2 = _stream.ReadByte();
                        if (bcc1 < 0 || bcc2 < 0) return null;

                        var response = new List<byte>();
                        response.Add(headerBuf[0]);
                        response.Add(headerBuf[1]);
                        response.Add(headerBuf[2]);
                        response.Add(headerBuf[3]);
                        response.AddRange(dataBytes);
                        response.Add(VigorConstants.STX);
                        response.Add(VigorConstants.ETX);
                        response.Add((byte)bcc1);
                        response.Add((byte)bcc2);
                        return response.ToArray();
                    }
                    else
                    {
                        dataBytes.Add(bt);
                        dataBytes.Add((byte)next);
                    }
                }
                else
                {
                    dataBytes.Add(bt);
                }
            }

            return null;
        }

        // ── IBatchReadWrite ──

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("Address list is empty");

            var result = new Dictionary<string, object?>();
            foreach (string addr in addrList)
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
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("Address list is empty");

            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addrList)
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
                return OperateResult.Failed("Write list is empty");

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
                    _ => OperateResult.Failed($"Unsupported type: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

        public override string ToString() => $"VigorOverTcpClient[{Ip}:{Port}, Station={Station}]";
    }
}
