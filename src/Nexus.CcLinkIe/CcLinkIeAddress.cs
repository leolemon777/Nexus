using System;
using Nexus;

namespace Nexus.CcLinkIe
{
    public sealed class CcLinkIeAddress : IDataAddress
    {
        public string Original { get; }
        public CcLinkIeDeviceType DeviceType { get; }
        public ushort StartAddress { get; }
        public int BitOffset { get; }
        public bool IsBit { get; }

        public CcLinkIeAddress(string original, CcLinkIeDeviceType deviceType, ushort startAddress, bool isBit = false, int bitOffset = 0)
        {
            Original = original;
            DeviceType = deviceType;
            StartAddress = startAddress;
            IsBit = isBit;
            BitOffset = bitOffset;
        }
    }

    public enum CcLinkIeDeviceType { R, WR, LR, SW, SB, DX, DY, W, B, D }

    public sealed class CcLinkIeAddressParser : IAddressParser<CcLinkIeAddress>
    {
        public CcLinkIeAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim().ToUpperInvariant();

            string prefix = "";
            int prefixLen = 1;
            if (address.StartsWith("WR") || address.StartsWith("LR") || address.StartsWith("SW") || address.StartsWith("SB") || address.StartsWith("DX") || address.StartsWith("DY"))
            { prefix = address.Substring(0, 2); prefixLen = 2; }
            else
            { prefix = address.Substring(0, 1); prefixLen = 1; }

            string numPart = address.Substring(prefixLen);
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

            CcLinkIeDeviceType deviceType = prefix switch
            {
                "R" => CcLinkIeDeviceType.R, "WR" => CcLinkIeDeviceType.WR, "LR" => CcLinkIeDeviceType.LR,
                "SW" => CcLinkIeDeviceType.SW, "SB" => CcLinkIeDeviceType.SB, "DX" => CcLinkIeDeviceType.DX,
                "DY" => CcLinkIeDeviceType.DY, "W" => CcLinkIeDeviceType.W, "B" => CcLinkIeDeviceType.B,
                "D" => CcLinkIeDeviceType.D,
                _ => throw new AddressParseException(address, $"不支持的 CC-Link IE 设备类型: {prefix}")
            };

            return new CcLinkIeAddress(original, deviceType, addr, isBit, bitOffset);
        }

        public bool TryParse(string address, out CcLinkIeAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
