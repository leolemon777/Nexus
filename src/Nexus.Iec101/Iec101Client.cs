using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Iec101
{
    /// <summary>
    /// IEC 60870-5-101 远动协议客户端 — 通过串口与 RTU/IED 通信。
    /// <para>协议基于 FT1.2 帧格式，支持平衡/非平衡传输。</para>
    /// <para>地址格式: Type.IOA[@CA]，例如 M_ME_NC_1.100, C_SC_NA_1.1@0</para>
    /// </summary>
    public class Iec101Client : SerialDeviceBase, IBatchReadWrite
    {
        public byte LinkAddress { get; set; } = 1;
        public ushort CommonAddress { get; set; } = 1;
        public byte IoaLength { get; set; } = 3; // 信息对象地址长度 (2 或 3 字节)
        public byte CaLength { get; set; } = 2;  // 公共地址长度 (1 或 2 字节)
        public bool Balanced { get; set; } = false;

        private readonly Iec101AddressParser _parser = new Iec101AddressParser();
        private byte _sendSequence;
        private byte _receiveSequence;

        public Iec101Client(ISerialPort port, byte linkAddress = 1, int timeout = 5000)
            : base(port, timeout)
        {
            LinkAddress = linkAddress;
        }

        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            if (header[0] == 0x10) return 0; // Fixed frame
            if (header[0] == 0x68)
            {
                int le = header[1];
                return le; // LE field
            }
            return 0;
        }

        // ═══════════════════════════════════════════
        //  FT1.2 帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建固定长度帧 (6 字节)。</summary>
        private byte[] BuildFixedFrame(byte control, byte address)
        {
            byte[] frame = new byte[6];
            frame[0] = 0x10;        // 启动字符
            frame[1] = control;     // 控制域
            frame[2] = address;     // 地址域
            frame[3] = (byte)(frame[1] + frame[2]); // 校验和
            frame[4] = 0x16;        // 结束字符
            return frame;
        }

        /// <summary>构建可变长度帧。</summary>
        private byte[] BuildVariableFrame(byte control, byte address, byte[] asdu)
        {
            int le = 2 + asdu.Length; // 控制域 + 地址域 + ASDU
            byte[] frame = new byte[le + 6];
            frame[0] = 0x68;        // 启动字符
            frame[1] = (byte)le;    // 长度
            frame[2] = (byte)le;    // 长度重复
            frame[3] = 0x68;        // 启动字符重复
            frame[4] = control;     // 控制域
            frame[5] = address;     // 地址域
            Buffer.BlockCopy(asdu, 0, frame, 6, asdu.Length);
            byte cs = 0;
            for (int i = 4; i < 6 + asdu.Length; i++) cs += frame[i];
            frame[6 + asdu.Length] = cs;
            frame[7 + asdu.Length] = 0x16;
            return frame;
        }

        /// <summary>构建 ASDU。</summary>
        private byte[] BuildAsdu(AsduType type, CauseOfTransmission cot, ushort ca, byte[] infoObjects)
        {
            int asduLen = 1 + 1 + CaLength + infoObjects.Length; // 类型 + VSQ + COT + CA + IO
            byte[] asdu = new byte[asduLen];
            int offset = 0;
            asdu[offset++] = (byte)type;       // 类型标识
            asdu[offset++] = 0x01;              // 可变结构限定词 (1 个信息对象，顺序)
            asdu[offset++] = (byte)cot;         // 传送原因
            if (CaLength == 2) { asdu[offset++] = (byte)(ca >> 8); }
            asdu[offset++] = (byte)(ca & 0xFF); // 公共地址
            Buffer.BlockCopy(infoObjects, 0, asdu, offset, infoObjects.Length);
            return asdu;
        }

        /// <summary>构建信息对象。</summary>
        private byte[] BuildInfoObject(uint ioa, byte[] data)
        {
            byte[] io = new byte[IoaLength + data.Length];
            if (IoaLength == 3) { io[0] = (byte)(ioa & 0xFF); io[1] = (byte)((ioa >> 8) & 0xFF); io[2] = (byte)((ioa >> 16) & 0xFF); }
            else if (IoaLength == 2) { io[0] = (byte)(ioa & 0xFF); io[1] = (byte)((ioa >> 8) & 0xFF); }
            else { io[0] = (byte)(ioa & 0xFF); }
            Buffer.BlockCopy(data, 0, io, IoaLength, data.Length);
            return io;
        }

        // ═══════════════════════════════════════════
        //  控制域构建
        // ═══════════════════════════════════════════

        // 主站 → 子站 (发送/确认)
        private byte BuildControlSend(byte fc)
        {
            return (byte)((_sendSequence << 1) | 0x00 | (fc & 0x0F)); // PRM=1, RES=0
        }

        // 子站 → 主站 (接收)
        private byte BuildControlReceive()
        {
            return (byte)((_receiveSequence << 1) | 0x00); // PRM=0, ACD=0
        }

        // ═══════════════════════════════════════════
        //  收发
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendFt12(byte[] frame)
        {
            var result = base.SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] response = result.Content;
            if (response.Length < 1) return OperateResult<byte[]>.Failed("IEC 101 响应为空");

            // SC (Single Character)
            if (response[0] == 0xE5) return OperateResult<byte[]>.Success(Array.Empty<byte>());

            // Fixed frame
            if (response[0] == 0x10)
            {
                if (response.Length < 6) return OperateResult<byte[]>.Failed("固定帧过短");
                byte cs = (byte)(response[1] + response[2]);
                if (response[3] != cs) return OperateResult<byte[]>.Failed("固定帧校验和错误");
                if (response[4] != 0x16) return OperateResult<byte[]>.Failed("固定帧结束字符错误");
                _receiveSequence = (byte)((response[1] >> 1) & 0x7F);
                return OperateResult<byte[]>.Success(Array.Empty<byte>());
            }

            // Variable frame
            if (response[0] == 0x68)
            {
                if (response.Length < 7) return OperateResult<byte[]>.Failed("可变帧过短");
                int le = response[1];
                if (response[2] != le) return OperateResult<byte[]>.Failed("可变帧长度不一致");
                if (response[3] != 0x68) return OperateResult<byte[]>.Failed("可变帧启动字符错误");

                int dataLen = le - 2; // LE includes control + address
                if (response.Length < 6 + dataLen + 2) return OperateResult<byte[]>.Failed("可变帧数据不足");

                byte cs = 0;
                for (int i = 4; i < 6 + dataLen; i++) cs += response[i];
                if (response[6 + dataLen] != cs) return OperateResult<byte[]>.Failed("可变帧校验和错误");
                if (response[7 + dataLen] != 0x16) return OperateResult<byte[]>.Failed("可变帧结束字符错误");

                _receiveSequence = (byte)((response[4] >> 1) & 0x7F);

                // Extract ASDU
                byte[] asdu = new byte[dataLen - 2]; // Subtract control + address
                Buffer.BlockCopy(response, 6, asdu, 0, asdu.Length);
                return OperateResult<byte[]>.Success(asdu);
            }

            return OperateResult<byte[]>.Failed($"未知帧类型: 0x{response[0]:X2}");
        }

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        public override OperateResult Connect()
        {
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;

            // Reset link
            var resetResult = ResetLink();
            if (!resetResult.IsSuccess) { Disconnect(); return resetResult; }

            return OperateResult.Success();
        }

        private OperateResult ResetLink()
        {
            // 发送链路复位 (FC=0, PRM=1)
            byte control = BuildControlSend(0x00); // FC=0: Reset of remote link
            byte[] frame = BuildFixedFrame((byte)(control | 0x40), LinkAddress); // PRM=1
            var result = SendFt12(frame);
            if (!result.IsSuccess) return OperateResult.Failed($"链路复位失败: {result.Message}");
            _sendSequence = 0;
            _receiveSequence = 0;
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  ASDU 读写操作
        // ═══════════════════════════════════════════

        /// <summary>发送 ASDU 并接收响应。</summary>
        private OperateResult<byte[]> SendAsdu(AsduType type, CauseOfTransmission cot, ushort ca, byte[] infoObjects)
        {
            byte[] asdu = BuildAsdu(type, cot, ca, infoObjects);
            byte control = BuildControlSend(0x03); // FC=3: User data
            byte[] frame = BuildVariableFrame((byte)(control | 0x40), LinkAddress, asdu); // PRM=1

            _sendSequence = (byte)((_sendSequence + 1) & 0x7F);
            return SendFt12(frame);
        }

        /// <summary>总召唤 (C_IC_NA_1, Type 100)。</summary>
        public OperateResult GeneralInterrogation(ushort ca, byte qualifier = 20)
        {
            byte[] io = BuildInfoObject(0, new byte[] { qualifier });
            var r = SendAsdu(AsduType.C_IC_NA_1, CauseOfTransmission.Activation, ca, io);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        /// <summary>时钟同步 (C_CS_NA_1, Type 103)。</summary>
        public OperateResult ClockSync(ushort ca)
        {
            DateTime now = DateTime.Now;
            byte[] time = new byte[7];
            time[0] = (byte)(now.Millisecond % 1000 / 10); // 毫秒/10 低字节
            time[1] = (byte)((now.Millisecond / 25600) + ((now.Second % 64) << 2)); // 毫秒高 + 秒
            time[2] = (byte)now.Minute;
            time[3] = (byte)(now.Hour & 0x1F);
            time[4] = (byte)(now.Day & 0x1F);
            time[5] = (byte)(((int)now.Month & 0x0F) << 4 | ((int)now.DayOfWeek & 0x07));
            time[6] = (byte)(now.Year % 100);

            byte[] io = BuildInfoObject(0, time);
            var r = SendAsdu(AsduType.C_CS_NA_1, CauseOfTransmission.Activation, ca, io);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        /// <summary>解析 ASDU 响应中的信息对象。</summary>
        private List<(uint ioa, byte[] data)> ParseInfoObjects(byte[] asdu)
        {
            var result = new List<(uint ioa, byte[] data)>();
            if (asdu.Length < 4) return result;

            AsduType type = (AsduType)asdu[0];
            int vsq = asdu[1] & 0x7F;
            bool isSequence = (asdu[1] & 0x80) != 0;

            int offset = 2 + 1 + CaLength; // Type + VSQ + COT + CA

            int infoObjectSize = GetInfoObjectSize(type);

            if (isSequence)
            {
                // 顺序信息对象：第一个有 IOA，后续递增
                if (offset + IoaLength > asdu.Length) return result;
                uint baseIoa = ParseIoa(asdu, offset);
                offset += IoaLength;

                for (int i = 0; i < vsq; i++)
                {
                    if (offset + infoObjectSize > asdu.Length) break;
                    byte[] data = new byte[infoObjectSize];
                    Buffer.BlockCopy(asdu, offset, data, 0, infoObjectSize);
                    result.Add((baseIoa + (uint)i, data));
                    offset += infoObjectSize;
                }
            }
            else
            {
                // 非顺序信息对象：每个都有 IOA
                for (int i = 0; i < vsq; i++)
                {
                    if (offset + IoaLength > asdu.Length) break;
                    uint ioa = ParseIoa(asdu, offset);
                    offset += IoaLength;

                    if (offset + infoObjectSize > asdu.Length) break;
                    byte[] data = new byte[infoObjectSize];
                    Buffer.BlockCopy(asdu, offset, data, 0, infoObjectSize);
                    result.Add((ioa, data));
                    offset += infoObjectSize;
                }
            }

            return result;
        }

        private uint ParseIoa(byte[] data, int offset)
        {
            if (IoaLength == 3) return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));
            if (IoaLength == 2) return (uint)(data[offset] | (data[offset + 1] << 8));
            return data[offset];
        }

        private int GetInfoObjectSize(AsduType type) => type switch
        {
            AsduType.M_SP_NA_1 => 1,     // 单点: SIQ(1)
            AsduType.M_DP_NA_1 => 1,     // 双点: DIQ(1)
            AsduType.M_ME_NA_1 => 3,     // 标度化: NVA(2) + QDS(1)
            AsduType.M_ME_NB_1 => 3,     // 标度化: SVA(2) + QDS(1)
            AsduType.M_ME_NC_1 => 5,     // 短浮点: IEEE(4) + QDS(1)
            AsduType.M_IT_NA_1 => 5,     // 计数量: BCR(5)
            AsduType.C_SC_NA_1 => 1,     // 单命令: SCO(1)
            AsduType.C_DC_NA_1 => 1,     // 双命令: DCO(1)
            AsduType.C_SE_NA_1 => 3,     // 设定值标度化: NVA(2) + QOS(1)
            AsduType.C_SE_NB_1 => 3,     // 设定值标度化: SVA(2) + QOS(1)
            AsduType.C_SE_NC_1 => 5,     // 设定值短浮点: IEEE(4) + QOS(1)
            _ => 1
        };

        // ═══════════════════════════════════════════
        //  读写单个值
        // ═══════════════════════════════════════════

        private OperateResult<float> ReadFloatValue(uint ioa, ushort ca)
        {
            byte[] io = BuildInfoObject(ioa, Array.Empty<byte>());
            var r = SendAsdu((AsduType)5, CauseOfTransmission.Request, ca, io);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);

            var objects = ParseInfoObjects(r.Content);
            if (objects.Count == 0) return OperateResult<float>.Failed("无数据");

            byte[] data = objects[0].data;
            if (data.Length >= 5)
            {
                return OperateResult<float>.Success(DataConverter.ToFloat(data, 0));
            }
            if (data.Length >= 3)
            {
                short raw = (short)(data[0] | (data[1] << 8));
                return OperateResult<float>.Success((float)raw);
            }
            return OperateResult<float>.Failed("数据格式不支持");
        }

        private OperateResult WriteFloatValue(uint ioa, ushort ca, float value)
        {
            int bits;
            unsafe { bits = *(int*)&value; }
            byte[] data = new byte[] { (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), 0x00 };
            byte[] io = BuildInfoObject(ioa, data);
            var r = SendAsdu(AsduType.C_SE_NC_1, CauseOfTransmission.Activation, ca, io);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        private OperateResult<short> ReadInt16Value(uint ioa, ushort ca)
        {
            byte[] io = BuildInfoObject(ioa, Array.Empty<byte>());
            var r = SendAsdu((AsduType)5, CauseOfTransmission.Request, ca, io);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);

            var objects = ParseInfoObjects(r.Content);
            if (objects.Count == 0) return OperateResult<short>.Failed("无数据");

            byte[] data = objects[0].data;
            if (data.Length >= 3)
            {
                short raw = (short)(data[0] | (data[1] << 8));
                return OperateResult<short>.Success(raw);
            }
            return OperateResult<short>.Failed("数据格式不支持");
        }

        private OperateResult<bool> ReadBoolValue(uint ioa, ushort ca)
        {
            byte[] io = BuildInfoObject(ioa, Array.Empty<byte>());
            var r = SendAsdu((AsduType)1, CauseOfTransmission.Request, ca, io);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);

            var objects = ParseInfoObjects(r.Content);
            if (objects.Count == 0) return OperateResult<bool>.Failed("无数据");

            return OperateResult<bool>.Success((objects[0].data[0] & 0x01) != 0);
        }

        private OperateResult WriteBoolValue(uint ioa, ushort ca, bool value)
        {
            byte sco = (byte)(value ? 0x81 : 0x80); // S/E=1, QU=0, SCS=1/0
            byte[] io = BuildInfoObject(ioa, new byte[] { sco });
            var r = SendAsdu(AsduType.C_SC_NA_1, CauseOfTransmission.Activation, ca, io);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 实现
        // ═══════════════════════════════════════════

        private Iec101Address ParseAddr(string address)
        {
            var addr = _parser.Parse(address);
            if (addr.Ca == 0) return new Iec101Address(addr.Original, addr.Type, addr.Ioa, CommonAddress);
            return addr;
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddr(address);
            return ReadBoolValue(addr.Ioa, addr.Ca);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddr(address);
            return ReadInt16Value(addr.Ioa, addr.Ca);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<int>.Success((int)r.Content) : OperateResult<int>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadUInt32(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = ParseAddr(address);
            return ReadFloatValue(addr.Ioa, addr.Ca);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadFloat(address);
            return r.IsSuccess ? OperateResult<string>.Success(r.Content.ToString("F2")) : OperateResult<string>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadFloat(address);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(DataConverter.GetBytes(r.Content));
        }

        // ── Write implementations ──────────────────

        public override OperateResult Write(string address, bool value)
        {
            var addr = ParseAddr(address);
            return WriteBoolValue(addr.Ioa, addr.Ca, value);
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = ParseAddr(address);
            byte[] data = new byte[] { (byte)value, (byte)(value >> 8), 0x00 };
            byte[] io = BuildInfoObject(addr.Ioa, data);
            var r = SendAsdu(AsduType.C_SE_NA_1, CauseOfTransmission.Activation, addr.Ca, io);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = ParseAddr(address);
            return WriteFloatValue(addr.Ioa, addr.Ca, value);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);

        public override OperateResult Write(string address, float value)
        {
            var addr = ParseAddr(address);
            return WriteFloatValue(addr.Ioa, addr.Ca, value);
        }

        public override OperateResult Write(string address, double value) => Write(address, (float)value);

        public override OperateResult Write(string address, string value)
        {
            if (float.TryParse(value, out float f)) return Write(address, f);
            return OperateResult.Failed($"无法解析字符串为数值: {value}");
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = ParseAddr(address);
            byte[] io = BuildInfoObject(addr.Ioa, data);
            var r = SendAsdu(AsduType.C_SE_NC_1, CauseOfTransmission.Activation, addr.Ca, io);
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
            foreach (var addr in addrList) { var r = ReadFloat(addr); if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; }
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
