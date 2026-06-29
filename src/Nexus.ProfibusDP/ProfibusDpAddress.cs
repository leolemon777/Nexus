using System;
using Nexus;

namespace Nexus.ProfibusDP
{
    public sealed class ProfibusDpAddress : IDataAddress
    {
        public string Original { get; }
        public byte SlaveAddress { get; }
        public ushort Slot { get; }
        public ushort Offset { get; }
        public ushort Length { get; }

        public ProfibusDpAddress(string original, byte slaveAddress, ushort slot, ushort offset, ushort length = 1)
        {
            Original = original;
            SlaveAddress = slaveAddress;
            Slot = slot;
            Offset = offset;
            Length = length;
        }
    }

    public sealed class ProfibusDpAddressParser : IAddressParser<ProfibusDpAddress>
    {
        public ProfibusDpAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            // 格式: slave:slot:offset 或 slave:offset (默认slot=0)
            string[] parts = address.Split(':');

            if (parts.Length == 3)
            {
                byte slave = byte.Parse(parts[0]);
                ushort slot = ushort.Parse(parts[1]);
                ushort offset = ushort.Parse(parts[2]);
                return new ProfibusDpAddress(original, slave, slot, offset);
            }

            if (parts.Length == 2)
            {
                byte slave = byte.Parse(parts[0]);
                ushort offset = ushort.Parse(parts[1]);
                return new ProfibusDpAddress(original, slave, 0, offset);
            }

            throw new AddressParseException(address, "Profibus DP 地址格式: slave[:slot]:offset");
        }

        public bool TryParse(string address, out ProfibusDpAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
