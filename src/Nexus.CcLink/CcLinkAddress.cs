using System;
using Nexus;

namespace Nexus.CcLink
{
    public sealed class CcLinkAddress : IDataAddress
    {
        public string Original { get; }
        public CcLinkDeviceType DeviceType { get; }
        public ushort StartAddress { get; }
        public int BitOffset { get; }
        public bool IsBit { get; }

        public CcLinkAddress(string original, CcLinkDeviceType deviceType, ushort startAddress, bool isBit = false, int bitOffset = 0)
        {
            Original = original;
            DeviceType = deviceType;
            StartAddress = startAddress;
            IsBit = isBit;
            BitOffset = bitOffset;
        }
    }

    public enum CcLinkDeviceType { R, RW, B, W, D, T, C, M, X, Y }

    public sealed class CcLinkAddressParser : IAddressParser<CcLinkAddress>
    {
        public CcLinkAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim().ToUpperInvariant();

            string prefix = address.Length >= 2 && char.IsLetter(address[0]) && char.IsLetter(address[1])
                ? address.Substring(0, 2) : address.Substring(0, 1);

            string numPart = address.Substring(prefix.Length);
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

            CcLinkDeviceType deviceType = prefix switch
            {
                "R" => CcLinkDeviceType.R, "RW" => CcLinkDeviceType.RW, "B" => CcLinkDeviceType.B,
                "W" => CcLinkDeviceType.W, "D" => CcLinkDeviceType.D, "T" => CcLinkDeviceType.T,
                "C" => CcLinkDeviceType.C, "M" => CcLinkDeviceType.M, "X" => CcLinkDeviceType.X,
                "Y" => CcLinkDeviceType.Y, _ => throw new AddressParseException(address, $"不支持的 CC-Link 设备类型: {prefix}")
            };

            return new CcLinkAddress(original, deviceType, addr, isBit, bitOffset);
        }

        public bool TryParse(string address, out CcLinkAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
