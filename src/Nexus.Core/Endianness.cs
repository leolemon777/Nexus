using System;

namespace Nexus
{
    /// <summary>
    /// 字节序枚举 — Modbus 等协议支持多种字节排列方式。
    /// </summary>
    public enum Endianness
    {
        /// <summary>大端序 ABCD（Modbus 默认）</summary>
        BigEndian = 0,
        /// <summary>小端序 DCBA</summary>
        LittleEndian = 1,
        /// <summary>中间大端 BADC</summary>
        MidBigEndian = 2,
        /// <summary>中间小端 CDAB</summary>
        MidLittleEndian = 3,
    }
}
