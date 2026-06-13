using System;

namespace Nexus.Keyence
{
    public sealed class KeyenceNanoAddress
    {
        public string AreaCode { get; }
        public int Address { get; }
        public int SubAddress { get; }
        public bool IsBitArea { get; }

        private KeyenceNanoAddress(string areaCode, int address, int subAddress, bool isBitArea)
        {
            AreaCode = areaCode;
            Address = address;
            SubAddress = subAddress;
            IsBitArea = isBitArea;
        }

        public static KeyenceNanoAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空", nameof(address));

            address = address.Trim().ToUpperInvariant();

            string areaCode;
            int numStart;

            if (address.Length >= 3)
            {
                string prefix2 = address.Substring(0, 2);
                if (prefix2 == "MR" || prefix2 == "LR" || prefix2 == "CR" ||
                    prefix2 == "DM" || prefix2 == "EM" || prefix2 == "FM" ||
                    prefix2 == "TM" || prefix2 == "CM")
                {
                    areaCode = prefix2;
                    numStart = 2;
                }
                else
                {
                    areaCode = address.Substring(0, 1);
                    numStart = 1;
                }
            }
            else
            {
                areaCode = address.Substring(0, 1);
                numStart = 1;
            }

            string remaining = address.Substring(numStart);
            string[] parts = remaining.Split('.');
            if (parts.Length < 1 || parts.Length > 2)
                throw new ArgumentException($"地址格式无效: {address}", nameof(address));

            if (!int.TryParse(parts[0], out int addr))
                throw new ArgumentException($"地址数字无效: {address}", nameof(address));

            int subAddr = 0;
            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[1], out subAddr))
                    throw new ArgumentException($"子地址数字无效: {address}", nameof(address));
            }

            bool isBit = IsBitAreaCode(areaCode);
            return new KeyenceNanoAddress(areaCode, addr, subAddr, isBit);
        }

        public static KeyenceNanoAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        public string BuildReadCommand()
        {
            if (IsBitArea || SubAddress > 0)
                return $"RDS {AreaCode}{Address}.{SubAddress}";
            return $"RD {AreaCode}{Address}.{SubAddress}";
        }

        public string BuildWriteCommand(string data)
        {
            if (IsBitArea || SubAddress > 0)
                return $"WRS {AreaCode}{Address}.{SubAddress} {data}";
            return $"WD {AreaCode}{Address}.{SubAddress} {data}";
        }

        private static bool IsBitAreaCode(string code)
        {
            return code == "R" || code == "B" || code == "T" || code == "C" ||
                   code == "MR" || code == "LR" || code == "CR";
        }
    }
}
