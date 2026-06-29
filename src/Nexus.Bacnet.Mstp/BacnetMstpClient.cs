using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Bacnet.Mstp
{
    /// <summary>
    /// BACnet MS/TP 客户端 — 通过串口传输 BACnet MS/TP 格式报文。
    /// <para>支持服务: ReadProperty, WriteProperty, ReadPropertyMultiple</para>
    /// <para>地址格式: network:device.objectType:instance.property</para>
    /// </summary>
    public class BacnetMstpClient : SerialDeviceBase, IBatchReadWrite
    {
        public byte SourceAddress { get; set; } = 0;

        private readonly BacnetMstpAddressParser _parser = new BacnetMstpAddressParser();

        public BacnetMstpClient(ISerialPort port, byte sourceAddress = 0, int timeout = 5000)
            : base(port, timeout)
        {
            SourceAddress = sourceAddress;
        }

        protected override int ResponseHeaderLength => 8;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 8) return 0;
            return header[6] | (header[7] << 8);
        }

        private byte[] BuildFrame(byte frameType, byte destination, byte[] data)
        {
            int dataLen = data?.Length ?? 0;
            byte[] frame = new byte[9 + dataLen + 1];
            frame[0] = 0x55; frame[1] = 0xFF;
            frame[2] = frameType;
            frame[3] = destination;
            frame[4] = SourceAddress;
            frame[5] = (byte)(dataLen >> 8);
            frame[6] = (byte)(dataLen & 0xFF);
            if (dataLen > 0) Buffer.BlockCopy(data, 0, frame, 8, dataLen);
            byte crc = 0;
            for (int i = 2; i < 8 + dataLen; i++) crc ^= frame[i];
            frame[8 + dataLen] = crc;
            return frame;
        }

        private OperateResult<byte[]> SendBacnet(byte destination, byte[] apdu)
        {
            byte[] frame = BuildFrame(0x03, destination, apdu);
            var result = base.SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            byte[] response = result.Content;
            if (response.Length < 9) return OperateResult<byte[]>.Failed("BACnet MS/TP 响应过短");
            int dataLen = response[5] << 8 | response[6];
            byte[] apduData = new byte[dataLen];
            Buffer.BlockCopy(response, 8, apduData, 0, dataLen);
            return OperateResult<byte[]>.Success(apduData);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendBacnet((byte)addr.DeviceId, BuildReadPropertyApdu(addr));
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendBacnet((byte)addr.DeviceId, BuildReadPropertyApdu(addr));
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
            var r = SendBacnet((byte)addr.DeviceId, BuildReadPropertyApdu(addr));
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
            var r = SendBacnet((byte)addr.DeviceId, BuildReadPropertyApdu(addr));
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
            var r = SendBacnet((byte)addr.DeviceId, BuildReadPropertyApdu(addr));
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
            var r = SendBacnet((byte)addr.DeviceId, BuildReadPropertyApdu(addr));
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            var r = SendBacnet((byte)addr.DeviceId, BuildReadPropertyApdu(addr));
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(r.Content);
        }

        private byte[] BuildReadPropertyApdu(BacnetMstpAddress addr)
        {
            return new byte[]
            {
                0x00, 0x00, 0x05,
                (byte)(addr.ObjectType >> 8), (byte)(addr.ObjectType & 0xFF),
                (byte)(addr.Instance >> 16), (byte)(addr.Instance >> 8), (byte)(addr.Instance & 0xFF),
                addr.PropertyId
            };
        }

        public override OperateResult Write(string address, short value) => WriteGeneric(address, new byte[] { (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => WriteGeneric(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => WriteGeneric(address, new byte[] { (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.DoubleToInt64Bits(value));
        public override OperateResult Write(string address, string value) => WriteGeneric(address, System.Text.Encoding.ASCII.GetBytes(value));
        public override OperateResult Write(string address, byte[] data) => WriteGeneric(address, data);
        public override OperateResult Write(string address, bool value) => WriteGeneric(address, new byte[] { (byte)(value ? 1 : 0) });

        private OperateResult WriteGeneric(string address, byte[] data)
        {
            var addr = _parser.Parse(address);
            byte[] apdu = new byte[4 + data.Length];
            apdu[0] = 0x00; apdu[1] = 0x01; apdu[2] = 0x05;
            apdu[3] = addr.PropertyId;
            Buffer.BlockCopy(data, 0, apdu, 4, data.Length);
            var r = SendBacnet((byte)addr.DeviceId, apdu);
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
