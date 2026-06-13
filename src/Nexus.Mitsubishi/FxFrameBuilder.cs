using System;
using System.Text;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 FX 系列编程口协议帧构建器。
    /// <para>帧结构: ENQ(0x05) -> ACK(0x06)/NAK(0x15) -> STX(0x02) + Command(1) + Address(4) + Data(N) + ETX(0x03) + SUM(2 chars hex)</para>
    /// </summary>
    public static class FxFrameBuilder
    {
        private const byte ENQ = 0x05;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;

        /// <summary>
        /// 构建 FX 读取命令帧 (Command '0')。
        /// </summary>
        /// <param name="deviceCode">设备代码 (如 'D'=0x44, 'M'=0x4D, 'X'=0x58, 'Y'=0x59)</param>
        /// <param name="address">起始地址 (十进制)。</param>
        /// <param name="wordCount">读取字数 (1 word = 2 bytes)。</param>
        public static byte[] BuildReadCommand(char deviceCode, int address, int wordCount)
        {
            string addrStr = address.ToString("D4");
            string countStr = wordCount.ToString("X2"); // 2位十六进制

            string cmd = $"0{deviceCode}{addrStr}{countStr}";
            byte[] data = Encoding.ASCII.GetBytes(cmd);
            
            byte[] frame = new byte[1 + data.Length + 1 + 2]; // STX + Data + ETX + SUM
            frame[0] = STX;
            Buffer.BlockCopy(data, 0, frame, 1, data.Length);
            frame[frame.Length - 3] = ETX;
            
            byte[] sumBytes = CalculateSum(frame, 0, frame.Length - 3);
            frame[frame.Length - 2] = sumBytes[0];
            frame[frame.Length - 1] = sumBytes[1];

            return frame;
        }

        /// <summary>
        /// 构建 FX 写入命令帧 (Command '1')。
        /// </summary>
        /// <param name="deviceCode">设备代码。</param>
        /// <param name="address">起始地址。</param>
        /// <param name="data">要写入的字节数据 (必须是偶数长度)。</param>
        public static byte[] BuildWriteCommand(char deviceCode, int address, byte[] data)
        {
            string addrStr = address.ToString("D4");
            string dataStr = BitConverter.ToString(data).Replace("-", ""); // 转为十六进制字符串
            
            string cmd = $"1{deviceCode}{addrStr}{dataStr}";
            byte[] cmdBytes = Encoding.ASCII.GetBytes(cmd);
            
            byte[] frame = new byte[1 + cmdBytes.Length + 1 + 2]; // STX + Data + ETX + SUM
            frame[0] = STX;
            Buffer.BlockCopy(cmdBytes, 0, frame, 1, cmdBytes.Length);
            frame[frame.Length - 3] = ETX;
            
            byte[] sumBytes = CalculateSum(frame, 0, frame.Length - 3);
            frame[frame.Length - 2] = sumBytes[0];
            frame[frame.Length - 1] = sumBytes[1];

            return frame;
        }

        /// <summary>
        /// 计算 FX 协议的 SUM 校验和 (2位十六进制 ASCII 字符)。
        /// 校验范围：从 STX 到 ETX (包含)。
        /// </summary>
        private static byte[] CalculateSum(byte[] frame, int offset, int length)
        {
            int sum = 0;
            for (int i = offset; i < offset + length; i++)
            {
                sum += frame[i];
            }
            sum &= 0xFF; // 取低8位
            
            string sumStr = sum.ToString("X2");
            return Encoding.ASCII.GetBytes(sumStr);
        }

        /// <summary>
        /// 验证 FX 响应帧的 SUM 校验和。
        /// </summary>
        public static bool VerifyResponse(byte[] response, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (response.Length < 4) return false;

            // 检查是否以 ACK 或 NAK 开头 (握手阶段)
            if (response[0] == NAK) return false;
            if (response[0] != ACK && response[0] != STX) return false;

            // 如果是纯 ACK (无数据返回，如写入成功)
            if (response[0] == ACK && response.Length == 1)
            {
                data = Array.Empty<byte>();
                return true;
            }

            // 查找 STX 和 ETX
            int stxIndex = Array.IndexOf(response, STX);
            int etxIndex = Array.IndexOf(response, ETX);
            
            if (stxIndex < 0 || etxIndex < stxIndex + 5 || etxIndex + 3 > response.Length)
                return false;

            // 验证 SUM
            int sum = 0;
            for (int i = stxIndex; i <= etxIndex; i++)
            {
                sum += response[i];
            }
            sum &= 0xFF;
            string expectedSum = sum.ToString("X2");
            
            string actualSum = Encoding.ASCII.GetString(response, etxIndex + 1, 2);
            if (!expectedSum.Equals(actualSum, StringComparison.OrdinalIgnoreCase))
                return false;

            // 提取数据 (跳过 STX, Command, Device, Address)
            // Data 长度 = (etxIndex - stxIndex - 1) - 1(Command) - 1(Device) - 4(Address)
            int dataLen = etxIndex - stxIndex - 7;
            if (dataLen > 0)
            {
                string hexData = Encoding.ASCII.GetString(response, stxIndex + 7, dataLen);
                data = HexStringToByteArray(hexData);
            }

            return true;
        }

        private static byte[] HexStringToByteArray(string hex)
        {
            if (hex.Length % 2 != 0) hex = "0" + hex;
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
