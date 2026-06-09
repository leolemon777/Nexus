using System;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 KV 系列 PLC 地址解析。
    /// <para>支持区域: DM(数据内存)、WR(Word Relay)、HR(保持继电器)、AR(辅助继电器)、
    /// TC(Timer Coil)、CC(Counter Coil)、CM(Timer 当前值)、TM(Counter 当前值)</para>
    /// </summary>
    public sealed class KeyenceKvAddress
    {
        public ushort Address { get; }
        public byte ReadFunctionCode { get; }
        public byte WriteFunctionCode { get; }
        public KeyenceArea Area { get; }
        public int RawOffset { get; }

        private KeyenceKvAddress(ushort address, byte readFc, byte writeFc, KeyenceArea area, int rawOffset)
        {
            Address = address;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
            Area = area;
            RawOffset = rawOffset;
        }

        /// <summary>
        /// 解析 KV 地址。示例: "DM100", "WR0", "HR10", "AR3", "TC0", "CC0", "CM100", "TM50"
        /// </summary>
        public static KeyenceKvAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            address = address.Trim().ToUpperInvariant();
            if (address.Length < 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            // 双字符前缀匹配（只匹配已知前缀）
            if (address.Length >= 3)
            {
                string prefix2 = address.Substring(0, 2);
                string numStr = address.Substring(2);
                if (int.TryParse(numStr, out int num2))
                {
                    switch (prefix2)
                    {
                        case "DM": return new KeyenceKvAddress((ushort)num2, 0x03, 0x06, KeyenceArea.DataMemory, num2);
                        case "WR": return new KeyenceKvAddress((ushort)num2, 0x01, 0x05, KeyenceArea.WordRelay, num2);
                        case "HR": return new KeyenceKvAddress((ushort)(0x0800 + num2), 0x01, 0x05, KeyenceArea.KeepRelay, num2);
                        case "AR": return new KeyenceKvAddress((ushort)(0x1000 + num2), 0x01, 0x05, KeyenceArea.AuxRelay, num2);
                        case "TC": return new KeyenceKvAddress((ushort)(0x1800 + num2), 0x01, 0x05, KeyenceArea.TimerCoil, num2);
                        case "CC": return new KeyenceKvAddress((ushort)(0x1C00 + num2), 0x01, 0x05, KeyenceArea.CounterCoil, num2);
                        case "CM": return new KeyenceKvAddress((ushort)(0x2000 + num2), 0x03, 0x06, KeyenceArea.TimerValue, num2);
                        case "TM": return new KeyenceKvAddress((ushort)(0x2400 + num2), 0x03, 0x06, KeyenceArea.CounterValue, num2);
                        // 未知双字符前缀 → 继续尝试单字符匹配（不抛异常）
                    }
                }
            }

            // 单字符前缀回退
            char prefix = address[0];
            string num = address.Substring(1);
            if (!int.TryParse(num, out int dNum))
                throw new ArgumentException($"无法解析 KV 地址: {address}", nameof(address));

            return prefix switch
            {
                'D' => new KeyenceKvAddress((ushort)dNum, 0x03, 0x06, KeyenceArea.DataMemory, dNum),
                'W' => new KeyenceKvAddress((ushort)dNum, 0x01, 0x05, KeyenceArea.WordRelay, dNum),
                _ => throw new ArgumentException($"无法解析 KV 地址: {address}", nameof(address))
            };
        }

        public static KeyenceKvAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        public KeyenceKvAddress WithOffset(int offset)
            => new KeyenceKvAddress((ushort)(Address + offset), ReadFunctionCode, WriteFunctionCode, Area, RawOffset + offset);

        public bool IsBitArea => ReadFunctionCode == 0x01 || ReadFunctionCode == 0x02;
        public bool IsRegisterArea => ReadFunctionCode == 0x03 || ReadFunctionCode == 0x04;

        public override string ToString() => $"{Area}{RawOffset} → 0x{Address:X4} FC{ReadFunctionCode}";
    }

    /// <summary>基恩士 KV 区域枚举。</summary>
    public enum KeyenceArea
    {
        DataMemory,
        WordRelay,
        KeepRelay,
        AuxRelay,
        TimerCoil,
        CounterCoil,
        TimerValue,
        CounterValue,
    }

    /// <summary>基恩士 KV 型号。</summary>
    public enum KeyenceKvModel
    {
        Unknown,
        Kv3000,
        Kv5000,
        Kv5500,
        Kv7000,
        Kv7500,
        Kv8000,
        KvNano,
    }

    /// <summary>KV 常量。</summary>
    public static class KeyenceKvConstants
    {
        public const ushort DM_Base = 0x0000;
        public const ushort WR_Base = 0x0000;
        public const ushort HR_Base = 0x0800;
        public const ushort AR_Base = 0x1000;
        public const ushort TC_Base = 0x1800;
        public const ushort CC_Base = 0x1C00;
        public const ushort CM_Base = 0x2000;
        public const ushort TM_Base = 0x2400;

        public const int MaxRegistersRead = 125;
        public const int MaxRegistersWrite = 123;
        public const int MaxBitsPerRequest = 2000;
        public const int DefaultPort = 502;
    }
}
