using System;

namespace Nexus.Siemens
{
    /// <summary>
    /// 西门子 PLC 型号枚举。
    /// </summary>
    public enum SiemensPLCS
    {
        /// <summary>S7-200</summary>
        S7_200 = 0,
        /// <summary>S7-200Smart</summary>
        S7_200Smart = 1,
        /// <summary>S7-300</summary>
        S7_300 = 2,
        /// <summary>S7-400</summary>
        S7_400 = 3,
        /// <summary>S7-1200</summary>
        S7_1200 = 4,
        /// <summary>S7-1500</summary>
        S7_1500 = 5,
    }

    /// <summary>
    /// S7 数据区类型（Variable Type）。
    /// </summary>
    internal enum S7Area : byte
    {
        PE = 0x81,  // 输入区 I
        PA = 0x82,  // 输出区 Q
        MK = 0x83,  // 中间存储区 M
        DB = 0x84,  // 数据块 DB
        CT = 0x1C,  // 计数器
        TM = 0x1D,  // 定时器
    }
}
