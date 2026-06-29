using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Siemens.S7Plus
{
    /// <summary>
    /// Siemens S7 Plus 客户端 — 支持 TIA Portal S7-1500 系列 PLC。
    /// <para>扩展 S7 协议，支持更大的 PDU 和优化数据块访问。</para>
    /// <para>地址格式: DB1.DBX0.0, DB1.DBW0, DB1.DBD0, I0.0, Q0.0, M0.0, T0, C0</para>
    /// </summary>
    public class S7PlusClient : TcpDeviceBase, IBatchReadWrite
    {
        public byte LocalTSAP { get; set; } = 0x01;
        public byte RemoteTSAP { get; set; } = 0x00;
        public ushort MaxPduSize { get; set; } = 480;

        private readonly S7PlusAddressParser _parser = new S7PlusAddressParser();
        private bool _connected;

        public S7PlusClient(string ip, int port = 102, int timeout = 5000)
            : base(ip, port, timeout) { }

        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            return (header[2] << 8) | header[3];
        }

        public override OperateResult Connect()
        {
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;
            var handshake = S7Handshake();
            if (!handshake.IsSuccess) { Disconnect(); return handshake; }
            return OperateResult.Success();
        }

        private OperateResult S7Handshake()
        {
            byte[] cotpConnect = {
                0x03, 0x00, 0x00, 0x16, 0x11, 0xD0, 0x00, 0x01,
                0x00, 0x01, 0x00, 0xC1, 0x02, 0x01, 0x00,
                0xC2, 0x02, 0x01, 0x02, 0xC0, 0x01, 0x09
            };
            var r1 = SendAndReceive(cotpConnect);
            if (!r1.IsSuccess) return OperateResult.Failed($"COTP 连接失败: {r1.Message}");
            byte[] s7Setup = {
                0x03, 0x00, 0x00, 0x19, 0x02, 0xF0, 0x80, 0x32,
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00,
                0x00, 0x00, 0x00, 0xF0, 0x00, 0x00, 0x01, 0x00,
                0x01
            };
            var r2 = SendAndReceive(s7Setup);
            if (!r2.IsSuccess) return OperateResult.Failed($"S7 Setup 失败: {r2.Message}");
            _connected = true;
            return OperateResult.Success();
        }

        private OperateResult<byte[]> ReadRaw(S7PlusAddress addr, ushort byteCount)
        {
            if (!_connected) return OperateResult<byte[]>.Failed("未连接到 PLC");

            byte area = addr.Area switch
            {
                S7PlusArea.DB => 0x84, S7PlusArea.I => 0x81, S7PlusArea.Q => 0x82,
                S7PlusArea.M => 0x83, S7PlusArea.T => 0x1D, S7PlusArea.C => 0x1C,
                _ => 0x84
            };

            byte[] readReq = new byte[31];
            readReq[0] = 0x03; readReq[1] = 0x00;
            readReq[2] = 0x00; readReq[3] = 0x1F;
            readReq[4] = 0x02; readReq[5] = 0xF0; readReq[6] = 0x80;
            readReq[7] = 0x32; readReq[8] = 0x01;
            readReq[13] = 0x01;
            readReq[14] = 0x00; readReq[15] = 0x00;
            readReq[16] = 0x00; readReq[17] = 0x01;
            readReq[18] = 0x00; readReq[19] = 0x0E;
            readReq[20] = 0x00;
            readReq[21] = 0x01;
            readReq[22] = 0x12;
            readReq[23] = 0x0A;
            readReq[24] = 0x10;
            readReq[25] = 0x02;
            readReq[26] = (byte)(byteCount >> 8); readReq[27] = (byte)byteCount;
            readReq[28] = (byte)(addr.DbNumber >> 8); readReq[29] = (byte)addr.DbNumber;
            readReq[30] = area;
            int dbx = addr.StartByte * 8 + addr.BitOffset;
            readReq[24] = 0x10; readReq[25] = 0x02;

            var result = SendAndReceive(readReq);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

            byte[] response = result.Content;
            if (response.Length < 25) return OperateResult<byte[]>.Failed("S7 Plus 响应过短");

            ushort error = (ushort)((response[17] << 8) | response[18]);
            if (error != 0) return OperateResult<byte[]>.Failed($"S7 Plus 错误: 0x{error:X4}");

            int dataOffset = 25;
            if (dataOffset + byteCount > response.Length)
                return OperateResult<byte[]>.Failed("S7 Plus 响应数据不足");

            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(response, dataOffset, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        private OperateResult WriteRaw(S7PlusAddress addr, byte[] data)
        {
            if (!_connected) return OperateResult.Failed("未连接到 PLC");

            byte area = addr.Area switch
            {
                S7PlusArea.DB => 0x84, S7PlusArea.I => 0x81, S7PlusArea.Q => 0x82,
                S7PlusArea.M => 0x83, S7PlusArea.T => 0x1D, S7PlusArea.C => 0x1C,
                _ => 0x84
            };

            byte[] writeReq = new byte[35 + data.Length];
            writeReq[0] = 0x03; writeReq[1] = 0x00;
            writeReq[2] = (byte)((35 + data.Length) >> 8); writeReq[3] = (byte)(35 + data.Length);
            writeReq[4] = 0x02; writeReq[5] = 0xF0; writeReq[6] = 0x80;
            writeReq[7] = 0x32; writeReq[8] = 0x01;
            writeReq[13] = 0x02;
            writeReq[14] = 0x00; writeReq[15] = 0x00;
            writeReq[16] = 0x00; writeReq[17] = 0x01;
            writeReq[18] = 0x00; writeReq[19] = (byte)(12 + data.Length);
            writeReq[20] = 0x00;
            writeReq[21] = 0x01;
            writeReq[22] = 0x12;
            writeReq[23] = 0x0A;
            writeReq[24] = 0x10;
            writeReq[25] = 0x02;
            writeReq[26] = (byte)(data.Length >> 8); writeReq[27] = (byte)data.Length;
            writeReq[28] = (byte)(addr.DbNumber >> 8); writeReq[29] = (byte)addr.DbNumber;
            writeReq[30] = area;
            writeReq[31] = (byte)(addr.StartByte >> 5); writeReq[32] = (byte)((addr.StartByte << 3) | addr.BitOffset);
            writeReq[33] = 0x00; writeReq[34] = 0x04;
            Buffer.BlockCopy(data, 0, writeReq, 35, data.Length);

            var result = SendAndReceive(writeReq);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);
            return OperateResult.Success();
        }

        // ── Read implementations ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success((r.Content[0] & (1 << addr.BitOffset)) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(
                ((long)r.Content[0] << 56) | ((long)r.Content[1] << 48) | ((long)r.Content[2] << 40) | ((long)r.Content[3] << 32) |
                ((long)r.Content[4] << 24) | ((long)r.Content[5] << 16) | ((long)r.Content[6] << 8) | r.Content[7]);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            int bits = (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3];
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, (ushort)(length + 2));
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 2, length).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, length);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(r.Content);
        }

        // ── Write implementations ──────────────────

        public override OperateResult Write(string address, bool value)
        {
            var addr = _parser.Parse(address);
            var current = ReadRaw(addr, 1);
            if (!current.IsSuccess) return OperateResult.Failed(current.Message);
            byte b = current.Content[0];
            if (value) b |= (byte)(1 << addr.BitOffset);
            else b &= (byte)~(1 << addr.BitOffset);
            return WriteRaw(addr, new byte[] { b });
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = _parser.Parse(address);
            return WriteRaw(addr, new byte[] { (byte)(value >> 8), (byte)value });
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = _parser.Parse(address);
            return WriteRaw(addr, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = _parser.Parse(address);
            return WriteRaw(addr, new byte[] {
                (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            });
        }

        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);

        public override OperateResult Write(string address, float value)
        {
            int bits;
            unsafe { bits = *(int*)&value; }
            return Write(address, bits);
        }

        public override OperateResult Write(string address, double value)
        {
            return Write(address, BitConverter.DoubleToInt64Bits(value));
        }

        public override OperateResult Write(string address, string value)
        {
            var addr = _parser.Parse(address);
            byte[] strData = System.Text.Encoding.ASCII.GetBytes(value);
            return WriteRaw(addr, strData);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _parser.Parse(address);
            return WriteRaw(addr, data);
        }

        // ── Async ──────────────────────

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
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ── IBatchReadWrite ──────────────────────

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList) { var r = ReadInt16(addr); if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList) { var r = ReadBytes(addr, 1); if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b), short s => Write(kv.Key, s), ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i), uint ui => Write(kv.Key, ui), float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s), byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));

        public new void Disconnect()
        {
            _connected = false;
            base.Disconnect();
        }
    }
}
