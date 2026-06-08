using System;

namespace Nexus
{
    /// <summary>
    /// CRC / LRC 校验计算工具 — 支持 Modbus RTU (CRC16) 和 Modbus ASCII (LRC)。
    /// </summary>
    public static class CrcCalculator
    {
        private static readonly ushort[] Crc16Table = BuildTable();

        private static ushort[] BuildTable()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                ushort crc = (ushort)i;
                for (int j = 0; j < 8; j++)
                    crc = (crc & 1) != 0
                        ? (ushort)((crc >> 1) ^ 0xA001)
                        : (ushort)(crc >> 1);
                table[i] = crc;
            }
            return table;
        }

        /// <summary>
        /// 计算 CRC16-Modbus（多项式 0xA001，初始值 0xFFFF）。
        /// </summary>
        public static ushort ComputeCrc16(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            int end = offset + length;
            for (int i = offset; i < end; i++)
                crc = (ushort)((crc >> 8) ^ Crc16Table[(crc ^ data[i]) & 0xFF]);
            return crc;
        }

        /// <summary>计算整个字节数组的 CRC16-Modbus。</summary>
        public static ushort ComputeCrc16(byte[] data)
            => ComputeCrc16(data, 0, data.Length);

        /// <summary>
        /// 验证帧末尾 2 字节 CRC 是否正确。
        /// 帧格式：[...payload][CRC_Lo][CRC_Hi]。
        /// </summary>
        public static bool VerifyCrc16(byte[] frame)
        {
            if (frame == null || frame.Length < 3) return false;
            int dataLen = frame.Length - 2;
            ushort expected = ComputeCrc16(frame, 0, dataLen);
            ushort actual = (ushort)(frame[dataLen] | (frame[dataLen + 1] << 8));
            return expected == actual;
        }

        /// <summary>
        /// 计算 LRC（纵向冗余校验），用于 Modbus ASCII。
        /// 将字节逐个相加，取低 8 位的二进制补码。
        /// </summary>
        public static byte ComputeLrc(byte[] data, int offset, int length)
        {
            int sum = 0;
            int end = offset + length;
            for (int i = offset; i < end; i++)
                sum += data[i];
            return (byte)(-sum & 0xFF);
        }

        /// <summary>计算整个字节数组的 LRC。</summary>
        public static byte ComputeLrc(byte[] data)
            => ComputeLrc(data, 0, data.Length);

        /// <summary>
        /// 验证帧末尾 1 字节 LRC 是否正确。
        /// 帧格式：[...payload][LRC]。
        /// </summary>
        public static bool VerifyLrc(byte[] frame)
        {
            if (frame == null || frame.Length < 2) return false;
            int dataLen = frame.Length - 1;
            byte expected = ComputeLrc(frame, 0, dataLen);
            return expected == frame[dataLen];
        }
    }
}
