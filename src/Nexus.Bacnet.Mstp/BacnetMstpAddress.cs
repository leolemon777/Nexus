using System;
using Nexus;

namespace Nexus.Bacnet.Mstp
{
    public sealed class BacnetMstpAddress : IDataAddress
    {
        public string Original { get; }
        public ushort Network { get; }
        public uint DeviceId { get; }
        public ushort ObjectType { get; }
        public uint Instance { get; }
        public byte PropertyId { get; }

        public BacnetMstpAddress(string original, ushort network, uint deviceId, ushort objectType, uint instance, byte propertyId)
        {
            Original = original;
            Network = network;
            DeviceId = deviceId;
            ObjectType = objectType;
            Instance = instance;
            PropertyId = propertyId;
        }
    }

    public sealed class BacnetMstpAddressParser : IAddressParser<BacnetMstpAddress>
    {
        public BacnetMstpAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            // 格式: device:objectType:instance.property 或 network:device.objectType:instance.property
            string[] parts = address.Split(':');
            if (parts.Length == 3)
            {
                ushort network = ushort.Parse(parts[0]);
                uint deviceId = uint.Parse(parts[1]);
                string objPart = parts[2];
                int dotIdx = objPart.IndexOf('.');
                string typeInst = dotIdx >= 0 ? objPart.Substring(0, dotIdx) : objPart;
                byte propId = dotIdx >= 0 ? byte.Parse(objPart.Substring(dotIdx + 1)) : (byte)85;
                string[] ti = typeInst.Split(':');
                ushort objType = ushort.Parse(ti[0]);
                uint instance = ti.Length > 1 ? uint.Parse(ti[1]) : 0;
                return new BacnetMstpAddress(original, network, deviceId, objType, instance, propId);
            }

            throw new AddressParseException(address, "BACnet MS/TP 地址格式: network:device.objectType:instance.property");
        }

        public bool TryParse(string address, out BacnetMstpAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
