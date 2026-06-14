using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Dlt
{
    /// <summary>
    /// DLT698.45-2017 电能表通讯协议客户端（面向对象协议）。
    /// <para>帧格式: 68H + Length(2) + Control(1) + Address(1-7) + HCS(2) + APDU + FCS(2) + 16H</para>
    /// <para>与 DLT645 不同，DLT698 采用面向对象的编码方式（COSEM/OBIS）。</para>
    /// </summary>
    public class Dlt698Client : SerialDeviceBase, IBatchReadWrite
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        private const byte FRAME_HEADER = 0x68;
        private const byte FRAME_END = 0x16;

        private const byte APDU_GET_REQUEST = 0xC0;
        private const byte APDU_GET_RESPONSE = 0xC1;
        private const byte APDU_SET_REQUEST = 0xC4;
        private const byte APDU_SET_RESPONSE = 0xC5;
        private const byte APDU_ACTION_REQUEST = 0xC8;
        private const byte APDU_ACTION_RESPONSE = 0xC9;

        public byte[] ServerAddress { get; set; } = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        public byte ClientAddress { get; set; } = 0x00;

        private readonly object _serialLock = new object();

        public Dlt698Client(ISerialPort serialPort, int timeout = 5000)
            : base(serialPort, timeout) { }

        public void SetServerAddress(string address12)
        {
            if (address12 == null || address12.Length != 12)
                throw new ArgumentException("服务器地址必须为 12 位数字");
            ServerAddress = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                string pair = address12.Substring(10 - i * 2, 2);
                ServerAddress[i] = byte.Parse(pair);
            }
        }

        public OperateResult<byte[]> GetRequest(byte[] oad)
        {
            if (oad == null || oad.Length != 4)
                return OperateResult<byte[]>.Failed("OAD 必须为 4 字节");

            byte[] apdu = new byte[5];
            apdu[0] = APDU_GET_REQUEST;
            apdu[1] = 0x01;
            apdu[2] = 0x01;
            Array.Copy(oad, 0, apdu, 3, 4);

            var frame = BuildFrame(apdu);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            return ParseResponse(recv.Content, APDU_GET_RESPONSE);
        }

        public OperateResult SetRequest(byte[] oad, byte[] data)
        {
            if (oad == null || oad.Length != 4)
                return OperateResult.Failed("OAD 必须为 4 字节");

            byte[] apdu = new byte[5 + data.Length];
            apdu[0] = APDU_SET_REQUEST;
            apdu[1] = 0x01;
            apdu[2] = 0x01;
            Array.Copy(oad, 0, apdu, 3, 4);
            Array.Copy(data, 0, apdu, 7, data.Length);

            var frame = BuildFrame(apdu);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = ParseResponse(recv.Content, APDU_SET_RESPONSE);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        public OperateResult<byte[]> ActionRequest(byte[] omd, byte[] data)
        {
            if (omd == null || omd.Length != 4)
                return OperateResult<byte[]>.Failed("OMD 必须为 4 字节");

            byte[] apdu = new byte[5 + data.Length];
            apdu[0] = APDU_ACTION_REQUEST;
            apdu[1] = 0x01;
            apdu[2] = 0x01;
            Array.Copy(omd, 0, apdu, 3, 4);
            Array.Copy(data, 0, apdu, 7, data.Length);

            var frame = BuildFrame(apdu);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            return ParseResponse(recv.Content, APDU_ACTION_RESPONSE);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content != 0);
        }

        public override OperateResult<short> ReadInt16(string oadStr)
        {
            var oad = ParseOad(oadStr);
            if (oad == null) return OperateResult<short>.Failed($"OAD 格式错误: {oadStr}");
            var r = GetRequest(oad);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足 2 字节");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult<int> ReadInt32(string oadStr)
        {
            var oad = ParseOad(oadStr);
            if (oad == null) return OperateResult<int>.Failed($"OAD 格式错误: {oadStr}");
            var r = GetRequest(oad);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足 4 字节");
            return OperateResult<int>.Success(
                (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success((uint)r.Content);
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
        {
            var oad = ParseOad(address);
            if (oad == null) return OperateResult<byte[]>.Failed($"OAD 格式错误: {address}");
            return GetRequest(oad);
        }

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
            var oad = ParseOad(address);
            if (oad == null) return OperateResult.Failed($"OAD 格式错误: {address}");
            return SetRequest(oad, data);
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

        public override string ToString() => $"Dlt698Client[Addr={BitConverter.ToString(ServerAddress)}]";

        private byte[] BuildFrame(byte[] apdu)
        {
            int length = 2 + ServerAddress.Length + 2 + apdu.Length + 2;
            byte[] frame = new byte[length + 2];
            int idx = 0;
            frame[idx++] = FRAME_HEADER;
            frame[idx++] = (byte)(length >> 8);
            frame[idx++] = (byte)(length & 0xFF);
            frame[idx++] = ClientAddress;
            Array.Copy(ServerAddress, 0, frame, idx, ServerAddress.Length);
            idx += ServerAddress.Length;

            ushort hcs = Crc16(frame, 1, idx - 1);
            frame[idx++] = (byte)(hcs & 0xFF);
            frame[idx++] = (byte)(hcs >> 8);

            Array.Copy(apdu, 0, frame, idx, apdu.Length);
            idx += apdu.Length;

            ushort fcs = Crc16(frame, 1, idx - 1);
            frame[idx++] = (byte)(fcs & 0xFF);
            frame[idx++] = (byte)(fcs >> 8);
            frame[idx++] = FRAME_END;

            return frame;
        }

        private static OperateResult<byte[]> ParseResponse(byte[] response, byte expectedApduType)
        {
            if (response == null || response.Length < 12)
                return OperateResult<byte[]>.Failed($"响应帧过短 ({response?.Length ?? 0} 字节)");

            if (response[0] != FRAME_HEADER)
                return OperateResult<byte[]>.Failed("帧头不匹配");

            int apduStart = 3 + 6 + 2;
            if (response.Length <= apduStart)
                return OperateResult<byte[]>.Failed("APDU 数据不足");

            byte apduType = response[apduStart];
            if (apduType == 0xD0 || apduType == 0xD1)
                return OperateResult<byte[]>.Failed($"设备错误: 0x{apduType:X2}");

            if (response.Length > apduStart + 5)
            {
                byte[] data = new byte[response.Length - apduStart - 5];
                Array.Copy(response, apduStart + 5, data, 0, data.Length);
                return OperateResult<byte[]>.Success(data);
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

                    var response = new List<byte>();
                    byte[] buf = new byte[1024];
                    int start = Environment.TickCount;

                    while (unchecked(Environment.TickCount - start) < Timeout)
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

                    return OperateResult<byte[]>.Failed($"DLT698 响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"DLT698 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"DLT698 通讯异常: {ex.Message}");
                }
            }
        }

        public static byte[]? ParseOad(string oadStr)
        {
            if (string.IsNullOrEmpty(oadStr) || oadStr.Length != 8)
                return null;
            try
            {
                return new byte[]
                {
                    Convert.ToByte(oadStr.Substring(0, 2), 16),
                    Convert.ToByte(oadStr.Substring(2, 2), 16),
                    Convert.ToByte(oadStr.Substring(4, 2), 16),
                    Convert.ToByte(oadStr.Substring(6, 2), 16)
                };
            }
            catch { return null; }
        }

        private static ushort Crc16(byte[] data, int offset, int count)
        {
            // C2 修复：DL/T 698.45 使用多项式 x16+x12+x5+1 反射形式 0x8408（CCITT/X.25），
            // 初值 0xFFFF，XorOut 0xFFFF。原用 0xA001(Modbus) 且无 XorOut，导致所有 698 帧
            // HCS/FCS 校验错误，无法对接标准 698 设备。
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                        crc = (ushort)((crc >> 1) ^ 0x8408);
                    else
                        crc >>= 1;
                }
            }
            return (ushort)(crc ^ 0xFFFF);
        }
    }
}
