using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Dlt
{
    /// <summary>
    /// DLT645-1997 电能表通讯协议客户端（旧版协议）。
    /// <para>帧格式: 68H + A0..A5(地址) + 68H + C(控制) + L(长度) + DATA + CS + 16H</para>
    /// <para>与 2007 版的主要区别：数据标识为 2 字节（DI1 DI0），无 33H 加密。</para>
    /// </summary>
    public class Dlt1997Client : SerialDeviceBase, IBatchReadWrite
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        private const byte FRAME_HEADER = 0x68;
        private const byte FRAME_END = 0x16;

        private const byte CTRL_READ_DATA = 0x01;
        private const byte CTRL_WRITE_DATA = 0x04;
        private const byte CTRL_BROADCAST = 0x08;

        public byte[] MeterAddress { get; set; } = new byte[6];

        private readonly object _serialLock = new object();

        public Dlt1997Client(ISerialPort serialPort, int timeout = 5000)
            : base(serialPort, timeout) { }

        public void SetMeterAddress(string address12)
        {
            if (address12 == null || address12.Length != 12)
                throw new ArgumentException("电表地址必须为 12 位数字");
            for (int i = 0; i < 6; i++)
            {
                string pair = address12.Substring(10 - i * 2, 2);
                MeterAddress[i] = byte.Parse(pair);
            }
        }

        public OperateResult<byte[]> ReadData(byte[] dataId)
        {
            if (dataId == null || dataId.Length != 2)
                return OperateResult<byte[]>.Failed("数据标识必须为 2 字节");

            var frame = BuildFrame(CTRL_READ_DATA, dataId, null);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            return ParseResponse(recv.Content, CTRL_READ_DATA);
        }

        public OperateResult<byte[]> ReadData(string dataIdStr)
        {
            var id = ParseDataId(dataIdStr);
            if (id == null) return OperateResult<byte[]>.Failed($"数据标识格式错误: {dataIdStr}");
            return ReadData(id);
        }

        public OperateResult WriteData(byte[] dataId, byte[] data)
        {
            if (dataId == null || dataId.Length != 2)
                return OperateResult.Failed("数据标识必须为 2 字节");

            byte[] payload = new byte[dataId.Length + data.Length];
            Array.Copy(dataId, 0, payload, 0, dataId.Length);
            Array.Copy(data, 0, payload, dataId.Length, data.Length);

            var frame = BuildFrame(CTRL_WRITE_DATA, dataId, data);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = ParseResponse(recv.Content, CTRL_WRITE_DATA);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足 2 字节");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("响应数据不足 2 字节");
            return OperateResult<ushort>.Success((ushort)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足 4 字节");
            return OperateResult<int>.Success(
                (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<uint>.Failed("响应数据不足 4 字节");
            return OperateResult<uint>.Success(
                (uint)((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]));
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足 8 字节");
            long val = 0;
            for (int i = 0; i < 8; i++) val = (val << 8) | r.Content[i];
            return OperateResult<long>.Success(val);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            if (r.Content.Length < 8) return OperateResult<ulong>.Failed("响应数据不足 8 字节");
            ulong val = 0;
            for (int i = 0; i < 8; i++) val = (val << 8) | r.Content[i];
            return OperateResult<ulong>.Success(val);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足 4 字节");
            int bits = (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3];
            return OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(bits), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadFloat(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success((double)r.Content);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(DataConverter.ToHexString(r.Content));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
            => ReadData(address);

        public override OperateResult Write(string address, bool value)
            => Write(address, new byte[] { (byte)(value ? 1 : 0) });

        public override OperateResult Write(string address, short value)
            => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, ushort value)
            => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, int value)
            => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, uint value)
            => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, long value)
        {
            byte[] data = new byte[8];
            for (int i = 7; i >= 0; i--) { data[i] = (byte)(value & 0xFF); value >>= 8; }
            return Write(address, data);
        }

        public override OperateResult Write(string address, ulong value)
        {
            byte[] data = new byte[8];
            for (int i = 7; i >= 0; i--) { data[i] = (byte)(value & 0xFF); value >>= 8; }
            return Write(address, data);
        }

        public override OperateResult Write(string address, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return Write(address, bytes);
        }

        public override OperateResult Write(string address, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return Write(address, bytes);
        }

        public override OperateResult Write(string address, string value)
            => Write(address, Encoding.ASCII.GetBytes(value));

        public override OperateResult Write(string address, byte[] data)
        {
            var id = ParseDataId(address);
            if (id == null) return OperateResult.Failed($"数据标识格式错误: {address}");
            return WriteData(id, data);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
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
                var r = ReadBytes(addr, 0);
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

        public override string ToString() => $"Dlt1997Client[Addr={BitConverter.ToString(MeterAddress)}]";

        private byte[] BuildFrame(byte control, byte[] dataId, byte[]? data)
        {
            int dataLen = dataId.Length + (data?.Length ?? 0);
            byte[] dataField = new byte[dataLen];
            Array.Copy(dataId, 0, dataField, 0, dataId.Length);
            if (data != null) Array.Copy(data, 0, dataField, dataId.Length, data.Length);

            byte cs = 0;
            for (int i = 0; i < 6; i++) cs += MeterAddress[i];
            cs += control;
            cs += (byte)dataLen;
            for (int i = 0; i < dataLen; i++) cs += dataField[i];

            byte[] frame = new byte[12 + dataLen];
            frame[0] = FRAME_HEADER;
            Array.Copy(MeterAddress, 0, frame, 1, 6);
            frame[7] = FRAME_HEADER;
            frame[8] = control;
            frame[9] = (byte)dataLen;
            Array.Copy(dataField, 0, frame, 10, dataLen);
            frame[10 + dataLen] = cs;
            frame[11 + dataLen] = FRAME_END;
            return frame;
        }

        public static OperateResult<byte[]> ParseResponse(byte[] response, byte expectedCtrl)
        {
            if (response == null || response.Length < 12)
                return OperateResult<byte[]>.Failed($"响应帧过短 ({response?.Length ?? 0} 字节)");

            if (response[0] != FRAME_HEADER || response[7] != FRAME_HEADER)
                return OperateResult<byte[]>.Failed("帧头不匹配");
            if (response[response.Length - 1] != FRAME_END)
                return OperateResult<byte[]>.Failed("帧尾不匹配");

            byte ctrl = response[8];
            byte dataLen = response[9];

            if ((ctrl & 0x80) != 0)
                return OperateResult<byte[]>.Failed("电表返回错误");

            if (response.Length < 10 + dataLen + 2)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            byte cs = 0;
            for (int i = 1; i < 7; i++) cs += response[i];
            cs += ctrl;
            cs += dataLen;
            for (int i = 0; i < dataLen; i++) cs += response[10 + i];

            if (cs != response[10 + dataLen])
                return OperateResult<byte[]>.Failed("校验和不匹配");

            if (dataLen > 2)
            {
                byte[] pureData = new byte[dataLen - 2];
                Array.Copy(response, 12, pureData, 0, pureData.Length);
                return OperateResult<byte[]>.Success(pureData);
            }

            return OperateResult<byte[]>.Success(new byte[0]);
        }

        private OperateResult<byte[]> SendAndReceiveSerial(byte[] frame)
        {
            lock (_serialLock)
            {
                try
                {
                    RaiseMessageSent(DataConverter.ToHexString(frame));
                    Port.Write(frame, 0, frame.Length);

                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[256];
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        int read = Port.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                response.Add(buf[i]);
                                if (buf[i] == FRAME_END && response.Count >= 12 && response[0] == FRAME_HEADER)
                                {
                                    byte[] result = response.ToArray();
                                    RaiseMessageReceived(DataConverter.ToHexString(result));
                                    return OperateResult<byte[]>.Success(result);
                                }
                            }
                        }
                    }

                    return OperateResult<byte[]>.Failed($"DLT1997 响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"DLT1997 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"DLT1997 通讯异常: {ex.Message}");
                }
            }
        }

        public static byte[]? ParseDataId(string dataIdStr)
        {
            if (string.IsNullOrEmpty(dataIdStr) || dataIdStr.Length != 4)
                return null;
            try
            {
                return new byte[]
                {
                    Convert.ToByte(dataIdStr.Substring(2, 2), 16),
                    Convert.ToByte(dataIdStr.Substring(0, 2), 16)
                };
            }
            catch { return null; }
        }
    }
}
