using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.EtherNetIp
{
    /// <summary>
    /// EtherNet/IP 客户端 — CIP 显式消息 over TCP。
    /// <para>支持标签读写（Read Tag / Write Tag）。</para>
    /// <para>地址格式: TagName 或 TagName[index]</para>
    /// </summary>
    public class EtherNetIpClient : TcpDeviceBase, IBatchReadWrite
    {
        private uint _sessionHandle;
        private readonly EtherNetIpAddressParser _parser = new EtherNetIpAddressParser();

        public EtherNetIpClient(string ip, int port = 44818, int timeout = 5000)
            : base(ip, port, timeout) { }

        protected override int ResponseHeaderLength => 28;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            return (header[2] << 0) | (header[3] << 8);
        }

        public override OperateResult Connect()
        {
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;
            var sessionResult = RegisterSession();
            if (!sessionResult.IsSuccess) { Disconnect(); return sessionResult; }
            return OperateResult.Success();
        }

        private OperateResult RegisterSession()
        {
            byte[] register = {
                0x65, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00
            };
            var result = SendAndReceive(register);
            if (!result.IsSuccess) return OperateResult.Failed($"RegisterSession 失败: {result.Message}");
            if (result.Content.Length >= 8)
                _sessionHandle = (uint)(result.Content[4] | (result.Content[5] << 8) | (result.Content[6] << 16) | (result.Content[7] << 24));
            return OperateResult.Success();
        }

        private OperateResult<byte[]> SendCipService(byte service, string tagName, byte[]? data = null)
        {
            byte[] tagBytes = System.Text.Encoding.ASCII.GetBytes(tagName);
            int cipLen = 4 + tagBytes.Length + 2 + (data?.Length ?? 0);
            byte[] cipPath = new byte[cipLen];
            cipPath[0] = service;
            cipPath[1] = (byte)(tagBytes.Length / 2);
            Buffer.BlockCopy(tagBytes, 0, cipPath, 2, tagBytes.Length);
            int offset = 2 + tagBytes.Length;
            if (tagBytes.Length % 2 != 0) { cipPath[offset] = 0; offset++; }
            cipPath[offset] = 0x01; cipPath[offset + 1] = 0x00;
            if (data != null) Buffer.BlockCopy(data, 0, cipPath, offset + 2, data.Length);

            byte[] enipHeader = new byte[24 + cipLen];
            enipHeader[0] = 0x6F; enipHeader[1] = 0x00;
            enipHeader[2] = (byte)((cipLen + 24) & 0xFF); enipHeader[3] = (byte)((cipLen + 24) >> 8);
            enipHeader[4] = (byte)(_sessionHandle & 0xFF); enipHeader[5] = (byte)((_sessionHandle >> 8) & 0xFF);
            enipHeader[6] = (byte)((_sessionHandle >> 16) & 0xFF); enipHeader[7] = (byte)((_sessionHandle >> 24) & 0xFF);
            enipHeader[16] = 0x01; enipHeader[17] = 0x00;
            enipHeader[20] = 0xB1; enipHeader[21] = 0x00;
            enipHeader[22] = (byte)(cipLen & 0xFF); enipHeader[23] = (byte)(cipLen >> 8);
            Buffer.BlockCopy(cipPath, 0, enipHeader, 24, cipLen);

            var result = SendAndReceive(enipHeader);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

            byte[] response = result.Content;
            if (response.Length < 42) return OperateResult<byte[]>.Failed("EtherNet/IP 响应过短");

            ushort status = (ushort)(response[8] | (response[9] << 8));
            if (status != 0) return OperateResult<byte[]>.Failed($"EtherNet/IP 状态错误: 0x{status:X4}");

            byte[] pdu = new byte[response.Length - 42];
            Buffer.BlockCopy(response, 42, pdu, 0, pdu.Length);
            return OperateResult<byte[]>.Success(pdu);
        }

        // ── Read implementations ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4C, addr.TagName);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4C, addr.TagName);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足");
            return OperateResult<short>.Success((short)(r.Content[0] | (r.Content[1] << 8)));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4C, addr.TagName);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据不足");
            return OperateResult<int>.Success(r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4C, addr.TagName);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("数据不足");
            return OperateResult<long>.Success(
                (long)r.Content[0] | ((long)r.Content[1] << 8) | ((long)r.Content[2] << 16) | ((long)r.Content[3] << 24) |
                ((long)r.Content[4] << 32) | ((long)r.Content[5] << 40) | ((long)r.Content[6] << 48) | ((long)r.Content[7] << 56));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4C, addr.TagName);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足");
            int bits = r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24);
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
            var r = SendCipService(0x4C, addr.TagName);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4C, addr.TagName);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(r.Content);
        }

        // ── Write implementations ──────────────────

        public override OperateResult Write(string address, bool value)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4D, addr.TagName, new byte[] { 0xC1, 0x00, (byte)(value ? 1 : 0) });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4D, addr.TagName, new byte[] { 0xC3, 0x00, (byte)value, (byte)(value >> 8) });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4D, addr.TagName, new byte[] { 0xC4, 0x00, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = _parser.Parse(address);
            var r = SendCipService(0x4D, addr.TagName, new byte[] {
                0xC5, 0x00, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
                (byte)(value >> 32), (byte)(value >> 40), (byte)(value >> 48), (byte)(value >> 56)
            });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);

        public override OperateResult Write(string address, float value)
        {
            int bits;
            unsafe { bits = *(int*)&value; }
            return Write(address, bits);
        }

        public override OperateResult Write(string address, double value) => Write(address, BitConverter.DoubleToInt64Bits(value));

        public override OperateResult Write(string address, string value)
        {
            var addr = _parser.Parse(address);
            byte[] strData = System.Text.Encoding.ASCII.GetBytes(value);
            byte[] data = new byte[2 + strData.Length];
            data[0] = 0xD2; data[1] = 0x00;
            Buffer.BlockCopy(strData, 0, data, 2, strData.Length);
            var r = SendCipService(0x4D, addr.TagName, data);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _parser.Parse(address);
            byte[] payload = new byte[2 + data.Length];
            payload[0] = 0xD3; payload[1] = 0x00;
            Buffer.BlockCopy(data, 0, payload, 2, data.Length);
            var r = SendCipService(0x4D, addr.TagName, payload);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
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
    }
}
