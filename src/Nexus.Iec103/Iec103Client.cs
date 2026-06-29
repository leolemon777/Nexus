using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Iec103
{
    /// <summary>
    /// IEC 60870-5-103 继电保护通信协议客户端 — 通过串口与保护设备通信。
    /// <para>协议基于 FT1.2 帧格式，用于变电站继电保护设备信息传输。</para>
    /// <para>支持: 时间同步、总召唤、命令发送、参数设定、文件传输</para>
    /// <para>地址格式: Type.FunctionType.InformationNumber[@CA]</para>
    /// <para>示例: M_ME_NC_1.1.1, C_SC_NA_1.1.1@0, 13.1.1</para>
    /// </summary>
    public class Iec103Client : SerialDeviceBase, IBatchReadWrite
    {
        public byte LinkAddress { get; set; } = 1;
        public ushort CommonAddress { get; set; } = 1;
        public byte CaLength { get; set; } = 2;

        private readonly Iec103AddressParser _parser = new Iec103AddressParser();
        private byte _sendSequence;
        private byte _receiveSequence;

        public Iec103Client(ISerialPort port, byte linkAddress = 1, int timeout = 5000)
            : base(port, timeout)
        {
            LinkAddress = linkAddress;
        }

        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            if (header[0] == 0x10) return 0;
            if (header[0] == 0x68)
            {
                int le = header[1];
                return le;
            }
            return 0;
        }

        // ── FT1.2 帧构建 ──────────────────

        private byte[] BuildFixedFrame(byte control, byte address)
        {
            byte[] frame = new byte[6];
            frame[0] = 0x10;
            frame[1] = control;
            frame[2] = address;
            frame[3] = (byte)(frame[1] + frame[2]);
            frame[4] = 0x16;
            return frame;
        }

        private byte[] BuildVariableFrame(byte control, byte address, byte[] asdu)
        {
            int le = 2 + asdu.Length;
            byte[] frame = new byte[le + 6];
            frame[0] = 0x68;
            frame[1] = (byte)le;
            frame[2] = (byte)le;
            frame[3] = 0x68;
            frame[4] = control;
            frame[5] = address;
            Buffer.BlockCopy(asdu, 0, frame, 6, asdu.Length);
            byte cs = 0;
            for (int i = 4; i < 6 + asdu.Length; i++) cs += frame[i];
            frame[6 + asdu.Length] = cs;
            frame[7 + asdu.Length] = 0x16;
            return frame;
        }

        private byte[] BuildAsdu(Iec103AsduType type, byte cause, ushort ca, byte functionType, byte infoNumber, byte[] data)
        {
            int asduLen = 1 + 1 + 1 + CaLength + 1 + 1 + data.Length;
            byte[] asdu = new byte[asduLen];
            int offset = 0;
            asdu[offset++] = (byte)type;
            asdu[offset++] = 0x01; // VSQ: 1 information object
            asdu[offset++] = cause;
            if (CaLength == 2) { asdu[offset++] = (byte)(ca >> 8); }
            asdu[offset++] = (byte)(ca & 0xFF);
            asdu[offset++] = functionType;
            asdu[offset++] = infoNumber;
            Buffer.BlockCopy(data, 0, asdu, offset, data.Length);
            return asdu;
        }

        // ── 收发 ──────────────────

        private OperateResult<byte[]> SendFt12(byte[] frame)
        {
            var result = base.SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] response = result.Content;
            if (response.Length < 1) return OperateResult<byte[]>.Failed("IEC 103 响应为空");

            if (response[0] == 0xE5) return OperateResult<byte[]>.Success(Array.Empty<byte>());

            if (response[0] == 0x10)
            {
                if (response.Length < 6) return OperateResult<byte[]>.Failed("固定帧过短");
                byte cs = (byte)(response[1] + response[2]);
                if (response[3] != cs) return OperateResult<byte[]>.Failed("固定帧校验和错误");
                if (response[4] != 0x16) return OperateResult<byte[]>.Failed("固定帧结束字符错误");
                _receiveSequence = (byte)((response[1] >> 1) & 0x7F);
                return OperateResult<byte[]>.Success(Array.Empty<byte>());
            }

            if (response[0] == 0x68)
            {
                if (response.Length < 7) return OperateResult<byte[]>.Failed("可变帧过短");
                int le = response[1];
                if (response[2] != le) return OperateResult<byte[]>.Failed("可变帧长度不一致");
                if (response[3] != 0x68) return OperateResult<byte[]>.Failed("可变帧启动字符错误");

                int dataLen = le - 2;
                if (response.Length < 6 + dataLen + 2) return OperateResult<byte[]>.Failed("可变帧数据不足");

                byte cs = 0;
                for (int i = 4; i < 6 + dataLen; i++) cs += response[i];
                if (response[6 + dataLen] != cs) return OperateResult<byte[]>.Failed("可变帧校验和错误");
                if (response[7 + dataLen] != 0x16) return OperateResult<byte[]>.Failed("可变帧结束字符错误");

                _receiveSequence = (byte)((response[4] >> 1) & 0x7F);

                byte[] asdu = new byte[dataLen - 2];
                Buffer.BlockCopy(response, 6, asdu, 0, asdu.Length);
                return OperateResult<byte[]>.Success(asdu);
            }

            return OperateResult<byte[]>.Failed($"未知帧类型: 0x{response[0]:X2}");
        }

        private OperateResult<byte[]> SendAsdu(Iec103AsduType type, byte cause, ushort ca, byte functionType, byte infoNumber, byte[] data)
        {
            byte[] asdu = BuildAsdu(type, cause, ca, functionType, infoNumber, data);
            byte control = (byte)((_sendSequence << 1) | 0x40 | 0x03);
            byte[] frame = BuildVariableFrame(control, LinkAddress, asdu);
            _sendSequence = (byte)((_sendSequence + 1) & 0x7F);
            return SendFt12(frame);
        }

        // ── 连接管理 ──────────────────

        public override OperateResult Connect()
        {
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;
            var resetResult = ResetLink();
            if (!resetResult.IsSuccess) { Disconnect(); return resetResult; }
            return OperateResult.Success();
        }

        private OperateResult ResetLink()
        {
            byte control = (byte)((_sendSequence << 1) | 0x40 | 0x00);
            byte[] frame = BuildFixedFrame(control, LinkAddress);
            var result = SendFt12(frame);
            if (!result.IsSuccess) return OperateResult.Failed($"链路复位失败: {result.Message}");
            _sendSequence = 0;
            _receiveSequence = 0;
            return OperateResult.Success();
        }

        // ── 总召唤 ──────────────────

        public OperateResult GeneralInterrogation(ushort ca)
        {
            var r = SendAsdu(Iec103AsduType.C_IC_NA_1, 20, ca, 0, 0, new byte[] { 0x14 });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        // ── 时钟同步 ──────────────────

        public OperateResult ClockSync(ushort ca)
        {
            DateTime now = DateTime.Now;
            byte[] time = new byte[7];
            time[0] = (byte)(now.Millisecond % 1000 / 10);
            time[1] = (byte)((now.Millisecond / 25600) + ((now.Second % 64) << 2));
            time[2] = (byte)now.Minute;
            time[3] = (byte)(now.Hour & 0x1F);
            time[4] = (byte)(now.Day & 0x1F);
            time[5] = (byte)(((int)now.Month & 0x0F) << 4 | ((int)now.DayOfWeek & 0x07));
            time[6] = (byte)(now.Year % 100);
            var r = SendAsdu(Iec103AsduType.C_CS_NA_1, 24, ca, 0, 0, time);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        // ── 读写 ──────────────────

        private Iec103Address ParseAddr(string address)
        {
            var addr = _parser.Parse(address);
            if (addr.Ca == 0) return new Iec103Address(addr.Original, addr.Type, addr.FunctionType, addr.InformationNumber, CommonAddress);
            return addr;
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddr(address);
            var r = SendAsdu(addr.Type, 5, addr.Ca, addr.FunctionType, addr.InformationNumber, Array.Empty<byte>());
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddr(address);
            var r = SendAsdu(addr.Type, 5, addr.Ca, addr.FunctionType, addr.InformationNumber, Array.Empty<byte>());
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<int> ReadInt32(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<int>.Success((int)r.Content) : OperateResult<int>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<uint> ReadUInt32(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<ulong> ReadUInt64(string address) { var r = ReadUInt32(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }

        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = ParseAddr(address);
            var r = SendAsdu(addr.Type, 5, addr.Ca, addr.FunctionType, addr.InformationNumber, Array.Empty<byte>());
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address) { var r = ReadFloat(address); return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<string> ReadString(string address, ushort length) { var r = ReadFloat(address); return r.IsSuccess ? OperateResult<string>.Success(r.Content.ToString("F2")) : OperateResult<string>.Failed(r.Message, r.ErrorCode); }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) { var r = ReadFloat(address); if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode); return OperateResult<byte[]>.Success(DataConverter.GetBytes(r.Content)); }

        public override OperateResult Write(string address, bool value)
        {
            var addr = ParseAddr(address);
            byte sco = (byte)(value ? 0x81 : 0x80);
            var r = SendAsdu(Iec103AsduType.C_SC_NA_1, 20, addr.Ca, addr.FunctionType, addr.InformationNumber, new byte[] { sco });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, short value) { var addr = ParseAddr(address); var r = SendAsdu(Iec103AsduType.C_SE_NA_1, 20, addr.Ca, addr.FunctionType, addr.InformationNumber, new byte[] { (byte)(value >> 8), (byte)value, 0x00 }); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode); }
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) { var addr = ParseAddr(address); var r = SendAsdu(Iec103AsduType.C_SE_NA_1, 20, addr.Ca, addr.FunctionType, addr.InformationNumber, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value, 0x00 }); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode); }
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public override OperateResult Write(string address, double value) => Write(address, (float)value);
        public override OperateResult Write(string address, string value) { if (float.TryParse(value, out float f)) return Write(address, f); return OperateResult.Failed($"无法解析: {value}"); }
        public override OperateResult Write(string address, byte[] data) { var addr = ParseAddr(address); var r = SendAsdu(Iec103AsduType.C_SE_NA_1, 20, addr.Ca, addr.FunctionType, addr.InformationNumber, data); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode); }

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

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses) { var addrList = addresses.ToList(); if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空"); var result = new Dictionary<string, object?>(); foreach (var addr in addrList) { var r = ReadInt16(addr); if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; } return OperateResult<Dictionary<string, object?>>.Success(result); }
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses) { var addrList = addresses.ToList(); if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空"); var result = new Dictionary<string, byte[]>(); foreach (var addr in addrList) { var r = ReadBytes(addr, 1); if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; } return OperateResult<Dictionary<string, byte[]>>.Success(result); }
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items) { foreach (var kv in items) { OperateResult r = kv.Value switch { bool b => Write(kv.Key, b), short s => Write(kv.Key, s), ushort us => Write(kv.Key, us), int i => Write(kv.Key, i), uint ui => Write(kv.Key, ui), float f => Write(kv.Key, f), string s => Write(kv.Key, s), byte[] b => Write(kv.Key, b), _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}") }; if (!r.IsSuccess) return r; } return OperateResult.Success(); }
        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));
    }
}
