using System;
using Nexus;

namespace Nexus.FF_H1
{
    public sealed class FF_H1Address : IDataAddress
    {
        public string Original { get; }
        public ushort DeviceAddress { get; }
        public string BlockTag { get; }
        public string ParameterName { get; }

        public FF_H1Address(string original, ushort deviceAddress, string blockTag, string parameterName)
        {
            Original = original;
            DeviceAddress = deviceAddress;
            BlockTag = blockTag;
            ParameterName = parameterName;
        }
    }

    public sealed class FF_H1AddressParser : IAddressParser<FF_H1Address>
    {
        public FF_H1Address Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new AddressParseException(address, "地址不能为空");
            string original = address;
            address = address.Trim();
            string[] parts = address.Split(':');
            if (parts.Length == 3) return new FF_H1Address(original, ushort.Parse(parts[0]), parts[1], parts[2]);
            if (parts.Length == 2) return new FF_H1Address(original, 0, parts[0], parts[1]);
            throw new AddressParseException(address, "FF H1 地址格式: [deviceAddress:]blockTag.parameterName");
        }
        public bool TryParse(string address, out FF_H1Address? parsed) { try { parsed = Parse(address); return true; } catch { parsed = null; return false; } }
    }
}
