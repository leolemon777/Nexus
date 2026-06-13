using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Fuji
{
    /// <summary>
    /// 富士 Command Setting 协议客户端 — 配置/监控协议。
    /// <para>通过 TCP 连接发送 Command Setting 帧读写 PLC 参数。</para>
    /// <para>帧格式: Header(2) + Length(2) + Station(1) + Command(1) + DataType(1) + Reserved(1) + Address(4) + Count(2) + Data(N)</para>
    /// <para>响应: Header(2) + Length(2) + Station(1) + Command(1) + Status(1) + Reserved(1) + Data(N)</para>
    /// </summary>
    public class FujiCommandSettingClient : TcpDeviceBase, IBatchReadWrite
    {
        private const ushort FrameHeader = 0x4643; // "FC"
        private const int MinResponseLength = 8;

        /// <summary>站号。</summary>
        public byte Station { get; set; }

        public FujiCommandSettingClient(string ip, int port = 18245, byte station = 1, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
        }

        // ── TcpDeviceBase 抽象实现 ─────────────────

        protected override int ResponseHeaderLength => 4;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 4) return 0;
            int length = (header[2] << 8) | header[3];
            return Math.Max(0, length - 4);
        }

        // ═══════════════════════════════════════════
        //  帧构建
        // ═══════════════════════════════════════════

        private byte[] BuildReadCommand(FujiSpbTypeCode typeCode, int address, ushort count)
        {
            // Header(2) + Length(2) + Station(1) + Command(1) + DataType(1) + Reserved(1) + Address(4) + Count(2)
            int payloadLen = 4 + 4 + 2;
            byte[] frame = new byte[4 + payloadLen];
            frame[0] = (byte)(FrameHeader >> 8);
            frame[1] = (byte)(FrameHeader & 0xFF);
            frame[2] = (byte)(payloadLen >> 8);
            frame[3] = (byte)(payloadLen & 0xFF);
            frame[4] = Station;
            frame[5] = (byte)FujiCommandCode.Read;
            frame[6] = (byte)FujiCommandDataType.Word;
            frame[7] = 0x00;
            frame[8] = (byte)((address >> 24) & 0xFF);
            frame[9] = (byte)((address >> 16) & 0xFF);
            frame[10] = (byte)((address >> 8) & 0xFF);
            frame[11] = (byte)(address & 0xFF);
            frame[12] = (byte)(count >> 8);
            frame[13] = (byte)(count & 0xFF);
            return frame;
        }

        private byte[] BuildWriteCommand(FujiSpbTypeCode typeCode, int address, byte[] data)
        {
            int payloadLen = 4 + 4 + data.Length;
            byte[] frame = new byte[4 + payloadLen];
            frame[0] = (byte)(FrameHeader >> 8);
            frame[1] = (byte)(FrameHeader & 0xFF);
            frame[2] = (byte)(payloadLen >> 8);
            frame[3] = (byte)(payloadLen & 0xFF);
            frame[4] = Station;
            frame[5] = (byte)FujiCommandCode.Write;
            frame[6] = (byte)FujiCommandDataType.Word;
            frame[7] = 0x00;
            frame[8] = (byte)((address >> 24) & 0xFF);
            frame[9] = (byte)((address >> 16) & 0xFF);
            frame[10] = (byte)((address >> 8) & 0xFF);
            frame[11] = (byte)(address & 0xFF);
            Array.Copy(data, 0, frame, 12, data.Length);
            return frame;
        }

        // ═══════════════════════════════════════════
        //  通讯
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ExecuteRead(FujiSpbTypeCode typeCode, int address, ushort count)
        {
            byte[] cmd = BuildReadCommand(typeCode, address, count);
            RaiseMessageSent(BitConverter.ToString(cmd));
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            return ParseReadResponse(recv.Content, count);
        }

        private OperateResult ExecuteWrite(FujiSpbTypeCode typeCode, int address, byte[] data)
        {
            byte[] cmd = BuildWriteCommand(typeCode, address, data);
            RaiseMessageSent(BitConverter.ToString(cmd));
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            return ParseWriteResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  响应解析
        // ═══════════════════════════════════════════

        private static OperateResult<byte[]> ParseReadResponse(byte[] raw, ushort wordCount)
        {
            if (raw == null || raw.Length < MinResponseLength)
                return OperateResult<byte[]>.Failed("Command Setting 响应太短");

            byte status = raw[6];
            if (status != 0x00)
                return OperateResult<byte[]>.Failed($"Command Setting 错误: 状态码 0x{status:X2}");

            int dataLen = raw.Length - 8;
            if (dataLen < wordCount * 2)
                return OperateResult<byte[]>.Failed("Command Setting 响应数据不足");

            byte[] data = new byte[dataLen];
            Array.Copy(raw, 8, data, 0, dataLen);
            return OperateResult<byte[]>.Success(data);
        }

        private static OperateResult ParseWriteResponse(byte[] raw)
        {
            if (raw == null || raw.Length < MinResponseLength)
                return OperateResult.Failed("Command Setting 响应太短");

            byte status = raw[6];
            if (status != 0x00)
                return OperateResult.Failed($"Command Setting 错误: 状态码 0x{status:X2}");

            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        private static FujiCommandSettingAddress ParseAddress(string address)
        {
            var parsed = FujiCommandSettingAddress.TryParse(address);
            if (parsed == null)
                throw new ArgumentException($"无效的 Command Setting 地址: {address}");
            return parsed;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddress(address);
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            ushort val = r.Content.Length >= 2 ? (ushort)((r.Content[1] << 8) | r.Content[0]) : (ushort)0;
            if (addr.IsBit)
                return OperateResult<bool>.Success((val & (1 << addr.BitIndex)) != 0);
            return OperateResult<bool>.Success(val != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddress(address);
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            ushort val = r.Content.Length >= 2 ? (ushort)((r.Content[1] << 8) | r.Content[0]) : (ushort)0;
            return OperateResult<short>.Success((short)val);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddress(address);
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据长度不足");
            uint val = (uint)(r.Content[0] | (r.Content[1] << 8) | (r.Content[2] << 16) | (r.Content[3] << 24));
            return OperateResult<int>.Success((int)val);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadUInt64(address);
            return r.IsSuccess ? OperateResult<long>.Success(unchecked((long)r.Content)) : OperateResult<long>.Failed(r.Message);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var addr = ParseAddress(address);
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, 4);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            if (r.Content.Length < 8) return OperateResult<ulong>.Failed("数据长度不足");
            ulong val = 0;
            for (int i = 0; i < 8; i++)
                val |= (ulong)r.Content[i] << (i * 8);
            return OperateResult<ulong>.Success(val);
        }

        public override unsafe OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            int v = r.Content;
            return OperateResult<float>.Success(*(float*)&v);
        }

        public override unsafe OperateResult<double> ReadDouble(string address)
        {
            var r = ReadUInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            ulong v = r.Content;
            return OperateResult<double>.Success(*(double*)&v);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = ParseAddress(address);
            int wordCount = (length + 1) / 2;
            var r = ExecuteRead(addr.TypeCode, addr.WordAddress, (ushort)wordCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            byte[] data = new byte[length];
            Array.Copy(r.Content, 0, data, 0, Math.Min(r.Content.Length, length));
            return OperateResult<byte[]>.Success(data);
        }

        // ── 写入 ────────────────────────────────

        public override OperateResult Write(string address, bool value)
        {
            var r = ReadUInt16(address);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message);
            var addr = ParseAddress(address);
            ushort current = r.Content;
            if (addr.IsBit)
            {
                if (value)
                    current |= (ushort)(1 << addr.BitIndex);
                else
                    current &= (ushort)~(1 << addr.BitIndex);
            }
            else
            {
                current = value ? (ushort)1 : (ushort)0;
            }
            byte[] data = new byte[] { (byte)(current & 0xFF), (byte)((current >> 8) & 0xFF) };
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, data);
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = ParseAddress(address);
            byte[] data = new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, data);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = ParseAddress(address);
            byte[] data = new byte[] {
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 24) & 0xFF)
            };
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, data);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value) => Write(address, unchecked((ulong)value));

        public override OperateResult Write(string address, ulong value)
        {
            var addr = ParseAddress(address);
            byte[] data = new byte[8];
            for (int i = 0; i < 8; i++)
                data[i] = (byte)((value >> (i * 8)) & 0xFF);
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, data);
        }

        public override unsafe OperateResult Write(string address, float value) => Write(address, *(int*)&value);
        public override unsafe OperateResult Write(string address, double value) => Write(address, *(ulong*)&value);

        public override OperateResult Write(string address, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? "");
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            var addr = ParseAddress(address);
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, bytes);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null || data.Length == 0) return OperateResult.Failed("写入数据不能为空");
            byte[] padded = data;
            if (padded.Length % 2 != 0) { padded = new byte[data.Length + 1]; Array.Copy(data, padded, data.Length); }
            var addr = ParseAddress(address);
            return ExecuteWrite(addr.TypeCode, addr.WordAddress, padded);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = new List<string>(addresses);
            if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = new List<string>(addresses);
            if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = new List<KeyValuePair<string, object>>(items);
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

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        public override string ToString() => $"FujiCommandSetting[{Ip}:{Port}, Station={Station}]";
    }
}
