using System;

namespace Nexus.Dlt
{
    public class ShineInLightClient : SerialDeviceBase
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public byte Station { get; set; }
        private readonly object _serialLock = new object();

        public ShineInLightClient(ISerialPort serialPort, byte station = 1, int timeout = 1000)
            : base(serialPort, timeout)
        {
            Station = station;
        }

        public OperateResult SetBrightness(byte channel, byte brightness)
        {
            byte[] cmd = new byte[8];
            cmd[0] = Station;
            cmd[1] = 0x06;
            cmd[2] = 0x00;
            cmd[3] = channel;
            cmd[4] = 0x00;
            cmd[5] = brightness;
            ushort crc = CrcCalculator.ComputeCrc16(cmd, 0, 6);
            cmd[6] = (byte)(crc & 0xFF);
            cmd[7] = (byte)((crc >> 8) & 0xFF);

            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);
            return OperateResult.Success();
        }

        public OperateResult<byte> GetBrightness(byte channel)
        {
            byte[] cmd = new byte[8];
            cmd[0] = Station;
            cmd[1] = 0x03;
            cmd[2] = 0x00;
            cmd[3] = channel;
            cmd[4] = 0x00;
            cmd[5] = 0x01;
            ushort crc = CrcCalculator.ComputeCrc16(cmd, 0, 6);
            cmd[6] = (byte)(crc & 0xFF);
            cmd[7] = (byte)((crc >> 8) & 0xFF);

            var result = SendCommand(cmd);
            if (!result.IsSuccess) return OperateResult<byte>.Failed(result.Message);

            byte[] resp = result.Content;
            if (resp.Length < 7) return OperateResult<byte>.Failed("响应过短");
            return OperateResult<byte>.Success(resp[4]);
        }

        public OperateResult SetLightOn(bool on)
        {
            return SetBrightness(0, on ? (byte)255 : (byte)0);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success((short)r.Content);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success((int)r.Content);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success((long)r.Content);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success((ulong)r.Content);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success((float)r.Content);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success((double)r.Content);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(r.Content.ToString());
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            byte channel = byte.Parse(address);
            var r = GetBrightness(channel);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            return OperateResult<byte[]>.Success(new byte[] { r.Content });
        }

        public override OperateResult Write(string address, bool value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, value ? (byte)255 : (byte)0);
        }

        public override OperateResult Write(string address, short value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, (byte)value);
        }

        public override OperateResult Write(string address, ushort value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, (byte)value);
        }

        public override OperateResult Write(string address, int value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, (byte)value);
        }

        public override OperateResult Write(string address, uint value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, (byte)value);
        }

        public override OperateResult Write(string address, long value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, (byte)value);
        }

        public override OperateResult Write(string address, ulong value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, (byte)value);
        }

        public override OperateResult Write(string address, float value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, (byte)value);
        }

        public override OperateResult Write(string address, double value)
        {
            byte channel = byte.Parse(address);
            return SetBrightness(channel, (byte)value);
        }

        public override OperateResult Write(string address, string value)
        {
            byte channel = byte.Parse(address);
            if (!byte.TryParse(value, out byte brightness))
                return OperateResult.Failed($"无法解析亮度值: {value}");
            return SetBrightness(channel, brightness);
        }

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("ShineIn 光源控制器不支持字节数组写入");

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

                            if (response.Count >= 8)
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

                    return OperateResult<byte[]>.Failed($"ShineIn 响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"ShineIn 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"ShineIn 通讯异常: {ex.Message}");
                }
            }
        }

        public override string ToString() => $"ShineInLightClient[Station={Station}]";
    }
}
