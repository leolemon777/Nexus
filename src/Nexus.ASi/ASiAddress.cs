using System;
using Nexus;

namespace Nexus.ASi
{
    public sealed class ASiAddress : IDataAddress
    {
        public string Original { get; }
        public byte SlaveAddress { get; }
        public byte Offset { get; }
        public bool IsInput { get; }

        public ASiAddress(string original, byte slaveAddress, byte offset, bool isInput)
        {
            Original = original;
            SlaveAddress = slaveAddress;
            Offset = offset;
            IsInput = isInput;
        }
    }

    public sealed class ASiAddressParser : IAddressParser<ASiAddress>
    {
        public ASiAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");
            string original = address;
            address = address.Trim().ToUpperInvariant();
            if (address.StartsWith("I"))
            {
                string[] parts = address.Substring(1).Split('.');
                byte slave = byte.Parse(parts[0]);
                byte offset = parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0;
                return new ASiAddress(original, slave, offset, true);
            }
            if (address.StartsWith("Q"))
            {
                string[] parts = address.Substring(1).Split('.');
                byte slave = byte.Parse(parts[0]);
                byte offset = parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0;
                return new ASiAddress(original, slave, offset, false);
            }
            throw new AddressParseException(address, "AS-i 地址格式: I{slave}[.offset] 或 Q{slave}[.offset]");
        }
        public bool TryParse(string address, out ASiAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
