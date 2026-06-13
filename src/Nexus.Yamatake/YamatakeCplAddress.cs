using System;

namespace Nexus.Yamatake
{
    public sealed class YamatakeCplAddress : IDataAddress
    {
        public string Original { get; }
        public int Address { get; }
        public byte Station { get; }

        public YamatakeCplAddress(string original, int address, byte station)
        {
            Original = original;
            Address = address;
            Station = station;
        }

        public string ToHexString() => Address.ToString("X4");

        public static YamatakeCplAddress Parse(string address, byte defaultStation = 1)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            byte station = defaultStation;
            string addr = address.Trim();

            if (addr.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
            {
                int semiPos = addr.IndexOf(';');
                if (semiPos > 2)
                {
                    string stationStr = addr.Substring(2, semiPos - 2);
                    if (byte.TryParse(stationStr, out byte s)) station = s;
                    addr = addr.Substring(semiPos + 1).Trim();
                }
            }

            if (!int.TryParse(addr, System.Globalization.NumberStyles.HexNumber, null, out int addrNum))
            {
                if (!int.TryParse(addr, out addrNum))
                    throw new AddressParseException(address, $"地址必须为十六进制数（如 0100）或十进制数");
            }

            if (addrNum < 0 || addrNum > 0xFFFF)
                throw new AddressParseException(address, "地址范围 0000~FFFF");

            return new YamatakeCplAddress(address, addrNum, station);
        }

        public static bool TryParse(string address, out YamatakeCplAddress? parsed, byte defaultStation = 1)
        {
            try
            {
                parsed = Parse(address, defaultStation);
                return true;
            }
            catch
            {
                parsed = null;
                return false;
            }
        }
    }
}
