using System;
using Nexus;

namespace Nexus.BacnetIp
{
    public sealed class BacnetIpAddress : IDataAddress
    {
        public string Original { get; }
        public ushort Network { get; }
        public uint DeviceId { get; }
        public ushort ObjectType { get; }
        public uint Instance { get; }
        public byte PropertyId { get; }

        public BacnetIpAddress(string original, ushort network, uint deviceId, ushort objectType, uint instance, byte propertyId)
        {
            Original = original;
            Network = network;
            DeviceId = deviceId;
            ObjectType = objectType;
            Instance = instance;
            PropertyId = propertyId;
        }

        public override string ToString() => $"{Network}:{DeviceId}.{ObjectType}:{Instance}.{PropertyId} (from '{Original}')";
    }

    public sealed class BacnetIpAddressParser : IAddressParser<BacnetIpAddress>
    {
        public BacnetIpAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            // 格式: network:device.objectType:instance.property
            // 简化格式: device.objectType:instance (network=0, property=85 Present Value)
            // 最简格式: device.objectType:instance (默认 property=85)

            string[] parts = address.Split(':');

            if (parts.Length == 4)
            {
                ushort network = ushort.Parse(parts[0]);
                uint deviceId = uint.Parse(parts[1]);
                string objPart = parts[2];
                byte propId = byte.Parse(parts[3]);

                string[] ti = objPart.Split('.');
                ushort objType = ushort.Parse(ti[0]);
                uint instance = ti.Length > 1 ? uint.Parse(ti[1]) : 0;

                return new BacnetIpAddress(original, network, deviceId, objType, instance, propId);
            }

            if (parts.Length == 3)
            {
                // Format: network:device.objectType:instance.property
                ushort network = ushort.Parse(parts[0]);
                string devObj = parts[1];
                string instProp = parts[2];

                int dotIdx = devObj.IndexOf('.');
                if (dotIdx < 0) throw new AddressParseException(address, "格式: network:device.objectType:instance.property");

                uint deviceId = uint.Parse(devObj.Substring(0, dotIdx));
                ushort objType = ushort.Parse(devObj.Substring(dotIdx + 1));

                int colonIdx = instProp.IndexOf('.');
                if (colonIdx >= 0)
                {
                    uint instance = uint.Parse(instProp.Substring(0, colonIdx));
                    byte propId = byte.Parse(instProp.Substring(colonIdx + 1));
                    return new BacnetIpAddress(original, network, deviceId, objType, instance, propId);
                }

                return new BacnetIpAddress(original, network, deviceId, objType, uint.Parse(instProp), 85);
            }

            if (parts.Length == 2)
            {
                // Format: device.objectType:instance.property or device.objectType:instance
                string devObj = parts[0];
                string instProp = parts[1];

                int dotIdx = devObj.IndexOf('.');
                if (dotIdx < 0) throw new AddressParseException(address, "格式: device.objectType:instance.property");

                uint deviceId = uint.Parse(devObj.Substring(0, dotIdx));
                ushort objType = ushort.Parse(devObj.Substring(dotIdx + 1));

                int colonIdx = instProp.IndexOf('.');
                if (colonIdx >= 0)
                {
                    uint instance = uint.Parse(instProp.Substring(0, colonIdx));
                    byte propId = byte.Parse(instProp.Substring(colonIdx + 1));
                    return new BacnetIpAddress(original, 0, deviceId, objType, instance, propId);
                }

                return new BacnetIpAddress(original, 0, deviceId, objType, uint.Parse(instProp), 85);
            }

            throw new AddressParseException(address, "BACnet IP 地址格式: [network:]device.objectType[:instance[.property]]");
        }

        public bool TryParse(string address, out BacnetIpAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }

    /// <summary>BACnet 对象类型。</summary>
    public static class BacnetObjectType
    {
        public const ushort AnalogInput = 0;
        public const ushort AnalogOutput = 1;
        public const ushort AnalogValue = 2;
        public const ushort BinaryInput = 3;
        public const ushort BinaryOutput = 4;
        public const ushort BinaryValue = 5;
        public const ushort Calendar = 6;
        public const ushort Command = 7;
        public const ushort Device = 8;
        public const ushort EventEnrollment = 9;
        public const ushort File = 10;
        public const ushort Group = 11;
        public const ushort Loop = 12;
        public const ushort MultiStateInput = 13;
        public const ushort MultiStateOutput = 14;
        public const ushort NotificationClass = 15;
        public const ushort Program = 16;
        public const ushort Schedule = 17;
        public const ushort Averaging = 18;
        public const ushort MultiStateValue = 19;
    }

    /// <summary>BACnet 属性 ID。</summary>
    public static class BacnetPropertyId
    {
        public const byte PresentValue = 85;
        public const byte StatusFlags = 111;
        public const byte OutOfService = 81;
        public const byte ObjectName = 77;
        public const byte ObjectType = 79;
        public const byte Description = 28;
        public const byte Units = 117;
        public const byte Reliability = 103;
        public const byte EventState = 36;
    }
}
