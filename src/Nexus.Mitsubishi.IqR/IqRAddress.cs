using System;
using Nexus;

namespace Nexus.Mitsubishi.IqR
{
    public sealed class IqRAddress : IDataAddress
    {
        public string Original { get; }
        public string DeviceCode { get; }
        public ushort StartAddress { get; }
        public int BitOffset { get; }
        public bool IsBit { get; }

        public IqRAddress(string original, string deviceCode, ushort startAddress, bool isBit = false, int bitOffset = 0)
        {
            Original = original;
            DeviceCode = deviceCode;
            StartAddress = startAddress;
            IsBit = isBit;
            BitOffset = bitOffset;
        }
    }

    public sealed class IqRAddressParser : IAddressParser<IqRAddress>
    {
        public IqRAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim().ToUpperInvariant();

            string[] prefixes = { "SM", "SD", "X", "Y", "M", "L", "F", "V", "B", "D", "W", "R", "ZR", "TN", "CN", "TS", "CS", "SW" };
            foreach (var prefix in prefixes)
            {
                if (address.StartsWith(prefix))
                {
                    string numPart = address.Substring(prefix.Length);
                    bool isBit = false;
                    int bitOffset = 0;
                    int dotIdx = numPart.IndexOf('.');
                    if (dotIdx >= 0)
                    {
                        isBit = true;
                        bitOffset = int.Parse(numPart.Substring(dotIdx + 1));
                        numPart = numPart.Substring(0, dotIdx);
                    }
                    ushort addr = ushort.Parse(numPart.TrimStart('0').Length == 0 ? "0" : numPart.TrimStart('0'));
                    return new IqRAddress(original, prefix, addr, isBit, bitOffset);
                }
            }

            throw new AddressParseException(address, $"不支持的 iQ-R 地址: {address}");
        }

        public bool TryParse(string address, out IqRAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
