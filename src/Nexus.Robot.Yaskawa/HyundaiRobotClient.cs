using System;

namespace Nexus.Robot.Yaskawa
{
    public class HyundaiRobotClient : UdpDeviceBase
    {
        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            return (header[2] << 8) | header[3];
        }

        public HyundaiRobotClient(string ip, int port = 10000, int timeout = 3000)
            : base(ip, port, timeout) { }

        public OperateResult<int> ReadCurrentPosition(int axis = 0)
        {
            byte[] cmd = new byte[] { 0x01, 0x01, 0x00, 0x02, (byte)(axis >> 8), (byte)(axis & 0xFF) };
            var result = SendAndReceive(cmd);
            if (!result.IsSuccess) return OperateResult<int>.Failed(result.Message);

            byte[] resp = result.Content;
            if (resp.Length < 8) return OperateResult<int>.Failed("响应过短");

            int pos = (resp[4] << 24) | (resp[5] << 16) | (resp[6] << 8) | resp[7];
            return OperateResult<int>.Success(pos);
        }

        public OperateResult<byte> ReadRobotState()
        {
            byte[] cmd = new byte[] { 0x01, 0x02, 0x00, 0x01, 0x00, 0x01 };
            var result = SendAndReceive(cmd);
            if (!result.IsSuccess) return OperateResult<byte>.Failed(result.Message);

            byte[] resp = result.Content;
            if (resp.Length < 5) return OperateResult<byte>.Failed("响应过短");
            return OperateResult<byte>.Success(resp[4]);
        }

        public OperateResult<byte> ReadAlarmCode()
        {
            byte[] cmd = new byte[] { 0x01, 0x03, 0x00, 0x01, 0x00, 0x01 };
            var result = SendAndReceive(cmd);
            if (!result.IsSuccess) return OperateResult<byte>.Failed(result.Message);

            byte[] resp = result.Content;
            if (resp.Length < 5) return OperateResult<byte>.Failed("响应过短");
            return OperateResult<byte>.Success(resp[4]);
        }

        public OperateResult StartProgram()
        {
            byte[] cmd = new byte[] { 0x02, 0x01, 0x00, 0x01, 0x00, 0x01 };
            var result = SendAndReceive(cmd);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);
            return OperateResult.Success();
        }

        public OperateResult StopProgram()
        {
            byte[] cmd = new byte[] { 0x02, 0x02, 0x00, 0x01, 0x00, 0x01 };
            var result = SendAndReceive(cmd);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);
            return OperateResult.Success();
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            ushort addr = ushort.Parse(address);
            byte[] cmd = new byte[] { 0x01, 0x04, (byte)(addr >> 8), (byte)(addr & 0xFF), 0x00, 0x01 };
            var result = SendAndReceive(cmd);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message);

            byte[] resp = result.Content;
            if (resp.Length < 6) return OperateResult<short>.Failed("响应过短");
            return OperateResult<short>.Success((short)((resp[4] << 8) | resp[5]));
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadRobotState();
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content != 0);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            if (string.Equals(address, "pos", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(address, "position", StringComparison.OrdinalIgnoreCase))
            {
                return ReadCurrentPosition();
            }
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success((int)r.Content);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success((long)r.Content);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success((ulong)r.Content);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success((float)r.Content);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success((double)r.Content);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            if (string.Equals(address, "state", StringComparison.OrdinalIgnoreCase))
            {
                var r = ReadRobotState();
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
                return OperateResult<string>.Success($"0x{r.Content:X2}");
            }
            if (string.Equals(address, "alarm", StringComparison.OrdinalIgnoreCase))
            {
                var r = ReadAlarmCode();
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
                return OperateResult<string>.Success($"0x{r.Content:X2}");
            }
            var pos = ReadCurrentPosition();
            if (!pos.IsSuccess) return OperateResult<string>.Failed(pos.Message);
            return OperateResult<string>.Success(pos.Content.ToString());
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            return OperateResult<byte[]>.Success(new byte[] { (byte)(r.Content >> 8), (byte)(r.Content & 0xFF) });
        }

        public override OperateResult Write(string address, bool value)
            => OperateResult.Failed("现代机器人不支持写入操作，请使用 StartProgram/StopProgram");

        public override OperateResult Write(string address, short value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, ushort value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, int value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, uint value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, long value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, ulong value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, float value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, double value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("现代机器人不支持写入操作");

        public override string ToString() => $"HyundaiRobotClient[{Ip}:{Port}]";
    }
}
