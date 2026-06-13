using System;
using System.Collections.Generic;

namespace Nexus.Vigor
{
    public static class VigorProtocol
    {
        public static byte[] BuildReadCommand(byte station, VigorCommand cmd, byte dataCode, int address, int count)
        {
            byte[] addrBcd = VigorAddress.EncodeBcdAddress(address);
            byte[] countBytes = BitConverter.GetBytes((ushort)count);
            byte[] body = new byte[]
            {
                (byte)cmd,
                dataCode,
                addrBcd[0], addrBcd[1], addrBcd[2],
                countBytes[0], countBytes[1]
            };
            return BuildFrame(station, body);
        }

        public static byte[] BuildWriteCommand(byte station, VigorCommand cmd, byte dataCode, int address, int count, byte[] leData)
        {
            byte[] addrBcd = VigorAddress.EncodeBcdAddress(address);
            byte[] countBytes = BitConverter.GetBytes((ushort)count);
            byte[] body = new byte[7 + leData.Length];
            body[0] = (byte)cmd;
            body[1] = dataCode;
            body[2] = addrBcd[0];
            body[3] = addrBcd[1];
            body[4] = addrBcd[2];
            body[5] = countBytes[0];
            body[6] = countBytes[1];
            Buffer.BlockCopy(leData, 0, body, 7, leData.Length);
            return BuildFrame(station, body);
        }

        private static byte[] BuildFrame(byte station, byte[] body)
        {
            int dataLen = VigorConstants.FixedDataLen + body.Length - 7;
            byte[] dataLenBytes = BitConverter.GetBytes((ushort)dataLen);

            int rawLen = 4 + body.Length + 2;
            byte[] raw = new byte[rawLen];
            raw[0] = VigorConstants.CODE;
            raw[1] = station;
            raw[2] = dataLenBytes[0];
            raw[3] = dataLenBytes[1];
            Buffer.BlockCopy(body, 0, raw, 4, body.Length);
            raw[rawLen - 2] = VigorConstants.STX;
            raw[rawLen - 1] = VigorConstants.ETX;

            byte bcc = 0;
            for (int i = 0; i < rawLen; i++)
                bcc ^= raw[i];
            string bccHex = bcc.ToString("x2");

            var frame = new List<byte>();
            frame.Add(VigorConstants.STX);
            for (int i = 0; i < rawLen; i++)
            {
                if (raw[i] == VigorConstants.STX)
                    frame.Add(VigorConstants.STX);
                frame.Add(raw[i]);
            }
            frame.Add((byte)bccHex[0]);
            frame.Add((byte)bccHex[1]);
            return frame.ToArray();
        }

        public static OperateResult<byte[]> ParseResponse(byte[] response, VigorCommand expectedCmd)
        {
            if (response == null || response.Length < 12)
                return OperateResult<byte[]>.Failed($"Response too short ({response?.Length ?? 0} bytes)");

            if (response[0] != VigorConstants.STX || response[1] != VigorConstants.CODE)
                return OperateResult<byte[]>.Failed("Invalid response header");

            byte station = response[2];
            int dataLen = response[3] | (response[4] << 8);

            List<byte> unstuffed = new List<byte>();
            int idx = 5;
            while (idx < response.Length)
            {
                byte bt = response[idx];
                if (bt == VigorConstants.STX)
                {
                    if (idx + 1 < response.Length && response[idx + 1] == VigorConstants.ETX)
                    {
                        idx += 2;
                        break;
                    }
                    else if (idx + 1 < response.Length && response[idx + 1] == VigorConstants.STX)
                    {
                        unstuffed.Add(VigorConstants.STX);
                        idx += 2;
                    }
                    else
                    {
                        unstuffed.Add(bt);
                        idx++;
                    }
                }
                else
                {
                    unstuffed.Add(bt);
                    idx++;
                }
            }

            if (idx + 2 > response.Length)
                return OperateResult<byte[]>.Failed("Response missing BCC");

            byte bccCalc = VigorConstants.CODE;
            bccCalc ^= station;
            bccCalc ^= (byte)(dataLen & 0xFF);
            bccCalc ^= (byte)((dataLen >> 8) & 0xFF);
            for (int i = 0; i < unstuffed.Count; i++)
                bccCalc ^= unstuffed[i];
            bccCalc ^= VigorConstants.STX;
            bccCalc ^= VigorConstants.ETX;

            string bccExpected = bccCalc.ToString("x2");
            if (response[idx] != (byte)bccExpected[0] || response[idx + 1] != (byte)bccExpected[1])
                return OperateResult<byte[]>.Failed("BCC checksum mismatch");

            if (unstuffed.Count < 7)
                return OperateResult<byte[]>.Failed("Response body too short");

            byte respCmd = unstuffed[0];
            byte dataCode = unstuffed[1];
            int respAddress = (unstuffed[2] << 16) | (unstuffed[3] << 8) | unstuffed[4];
            int respCount = unstuffed[5] | (unstuffed[6] << 8);

            if ((respCmd & 0x80) == 0)
            {
                byte errorCode = unstuffed.Count > 7 ? unstuffed[7] : (byte)0;
                return OperateResult<byte[]>.Failed($"Vigor PLC error 0x{errorCode:X2}", errorCode);
            }

            if (unstuffed.Count > 7)
            {
                byte[] data = new byte[unstuffed.Count - 7];
                for (int i = 7; i < unstuffed.Count; i++)
                    data[i - 7] = unstuffed[i];
                return OperateResult<byte[]>.Success(data);
            }

            return OperateResult<byte[]>.Success(new byte[0]);
        }
    }
}
