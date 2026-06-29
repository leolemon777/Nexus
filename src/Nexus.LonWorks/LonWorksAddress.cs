using System;
using Nexus;

namespace Nexus.LonWorks
{
    public sealed class LonWorksAddress : IDataAddress
    {
        public string Original { get; }
        public ushort NodeId { get; }
        public ushort NetworkVariableIndex { get; }
        public string NetworkVariableName { get; }

        public LonWorksAddress(string original, ushort nodeId, ushort nvIndex, string nvName = "")
        {
            Original = original;
            NodeId = nodeId;
            NetworkVariableIndex = nvIndex;
            NetworkVariableName = nvName;
        }
    }

    public sealed class LonWorksAddressParser : IAddressParser<LonWorksAddress>
    {
        public LonWorksAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");
            string original = address;
            address = address.Trim();
            string[] parts = address.Split(':');
            if (parts.Length == 2)
            {
                ushort node = ushort.Parse(parts[0]);
                ushort nv = ushort.Parse(parts[1]);
                return new LonWorksAddress(original, node, nv);
            }
            if (parts.Length == 1)
                return new LonWorksAddress(original, 0, ushort.Parse(parts[0]));
            throw new AddressParseException(address, "LonWorks 地址格式: node:nvIndex 或 nvIndex");
        }
        public bool TryParse(string address, out LonWorksAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
