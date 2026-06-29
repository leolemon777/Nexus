using System;
using Nexus;

namespace Nexus.EtherCAT
{
    public sealed class EtherCATAddress : IDataAddress
    {
        public string Original { get; }
        public ushort SlaveAddress { get; }
        public ushort Index { get; }
        public byte SubIndex { get; }
        public EtherCATAccessType AccessType { get; }

        public EtherCATAddress(string original, ushort slaveAddress, ushort index, byte subIndex, EtherCATAccessType accessType)
        {
            Original = original;
            SlaveAddress = slaveAddress;
            Index = index;
            SubIndex = subIndex;
            AccessType = accessType;
        }
    }

    public enum EtherCATAccessType { SdoRead, SdoWrite, ProcessData }

    public sealed class EtherCATAddressParser : IAddressParser<EtherCATAddress>
    {
        public EtherCATAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            // 格式: slave.index.subindex 或 slave:index.subindex
            // 例如: 0.0x6000.0 或 1:0x1C12.0
            char separator = address.Contains(':') ? ':' : '.';
            string[] parts = address.Split(separator);

            if (parts.Length == 3)
            {
                ushort slave = ushort.Parse(parts[0]);
                ushort index = parts[1].StartsWith("0x") ? Convert.ToUInt16(parts[1], 16) : ushort.Parse(parts[1]);
                byte subIndex = parts[2].StartsWith("0x") ? Convert.ToByte(parts[2], 16) : byte.Parse(parts[2]);
                return new EtherCATAddress(original, slave, index, subIndex, EtherCATAccessType.SdoRead);
            }

            if (parts.Length == 2)
            {
                ushort slave = ushort.Parse(parts[0]);
                ushort index = parts[1].StartsWith("0x") ? Convert.ToUInt16(parts[1], 16) : ushort.Parse(parts[1]);
                return new EtherCATAddress(original, slave, index, 0, EtherCATAccessType.SdoRead);
            }

            throw new AddressParseException(address, "EtherCAT 地址格式: slave.index.subindex");
        }

        public bool TryParse(string address, out EtherCATAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
