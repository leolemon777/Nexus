using System;
using Nexus;

namespace Nexus.Mitsubishi.Fx5u
{
    public sealed class Fx5uAddress : IDataAddress
    {
        public string Original { get; }
        public Fx5uDeviceType DeviceType { get; }
        public ushort StartAddress { get; }
        public int BitOffset { get; }
        public bool IsBit { get; }
        public ushort SubCommand { get; }

        public Fx5uAddress(string original, Fx5uDeviceType deviceType, ushort startAddress, bool isBit = false, int bitOffset = 0, ushort subCommand = 0)
        {
            Original = original;
            DeviceType = deviceType;
            StartAddress = startAddress;
            IsBit = isBit;
            BitOffset = bitOffset;
            SubCommand = subCommand;
        }
    }

    public enum Fx5uDeviceType
    {
        D, M, X, Y, T, C, R, SM, SD, W
    }

    public sealed class Fx5uAddressParser : IAddressParser<Fx5uAddress>
    {
        public Fx5uAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = AddressContext.ExtractCoreAddress(address).Trim().ToUpperInvariant();

            string prefix = "";
            string numPart = "";

            if (address.StartsWith("SM") || address.StartsWith("SD"))
            {
                prefix = address.Substring(0, 2);
                numPart = address.Substring(2);
            }
            else
            {
                prefix = address.Substring(0, 1);
                numPart = address.Substring(1);
            }

            bool isBit = false;
            int bitOffset = 0;
            int dotIdx = numPart.IndexOf('.');
            if (dotIdx >= 0)
            {
                isBit = true;
                bitOffset = int.Parse(numPart.Substring(dotIdx + 1));
                numPart = numPart.Substring(0, dotIdx);
            }

            ushort addr = ushort.Parse(numPart.TrimStart('0').Length == 0 ? "0" : numPart.TrimStart('0'));

            Fx5uDeviceType deviceType = prefix switch
            {
                "D" => Fx5uDeviceType.D,
                "M" => Fx5uDeviceType.M,
                "X" => Fx5uDeviceType.X,
                "Y" => Fx5uDeviceType.Y,
                "T" => Fx5uDeviceType.T,
                "C" => Fx5uDeviceType.C,
                "R" => Fx5uDeviceType.R,
                "SM" => Fx5uDeviceType.SM,
                "SD" => Fx5uDeviceType.SD,
                "W" => Fx5uDeviceType.W,
                _ => throw new AddressParseException(address, $"不支持的 FX5U 设备类型: {prefix}")
            };

            return new Fx5uAddress(original, deviceType, addr, isBit, bitOffset);
        }

        public bool TryParse(string address, out Fx5uAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
