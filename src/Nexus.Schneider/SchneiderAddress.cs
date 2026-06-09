using System;

namespace Nexus.Schneider
{
    /// <summary>施耐德 Modicon 地址解析器。</summary>
    public sealed class SchneiderAddress
    {
        /// <summary>区域类型。</summary>
        public SchneiderArea Area { get; }
        /// <summary>Modbus 功能码。</summary>
        public byte FunctionCode { get; }
        /// <summary>Modbus 从站地址偏移。</summary>
        public ushort AddressValue { get; }
        /// <summary>原始地址字符串。</summary>
        public string RawAddress { get; }

        private SchneiderAddress(SchneiderArea area, byte fc, ushort addr, string raw)
        {
            Area = area;
            FunctionCode = fc;
            AddressValue = addr;
            RawAddress = raw;
        }

        /// <summary>
        /// 解析施耐德 Modicon 地址字符串。
        /// 支持: %MW100, %M50, %I0.0, %IW10, %Q0.1, %QW20, %S0, %SW100, %KW50
        /// 也支持不带 % 前缀: MW100, M50, IW10 等。
        /// </summary>
        public static SchneiderAddress? TryParse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            try
            {
                string addr = address.Trim().ToUpperInvariant().Replace("%", "");
                if (addr.Length < 2) return null;

                // 系统位 (%S)
                if (addr[0] == 'S' && addr.Length > 1 && char.IsDigit(addr[1]))
                {
                    if (!ushort.TryParse(addr.Substring(1), out ushort num)) return null;
                    return new SchneiderAddress(SchneiderArea.SystemBit, SchneiderConstants.Fc01ReadCoil, num, address);
                }

                // 系统字 (%SW)
                if (addr.StartsWith("SW") && addr.Length > 2)
                {
                    if (!ushort.TryParse(addr.Substring(2), out ushort num)) return null;
                    return new SchneiderAddress(SchneiderArea.SystemWord, SchneiderConstants.Fc03ReadHolding, (ushort)(num + 0x0400), address);
                }

                // 输入字 (%IW)
                if (addr.StartsWith("IW") && addr.Length > 2)
                {
                    if (!ushort.TryParse(addr.Substring(2), out ushort num)) return null;
                    return new SchneiderAddress(SchneiderArea.InputWord, SchneiderConstants.Fc04ReadInput, num, address);
                }

                // 输出字 (%QW)
                if (addr.StartsWith("QW") && addr.Length > 2)
                {
                    if (!ushort.TryParse(addr.Substring(2), out ushort num)) return null;
                    return new SchneiderAddress(SchneiderArea.OutputWord, SchneiderConstants.Fc03ReadHolding, (ushort)(num + 0x0600), address);
                }

                // 常量字 (%KW)
                if (addr.StartsWith("KW") && addr.Length > 2)
                {
                    if (!ushort.TryParse(addr.Substring(2), out ushort num)) return null;
                    return new SchneiderAddress(SchneiderArea.ConstantWord, SchneiderConstants.Fc03ReadHolding, (ushort)(num + 0x0800), address);
                }

                // 内部字 (%MW)
                if (addr.StartsWith("MW") && addr.Length > 2)
                {
                    if (!ushort.TryParse(addr.Substring(2), out ushort num)) return null;
                    return new SchneiderAddress(SchneiderArea.InternalWord, SchneiderConstants.Fc03ReadHolding, num, address);
                }

                // 输入位 (%I 或 %Ix.y)
                if (addr[0] == 'I')
                {
                    string body = addr.Substring(1);
                    int dotIdx = body.IndexOf('.');
                    if (dotIdx >= 0)
                    {
                        // I0.5 格式
                        ushort word = ushort.Parse(body.Substring(0, dotIdx));
                        byte bit = byte.Parse(body.Substring(dotIdx + 1));
                        return new SchneiderAddress(SchneiderArea.InputBit, SchneiderConstants.Fc02ReadDiscrete, (ushort)(word * 16 + bit), address);
                    }
                    if (ushort.TryParse(body, out ushort inum))
                        return new SchneiderAddress(SchneiderArea.InputBit, SchneiderConstants.Fc02ReadDiscrete, inum, address);
                    return null;
                }

                // 输出位 (%Q 或 %Qx.y)
                if (addr[0] == 'Q')
                {
                    string body = addr.Substring(1);
                    // 排除 QW（已处理）
                    if (body.Length > 0 && char.IsDigit(body[0]))
                    {
                        int dotIdx = body.IndexOf('.');
                        if (dotIdx >= 0)
                        {
                            ushort word = ushort.Parse(body.Substring(0, dotIdx));
                            byte bit = byte.Parse(body.Substring(dotIdx + 1));
                            return new SchneiderAddress(SchneiderArea.OutputBit, SchneiderConstants.Fc01ReadCoil, (ushort)(word * 16 + bit), address);
                        }
                        if (ushort.TryParse(body, out ushort qnum))
                            return new SchneiderAddress(SchneiderArea.OutputBit, SchneiderConstants.Fc05WriteCoil, qnum, address);
                    }
                    return null;
                }

                // 内部位 (%M)
                if (addr[0] == 'M' && addr.Length > 1 && char.IsDigit(addr[1]))
                {
                    if (!ushort.TryParse(addr.Substring(1), out ushort mnum)) return null;
                    return new SchneiderAddress(SchneiderArea.InternalBit, SchneiderConstants.Fc01ReadCoil, mnum, address);
                }

                return null;
            }
            catch { return null; }
        }
    }
}
