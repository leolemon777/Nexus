using System;
using System.Text;

namespace Nexus.Sam
{
    public class SamTcpClient : TcpDeviceBase
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public SamTcpClient(string ip, int port = 5000, int timeout = 3000)
            : base(ip, port, timeout) { }

        public OperateResult<string> ReadIdCard()
        {
            byte[] cmd = new byte[] { 0xAA, 0xAA, 0xAA, 0x96, 0x69, 0x00, 0x03, 0x20, 0x00, 0x22 };
            var result = SendCustomMessage(cmd);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message);

            byte[] resp = result.Content;
            if (resp.Length < 10) return OperateResult<string>.Failed("响应数据过短");

            if (resp[5] == 0x01 && resp[6] == 0x01 && resp[7] == 0x8F)
                return OperateResult<string>.Failed("未检测到身份证");

            if (resp.Length >= 18)
            {
                byte[] idBytes = new byte[8];
                Array.Copy(resp, 10, idBytes, 0, 8);
                string id = BitConverter.ToString(idBytes).Replace("-", "");
                return OperateResult<string>.Success(id);
            }

            return OperateResult<string>.Success(DataConverter.ToHexString(resp));
        }

        public override OperateResult<bool> ReadBool(string address)
            => OperateResult<bool>.Failed("SAM 身份证读卡器不支持布尔读取，请使用 ReadString");

        public override OperateResult<short> ReadInt16(string address)
            => OperateResult<short>.Failed("SAM 身份证读卡器不支持整数读取，请使用 ReadString");

        public override OperateResult<ushort> ReadUInt16(string address)
            => OperateResult<ushort>.Failed("SAM 身份证读卡器不支持整数读取，请使用 ReadString");

        public override OperateResult<int> ReadInt32(string address)
            => OperateResult<int>.Failed("SAM 身份证读卡器不支持整数读取，请使用 ReadString");

        public override OperateResult<uint> ReadUInt32(string address)
            => OperateResult<uint>.Failed("SAM 身份证读卡器不支持整数读取，请使用 ReadString");

        public override OperateResult<long> ReadInt64(string address)
            => OperateResult<long>.Failed("SAM 身份证读卡器不支持整数读取，请使用 ReadString");

        public override OperateResult<ulong> ReadUInt64(string address)
            => OperateResult<ulong>.Failed("SAM 身份证读卡器不支持整数读取，请使用 ReadString");

        public override OperateResult<float> ReadFloat(string address)
            => OperateResult<float>.Failed("SAM 身份证读卡器不支持浮点读取，请使用 ReadString");

        public override OperateResult<double> ReadDouble(string address)
            => OperateResult<double>.Failed("SAM 身份证读卡器不支持浮点读取，请使用 ReadString");

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadIdCard();
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(r.Content);
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadIdCard();
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(r.Content));
        }

        public override OperateResult Write(string address, bool value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, short value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, ushort value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, int value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, uint value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, long value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, ulong value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, float value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, double value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("SAM 身份证读卡器不支持写入操作");

        public override string ToString() => $"SamTcpClient[{Ip}:{Port}]";
    }
}
