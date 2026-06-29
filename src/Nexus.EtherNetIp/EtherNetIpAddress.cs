using System;
using Nexus;

namespace Nexus.EtherNetIp
{
    public sealed class EtherNetIpAddress : IDataAddress
    {
        public string Original { get; }
        public string TagName { get; }
        public int ArrayIndex { get; }

        public EtherNetIpAddress(string original, string tagName, int arrayIndex = -1)
        {
            Original = original;
            TagName = tagName;
            ArrayIndex = arrayIndex;
        }
    }

    public sealed class EtherNetIpAddressParser : IAddressParser<EtherNetIpAddress>
    {
        public EtherNetIpAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            int bracketStart = address.IndexOf('[');
            if (bracketStart >= 0)
            {
                int bracketEnd = address.IndexOf(']', bracketStart);
                if (bracketEnd < 0) throw new AddressParseException(address, "缺少右方括号 ']'");
                string tagName = address.Substring(0, bracketStart);
                int index = int.Parse(address.Substring(bracketStart + 1, bracketEnd - bracketStart - 1));
                return new EtherNetIpAddress(original, tagName, index);
            }

            return new EtherNetIpAddress(original, address);
        }

        public bool TryParse(string address, out EtherNetIpAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
