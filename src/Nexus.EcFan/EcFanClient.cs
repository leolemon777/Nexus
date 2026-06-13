using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.EcFan
{
    public class EcFanClient : SerialDeviceBase, IBatchReadWrite
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public byte Station { get; set; }
        private readonly object _serialLock = new object();

        public EcFanClient(ISerialPort serialPort, byte station = 1, int timeout = 1000)
            : base(serialPort, timeout)
        {
            Station = station;
        }

        public OperateResult<ushort> ReadSpeed()
        {
            byte[] cmd = BuildCommand(0x03, 0x0000, 1);
            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult<ushort>.Failed(result.Message);
            return ParseU16Response(result.Content);
        }

        public OperateResult SetSpeed(ushort speed)
        {
            byte[] cmd = BuildCommand(0x06, 0x0001, speed);
            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);
            return OperateResult.Success();
        }

        public OperateResult<ushort> ReadStatus()
        {
            byte[] cmd = BuildCommand(0x03, 0x0002, 1);
            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult<ushort>.Failed(result.Message);
            return ParseU16Response(result.Content);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadUInt16(address);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            ushort addr = ushort.Parse(address);
            byte[] cmd = BuildCommand(0x03, addr, 1);
            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message);
            var parsed = ParseU16Response(result.Content);
            if (!parsed.IsSuccess) return OperateResult<short>.Failed(parsed.Message);
            return OperateResult<short>.Success((short)parsed.Content);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            ushort addr = ushort.Parse(address);
            byte[] cmd = BuildCommand(0x03, addr, 1);
            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult<ushort>.Failed(result.Message);
            return ParseU16Response(result.Content);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadUInt16(address);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success((int)r.Content);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadUInt16(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadUInt16(address);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success((long)r.Content);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadUInt16(address);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success((ulong)r.Content);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadSpeed();
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success((float)r.Content);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadSpeed();
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success((double)r.Content);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadSpeed();
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(r.Content.ToString());
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadUInt16(address);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            return OperateResult<byte[]>.Success(new byte[] { (byte)(r.Content >> 8), (byte)(r.Content & 0xFF) });
        }

        public override OperateResult Write(string address, bool value)
            => Write(address, (ushort)(value ? 1 : 0));

        public override OperateResult Write(string address, short value)
        {
            ushort addr = ushort.Parse(address);
            byte[] cmd = BuildCommand(0x06, addr, (ushort)value);
            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, ushort value)
        {
            ushort addr = ushort.Parse(address);
            byte[] cmd = BuildCommand(0x06, addr, value);
            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, int value)
            => Write(address, (ushort)value);

        public override OperateResult Write(string address, uint value)
            => Write(address, (ushort)value);

        public override OperateResult Write(string address, long value)
            => Write(address, (ushort)value);

        public override OperateResult Write(string address, ulong value)
            => Write(address, (ushort)value);

        public override OperateResult Write(string address, float value)
            => Write(address, (ushort)value);

        public override OperateResult Write(string address, double value)
            => Write(address, (ushort)value);

        public override OperateResult Write(string address, string value)
        {
            if (!ushort.TryParse(value, out ushort v))
                return OperateResult.Failed($"无法解析写入值: {value}");
            return Write(address, v);
        }

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("EC 风机不支持字节数组写入");

        private byte[] BuildCommand(byte fc, ushort register, ushort value)
        {
            byte[] cmd = new byte[8];
            cmd[0] = Station;
            cmd[1] = fc;
            cmd[2] = (byte)(register >> 8);
            cmd[3] = (byte)(register & 0xFF);
            cmd[4] = (byte)(value >> 8);
            cmd[5] = (byte)(value & 0xFF);
            ushort crc = CrcCalculator.ComputeCrc16(cmd, 0, 6);
            cmd[6] = (byte)(crc & 0xFF);
            cmd[7] = (byte)((crc >> 8) & 0xFF);
            return cmd;
        }

        private OperateResult<byte[]> SendCommand(byte[] cmd)
        {
            lock (_serialLock)
            {
                try
                {
                    RaiseMessageSent(DataConverter.ToHexString(cmd));
                    Port.Write(cmd, 0, cmd.Length);

                    if (InterFrameDelay > 0)
                        System.Threading.Thread.Sleep(InterFrameDelay);

                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[256];
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        int read = Port.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            for (int i = 0; i < read; i++)
                                response.Add(buf[i]);

                            if (response.Count >= 5)
                            {
                                byte[] resp = response.ToArray();
                                RaiseMessageReceived(DataConverter.ToHexString(resp));

                                if (resp[0] != Station)
                                    return OperateResult<byte[]>.Failed("站号不匹配");

                                if ((resp[1] & 0x80) != 0)
                                    return OperateResult<byte[]>.Failed($"异常码: {resp[2]}");

                                return OperateResult<byte[]>.Success(resp);
                            }
                        }
                    }

                    return OperateResult<byte[]>.Failed($"EcFan 响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"EcFan 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"EcFan 通讯异常: {ex.Message}");
                }
            }
        }

        private static OperateResult<ushort> ParseU16Response(byte[] resp)
        {
            if (resp.Length < 5) return OperateResult<ushort>.Failed("响应过短");
            ushort val = (ushort)((resp[3] << 8) | resp[4]);
            return OperateResult<ushort>.Success(val);
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
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        public override string ToString() => $"EcFanClient[Station={Station}]";
    }
}
