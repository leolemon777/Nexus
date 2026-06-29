using System;
using Nexus;

namespace Nexus.Omron.NxNj
{
    public sealed class OmronNxNjAddress : IDataAddress
    {
        public string Original { get; }
        public string AreaCode { get; }
        public ushort WordAddress { get; }
        public byte BitOffset { get; }
        public bool IsBit { get; }

        public OmronNxNjAddress(string original, string areaCode, ushort wordAddress, bool isBit = false, byte bitOffset = 0)
        {
            Original = original;
            AreaCode = areaCode;
            WordAddress = wordAddress;
            IsBit = isBit;
            BitOffset = bitOffset;
        }
    }

    public sealed class OmronNxNjAddressParser : IAddressParser<OmronNxNjAddress>
    {
        public OmronNxNjAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = AddressContext.ExtractCoreAddress(address).Trim().ToUpperInvariant();

            string areaCode;
            string numPart;

            if (address.StartsWith("D") && !address.StartsWith("DM"))
            {
                areaCode = "D";
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("W"))
            {
                areaCode = "W";
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("H"))
            {
                areaCode = "H";
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("CIO"))
            {
                areaCode = "CIO";
                numPart = address.Substring(3);
            }
            else if (address.StartsWith("A"))
            {
                areaCode = "A";
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("E"))
            {
                areaCode = "E";
                numPart = address.Substring(1);
            }
            else if (address.StartsWith("I"))
            {
                areaCode = "I";
                numPart = address.Substring(1);
            }
            else
            {
                throw new AddressParseException(address, $"不支持的 Omron NX/NJ 地址前缀: {address}");
            }

            bool isBit = false;
            byte bitOffset = 0;
            int dotIdx = numPart.IndexOf('.');
            if (dotIdx >= 0)
            {
                isBit = true;
                bitOffset = byte.Parse(numPart.Substring(dotIdx + 1));
                numPart = numPart.Substring(0, dotIdx);
            }

            ushort wordAddr = ushort.Parse(numPart.TrimStart('0').Length == 0 ? "0" : numPart.TrimStart('0'));
            return new OmronNxNjAddress(original, areaCode, wordAddr, isBit, bitOffset);
        }

        public bool TryParse(string address, out OmronNxNjAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
