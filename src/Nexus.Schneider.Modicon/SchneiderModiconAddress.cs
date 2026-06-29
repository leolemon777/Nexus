using System;
using Nexus;

namespace Nexus.Schneider.Modicon
{
    public sealed class SchneiderModiconAddress : IDataAddress
    {
        public string Original { get; }
        public SchneiderArea Area { get; }
        public ushort StartAddress { get; }
        public int BitOffset { get; }
        public bool IsBit { get; }
        public byte ReadFunctionCode { get; }
        public byte WriteFunctionCode { get; }

        public SchneiderModiconAddress(string original, SchneiderArea area, ushort startAddress,
            byte readFc, byte writeFc, bool isBit = false, int bitOffset = 0)
        {
            Original = original;
            Area = area;
            StartAddress = startAddress;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
            IsBit = isBit;
            BitOffset = bitOffset;
        }

        public override string ToString() => $"{Area}:{StartAddress}{(IsBit ? $".{BitOffset}" : "")} (from '{Original}')";
    }

    public enum SchneiderArea
    {
        Coil,
        DiscreteInput,
        InputRegister,
        HoldingRegister,
        NetworkRegister,
    }

    public sealed class SchneiderModiconAddressParser : IAddressParser<SchneiderModiconAddress>
    {
        public SchneiderModiconAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = AddressContext.ExtractCoreAddress(address).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(original, "地址不能为空");

            if (!address.StartsWith("%"))
                throw new AddressParseException(address, "Schneider Modicon 地址必须以 '%' 开头");

            string body = address.Substring(1);
            if (body.Length < 2)
                throw new AddressParseException(address, "地址格式不完整");

            char prefix = body[0];
            char type = body[1];
            string numPart = body.Substring(2);

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

            return (prefix, type) switch
            {
                ('M', 'X') => new SchneiderModiconAddress(original, SchneiderArea.Coil, addr, 0x01, 0x05, true, bitOffset),
                ('M', 'W') => new SchneiderModiconAddress(original, SchneiderArea.HoldingRegister, addr, 0x03, 0x06),
                ('M', 'B') => new SchneiderModiconAddress(original, SchneiderArea.HoldingRegister, addr, 0x03, 0x06),
                ('M', 'D') => new SchneiderModiconAddress(original, SchneiderArea.HoldingRegister, addr, 0x03, 0x06),
                ('I', 'W') => new SchneiderModiconAddress(original, SchneiderArea.InputRegister, addr, 0x04, 0x00),
                ('I', 'X') => new SchneiderModiconAddress(original, SchneiderArea.DiscreteInput, addr, 0x02, 0x00, true, bitOffset),
                ('Q', 'W') => new SchneiderModiconAddress(original, SchneiderArea.HoldingRegister, addr, 0x03, 0x06),
                ('Q', 'X') => new SchneiderModiconAddress(original, SchneiderArea.Coil, addr, 0x01, 0x05, true, bitOffset),
                ('N', 'W') => new SchneiderModiconAddress(original, SchneiderArea.NetworkRegister, addr, 0x03, 0x06),
                ('N', 'B') => new SchneiderModiconAddress(original, SchneiderArea.NetworkRegister, addr, 0x03, 0x06),
                _ => throw new AddressParseException(address, $"不支持的 Schneider 地址类型: %{prefix}{type}")
            };
        }

        public bool TryParse(string address, out SchneiderModiconAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
