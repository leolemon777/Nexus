using System;
using Nexus;
using Nexus.Siemens.MPI;

namespace Nexus.Siemens.MpiOverTcp
{
    /// <summary>
    /// Siemens MPI Over TCP 客户端 — 通过 TCP 网关访问 MPI 协议。
    /// <para>与 SiemensMpiClient 相同功能，但通过 TCP 传输（MPI 转 TCP 网关设备）。</para>
    /// <para>地址格式: I0.0, Q0.0, M0.0, DB1.DBX0.0, T0, C0, V0</para>
    /// </summary>
    public class SiemensMpiOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        public byte LocalAddress { get; set; } = 0;
        public byte RemoteAddress { get; set; } = 2;
        public ushort MaxPduSize { get; set; } = 480;

        private readonly MpiAddressParser _parser = new MpiAddressParser();
        private bool _connected;

        public SiemensMpiOverTcpClient(string ip, int port = 102, int timeout = 5000)
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
            _connected = true;
            return OperateResult.Success();
        }

        private OperateResult<byte[]> ReadRaw(MpiAddress addr, ushort byteCount)
        {
            if (!_connected) return OperateResult<byte[]>.Failed("未连接到 PLC");
            byte area = addr.Area switch
            {
                MpiArea.I => 0x81, MpiArea.Q => 0x82, MpiArea.M => 0x83,
                MpiArea.DB => 0x84, MpiArea.T => 0x1D, MpiArea.C => 0x1C,
                MpiArea.V => 0x84, _ => 0x84
            };
            ushort db = addr.Area == MpiArea.DB ? addr.DbNumber : (ushort)0;
            byte[] readReq = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x01, 0x12,
                0x0A, 0x10, 0x02,
                (byte)(byteCount >> 8), (byte)(byteCount & 0xFF),
                (byte)(db >> 8), (byte)(db & 0xFF),
                area,
                (byte)(addr.StartByte >> 5), (byte)((addr.StartByte << 3) | addr.BitOffset)
            };
            var result = SendAndReceive(readReq);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);
            byte[] response = result.Content;
            if (response.Length < 18) return OperateResult<byte[]>.Failed("S7 响应过短");
            ushort error = (ushort)((response[10] << 8) | response[11]);
            if (error != 0) return OperateResult<byte[]>.Failed($"S7 错误: 0x{error:X4}");
            int dataOffset = response.Length - byteCount;
            if (dataOffset < 0) return OperateResult<byte[]>.Failed("S7 数据不足");
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(response, dataOffset, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        private OperateResult WriteRaw(MpiAddress addr, byte[] data)
        {
            if (!_connected) return OperateResult.Failed("未连接到 PLC");
            byte area = addr.Area switch
            {
                MpiArea.I => 0x81, MpiArea.Q => 0x82, MpiArea.M => 0x83,
                MpiArea.DB => 0x84, MpiArea.T => 0x1D, MpiArea.C => 0x1C,
                MpiArea.V => 0x84, _ => 0x84
            };
            ushort db = addr.Area == MpiArea.DB ? addr.DbNumber : (ushort)0;
            byte[] writeReq = new byte[24 + data.Length];
            writeReq[0] = 0x00; writeReq[1] = 0x01;
            writeReq[6] = 0x00; writeReq[7] = (byte)(8 + data.Length);
            writeReq[12] = 0x00; writeReq[13] = 0x04;
            writeReq[14] = 0x01; writeReq[15] = 0x12;
            writeReq[16] = 0x0A; writeReq[17] = 0x10;
            writeReq[18] = 0x02;
            writeReq[19] = (byte)(data.Length >> 8); writeReq[20] = (byte)(data.Length & 0xFF);
            writeReq[21] = (byte)(db >> 8); writeReq[22] = (byte)(db & 0xFF);
            writeReq[23] = area;
            writeReq[24] = (byte)(addr.StartByte >> 5); writeReq[25] = (byte)((addr.StartByte << 3) | addr.BitOffset);
            Buffer.BlockCopy(data, 0, writeReq, 24, data.Length);
            var result = SendAndReceive(writeReq);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message);
        }

        public override OperateResult<bool> ReadBool(string address) { var addr = _parser.Parse(address); var r = ReadRaw(addr, 1); if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode); return OperateResult<bool>.Success((r.Content[0] & (1 << addr.BitOffset)) != 0); }
        public override OperateResult<short> ReadInt16(string address) { var addr = _parser.Parse(address); var r = ReadRaw(addr, 2); if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode); return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1])); }
        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address) { var addr = _parser.Parse(address); var r = ReadRaw(addr, 4); if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode); return OperateResult<int>.Success((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]); }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address) { var addr = _parser.Parse(address); var r = ReadRaw(addr, 8); if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode); return OperateResult<long>.Success(((long)r.Content[0] << 56) | ((long)r.Content[1] << 48) | ((long)r.Content[2] << 40) | ((long)r.Content[3] << 32) | ((long)r.Content[4] << 24) | ((long)r.Content[5] << 16) | ((long)r.Content[6] << 8) | r.Content[7]); }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<float> ReadFloat(string address) { var addr = _parser.Parse(address); var r = ReadRaw(addr, 4); if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode); return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0)); }
        public override OperateResult<double> ReadDouble(string address) { var r = ReadInt64(address); if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode); return OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0)); }
        public override OperateResult<string> ReadString(string address, ushort length) { var addr = _parser.Parse(address); var r = ReadRaw(addr, length); if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode); return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0')); }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) { var addr = _parser.Parse(address); return ReadRaw(addr, length); }

        public override OperateResult Write(string address, bool value) { var addr = _parser.Parse(address); var current = ReadRaw(addr, 1); if (!current.IsSuccess) return OperateResult.Failed(current.Message); byte b = current.Content[0]; if (value) b |= (byte)(1 << addr.BitOffset); else b &= (byte)~(1 << addr.BitOffset); return WriteRaw(addr, new byte[] { b }); }
        public override OperateResult Write(string address, short value) { var addr = _parser.Parse(address); return WriteRaw(addr, new byte[] { (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) { var addr = _parser.Parse(address); return WriteRaw(addr, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) { var addr = _parser.Parse(address); return WriteRaw(addr, new byte[] { (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.DoubleToInt64Bits(value));
        public override OperateResult Write(string address, string value) { var addr = _parser.Parse(address); return WriteRaw(addr, System.Text.Encoding.ASCII.GetBytes(value)); }
        public override OperateResult Write(string address, byte[] data) { var addr = _parser.Parse(address); return WriteRaw(addr, data); }

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

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses) { var addrList = addresses.ToList(); if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空"); var result = new Dictionary<string, object?>(); foreach (var addr in addrList) { var r = ReadInt16(addr); if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; } return OperateResult<Dictionary<string, object?>>.Success(result); }
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses) { var addrList = addresses.ToList(); if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空"); var result = new Dictionary<string, byte[]>(); foreach (var addr in addrList) { var r = ReadBytes(addr, 1); if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; } return OperateResult<Dictionary<string, byte[]>>.Success(result); }
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items) { foreach (var kv in items) { OperateResult r = kv.Value switch { bool b => Write(kv.Key, b), short s => Write(kv.Key, s), ushort us => Write(kv.Key, us), int i => Write(kv.Key, i), uint ui => Write(kv.Key, ui), float f => Write(kv.Key, f), string s => Write(kv.Key, s), byte[] b => Write(kv.Key, b), _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}") }; if (!r.IsSuccess) return r; } return OperateResult.Success(); }
        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));
    }
}
