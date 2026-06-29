using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi.IqR
{
    /// <summary>
    /// 三菱 iQ-R 系列 MC 协议客户端 — 基于 TCP 的二进制 MC 协议。
    /// <para>支持设备: SM, SD, X, Y, M, L, F, V, B, D, W, R, ZR, TN, CN, TS, CS, SW</para>
    /// <para>支持命令: 批量读(0x0401), 批量写(0x1401), 位读(0x0401/子命令1), 位写(0x1401/子命令1)</para>
    /// </summary>
    public class IqRClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        public byte NetworkNo { get; set; } = 0;
        public byte PcNo { get; set; } = 0xFF;
        public ushort IoNo { get; set; } = 0x03FF;
        public byte Channel { get; set; } = 0;

        private readonly IqRAddressParser _parser = new IqRAddressParser();

        public IqRClient(string ip, int port = 4999, int timeout = 5000)
            : base(ip, port, timeout) { }

        protected override int ResponseHeaderLength => 9;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        private ushort DeviceCode(string code) => code switch
        {
            "SM" => 0x91, "SD" => 0xA9, "X" => 0x9C, "Y" => 0x9D,
            "M" => 0x90, "L" => 0x92, "F" => 0x93, "V" => 0x94,
            "B" => 0xA0, "D" => 0xA8, "W" => 0xB4, "R" => 0xAF,
            "ZR" => 0xB0, "TN" => 0xC2, "CN" => 0xC3, "TS" => 0xC4,
            "CS" => 0xC5, "SW" => 0xB5, _ => 0xA8
        };

        private byte[] BuildMcFrame(ushort command, ushort subCommand, byte[] data)
        {
            int dataLen = data.Length;
            byte[] frame = new byte[11 + dataLen + 2];
            frame[0] = 0x50; frame[1] = 0x00;
            frame[2] = NetworkNo; frame[3] = PcNo;
            frame[4] = (byte)(IoNo & 0xFF); frame[5] = (byte)(IoNo >> 8);
            frame[6] = Channel;
            frame[7] = (byte)((dataLen + 4) & 0xFF); frame[8] = (byte)((dataLen + 4) >> 8);
            frame[9] = 0x0A; frame[10] = 0x00;
            frame[11] = (byte)(command & 0xFF); frame[12] = (byte)(command >> 8);
            frame[13] = (byte)(subCommand & 0xFF); frame[14] = (byte)(subCommand >> 8);
            Buffer.BlockCopy(data, 0, frame, 15, dataLen);
            ushort sum = 0;
            for (int i = 0; i < 11 + dataLen; i++) sum += frame[i];
            frame[11 + dataLen] = (byte)(sum & 0xFF);
            frame[12 + dataLen] = (byte)(sum >> 8);
            return frame;
        }

        private OperateResult<byte[]> SendMc(ushort command, ushort subCommand, byte[] data)
        {
            byte[] request = BuildMcFrame(command, subCommand, data);
            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            byte[] response = result.Content;
            if (response.Length < 11) return OperateResult<byte[]>.Failed("iQ-R 响应过短");
            ushort completionCode = (ushort)(response[9] | (response[10] << 8));
            if (completionCode != 0)
                return OperateResult<byte[]>.Failed($"iQ-R 错误: 0x{completionCode:X4}", completionCode);
            byte[] pdu = new byte[response.Length - 11];
            Buffer.BlockCopy(response, 11, pdu, 0, pdu.Length);
            return OperateResult<byte[]>.Success(pdu);
        }

        private byte[] BuildReadData(IqRAddress addr, ushort count)
        {
            return new byte[] {
                (byte)(addr.StartAddress & 0xFF), (byte)(addr.StartAddress >> 8),
                0, (byte)DeviceCode(addr.DeviceCode),
                (byte)(count & 0xFF), (byte)(count >> 8)
            };
        }

        // ── Read ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendMc(0x0401, 0x0001, BuildReadData(addr, 1));
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendMc(0x0401, 0x0000, BuildReadData(addr, 1));
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success((short)(r.Content[0] | (r.Content[1] << 8)));
        }

        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendMc(0x0401, 0x0000, BuildReadData(addr, 2));
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24));
        }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendMc(0x0401, 0x0000, BuildReadData(addr, 4));
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success((long)r.Content[0] | ((long)r.Content[1] << 8) | ((long)r.Content[2] << 16) | ((long)r.Content[3] << 24) | ((long)r.Content[4] << 32) | ((long)r.Content[5] << 40) | ((long)r.Content[6] << 48) | ((long)r.Content[7] << 56));
        }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = _parser.Parse(address);
            var r = SendMc(0x0401, 0x0000, BuildReadData(addr, 2));
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
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
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendMc(0x0401, 0x0000, BuildReadData(addr, regCount));
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, length));
        }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            ushort regCount = (ushort)((length + 1) / 2);
            var r = SendMc(0x0401, 0x0000, BuildReadData(addr, regCount));
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 0, data, 0, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        // ── Write ──────────────────

        private OperateResult WriteWords(IqRAddress addr, byte[] wordData)
        {
            ushort count = (ushort)(wordData.Length / 2);
            byte[] data = new byte[6 + wordData.Length];
            data[0] = (byte)(addr.StartAddress & 0xFF); data[1] = (byte)(addr.StartAddress >> 8);
            data[2] = 0; data[3] = (byte)DeviceCode(addr.DeviceCode);
            data[4] = (byte)(count & 0xFF); data[5] = (byte)(count >> 8);
            Buffer.BlockCopy(wordData, 0, data, 6, wordData.Length);
            var r = SendMc(0x1401, 0x0000, data);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, bool value)
        {
            var addr = _parser.Parse(address);
            byte[] data = new byte[] { (byte)(addr.StartAddress & 0xFF), (byte)(addr.StartAddress >> 8), 0, (byte)DeviceCode(addr.DeviceCode), 1, 0, (byte)(value ? 0x01 : 0x00) };
            var r = SendMc(0x1401, 0x0001, data);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult Write(string address, short value) { var addr = _parser.Parse(address); return WriteWords(addr, new byte[] { (byte)value, (byte)(value >> 8) }); }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) { var addr = _parser.Parse(address); return WriteWords(addr, new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) }); }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) { var addr = _parser.Parse(address); return WriteWords(addr, new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24), (byte)(value >> 32), (byte)(value >> 40), (byte)(value >> 48), (byte)(value >> 56) }); }
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.DoubleToInt64Bits(value));
        public override OperateResult Write(string address, string value) { var addr = _parser.Parse(address); byte[] strData = DataConverter.GetBytes(value); if (strData.Length % 2 != 0) Array.Resize(ref strData, strData.Length + 1); return WriteWords(addr, strData); }
        public override OperateResult Write(string address, byte[] data) { var addr = _parser.Parse(address); if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1); return WriteWords(addr, data); }

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

        // ── ISubscribeDevice ──────────────────
        private readonly Dictionary<string, IqRSub> _subs = new Dictionary<string, IqRSub>();
        private readonly object _subLock = new object();
        private Timer? _timer;
        private bool _monitoring;
        private class IqRSub { public string Address = ""; public string DataType = "Int16"; public object? LastValue; }
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;
        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16") { lock (_subLock) { _subs[address] = new IqRSub { Address = address, DataType = dataType }; } }
        public void Unsubscribe(string address) { lock (_subLock) { _subs.Remove(address); } }
        public void StartSubscriptions(int globalIntervalMs = 500) { if (_monitoring) return; _monitoring = true; _timer = new Timer(Poll, null, globalIntervalMs, globalIntervalMs); }
        public void StopSubscriptions() { _monitoring = false; _timer?.Dispose(); _timer = null; }
        private void Poll(object? state)
        {
            if (!_monitoring) return;
            try
            {
                List<IqRSub> entries; lock (_subLock) { entries = new List<IqRSub>(_subs.Values); }
                foreach (var e in entries)
                {
                    try
                    {
                        object? cur = e.DataType switch { "Int16" => ReadInt16(e.Address).Content, "UInt16" => ReadUInt16(e.Address).Content, "Int32" => ReadInt32(e.Address).Content, "Float" => ReadFloat(e.Address).Content, "Bool" => ReadBool(e.Address).Content, _ => null };
                        if (cur != null && !Equals(cur, e.LastValue))
                        {
                            if (e.LastValue == null) { e.LastValue = cur; continue; }
                            OnDataChanged?.Invoke(this, new DataChangeEventArgs { Address = e.Address, OldValue = e.LastValue, NewValue = cur, Timestamp = DateTime.Now, Quality = "Good" });
                            e.LastValue = cur;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
