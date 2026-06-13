using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.LsElectric
{
    /// <summary>
    /// LS Electric CPU Direct 协议串口客户端 — 简化的直接访问协议。
    /// <para>帧格式 (二进制模式):</para>
    /// <para>请求: ENQ(1) + Station(1) + Cmd(1) + Area(1) + Addr(2 LE) + Count(2 LE) + Data(N) + BCC(1)</para>
    /// <para>响应: ACK(1) + Station(1) + Cmd(1) + Data(N) + BCC(1)</para>
    /// <para>BCC = XOR(Station..Data末字节)</para>
    /// </summary>
    public class LsCpuSerialClient : SerialDeviceBase, IBatchReadWrite
    {
        /// <summary>站号 (0-255)。</summary>
        public byte Station { get; set; }

        // 命令码
        private const byte CmdReadByte = 0x01;
        private const byte CmdReadWord = 0x02;
        private const byte CmdWriteByte = 0x03;
        private const byte CmdWriteWord = 0x04;

        public LsCpuSerialClient(ISerialPort port, byte station = 0, int timeout = 5000)
            : base(port, timeout)
        {
            Station = station;
        }

        // ═══════════════════════════════════════════
        //  SerialDeviceBase 抽象成员实现
        // ═══════════════════════════════════════════

        /// <summary>
        /// CPU Direct 响应头固定为 3 字节: ACK(1) + Station(1) + Cmd(1)
        /// </summary>
        protected override int ResponseHeaderLength => 3;

        /// <summary>
        /// 根据响应头计算剩余载荷长度（包含 1 字节 BCC）。
        /// CPU Direct 协议需要提前知道数据长度，这里通过命令码推断。
        /// </summary>
        private int? _overridePayloadLength;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (_overridePayloadLength.HasValue) return _overridePayloadLength.Value;
            // 默认返回 1（只有 BCC），实际在发送前设置
            return 1;
        }

        // ═══════════════════════════════════════════
        //  CPU Direct 帧构建
        // ═══════════════════════════════════════════

        private static byte ComputeBcc(byte[] data, int offset, int length)
        {
            byte bcc = 0;
            for (int i = offset; i < offset + length; i++)
                bcc ^= data[i];
            return bcc;
        }

        /// <summary>将地址前缀转换为区域代码。</summary>
        private static byte GetAreaCode(LSCnetAddress address)
        {
            // CPU Direct 使用不同的区域代码
            return address.AreaCode switch
            {
                0x50 => 0x00, // P -> 0x00
                0x4D => 0x01, // M -> 0x01
                0x4B => 0x03, // K -> 0x03
                0x54 => 0x05, // T -> 0x05
                0x43 => 0x06, // C -> 0x06
                0x44 => 0x07, // D -> 0x07
                0x4C => 0x02, // L -> 0x02
                0x4E => 0x08, // N -> 0x08
                _ => 0x07
            };
        }

        /// <summary>
        /// 构建 CPU Direct 读取请求。
        /// <para>ENQ + Station + Cmd + Area + Addr(2 LE) + Count(2 LE) + BCC</para>
        /// </summary>
        private byte[] BuildReadRequest(LSCnetAddress address, ushort count, bool isWord)
        {
            byte[] frame = new byte[8]; // ENQ(1) + Station(1) + Cmd(1) + Area(1) + Addr(2) + Count(2) + BCC(1)
            int i = 0;

            frame[i++] = 0x05; // ENQ
            frame[i++] = Station;
            frame[i++] = isWord ? CmdReadWord : CmdReadByte;
            frame[i++] = GetAreaCode(address);
            frame[i++] = (byte)(address.Offset & 0xFF);
            frame[i++] = (byte)((address.Offset >> 8) & 0xFF);
            frame[i++] = (byte)(count & 0xFF);
            frame[i++] = (byte)((count >> 8) & 0xFF);

            // BCC = XOR(Station..Count)
            byte bcc = ComputeBcc(frame, 1, 6);
            frame[i++] = bcc;

            return frame;
        }

        /// <summary>
        /// 构建 CPU Direct 写入请求。
        /// <para>ENQ + Station + Cmd + Area + Addr(2 LE) + Count(2 LE) + Data(N) + BCC</para>
        /// </summary>
        private byte[] BuildWriteRequest(LSCnetAddress address, ushort count, byte[] data, bool isWord)
        {
            byte[] frame = new byte[9 + data.Length]; // ENQ(1) + Station(1) + Cmd(1) + Area(1) + Addr(2) + Count(2) + Data(N) + BCC(1)
            int i = 0;

            frame[i++] = 0x05; // ENQ
            frame[i++] = Station;
            frame[i++] = isWord ? CmdWriteWord : CmdWriteByte;
            frame[i++] = GetAreaCode(address);
            frame[i++] = (byte)(address.Offset & 0xFF);
            frame[i++] = (byte)((address.Offset >> 8) & 0xFF);
            frame[i++] = (byte)(count & 0xFF);
            frame[i++] = (byte)((count >> 8) & 0xFF);

            Buffer.BlockCopy(data, 0, frame, i, data.Length);
            i += data.Length;

            // BCC = XOR(Station..Data末字节)
            byte bcc = ComputeBcc(frame, 1, 7 + data.Length);
            frame[i++] = bcc;

            return frame;
        }

        // ═══════════════════════════════════════════
        //  CPU Direct 帧收发
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送 CPU Direct 请求并接收响应。
        /// </summary>
        private OperateResult<byte[]> SendCpuRequest(byte[] request, int expectedDataLen)
        {
            try
            {
                // 设置预期的响应数据长度
                _overridePayloadLength = expectedDataLen + 1; // +1 for BCC

                var result = SendAndReceive(request);
                _overridePayloadLength = null;

                if (!result.IsSuccess) return result;

                byte[] response = result.Content;
                if (response.Length < 3)
                    return OperateResult<byte[]>.Failed("CPU Direct 响应长度不足");

                // 检查 ACK
                if (response[0] == 0x15) // NAK
                    return OperateResult<byte[]>.Failed("CPU Direct NAK 响应");

                if (response[0] != 0x06) // ACK
                    return OperateResult<byte[]>.Failed($"CPU Direct 响应异常: 0x{response[0]:X2}");

                // 验证 BCC
                int dataLen = response.Length - 4; // 减去 ACK(1) + Station(1) + Cmd(1) + BCC(1)
                byte expectedBcc = ComputeBcc(response, 1, response.Length - 2);
                byte actualBcc = response[response.Length - 1];

                if (expectedBcc != actualBcc)
                    return OperateResult<byte[]>.Failed($"BCC 校验失败: 期望 0x{expectedBcc:X2} 实际 0x{actualBcc:X2}");

                // 提取数据（跳过 ACK + Station + Cmd）
                byte[] data = new byte[dataLen];
                Buffer.BlockCopy(response, 3, data, 0, dataLen);

                return OperateResult<byte[]>.Success(data);
            }
            catch (Exception ex)
            {
                Log.Error($"CPU Direct 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadWordRegisters(LSCnetAddress address, ushort count)
        {
            byte[] request = BuildReadRequest(address, count, true);
            return SendCpuRequest(request, count * 2);
        }

        private OperateResult WriteWordRegisters(LSCnetAddress address, byte[] data)
        {
            int count = data.Length / 2;
            byte[] request = BuildWriteRequest(address, (ushort)count, data, true);
            return SendCpuRequest(request, 0);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            byte[] request = BuildReadRequest(parsed, 1, false);
            var r = SendCpuRequest(request, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 1) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadWordRegisters(parsed, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足");
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadWordRegisters(parsed, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadWordRegisters(parsed, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足");
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadWordRegisters(parsed, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadWordRegisters(parsed, 4);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<double>.Failed("响应数据不足");
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var parsed = LSCnetAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var r = ReadWordRegisters(parsed, (ushort)regCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var parsed = LSCnetAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var r = ReadWordRegisters(parsed, (ushort)regCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Array.Copy(r.Content, data, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, bool value)
        {
            var parsed = LSCnetAddress.Parse(address);
            byte[] request = BuildWriteRequest(parsed, 1, new byte[] { (byte)(value ? 1 : 0) }, false);
            return SendCpuRequest(request, 0);
        }

        public override OperateResult Write(string address, short value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteWordRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteWordRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteWordRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, ulong value) => Write(address, (long)(long)value);

        public override OperateResult Write(string address, float value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteWordRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, double value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteWordRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, string value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteWordRegisters(parsed, Encoding.ASCII.GetBytes(value ?? ""));
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var parsed = LSCnetAddress.Parse(address);
            return WriteWordRegisters(parsed, data);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 1);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    long l => Write(kv.Key, l),
                    ulong ul => Write(kv.Key, ul),
                    float f => Write(kv.Key, f),
                    double d => Write(kv.Key, d),
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
    }
}
