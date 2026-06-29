using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.FF_H1
{
    /// <summary>
    /// Foundation Fieldbus H1 客户端 — 过程控制现场总线。
    /// <para>地址格式: [deviceAddress:]blockTag.parameterName</para>
    /// </summary>
    public class FF_H1Client : SerialDeviceBase, IBatchReadWrite
    {
        public byte LinkAddress { get; set; } = 1;
        private readonly FF_H1AddressParser _parser = new FF_H1AddressParser();

        public FF_H1Client(ISerialPort port, byte linkAddress = 1, int timeout = 5000) : base(port, timeout) { LinkAddress = linkAddress; }
        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header) { if (header.Length < 4) return 0; return (header[2] << 8) | header[3]; }

        private OperateResult<byte[]> ReadParam(ushort deviceAddress, string blockTag, string paramName)
        {
            byte[] tagBytes = System.Text.Encoding.ASCII.GetBytes(blockTag);
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(paramName);
            byte[] request = new byte[6 + tagBytes.Length + nameBytes.Length];
            request[0] = LinkAddress; request[1] = (byte)deviceAddress;
            request[2] = 0x01; request[3] = (byte)tagBytes.Length;
            Buffer.BlockCopy(tagBytes, 0, request, 4, tagBytes.Length);
            request[4 + tagBytes.Length] = (byte)nameBytes.Length;
            Buffer.BlockCopy(nameBytes, 0, request, 5 + tagBytes.Length, nameBytes.Length);
            var result = base.SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            byte[] response = result.Content;
            if (response.Length < 6) return OperateResult<byte[]>.Failed("FF H1 响应过短");
            byte[] data = new byte[response.Length - 6];
            Buffer.BlockCopy(response, 6, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        private OperateResult WriteParam(ushort deviceAddress, string blockTag, string paramName, byte[] data)
        {
            byte[] tagBytes = System.Text.Encoding.ASCII.GetBytes(blockTag);
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(paramName);
            byte[] request = new byte[6 + tagBytes.Length + nameBytes.Length + data.Length];
            request[0] = LinkAddress; request[1] = (byte)deviceAddress;
            request[2] = 0x02; request[3] = (byte)tagBytes.Length;
            Buffer.BlockCopy(tagBytes, 0, request, 4, tagBytes.Length);
            request[4 + tagBytes.Length] = (byte)nameBytes.Length;
            Buffer.BlockCopy(nameBytes, 0, request, 5 + tagBytes.Length, nameBytes.Length);
            Buffer.BlockCopy(data, 0, request, 5 + tagBytes.Length + nameBytes.Length, data.Length);
            var result = base.SendAndReceive(request);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        private FF_H1Address ParseAddr(string address) => _parser.Parse(address);

        public override OperateResult<bool> ReadBool(string address) { var addr = ParseAddr(address); var r = ReadParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName); if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode); return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0); }
        public override OperateResult<short> ReadInt16(string address) { var addr = ParseAddr(address); var r = ReadParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName); if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode); if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足"); return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1])); }
        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address) { var addr = ParseAddr(address); var r = ReadParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName); if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode); if (r.Content.Length < 4) return OperateResult<int>.Failed("数据不足"); return OperateResult<int>.Success((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]); }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadUInt32(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<float> ReadFloat(string address) { var addr = ParseAddr(address); var r = ReadParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName); if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode); if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足"); return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0)); }
        public override OperateResult<double> ReadDouble(string address) { var r = ReadFloat(address); return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<string> ReadString(string address, ushort length) { var addr = ParseAddr(address); var r = ReadParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName); if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode); return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0')); }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) { var addr = ParseAddr(address); return ReadParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName); }

        public override OperateResult Write(string address, bool value) { var addr = ParseAddr(address); return WriteParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName, new byte[] { (byte)(value ? 1 : 0) }); }
        public override OperateResult Write(string address, short value) { var addr = ParseAddr(address); return WriteParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName, new byte[] { (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) { var addr = ParseAddr(address); return WriteParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, (float)value);
        public override OperateResult Write(string address, string value) { var addr = ParseAddr(address); return WriteParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName, System.Text.Encoding.ASCII.GetBytes(value)); }
        public override OperateResult Write(string address, byte[] data) { var addr = ParseAddr(address); return WriteParam(addr.DeviceAddress, addr.BlockTag, addr.ParameterName, data); }

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
