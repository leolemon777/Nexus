using System;
using System.Text;

namespace Nexus.Sam
{
    public class SamSerialClient : SerialDeviceBase
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        private readonly object _serialLock = new object();

        public SamSerialClient(ISerialPort serialPort, int timeout = 3000)
            : base(serialPort, timeout) { }

        public OperateResult<string> ReadIdCard()
        {
            byte[] cmd = new byte[] { 0xAA, 0xAA, 0xAA, 0x96, 0x69, 0x00, 0x03, 0x20, 0x00, 0x22 };

            lock (_serialLock)
            {
                try
                {
                    RaiseMessageSent(DataConverter.ToHexString(cmd));
                    Port.Write(cmd, 0, cmd.Length);

                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[256];
                    int start = Environment.TickCount;

                    while (unchecked(Environment.TickCount - start) < Timeout)
                    {
                        int read = Port.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            for (int i = 0; i < read; i++)
                                response.Add(buf[i]);

                            if (response.Count >= 10)
                            {
                                byte[] resp = response.ToArray();
                                RaiseMessageReceived(DataConverter.ToHexString(resp));

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
                        }
                    }

                    return OperateResult<string>.Failed($"SAM 响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"SAM 通讯异常: {ex.Message}");
                    return OperateResult<string>.Failed($"SAM 通讯异常: {ex.Message}");
                }
            }
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

        public override string ToString() => $"SamSerialClient[{Port?.PortName}]";
    }
}
