using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.ToyoPuc
{
    /// <summary>
    /// ToyoPuc PLC TCP 通讯协议客户端。
    /// <para>帧格式: Header(2) + Length(2) + Command(1) + Data(N) + Checksum(2)</para>
    /// <para>Header = 0x54 0x50 ("TP"), Length = 从 Length 字段后到末尾（含 checksum）。</para>
    /// </summary>
    public class ToyoPucClient : TcpDeviceBase, IBatchReadWrite
    {
        private const byte HEADER_0 = 0x54;
        private const byte HEADER_1 = 0x50;

        private const byte CMD_READ = 0x01;
        private const byte CMD_WRITE = 0x02;
        private const byte CMD_READ_MULTI = 0x03;
        private const byte CMD_WRITE_MULTI = 0x04;

        private const byte RESP_OK = 0x00;
        private const byte RESP_ERROR = 0x80;

        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            int length = (header[2] << 8) | header[3];
            return length > 0 ? length : 0;
        }

        public ToyoPucClient(string ip, int port, int timeout = 5000)
            : base(ip, port, timeout) { }

        public OperateResult<byte[]> ReadRegisters(ushort startAddress, ushort count)
        {
            byte[] data = new byte[4];
            data[0] = (byte)(startAddress >> 8);
            data[1] = (byte)(startAddress & 0xFF);
            data[2] = (byte)(count >> 8);
            data[3] = (byte)(count & 0xFF);

            var frame = BuildFrame(CMD_READ, data);
            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

            return ParseResponse(result.Content, CMD_READ);
        }

        public OperateResult WriteRegisters(ushort startAddress, byte[] values)
        {
            byte[] data = new byte[4 + values.Length];
            data[0] = (byte)(startAddress >> 8);
            data[1] = (byte)(startAddress & 0xFF);
            ushort count = (ushort)(values.Length / 2);
            data[2] = (byte)(count >> 8);
            data[3] = (byte)(count & 0xFF);
            Array.Copy(values, 0, data, 4, values.Length);

            var frame = BuildFrame(CMD_WRITE, data);
            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);

            var parsed = ParseResponse(result.Content, CMD_WRITE);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        private byte[] BuildFrame(byte command, byte[] data)
        {
            int length = 1 + data.Length + 2;
            byte[] frame = new byte[4 + length];
            frame[0] = HEADER_0;
            frame[1] = HEADER_1;
            frame[2] = (byte)(length >> 8);
            frame[3] = (byte)(length & 0xFF);
            frame[4] = command;
            Array.Copy(data, 0, frame, 5, data.Length);

            ushort crc = CalculateChecksum(frame, 0, frame.Length - 2);
            frame[frame.Length - 2] = (byte)(crc >> 8);
            frame[frame.Length - 1] = (byte)(crc & 0xFF);
            return frame;
        }

        private static OperateResult<byte[]> ParseResponse(byte[] response, byte expectedCmd)
        {
            if (response == null || response.Length < 7)
                return OperateResult<byte[]>.Failed($"响应帧过短 ({response?.Length ?? 0} 字节)");

            if (response[0] != HEADER_0 || response[1] != HEADER_1)
                return OperateResult<byte[]>.Failed("帧头不匹配");

            int length = (response[2] << 8) | response[3];
            if (response.Length < 4 + length)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            ushort crc = CalculateChecksum(response, 0, response.Length - 2);
            ushort recvCrc = (ushort)((response[response.Length - 2] << 8) | response[response.Length - 1]);
            if (crc != recvCrc)
                return OperateResult<byte[]>.Failed($"校验和不匹配: 计算 0x{crc:X4}, 接收 0x{recvCrc:X4}");

            byte status = response[4];
            if ((status & RESP_ERROR) != 0)
                return OperateResult<byte[]>.Failed($"设备错误: 0x{status:X2}", status);

            if (response.Length > 5)
            {
                byte[] data = new byte[response.Length - 7];
                Array.Copy(response, 5, data, 0, data.Length);
                return OperateResult<byte[]>.Success(data);
            }

            return OperateResult<byte[]>.Success(new byte[0]);
        }

        private static ushort CalculateChecksum(byte[] data, int offset, int count)
        {
            ushort sum = 0;
            for (int i = offset; i < offset + count; i++)
                sum += data[i];
            return sum;
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            if (!ushort.TryParse(address, out ushort regAddr))
                return OperateResult<short>.Failed($"地址格式错误: {address}");

            var r = ReadRegisters(regAddr, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足 2 字节");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            if (!ushort.TryParse(address, out ushort regAddr))
                return OperateResult<ushort>.Failed($"地址格式错误: {address}");

            var r = ReadRegisters(regAddr, 1);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("响应数据不足 2 字节");
            return OperateResult<ushort>.Success((ushort)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            if (!ushort.TryParse(address, out ushort regAddr))
                return OperateResult<int>.Failed($"地址格式错误: {address}");

            var r = ReadRegisters(regAddr, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足 4 字节");
            return OperateResult<int>.Success(
                (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            if (!ushort.TryParse(address, out ushort regAddr))
                return OperateResult<uint>.Failed($"地址格式错误: {address}");

            var r = ReadRegisters(regAddr, 2);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<uint>.Failed("响应数据不足 4 字节");
            return OperateResult<uint>.Success(
                (uint)((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]));
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            if (!ushort.TryParse(address, out ushort regAddr))
                return OperateResult<float>.Failed($"地址格式错误: {address}");

            var r = ReadRegisters(regAddr, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足 4 字节");
            int bits = (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3];
            return OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(bits), 0));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            if (!ushort.TryParse(address, out ushort regAddr))
                return OperateResult<byte[]>.Failed($"地址格式错误: {address}");

            ushort regCount = (ushort)((length + 1) / 2);
            return ReadRegisters(regAddr, regCount);
        }

        public override OperateResult Write(string address, short value)
            => Write(address, (ushort)(ushort)value);

        public override OperateResult Write(string address, ushort value)
        {
            if (!ushort.TryParse(address, out ushort regAddr))
                return OperateResult.Failed($"地址格式错误: {address}");

            byte[] data = new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) };
            return WriteRegisters(regAddr, data);
        }

        public override OperateResult Write(string address, int value)
        {
            byte[] data = BitConverter.GetBytes(value);
            Array.Reverse(data);
            return WriteRegisters(ushort.Parse(address), data);
        }

        public override OperateResult Write(string address, uint value)
        {
            byte[] data = BitConverter.GetBytes(value);
            Array.Reverse(data);
            return WriteRegisters(ushort.Parse(address), data);
        }

        public override OperateResult Write(string address, float value)
        {
            byte[] data = BitConverter.GetBytes(value);
            Array.Reverse(data);
            return WriteRegisters(ushort.Parse(address), data);
        }

        public override OperateResult Write(string address, byte[] data)
            => WriteRegisters(ushort.Parse(address), data);

        public override string ToString() => $"ToyoPucClient[{Ip}:{Port}]";

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
                var r = ReadBytes(addr, 2);
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
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
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
