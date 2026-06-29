using System;
using Nexus;

namespace Nexus.Omron.HostLinkUdp
{
    public class OmronHostLinkUdpClient : UdpDeviceBase, IBatchReadWrite
    {
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        private readonly Omron.NxNj.OmronNxNjAddressParser _parser = new Omron.NxNj.OmronNxNjAddressParser();

        public OmronHostLinkUdpClient(string ip, int port = 9600, int timeout = 5000) : base(ip, port, timeout) { }
        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header) { if (header.Length < 4) return 0; return (header[2] << 8) | header[3]; }

        private byte FinsAreaCode(string areaCode) => areaCode switch
        {
            "D" => 0x82, "W" => 0xB1, "H" => 0xB2, "CIO" => 0x30,
            "A" => 0xB3, "E" => 0x20, "I" => 0xDC, _ => 0x82
        };

        private OperateResult<byte[]> ReadWords(string areaCode, ushort startWord, ushort count)
        {
            byte[] data = new byte[] { FinsAreaCode(areaCode), (byte)(startWord >> 8), (byte)startWord, (byte)(count >> 8), (byte)count };
            byte[] finsFrame = new byte[12 + data.Length];
            finsFrame[0] = 0x80; finsFrame[1] = 0x00;
            finsFrame[2] = 0x02; finsFrame[3] = 0x00;
            finsFrame[4] = 0x00; finsFrame[5] = 0x00;
            finsFrame[6] = 0x00; finsFrame[7] = 0x00;
            finsFrame[8] = 0x00; finsFrame[9] = 0x00;
            finsFrame[10] = 0x01; finsFrame[11] = 0x01;
            Buffer.BlockCopy(data, 0, finsFrame, 12, data.Length);
            var result = SendAndReceive(finsFrame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);
            byte[] response = result.Content;
            if (response.Length < 16) return OperateResult<byte[]>.Failed("HostLink UDP 响应过短");
            ushort error = (ushort)((response[12] << 8) | response[13]);
            if (error != 0) return OperateResult<byte[]>.Failed($"HostLink UDP 错误: 0x{error:X4}");
            byte[] resultData = new byte[count * 2];
            Buffer.BlockCopy(response, 14, resultData, 0, Math.Min(resultData.Length, response.Length - 14));
            return OperateResult<byte[]>.Success(resultData);
        }

        private OperateResult WriteWords(string areaCode, ushort startWord, byte[] wordData)
        {
            ushort count = (ushort)(wordData.Length / 2);
            byte[] data = new byte[5 + wordData.Length];
            data[0] = FinsAreaCode(areaCode); data[1] = (byte)(startWord >> 8); data[2] = (byte)startWord;
            data[3] = (byte)(count >> 8); data[4] = (byte)count;
            Buffer.BlockCopy(wordData, 0, data, 5, wordData.Length);
            byte[] finsFrame = new byte[12 + data.Length];
            finsFrame[0] = 0x80; finsFrame[1] = 0x00;
            finsFrame[2] = 0x02; finsFrame[3] = 0x00;
            finsFrame[4] = 0x00; finsFrame[5] = 0x00;
            finsFrame[6] = 0x00; finsFrame[7] = 0x00;
            finsFrame[8] = 0x00; finsFrame[9] = 0x00;
            finsFrame[10] = 0x01; finsFrame[11] = 0x02;
            Buffer.BlockCopy(data, 0, finsFrame, 12, data.Length);
            var result = SendAndReceive(finsFrame);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message);
        }

        public override OperateResult<bool> ReadBool(string address) { var addr = _parser.Parse(address); var r = ReadWords(addr.AreaCode, addr.WordAddress, 1); if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode); return OperateResult<bool>.Success((r.Content[0] & (1 << addr.BitOffset)) != 0); }
        public override OperateResult<short> ReadInt16(string address) { var addr = _parser.Parse(address); var r = ReadWords(addr.AreaCode, addr.WordAddress, 1); if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode); return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1])); }
        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address) { var addr = _parser.Parse(address); var r = ReadWords(addr.AreaCode, addr.WordAddress, 2); if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode); return OperateResult<int>.Success((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]); }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadUInt32(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<float> ReadFloat(string address) { var addr = _parser.Parse(address); var r = ReadWords(addr.AreaCode, addr.WordAddress, 2); if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode); return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0)); }
        public override OperateResult<double> ReadDouble(string address) { var r = ReadFloat(address); return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<string> ReadString(string address, ushort length) { var addr = _parser.Parse(address); ushort regCount = (ushort)((length + 1) / 2); var r = ReadWords(addr.AreaCode, addr.WordAddress, regCount); if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode); return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0')); }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) { var addr = _parser.Parse(address); ushort regCount = (ushort)((length + 1) / 2); var r = ReadWords(addr.AreaCode, addr.WordAddress, regCount); if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode); byte[] data = new byte[length]; Buffer.BlockCopy(r.Content, 0, data, 0, Math.Min(length, r.Content.Length)); return OperateResult<byte[]>.Success(data); }

        public override OperateResult Write(string address, bool value) { var addr = _parser.Parse(address); var current = ReadWords(addr.AreaCode, addr.WordAddress, 1); if (!current.IsSuccess) return OperateResult.Failed(current.Message); byte b = current.Content[0]; if (value) b |= (byte)(1 << addr.BitOffset); else b &= (byte)~(1 << addr.BitOffset); return WriteWords(addr.AreaCode, addr.WordAddress, new byte[] { b, current.Content[1] }); }
        public override OperateResult Write(string address, short value) { var addr = _parser.Parse(address); return WriteWords(addr.AreaCode, addr.WordAddress, new byte[] { (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) { var addr = _parser.Parse(address); return WriteWords(addr.AreaCode, addr.WordAddress, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) { var addr = _parser.Parse(address); return WriteWords(addr.AreaCode, addr.WordAddress, new byte[] { (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.DoubleToInt64Bits(value));
        public override OperateResult Write(string address, string value) { var addr = _parser.Parse(address); byte[] strData = System.Text.Encoding.ASCII.GetBytes(value); if (strData.Length % 2 != 0) Array.Resize(ref strData, strData.Length + 1); return WriteWords(addr.AreaCode, addr.WordAddress, strData); }
        public override OperateResult Write(string address, byte[] data) { var addr = _parser.Parse(address); if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1); return WriteWords(addr.AreaCode, addr.WordAddress, data); }

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
