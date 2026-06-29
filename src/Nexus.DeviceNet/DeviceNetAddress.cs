using System;
using Nexus;

namespace Nexus.DeviceNet
{
    public sealed class DeviceNetAddress : IDataAddress
    {
        public string Original { get; }
        public byte MacId { get; }
        public ushort ClassId { get; }
        public byte InstanceId { get; }
        public byte AttributeId { get; }

        public DeviceNetAddress(string original, byte macId, ushort classId, byte instanceId, byte attributeId)
        {
            Original = original;
            MacId = macId;
            ClassId = classId;
            InstanceId = instanceId;
            AttributeId = attributeId;
        }
    }

    public sealed class DeviceNetAddressParser : IAddressParser<DeviceNetAddress>
    {
        public DeviceNetAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");
            string original = address;
            address = address.Trim();
            string[] parts = address.Split(':');
            if (parts.Length == 4)
            {
                return new DeviceNetAddress(original, byte.Parse(parts[0]), ushort.Parse(parts[1]), byte.Parse(parts[2]), byte.Parse(parts[3]));
            }
            if (parts.Length == 2)
            {
                return new DeviceNetAddress(original, byte.Parse(parts[0]), ushort.Parse(parts[1]), 1, 0);
            }
            throw new AddressParseException(address, "DeviceNet 地址格式: macId:classId:instanceId:attributeId");
        }
        public bool TryParse(string address, out DeviceNetAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
