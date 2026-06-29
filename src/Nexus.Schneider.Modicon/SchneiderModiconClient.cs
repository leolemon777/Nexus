using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Schneider.Modicon
{
    /// <summary>
    /// Schneider Modicon 客户端 — 基于 Modbus TCP，支持 Unity Pro 地址格式。
    /// <para>支持地址: %MW, %MB, %MD, %MX, %IW, %IX, %QW, %QX, %NW, %NB。</para>
    /// <para>支持功能码: FC01-06, FC15, FC16。</para>
    /// </summary>
    public class SchneiderModiconClient : TcpDeviceBase, IBatchReadWrite
    {
        public byte Station { get; set; }
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        private readonly SchneiderModiconAddressParser _parser = new SchneiderModiconAddressParser();
        private int _transactionId;

        public SchneiderModiconClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, timeout) { Station = station; }

        protected override int ResponseHeaderLength => 7;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            int length = (header[4] << 8) | header[5];
            return length - 1;
        }

        private ushort NextTid() => (ushort)(Interlocked.Increment(ref _transactionId) & 0xFFFF);

        private byte[] BuildMbap(byte[] pdu)
        {
            ushort tid = NextTid();
            int totalLen = pdu.Length + 1;
            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(tid >> 8); frame[1] = (byte)tid;
            frame[2] = 0; frame[3] = 0;
            frame[4] = (byte)(totalLen >> 8); frame[5] = (byte)totalLen;
            frame[6] = Station;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);
            return frame;
        }

        private static byte[] ExtractPdu(byte[] response)
        {
            byte[] pdu = new byte[response.Length - 7];
            Buffer.BlockCopy(response, 7, pdu, 0, pdu.Length);
            return pdu;
        }

        private OperateResult<byte[]> SendModbus(byte[] pdu)
        {
            byte[] request = BuildMbap(pdu);
            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            byte[] respPdu = ExtractPdu(result.Content);
            if ((respPdu[0] & 0x80) != 0)
            {
                byte exCode = respPdu.Length > 1 ? respPdu[1] : (byte)0;
                string msg = exCode switch
                {
                    1 => "非法功能码", 2 => "非法数据地址", 3 => "非法数据值",
                    4 => "从站设备故障", _ => $"Modbus异常码: {exCode}"
                };
                return OperateResult<byte[]>.Failed(msg, exCode);
            }
            return OperateResult<byte[]>.Success(respPdu);
        }

        // ── Read implementations ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _parser.Parse(address);
            byte fc = addr.IsBit ? addr.ReadFunctionCode : (byte)0x01;
            var result = SendModbus(new byte[] { fc, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 1 });
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return OperateResult<bool>.Success((result.Content[2] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendModbus(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 1 });
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 2, ByteOrder));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendModbus(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 1 });
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 2, ByteOrder));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendModbus(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 2 });
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 2, ByteOrder));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendModbus(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 4 });
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 2, ByteOrder));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendModbus(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 2 });
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 2, ByteOrder));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendModbus(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 4 });
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 2, ByteOrder));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendModbus(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, (byte)(regCount >> 8), (byte)regCount });
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 2, length));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendModbus(new byte[] { addr.ReadFunctionCode, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, (byte)(regCount >> 8), (byte)regCount });
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 2, data, 0, Math.Min(length, r.Content.Length - 2));
            return OperateResult<byte[]>.Success(data);
        }

        // ── Write implementations ──────────────────

        public override OperateResult Write(string address, bool value)
        {
            var addr = _parser.Parse(address);
            ushort coilAddr = addr.IsBit ? addr.StartAddress : ushort.Parse(address.TrimStart('%', 'M', 'X', '0'));
            var r = SendModbus(new byte[] { 0x05, (byte)(coilAddr >> 8), (byte)coilAddr, (byte)(value ? 0xFF : 0x00), 0x00 });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = _parser.Parse(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            var r = SendModbus(new byte[] { 0x06, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, data[0], data[1] });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = _parser.Parse(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            var r = SendModbus(new byte[] { 0x10, (byte)(addr.StartAddress >> 8), (byte)addr.StartAddress, 0, 2, 4, data[0], data[1], data[2], data[3] });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = _parser.Parse(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            byte[] pdu = new byte[13];
            pdu[0] = 0x10; pdu[1] = (byte)(addr.StartAddress >> 8); pdu[2] = (byte)addr.StartAddress;
            pdu[3] = 0; pdu[4] = 4; pdu[5] = 8;
            Buffer.BlockCopy(data, 0, pdu, 6, 8);
            var r = SendModbus(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);

        public override OperateResult Write(string address, float value)
        {
            var addr = _parser.Parse(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            byte[] pdu = new byte[11];
            pdu[0] = 0x10; pdu[1] = (byte)(addr.StartAddress >> 8); pdu[2] = (byte)addr.StartAddress;
            pdu[3] = 0; pdu[4] = 2; pdu[5] = 4;
            Buffer.BlockCopy(data, 0, pdu, 6, 4);
            var r = SendModbus(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, double value)
        {
            var addr = _parser.Parse(address);
            byte[] data = DataConverter.GetBytes(value, ByteOrder);
            byte[] pdu = new byte[13];
            pdu[0] = 0x10; pdu[1] = (byte)(addr.StartAddress >> 8); pdu[2] = (byte)addr.StartAddress;
            pdu[3] = 0; pdu[4] = 4; pdu[5] = 8;
            Buffer.BlockCopy(data, 0, pdu, 6, 8);
            var r = SendModbus(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, string value)
        {
            var addr = _parser.Parse(address);
            byte[] strData = DataConverter.GetBytes(value);
            ushort regCount = (ushort)((strData.Length + 1) / 2);
            if (strData.Length % 2 != 0) Array.Resize(ref strData, strData.Length + 1);
            byte[] pdu = new byte[6 + strData.Length];
            pdu[0] = 0x10; pdu[1] = (byte)(addr.StartAddress >> 8); pdu[2] = (byte)addr.StartAddress;
            pdu[3] = (byte)(regCount >> 8); pdu[4] = (byte)regCount; pdu[5] = (byte)strData.Length;
            Buffer.BlockCopy(strData, 0, pdu, 6, strData.Length);
            var r = SendModbus(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _parser.Parse(address);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            byte[] pdu = new byte[6 + data.Length];
            pdu[0] = 0x10; pdu[1] = (byte)(addr.StartAddress >> 8); pdu[2] = (byte)addr.StartAddress;
            pdu[3] = (byte)(regCount >> 8); pdu[4] = (byte)regCount; pdu[5] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, pdu, 6, data.Length);
            var r = SendModbus(pdu);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        // ── Async (delegate to sync via Task.Run) ──

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
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 1);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default)
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0) return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default)
            => Task.FromResult(BatchWrite(items));

        // ── Schneider 特有：诊断读取 ──────────────

        /// <summary>读取模块诊断信息（Schneider 专有）。</summary>
        public OperateResult<byte[]> ReadModuleDiagnostics(ushort startRegister, ushort count)
        {
            var r = SendModbus(new byte[] { 0x03, (byte)(startRegister >> 8), (byte)startRegister, (byte)(count >> 8), (byte)count });
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            int byteCount = r.Content[1];
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(r.Content, 2, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }
    }
}
