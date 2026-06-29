using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Hart
{
    /// <summary>
    /// HART 协议客户端 — 通过串口传输 HART 格式报文。
    /// <para>支持命令: Cmd0(读唯一ID), Cmd1(读PV), Cmd2(读电流), Cmd3(读PV和电流)</para>
    /// <para>地址格式: 短地址(0-15) 或 长地址(0x开头)</para>
    /// </summary>
    public class HartClient : SerialDeviceBase, IBatchReadWrite
    {
        private readonly HartAddressParser _parser = new HartAddressParser();

        public HartClient(ISerialPort port, int timeout = 5000)
            : base(port, timeout) { }

        protected override int ResponseHeaderLength => 5;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 5) return 0;
            return header[4];
        }

        private byte[] BuildFrame(byte command, HartAddress addr, byte[]? data = null)
        {
            int dataLen = data?.Length ?? 0;
            int addrLen = addr.UseShortAddress ? 1 : 5;
            int frameLen = 3 + addrLen + 1 + dataLen + 1;
            byte[] frame = new byte[frameLen];
            int offset = 0;
            frame[offset++] = 0xFF; frame[offset++] = 0xFF;
            if (addr.UseShortAddress)
            {
                frame[offset++] = (byte)(0x80 | (addr.ShortAddress & 0x0F));
            }
            else
            {
                frame[offset++] = 0x80;
                frame[offset++] = (byte)(addr.LongAddress >> 32);
                frame[offset++] = (byte)(addr.LongAddress >> 24);
                frame[offset++] = (byte)(addr.LongAddress >> 16);
                frame[offset++] = (byte)(addr.LongAddress >> 8);
                frame[offset++] = (byte)(addr.LongAddress & 0xFF);
            }
            frame[offset++] = command;
            frame[offset++] = (byte)dataLen;
            if (dataLen > 0) { Buffer.BlockCopy(data, 0, frame, offset, dataLen); offset += dataLen; }
            byte checksum = 0;
            for (int i = 2; i < offset; i++) checksum ^= frame[i];
            frame[offset] = checksum;
            return frame;
        }

        private OperateResult<byte[]> SendHart(byte command, HartAddress addr, byte[]? data = null)
        {
            byte[] frame = BuildFrame(command, addr, data);
            var result = base.SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            byte[] response = result.Content;
            if (response.Length < 5) return OperateResult<byte[]>.Failed("HART 响应过短");
            int dataLen = response[response.Length - 2];
            byte[] respData = new byte[dataLen];
            if (dataLen > 0 && response.Length >= 5 + dataLen)
                Buffer.BlockCopy(response, response.Length - 1 - dataLen, respData, 0, dataLen);
            return OperateResult<byte[]>.Success(respData);
        }

        // ── Read implementations ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<bool>.Success(r.Content != 0) : OperateResult<bool>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendHart(0x03, addr);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足");
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
            var r = SendHart(0x03, addr);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据不足");
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
            var r = SendHart(0x03, addr);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("数据不足");
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
            var r = SendHart(0x03, addr);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足");
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
            var r = SendHart(0x03, addr);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            var r = SendHart(0x03, addr);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(r.Content);
        }

        // ── Write implementations ──────────────────
        // HART 写入通过 Cmd6(写轮询地址) 实现

        public override OperateResult Write(string address, short value)
        {
            var addr = _parser.Parse(address);
            var r = SendHart(0x06, addr, new byte[] { (byte)(value >> 8), (byte)value });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, (short)value);
        public override OperateResult Write(string address, uint value) => Write(address, (short)value);
        public override OperateResult Write(string address, long value) => Write(address, (short)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (short)value);
        public override OperateResult Write(string address, float value) => Write(address, (short)value);
        public override OperateResult Write(string address, double value) => Write(address, (short)value);
        public override OperateResult Write(string address, bool value) => Write(address, (short)(value ? 1 : 0));
        public override OperateResult Write(string address, string value) => Write(address, short.Parse(value));
        public override OperateResult Write(string address, byte[] data) => Write(address, (short)(data.Length > 0 ? data[0] : 0));

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
