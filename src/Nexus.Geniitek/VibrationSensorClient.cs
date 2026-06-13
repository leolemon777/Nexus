using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Geniitek
{
    /// <summary>
    /// Geniitek 振动传感器 TCP 通讯客户端。
    /// <para>帧格式: Header(2) + Length(2) + Command(1) + Data(N) + Checksum(1)</para>
    /// <para>Header = 0xAA 0x55, Checksum = XOR(Length..Data)</para>
    /// <para>支持读取加速度、速度、温度、电池电量等。</para>
    /// </summary>
    public class VibrationSensorClient : TcpDeviceBase
    {
        private const byte HEADER_0 = 0xAA;
        private const byte HEADER_1 = 0x55;

        private const byte CMD_READ_ACCEL = 0x01;
        private const byte CMD_READ_VELOCITY = 0x02;
        private const byte CMD_READ_TEMP = 0x03;
        private const byte CMD_READ_BATTERY = 0x04;
        private const byte CMD_READ_STATUS = 0x05;
        private const byte CMD_READ_CONFIG = 0x06;
        private const byte CMD_WRITE_CONFIG = 0x07;

        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            int length = (header[2] << 8) | header[3];
            return length > 0 ? length : 0;
        }

        public VibrationSensorClient(string ip, int port, int timeout = 5000)
            : base(ip, port, timeout) { }

        public OperateResult<VibrationData> ReadAcceleration()
        {
            var result = SendCommand(CMD_READ_ACCEL);
            if (!result.IsSuccess)
                return OperateResult<VibrationData>.Failed(result.Message);
            return ParseVibrationData(result.Content);
        }

        public OperateResult<VelocityData> ReadVelocity()
        {
            var result = SendCommand(CMD_READ_VELOCITY);
            if (!result.IsSuccess)
                return OperateResult<VelocityData>.Failed(result.Message);
            return ParseVelocityData(result.Content);
        }

        public OperateResult<float> ReadTemperature()
        {
            var result = SendCommand(CMD_READ_TEMP);
            if (!result.IsSuccess) return OperateResult<float>.Failed(result.Message);
            if (result.Content.Length < 4)
                return OperateResult<float>.Failed("温度数据不足");
            int bits = (result.Content[0] << 24) | (result.Content[1] << 16) | (result.Content[2] << 8) | result.Content[3];
            return OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(bits), 0));
        }

        public OperateResult<byte> ReadBatteryLevel()
        {
            var result = SendCommand(CMD_READ_BATTERY);
            if (!result.IsSuccess) return OperateResult<byte>.Failed(result.Message);
            if (result.Content.Length < 1)
                return OperateResult<byte>.Failed("电池数据不足");
            return OperateResult<byte>.Success(result.Content[0]);
        }

        public OperateResult<SensorStatus> ReadStatus()
        {
            var result = SendCommand(CMD_READ_STATUS);
            if (!result.IsSuccess)
                return OperateResult<SensorStatus>.Failed(result.Message);
            return ParseSensorStatus(result.Content);
        }

        private OperateResult<byte[]> SendCommand(byte command)
        {
            byte[] frame = BuildFrame(command, new byte[0]);
            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);
            return ParseResponse(result.Content, command);
        }

        private byte[] BuildFrame(byte command, byte[] data)
        {
            int length = 1 + data.Length;
            byte[] frame = new byte[4 + length + 1];
            frame[0] = HEADER_0;
            frame[1] = HEADER_1;
            frame[2] = (byte)(length >> 8);
            frame[3] = (byte)(length & 0xFF);
            frame[4] = command;
            Array.Copy(data, 0, frame, 5, data.Length);
            frame[frame.Length - 1] = CalculateChecksum(frame);
            return frame;
        }

        private static OperateResult<byte[]> ParseResponse(byte[] response, byte expectedCmd)
        {
            if (response == null || response.Length < 6)
                return OperateResult<byte[]>.Failed($"响应帧过短 ({response?.Length ?? 0} 字节)");

            if (response[0] != HEADER_0 || response[1] != HEADER_1)
                return OperateResult<byte[]>.Failed("帧头不匹配");

            byte cmd = response[4];
            if ((cmd & 0x80) != 0)
                return OperateResult<byte[]>.Failed($"设备错误: 0x{cmd:X2}", cmd);

            int length = (response[2] << 8) | response[3];
            if (response.Length < 4 + length + 1)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            byte cs = CalculateChecksum(response, 0, response.Length - 1);
            if (cs != response[response.Length - 1])
                return OperateResult<byte[]>.Failed("校验和不匹配");

            byte[] data = new byte[length - 1];
            Array.Copy(response, 5, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        private static OperateResult<VibrationData> ParseVibrationData(byte[] data)
        {
            if (data.Length < 12)
                return OperateResult<VibrationData>.Failed("振动数据不足 12 字节");

            var vib = new VibrationData
            {
                X = ReadFloat(data, 0),
                Y = ReadFloat(data, 4),
                Z = ReadFloat(data, 8)
            };
            return OperateResult<VibrationData>.Success(vib);
        }

        private static OperateResult<VelocityData> ParseVelocityData(byte[] data)
        {
            if (data.Length < 12)
                return OperateResult<VelocityData>.Failed("速度数据不足 12 字节");

            var vel = new VelocityData
            {
                X = ReadFloat(data, 0),
                Y = ReadFloat(data, 4),
                Z = ReadFloat(data, 8)
            };
            return OperateResult<VelocityData>.Success(vel);
        }

        private static OperateResult<SensorStatus> ParseSensorStatus(byte[] data)
        {
            if (data.Length < 2)
                return OperateResult<SensorStatus>.Failed("状态数据不足");

            var status = new SensorStatus
            {
                BatteryLevel = data[0],
                ErrorCode = data[1],
                IsRunning = (data[1] & 0x01) == 0
            };
            return OperateResult<SensorStatus>.Success(status);
        }

        private static float ReadFloat(byte[] data, int offset)
        {
            int bits = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        private static byte CalculateChecksum(byte[] frame)
        {
            byte cs = 0;
            for (int i = 2; i < frame.Length - 1; i++)
                cs ^= frame[i];
            return cs;
        }

        private static byte CalculateChecksum(byte[] data, int offset, int count)
        {
            byte cs = 0;
            for (int i = offset; i < offset + count; i++)
                cs ^= data[i];
            return cs;
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success((short)r.Content);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
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
            if (string.Equals(address, "temp", StringComparison.OrdinalIgnoreCase))
                return ReadTemperature();

            var r = ReadAcceleration();
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return address.ToLowerInvariant() switch
            {
                "x" => OperateResult<float>.Success(r.Content.X),
                "y" => OperateResult<float>.Success(r.Content.Y),
                "z" => OperateResult<float>.Success(r.Content.Z),
                _ => OperateResult<float>.Failed($"未知地址: {address}")
            };
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadFloat(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success((double)r.Content);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            if (string.Equals(address, "status", StringComparison.OrdinalIgnoreCase))
            {
                var r = ReadStatus();
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
                return OperateResult<string>.Success($"Battery={r.Content.BatteryLevel}, Error=0x{r.Content.ErrorCode:X2}, Running={r.Content.IsRunning}");
            }
            var f = ReadFloat(address);
            if (!f.IsSuccess) return OperateResult<string>.Failed(f.Message);
            return OperateResult<string>.Success(f.Content.ToString("F4"));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadFloat(address);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            return OperateResult<byte[]>.Success(BitConverter.GetBytes(r.Content));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            if (string.Equals(address, "battery", StringComparison.OrdinalIgnoreCase))
            {
                var r = ReadBatteryLevel();
                return r.IsSuccess
                    ? OperateResult<int>.Success((int)r.Content)
                    : OperateResult<int>.Failed(r.Message, r.ErrorCode);
            }
            return OperateResult<int>.Failed($"不支持的地址: {address}");
        }

        public override OperateResult Write(string address, bool value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, short value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, ushort value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, int value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, uint value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, long value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, ulong value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, float value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, double value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("振动传感器不支持写入操作");

        public override string ToString() => $"VibrationSensorClient[{Ip}:{Port}]";
    }

    public class VibrationData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    public class VelocityData
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    public class SensorStatus
    {
        public int BatteryLevel { get; set; }
        public byte ErrorCode { get; set; }
        public bool IsRunning { get; set; }
    }
}
