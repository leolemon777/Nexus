using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.ProfibusDP
{
    /// <summary>
    /// Profibus DP 客户端 — 通过串口/DP 网关访问 Profibus DP 从站。
    /// <para>支持 I/O 数据读写和诊断。</para>
    /// <para>地址格式: slave[:slot]:offset</para>
    /// <para>注意: 本客户端通过 DP 网关设备访问 Profibus 网络。</para>
    /// </summary>
    public class ProfibusDpClient : SerialDeviceBase, IBatchReadWrite
    {
        public byte MasterAddress { get; set; } = 1;
        private readonly ProfibusDpAddressParser _parser = new ProfibusDpAddressParser();

        public ProfibusDpClient(ISerialPort port, byte masterAddress = 1, int timeout = 5000) : base(port, timeout) { MasterAddress = masterAddress; }

        protected override int ResponseHeaderLength => 6;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 6) return 0;
            return header[4] | (header[5] << 8);
        }

        // ── 数据读写 ──────────────────
        private OperateResult<byte[]> ReadIoData(byte slave, ushort slot, ushort offset, ushort length)
        {
            // FDL frame: SA(1) + DA(1) + FC(1) + PDU(N)
            byte[] request = new byte[8];
            request[0] = MasterAddress;
            request[1] = slave;
            request[2] = 0x0C; // FC: Read Request
            request[3] = 0x00; // SAP: Default
            request[4] = (byte)(slot & 0xFF);
            request[5] = (byte)(slot >> 8);
            request[6] = (byte)(offset & 0xFF);
            request[7] = (byte)length;

            var result = base.SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] response = result.Content;
            if (response.Length < 6) return OperateResult<byte[]>.Failed("Profibus DP 响应过短");

            byte status = response[2];
            if ((status & 0x80) != 0) return OperateResult<byte[]>.Failed($"Profibus DP 错误: 0x{status:X2}");

            byte[] data = new byte[response.Length - 6];
            Buffer.BlockCopy(response, 6, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        private OperateResult WriteIoData(byte slave, ushort slot, ushort offset, byte[] data)
        {
            byte[] request = new byte[6 + data.Length];
            request[0] = MasterAddress;
            request[1] = slave;
            request[2] = 0x0D; // FC: Write Request
            request[3] = 0x00;
            request[4] = (byte)(offset & 0xFF);
            request[5] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, request, 6, data.Length);

            var result = base.SendAndReceive(request);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        // ── IReadWriteDevice ──────────────────
        private ProfibusDpAddress ParseAddr(string address) => _parser.Parse(address);

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadIoData(addr.SlaveAddress, addr.Slot, addr.Offset, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Length > 0 && (r.Content[0] & 0x01) != 0);
        }
        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadIoData(addr.SlaveAddress, addr.Slot, addr.Offset, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }
        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadIoData(addr.SlaveAddress, addr.Slot, addr.Offset, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据不足");
            return OperateResult<int>.Success((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadUInt32(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadIoData(addr.SlaveAddress, addr.Slot, addr.Offset, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }
        public override OperateResult<double> ReadDouble(string address) { var r = ReadFloat(address); return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = ParseAddr(address);
            var r = ReadIoData(addr.SlaveAddress, addr.Slot, addr.Offset, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = ParseAddr(address);
            return ReadIoData(addr.SlaveAddress, addr.Slot, addr.Offset, length);
        }

        public override OperateResult Write(string address, bool value) { var addr = ParseAddr(address); return WriteIoData(addr.SlaveAddress, addr.Slot, addr.Offset, new byte[] { (byte)(value ? 1 : 0) }); }
        public override OperateResult Write(string address, short value) { var addr = ParseAddr(address); return WriteIoData(addr.SlaveAddress, addr.Slot, addr.Offset, new byte[] { (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) { var addr = ParseAddr(address); return WriteIoData(addr.SlaveAddress, addr.Slot, addr.Offset, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, (float)value);
        public override OperateResult Write(string address, string value) { var addr = ParseAddr(address); return WriteIoData(addr.SlaveAddress, addr.Slot, addr.Offset, System.Text.Encoding.ASCII.GetBytes(value)); }
        public override OperateResult Write(string address, byte[] data) { var addr = ParseAddr(address); return WriteIoData(addr.SlaveAddress, addr.Slot, addr.Offset, data); }

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
