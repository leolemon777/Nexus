using System;
using Nexus;

namespace Nexus.CANopen
{
    public sealed class CANopenAddress : IDataAddress
    {
        public string Original { get; }
        public ushort Index { get; }
        public byte SubIndex { get; }
        public byte NodeId { get; }

        public CANopenAddress(string original, ushort index, byte subIndex, byte nodeId = 0)
        {
            Original = original;
            Index = index;
            SubIndex = subIndex;
            NodeId = nodeId;
        }
    }

    public sealed class CANopenAddressParser : IAddressParser<CANopenAddress>
    {
        public CANopenAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            // 格式: node.index.subindex 或 index.subindex (默认node=0)
            string[] parts = address.Split('.');

            if (parts.Length == 3)
            {
                byte node = byte.Parse(parts[0]);
                ushort index = parts[1].StartsWith("0x") ? Convert.ToUInt16(parts[1], 16) : ushort.Parse(parts[1]);
                byte subIndex = parts[2].StartsWith("0x") ? Convert.ToByte(parts[2], 16) : byte.Parse(parts[2]);
                return new CANopenAddress(original, index, subIndex, node);
            }

            if (parts.Length == 2)
            {
                ushort index = parts[0].StartsWith("0x") ? Convert.ToUInt16(parts[0], 16) : ushort.Parse(parts[0]);
                byte subIndex = parts[1].StartsWith("0x") ? Convert.ToByte(parts[1], 16) : byte.Parse(parts[1]);
                return new CANopenAddress(original, index, subIndex);
            }

            throw new AddressParseException(address, "CANopen 地址格式: [node.]index.subindex");
        }

        public bool TryParse(string address, out CANopenAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
