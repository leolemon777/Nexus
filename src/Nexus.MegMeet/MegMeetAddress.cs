using System;
using Nexus.Modbus;

namespace Nexus.MegMeet
{
    /// <summary>
    /// 麦格米特 PLC 地址解析。
    /// <para>将 MegMeet PLC 地址 (X/Y/M/SM/S/T/C/D/SD/Z/R) 转换为标准 Modbus 地址。</para>
    /// </summary>
    public sealed class MegMeetAddress
    {
        /// <summary>Modbus 起始地址（0-based）。</summary>
        public ushort Address { get; }

        /// <summary>读功能码。</summary>
        public byte ReadFunctionCode { get; }

        /// <summary>写功能码（0 = 只读区域）。</summary>
        public byte WriteFunctionCode { get; }

        /// <summary>区域类型。</summary>
        public MegMeetArea Area { get; }

        /// <summary>原始地址数字。</summary>
        public int RawOffset { get; }

        private MegMeetAddress(ushort address, byte readFc, byte writeFc, MegMeetArea area, int rawOffset)
        {
            Address = address;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
            Area = area;
            RawOffset = rawOffset;
        }

        /// <summary>
        /// 解析麦格米特地址字符串。
        /// <para>位操作 (FC01/02/05/15): X(八进制), Y(八进制), M, SM, S, T, C</para>
        /// <para>字操作 (FC03/06/16): D, SD, Z, R, T(字), C(字)</para>
        /// </summary>
        public static MegMeetAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            address = address.Trim().ToUpperInvariant();

            string numStr;
            int num;

            if (address.StartsWith("SD"))
            {
                numStr = address.Substring(2);
                num = ParseInt(numStr);
                ushort modbusAddr = num < 256
                    ? (ushort)(num + 8000)
                    : (ushort)(num - 256 + 12000);
                return new MegMeetAddress(modbusAddr, 0x03, 0x06, MegMeetArea.SpecialRegister, num);
            }

            if (address.StartsWith("SM"))
            {
                numStr = address.Substring(2);
                num = ParseInt(numStr);
                ushort modbusAddr = num < 256
                    ? (ushort)(num + 4400)
                    : (ushort)(num - 256 + 30000);
                return new MegMeetAddress(modbusAddr, 0x01, 0x05, MegMeetArea.SpecialRelay, num);
            }

            char prefix = address[0];
            numStr = address.Substring(1);

            switch (prefix)
            {
                case 'X':
                    num = ParseOctal(numStr);
                    return new MegMeetAddress((ushort)num, 0x02, 0x00, MegMeetArea.Input, num);

                case 'Y':
                    num = ParseOctal(numStr);
                    return new MegMeetAddress((ushort)num, 0x01, 0x05, MegMeetArea.Output, num);

                case 'M':
                    num = ParseInt(numStr);
                    if (num < 2048)
                        return new MegMeetAddress((ushort)(num + 2000), 0x01, 0x05, MegMeetArea.InternalRelay, num);
                    return new MegMeetAddress((ushort)(num - 2048 + 12000), 0x01, 0x05, MegMeetArea.InternalRelay, num);

                case 'S':
                    num = ParseInt(numStr);
                    if (num < 1024)
                        return new MegMeetAddress((ushort)(num + 6000), 0x01, 0x05, MegMeetArea.StepRelay, num);
                    return new MegMeetAddress((ushort)(num - 1024 + 31000), 0x01, 0x05, MegMeetArea.StepRelay, num);

                case 'T':
                    num = ParseInt(numStr);
                    return ParseTimer(num);

                case 'C':
                    num = ParseInt(numStr);
                    return ParseCounter(num);

                case 'D':
                    num = ParseInt(numStr);
                    return new MegMeetAddress((ushort)num, 0x03, 0x06, MegMeetArea.DataRegister, num);

                case 'Z':
                    num = ParseInt(numStr);
                    return new MegMeetAddress((ushort)(num + 8500), 0x03, 0x06, MegMeetArea.IndexRegister, num);

                case 'R':
                    num = ParseInt(numStr);
                    return new MegMeetAddress((ushort)(num + 13000), 0x03, 0x06, MegMeetArea.FileRegister, num);

                default:
                    if (int.TryParse(address, out num))
                        return new MegMeetAddress((ushort)num, 0x03, 0x06, MegMeetArea.DataRegister, num);
                    throw new ArgumentException($"不支持的地址前缀: {prefix}", nameof(address));
            }
        }

        /// <summary>解析定时器地址（位/字自动判断由调用方决定，此处按位操作返回）。</summary>
        private static MegMeetAddress ParseTimer(int num)
        {
            if (num < 256)
                return new MegMeetAddress((ushort)(num + 8000), 0x01, 0x05, MegMeetArea.TimerContact, num);
            return new MegMeetAddress((ushort)(num - 256 + 11000), 0x01, 0x05, MegMeetArea.TimerContact, num);
        }

        /// <summary>解析计数器地址（位/字自动判断由调用方决定，此处按位操作返回）。</summary>
        private static MegMeetAddress ParseCounter(int num)
        {
            if (num < 256)
                return new MegMeetAddress((ushort)(num + 9200), 0x01, 0x05, MegMeetArea.CounterContact, num);
            return new MegMeetAddress((ushort)(num - 256 + 10000), 0x01, 0x05, MegMeetArea.CounterContact, num);
        }

        /// <summary>解析定时器字地址（当前值）。</summary>
        public static MegMeetAddress ParseTimerWord(int num)
        {
            if (num < 256)
                return new MegMeetAddress((ushort)(num + 9000), 0x03, 0x06, MegMeetArea.TimerValue, num);
            return new MegMeetAddress((ushort)(num - 256 + 11000), 0x03, 0x06, MegMeetArea.TimerValue, num);
        }

        /// <summary>解析计数器字地址。</summary>
        public static MegMeetAddress ParseCounterWord(int num)
        {
            if (num < 200)
                return new MegMeetAddress((ushort)(num + 9500), 0x03, 0x06, MegMeetArea.CounterValue, num);
            if (num < 256)
                return new MegMeetAddress((ushort)(num * 2 - 200 + 9700), 0x03, 0x06, MegMeetArea.CounterValue, num);
            return new MegMeetAddress((ushort)(num * 2 - 256 + 10000), 0x03, 0x06, MegMeetArea.CounterValue, num);
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static MegMeetAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        /// <summary>是否为只读区域。</summary>
        public bool IsReadOnly => WriteFunctionCode == 0;

        /// <summary>是否为位区域。</summary>
        public bool IsBitArea => ReadFunctionCode == 0x01 || ReadFunctionCode == 0x02;

        /// <summary>是否为字区域。</summary>
        public bool IsWordArea => ReadFunctionCode == 0x03 || ReadFunctionCode == 0x04;

        private static int ParseInt(string s) => int.Parse(s.TrimStart('0').Length == 0 ? "0" : s.TrimStart('0'));

        private static int ParseOctal(string s)
        {
            s = s.TrimStart('0');
            if (s.Length == 0) return 0;
            int result = 0;
            foreach (char c in s)
            {
                if (c < '0' || c > '7')
                    throw new ArgumentException($"八进制地址包含非法字符: {c}");
                result = result * 8 + (c - '0');
            }
            return result;
        }

        public override string ToString() => $"{Area}{RawOffset} → Modbus 0x{Address:X4} FC{ReadFunctionCode}";
    }
}
