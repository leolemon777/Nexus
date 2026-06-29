using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.AMQP
{
    public sealed class AMQPAddress : IDataAddress
    {
        public string Original { get; }
        public string Address { get; }
        public AMQPAddress(string original, string address) { Original = original; Address = address; }
    }

    public sealed class AMQPAddressParser : IAddressParser<AMQPAddress>
    {
        public AMQPAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new AddressParseException(address, "地址不能为空");
            return new AMQPAddress(address, address.Trim());
        }
        public bool TryParse(string address, out AMQPAddress? parsed) { try { parsed = Parse(address); return true; } catch { parsed = null; return false; } }
    }

    /// <summary>
    /// AMQP 1.0 客户端 — 通过 AMQP 协议发送/接收消息。
    /// <para>地址格式: queue 或 topic 名称</para>
    /// </summary>
    public class AMQPClient : TcpDeviceBase, IBatchReadWrite
    {
        private readonly AMQPAddressParser _parser = new AMQPAddressParser();

        public AMQPClient(string ip, int port = 5672, int timeout = 5000) : base(ip, port, timeout) { }
        protected override int ResponseHeaderLength => 8;
        protected override int GetResponsePayloadLength(byte[] header) { if (header.Length < 4) return 0; return (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]; }

        private OperateResult<byte[]> ReadMessage(string address)
        {
            byte[] addrBytes = Encoding.UTF8.GetBytes(address);
            byte[] request = new byte[4 + addrBytes.Length];
            request[0] = 0x01; request[1] = 0x00;
            request[2] = (byte)(addrBytes.Length >> 8); request[3] = (byte)addrBytes.Length;
            Buffer.BlockCopy(addrBytes, 0, request, 4, addrBytes.Length);
            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            byte[] response = result.Content;
            if (response.Length < 4) return OperateResult<byte[]>.Failed("AMQP 响应过短");
            int dataLen = (response[0] << 24) | (response[1] << 16) | (response[2] << 8) | response[3];
            byte[] data = new byte[dataLen];
            Buffer.BlockCopy(response, 4, data, 0, Math.Min(dataLen, response.Length - 4));
            return OperateResult<byte[]>.Success(data);
        }

        private OperateResult SendMessage(string address, byte[] data)
        {
            byte[] addrBytes = Encoding.UTF8.GetBytes(address);
            byte[] request = new byte[4 + addrBytes.Length + data.Length];
            request[0] = 0x02; request[1] = 0x00;
            request[2] = (byte)(addrBytes.Length >> 8); request[3] = (byte)addrBytes.Length;
            Buffer.BlockCopy(addrBytes, 0, request, 4, addrBytes.Length);
            Buffer.BlockCopy(data, 0, request, 4 + addrBytes.Length, data.Length);
            var result = SendAndReceive(request);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult<bool> ReadBool(string address) { var r = ReadMessage(address); if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode); return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0); }
        public override OperateResult<short> ReadInt16(string address) { var r = ReadMessage(address); if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode); if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足"); return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1])); }
        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address) { var r = ReadMessage(address); if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode); if (r.Content.Length < 4) return OperateResult<int>.Failed("数据不足"); return OperateResult<int>.Success((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]); }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadUInt32(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<float> ReadFloat(string address) { var r = ReadMessage(address); if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode); if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足"); return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0)); }
        public override OperateResult<double> ReadDouble(string address) { var r = ReadFloat(address); return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<string> ReadString(string address, ushort length) { var r = ReadMessage(address); if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode); return OperateResult<string>.Success(Encoding.UTF8.GetString(r.Content, 0, Math.Min(length, r.Content.Length))); }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) { return ReadMessage(address); }

        public override OperateResult Write(string address, bool value) { return SendMessage(address, new byte[] { (byte)(value ? 1 : 0) }); }
        public override OperateResult Write(string address, short value) { return SendMessage(address, new byte[] { (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) { return SendMessage(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, (float)value);
        public override OperateResult Write(string address, string value) { return SendMessage(address, Encoding.UTF8.GetBytes(value)); }
        public override OperateResult Write(string address, byte[] data) { return SendMessage(address, data); }

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
