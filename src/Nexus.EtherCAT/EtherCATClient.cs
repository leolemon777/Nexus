using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.EtherCAT
{
    /// <summary>
    /// EtherCAT 非实时客户端 — 通过 UDP/TCP 进行配置、诊断和参数读写。
    /// <para>支持 SDO (Service Data Object) 读写，用于 EtherCAT 从站配置。</para>
    /// <para>地址格式: slave.index.subindex (十进制或0x十六进制)</para>
    /// <para>注意: 实时过程数据交换需要内核驱动，本客户端仅支持非实时配置/诊断。</para>
    /// </summary>
    public class EtherCATClient : TcpDeviceBase, IBatchReadWrite
    {
        private readonly EtherCATAddressParser _parser = new EtherCATAddressParser();

        public EtherCATClient(string ip, int port = 34980, int timeout = 5000) : base(ip, port, timeout) { }

        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            return (header[2] << 8) | header[3];
        }

        // ── SDO 读写 ──────────────────
        private OperateResult<byte[]> ReadSdo(ushort slave, ushort index, byte subIndex, uint size)
        {
            // EtherCAT UDP command: SDO Read
            byte[] request = new byte[12];
            request[0] = 0x01; // Command: SDO Read
            request[1] = 0x00;
            request[2] = (byte)(slave >> 8); request[3] = (byte)(slave & 0xFF);
            request[4] = (byte)(index >> 8); request[5] = (byte)(index & 0xFF);
            request[6] = subIndex;
            request[7] = 0x00;
            request[8] = (byte)(size >> 24); request[9] = (byte)(size >> 16);
            request[10] = (byte)(size >> 8); request[11] = (byte)(size & 0xFF);

            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] response = result.Content;
            if (response.Length < 8) return OperateResult<byte[]>.Failed("EtherCAT 响应过短");

            uint error = (uint)((response[4] << 24) | (response[5] << 16) | (response[6] << 8) | response[7]);
            if (error != 0) return OperateResult<byte[]>.Failed($"EtherCAT SDO 错误: 0x{error:X8}");

            byte[] data = new byte[response.Length - 8];
            Buffer.BlockCopy(response, 8, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        private OperateResult WriteSdo(ushort slave, ushort index, byte subIndex, byte[] data)
        {
            byte[] request = new byte[12 + data.Length];
            request[0] = 0x02; // Command: SDO Write
            request[1] = 0x00;
            request[2] = (byte)(slave >> 8); request[3] = (byte)(slave & 0xFF);
            request[4] = (byte)(index >> 8); request[5] = (byte)(index & 0xFF);
            request[6] = subIndex;
            request[7] = 0x00;
            request[8] = (byte)(data.Length >> 24); request[9] = (byte)(data.Length >> 16);
            request[10] = (byte)(data.Length >> 8); request[11] = (byte)(data.Length & 0xFF);
            Buffer.BlockCopy(data, 0, request, 12, data.Length);

            var result = SendAndReceive(request);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        // ── IReadWriteDevice ──────────────────
        private EtherCATAddress ParseAddr(string address) => _parser.Parse(address);

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
        }
        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足");
            return OperateResult<short>.Success((short)(r.Content[0] | (r.Content[1] << 8)));
        }
        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据不足");
            return OperateResult<int>.Success(r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24));
        }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadUInt32(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }
        public override OperateResult<double> ReadDouble(string address) { var r = ReadFloat(address); return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = ParseAddr(address);
            var r = ReadSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = ParseAddr(address);
            return ReadSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, length);
        }

        public override OperateResult Write(string address, bool value) { var addr = ParseAddr(address); return WriteSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, new byte[] { (byte)(value ? 1 : 0) }); }
        public override OperateResult Write(string address, short value) { var addr = ParseAddr(address); return WriteSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, new byte[] { (byte)value, (byte)(value >> 8) }); }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) { var addr = ParseAddr(address); return WriteSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) }); }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, (float)value);
        public override OperateResult Write(string address, string value) { var addr = ParseAddr(address); return WriteSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, System.Text.Encoding.ASCII.GetBytes(value)); }
        public override OperateResult Write(string address, byte[] data) { var addr = ParseAddr(address); return WriteSdo(addr.SlaveAddress, addr.Index, addr.SubIndex, data); }

        // ── Async ──────────────────
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

        // ── IBatchReadWrite ──────────────────
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList(); if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>(); foreach (var addr in addrList) { var r = ReadInt16(addr); if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; } return OperateResult<Dictionary<string, object?>>.Success(result);
        }
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList(); if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>(); foreach (var addr in addrList) { var r = ReadBytes(addr, 1); if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; } return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items) { OperateResult r = kv.Value switch { bool b => Write(kv.Key, b), short s => Write(kv.Key, s), ushort us => Write(kv.Key, us), int i => Write(kv.Key, i), uint ui => Write(kv.Key, ui), float f => Write(kv.Key, f), string s => Write(kv.Key, s), byte[] b => Write(kv.Key, b), _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}") }; if (!r.IsSuccess) return r; } return OperateResult.Success();
        }
        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));
    }
}
