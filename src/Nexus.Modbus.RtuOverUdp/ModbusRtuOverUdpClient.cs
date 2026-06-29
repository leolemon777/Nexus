using System;
using Nexus.Modbus;

namespace Nexus.Modbus.RtuOverUdp
{
    /// <summary>
    /// Modbus RTU Over UDP 客户端 — RTU 帧格式通过 UDP 传输。
    /// <para>地址格式与标准 Modbus 相同: D100, 40001, M0 等</para>
    /// </summary>
    public class ModbusRtuOverUdpClient : UdpDeviceBase, IBatchReadWrite
    {
        public byte Station { get; set; }
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        private readonly ModbusAddressParser _parser = new ModbusAddressParser();

        public ModbusRtuOverUdpClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, timeout) { Station = station; }

        protected override int ResponseHeaderLength => 2;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        private byte[] BuildRtuFrame(byte[] pdu, byte station)
        {
            int dataLen = 1 + pdu.Length;
            byte[] frame = new byte[dataLen + 2];
            frame[0] = station;
            Buffer.BlockCopy(pdu, 0, frame, 1, pdu.Length);
            ushort crc = CrcCalculator.ComputeCrc16(frame, 0, dataLen);
            frame[dataLen] = (byte)(crc & 0xFF);
            frame[dataLen + 1] = (byte)((crc >> 8) & 0xFF);
            return frame;
        }

        private OperateResult<byte[]> SendRtu(byte[] pdu)
        {
            byte[] request = BuildRtuFrame(pdu, Station);
            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            byte[] response = result.Content;
            if (!CrcCalculator.VerifyCrc16(response))
                return OperateResult<byte[]>.Failed("RTU 响应 CRC 校验失败");
            if (response[0] != Station)
                return OperateResult<byte[]>.Failed($"响应站号不匹配: 期望={Station}, 实际={response[0]}");
            if ((response[1] & 0x80) != 0)
            {
                byte exCode = response.Length > 2 ? response[2] : (byte)0;
                string msg = exCode switch { 1 => "非法功能码", 2 => "非法数据地址", 3 => "非法数据值", 4 => "从站设备故障", _ => $"Modbus异常码: {exCode}" };
                return OperateResult<byte[]>.Failed(msg, exCode);
            }
            int pduLen = response.Length - 3;
            byte[] respPdu = new byte[pduLen];
            Buffer.BlockCopy(response, 1, respPdu, 0, pduLen);
            return OperateResult<byte[]>.Success(respPdu);
        }

        private ushort ParseAddress(string address) => _parser.Parse(address).StartAddress;
        private byte GetReadFc(string address) => _parser.Parse(address).ReadFunctionCode;

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendRtu(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 1 });
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success((r.Content[2] & 0x01) != 0);
        }
        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendRtu(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 1 });
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 2, ByteOrder));
        }
        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendRtu(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 2 });
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 2, ByteOrder));
        }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendRtu(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 4 });
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 2, ByteOrder));
        }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendRtu(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 2 });
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 2, ByteOrder));
        }
        public override OperateResult<double> ReadDouble(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendRtu(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 4 });
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 2, ByteOrder));
        }
        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendRtu(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, (byte)(regCount >> 8), (byte)regCount });
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 2, length));
        }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendRtu(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, (byte)(regCount >> 8), (byte)regCount });
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 2, data, 0, Math.Min(length, r.Content.Length - 2));
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, bool value)
        {
            ushort addr = ParseAddress(address);
            var r = SendRtu(new byte[] { 0x05, (byte)(addr >> 8), (byte)addr, (byte)(value ? 0xFF : 0x00), 0x00 });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult Write(string address, short value)
        {
            ushort addr = ParseAddress(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            var r = SendRtu(new byte[] { 0x06, (byte)(addr >> 8), (byte)addr, data[0], data[1] });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value)
        {
            ushort addr = ParseAddress(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            var r = SendRtu(new byte[] { 0x10, (byte)(addr >> 8), (byte)addr, 0, 2, 4, data[0], data[1], data[2], data[3] });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value)
        {
            ushort addr = ParseAddress(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            byte[] pdu = new byte[13];
            pdu[0] = 0x10; pdu[1] = (byte)(addr >> 8); pdu[2] = (byte)addr;
            pdu[3] = 0; pdu[4] = 4; pdu[5] = 8;
            Buffer.BlockCopy(data, 0, pdu, 6, 8);
            var r = SendRtu(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value)
        {
            ushort addr = ParseAddress(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            byte[] pdu = new byte[11];
            pdu[0] = 0x10; pdu[1] = (byte)(addr >> 8); pdu[2] = (byte)addr;
            pdu[3] = 0; pdu[4] = 2; pdu[5] = 4;
            Buffer.BlockCopy(data, 0, pdu, 6, 4);
            var r = SendRtu(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult Write(string address, double value)
        {
            ushort addr = ParseAddress(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            byte[] pdu = new byte[13];
            pdu[0] = 0x10; pdu[1] = (byte)(addr >> 8); pdu[2] = (byte)addr;
            pdu[3] = 0; pdu[4] = 4; pdu[5] = 8;
            Buffer.BlockCopy(data, 0, pdu, 6, 8);
            var r = SendRtu(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult Write(string address, string value)
        {
            ushort addr = ParseAddress(address);
            byte[] strData = DataConverter.GetBytes(value);
            ushort regCount = (ushort)((strData.Length + 1) / 2);
            if (strData.Length % 2 != 0) Array.Resize(ref strData, strData.Length + 1);
            byte[] pdu = new byte[6 + strData.Length];
            pdu[0] = 0x10; pdu[1] = (byte)(addr >> 8); pdu[2] = (byte)addr;
            pdu[3] = (byte)(regCount >> 8); pdu[4] = (byte)regCount; pdu[5] = (byte)strData.Length;
            Buffer.BlockCopy(strData, 0, pdu, 6, strData.Length);
            var r = SendRtu(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult Write(string address, byte[] data)
        {
            ushort addr = ParseAddress(address);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            byte[] pdu = new byte[6 + data.Length];
            pdu[0] = 0x10; pdu[1] = (byte)(addr >> 8); pdu[2] = (byte)addr;
            pdu[3] = (byte)(regCount >> 8); pdu[4] = (byte)regCount; pdu[5] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, pdu, 6, data.Length);
            var r = SendRtu(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

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
