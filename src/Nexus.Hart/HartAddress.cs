using System;
using Nexus;

namespace Nexus.Hart
{
    public sealed class HartAddress : IDataAddress
    {
        public string Original { get; }
        public byte ShortAddress { get; }
        public ulong LongAddress { get; }
        public bool UseShortAddress { get; }

        public HartAddress(string original, byte shortAddress)
        {
            Original = original;
            ShortAddress = shortAddress;
            LongAddress = 0;
            UseShortAddress = true;
        }

        public HartAddress(string original, ulong longAddress)
        {
            Original = original;
            ShortAddress = 0;
            LongAddress = longAddress;
            UseShortAddress = false;
        }
    }

    public sealed class HartAddressParser : IAddressParser<HartAddress>
    {
        public HartAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            if (address.StartsWith("0x") || address.StartsWith("0X"))
            {
                ulong longAddr = ulong.Parse(address.Substring(2), System.Globalization.NumberStyles.HexNumber);
                return new HartAddress(original, longAddr);
            }

            if (byte.TryParse(address, out byte shortAddr) && shortAddr <= 15)
                return new HartAddress(original, shortAddr);

            throw new AddressParseException(address, "HART 地址格式: 短地址(0-15) 或 长地址(0x开头)");
        }

        public bool TryParse(string address, out HartAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
