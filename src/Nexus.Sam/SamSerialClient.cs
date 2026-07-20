// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;
using System.Text;

namespace Nexus.Sam
{
    /// <summary>
    /// 身份证信息。
    /// </summary>
    public class IdentityCard
    {
        public string Name { get; set; } = string.Empty;
        public string Sex { get; set; } = string.Empty;
        public string Nation { get; set; } = string.Empty;
        public string Birthday { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string IdNumber { get; set; } = string.Empty;
        public string Issuer { get; set; } = string.Empty;
        public string ValidStart { get; set; } = string.Empty;
        public string ValidEnd { get; set; } = string.Empty;

        public override string ToString() => $"IdentityCard[{Name}, {IdNumber}]";

        public static IdentityCard Parse(byte[] data)
        {
            var card = new IdentityCard();
            if (data == null || data.Length < 256) return card;
            card.Name = DecodeUtf16(data, 0, 30).TrimEnd('\0', ' ');
            card.Sex = DecodeUtf16(data, 30, 2).TrimEnd('\0', ' ');
            card.Nation = DecodeUtf16(data, 32, 4).TrimEnd('\0', ' ');
            card.Birthday = DecodeUtf16(data, 36, 16).TrimEnd('\0', ' ');
            card.Address = DecodeUtf16(data, 52, 70).TrimEnd('\0', ' ');
            card.IdNumber = DecodeUtf16(data, 122, 36).TrimEnd('\0', ' ');
            if (data.Length >= 334)
            {
                card.Issuer = DecodeUtf16(data, 256, 30).TrimEnd('\0', ' ');
                card.ValidStart = DecodeUtf16(data, 286, 16).TrimEnd('\0', ' ');
                card.ValidEnd = DecodeUtf16(data, 302, 16).TrimEnd('\0', ' ');
            }
            return card;
        }

        private static string DecodeUtf16(byte[] data, int offset, int byteCount)
        {
            try { return Encoding.Unicode.GetString(data, offset, byteCount); }
            catch { return string.Empty; }
        }
    }

    /// <summary>
    /// 中国第二代身份证 SAM 读卡器串口客户端。
    /// </summary>
    public class SamSerialClient : SerialDeviceBase
    {
        public SamSerialClient(ISerialPort port, int timeout = 5000)
            : base(port, timeout)
        {
            InterFrameDelay = 50;
        }

        protected override int ResponseHeaderLength => 7;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 7) return 0;
            int len = (header[5] << 8) | header[6];
            return len - 2 + 1;
        }

        public static byte[] PackToSamCommand(byte[] command)
        {
            if (command == null) command = Array.Empty<byte>();
            byte[] frame = new byte[command.Length + 8];
            frame[0] = 0xAA;
            frame[1] = 0xAA;
            frame[2] = 0xAA;
            frame[3] = 0x96;
            frame[4] = 0x69;
            int len = frame.Length - 7;
            frame[5] = (byte)((len >> 8) & 0xFF);
            frame[6] = (byte)(len & 0xFF);
            Buffer.BlockCopy(command, 0, frame, 7, command.Length);
            int xor = 0;
            for (int i = 5; i < frame.Length - 1; i++)
                xor ^= frame[i];
            frame[frame.Length - 1] = (byte)xor;
            return frame;
        }

        public static byte[] BuildReadCommand(byte cmd, byte para, byte[]? data)
        {
            if (data == null) data = Array.Empty<byte>();
            byte[] result = new byte[2 + data.Length];
            result[0] = cmd;
            result[1] = para;
            Buffer.BlockCopy(data, 0, result, 2, data.Length);
            return result;
        }

        public static OperateResult CheckResponse(byte[] input)
        {
            if (input == null || input.Length < 8)
                return OperateResult.Failed("Response length < 8");
            if (input[0] != 0xAA || input[1] != 0xAA || input[2] != 0xAA
                || input[3] != 0x96 || input[4] != 0x69)
                return OperateResult.Failed("SAM header check failed");
            int xor = 0;
            for (int i = 5; i < input.Length - 1; i++)
                xor ^= input[i];
            if ((byte)xor != input[input.Length - 1])
                return OperateResult.Failed($"XOR mismatch: expected 0x{(byte)xor:X2}, got 0x{input[input.Length - 1]:X2}");
            return OperateResult.Success();
        }

        public static string GetErrorDescription(byte errorCode)
        {
            switch (errorCode)
            {
                case 0x90: return "OK";
                case 0x91: return "Packet length error";
                case 0x9F: return "SAM response timeout";
                case 0xA1: return "SAM reset failed";
                case 0xA2: return "SAM search card failed";
                case 0xA3: return "SAM select card failed";
                case 0xA4: return "SAM read card failed";
                case 0xA5: return "SAM write card failed";
                default: return $"Unknown error 0x{errorCode:X2}";
            }
        }

        public OperateResult<string> ReadSafeModuleNumber()
        {
            var send = PackToSamCommand(BuildReadCommand(0x12, 0xFF, null));
            var resp = SendAndReceive(send);
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message);
            var check = CheckResponse(resp.Content);
            if (!check.IsSuccess) return OperateResult<string>.Failed(check.Message);
            if (resp.Content.Length < 25)
                return OperateResult<string>.Failed("Response too short for module number");
            return OperateResult<string>.Success(
                Encoding.ASCII.GetString(resp.Content, 9, 16).TrimEnd('\0', ' '));
        }

        public OperateResult SearchCard()
        {
            var send = PackToSamCommand(BuildReadCommand(0x20, 0x01, null));
            var resp = SendAndReceive(send);
            if (!resp.IsSuccess) return resp;
            var check = CheckResponse(resp.Content);
            if (!check.IsSuccess) return check;
            if (resp.Content.Length > 9 && resp.Content[9] != 0x90)
                return OperateResult.Failed(GetErrorDescription(resp.Content[9]));
            return OperateResult.Success();
        }

        public OperateResult SelectCard()
        {
            var send = PackToSamCommand(BuildReadCommand(0x20, 0x02, null));
            var resp = SendAndReceive(send);
            if (!resp.IsSuccess) return resp;
            var check = CheckResponse(resp.Content);
            if (!check.IsSuccess) return check;
            if (resp.Content.Length > 9 && resp.Content[9] != 0x90)
                return OperateResult.Failed(GetErrorDescription(resp.Content[9]));
            return OperateResult.Success();
        }

        public OperateResult<IdentityCard> ReadCard()
        {
            var send = PackToSamCommand(BuildReadCommand(0x30, 0x01, null));
            var resp = SendAndReceive(send);
            if (!resp.IsSuccess) return OperateResult<IdentityCard>.Failed(resp.Message);
            var check = CheckResponse(resp.Content);
            if (!check.IsSuccess) return OperateResult<IdentityCard>.Failed(check.Message);
            if (resp.Content.Length > 9 && resp.Content[9] != 0x90)
                return OperateResult<IdentityCard>.Failed(GetErrorDescription(resp.Content[9]));
            if (resp.Content.Length < 12 + 256)
                return OperateResult<IdentityCard>.Failed("Response too short for ID card data");
            byte[] cardData = new byte[resp.Content.Length - 12];
            Buffer.BlockCopy(resp.Content, 12, cardData, 0, cardData.Length);
            return OperateResult<IdentityCard>.Success(IdentityCard.Parse(cardData));
        }

        public override string ToString() => $"SamSerialClient[{Port.PortName}]";
    }
}
