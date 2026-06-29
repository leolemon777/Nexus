using System;
using Nexus;

namespace Nexus.Inovance.H5u
{
    public sealed class InovanceH5uAddress : IDataAddress
    {
        public string Original { get; }
        public InovanceArea Area { get; }
        public ushort StartAddress { get; }
        public byte ReadFunctionCode { get; }
        public byte WriteFunctionCode { get; }

        public InovanceH5uAddress(string original, InovanceArea area, ushort startAddress, byte readFc, byte writeFc)
        {
            Original = original;
            Area = area;
            StartAddress = startAddress;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
        }
    }

    public enum InovanceArea
    {
        DataRegister,
        Coil,
        DiscreteInput,
        InputRegister,
        Timer,
        Counter,
        Step,
    }

    public sealed class InovanceH5uAddressParser : IAddressParser<InovanceH5uAddress>
    {
        public InovanceH5uAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = AddressContext.ExtractCoreAddress(address).Trim().ToUpperInvariant();

            char prefix = address[0];
            string numPart = address.Substring(1);
            ushort addr = ushort.Parse(numPart.TrimStart('0').Length == 0 ? "0" : numPart.TrimStart('0'));

            return prefix switch
            {
                'D' => new InovanceH5uAddress(original, InovanceArea.DataRegister, addr, 0x03, 0x06),
                'M' => new InovanceH5uAddress(original, InovanceArea.Coil, addr, 0x01, 0x05),
                'X' => new InovanceH5uAddress(original, InovanceArea.DiscreteInput, addr, 0x02, 0x00),
                'Y' => new InovanceH5uAddress(original, InovanceArea.Coil, (ushort)(addr + 1000), 0x01, 0x05),
                'T' => new InovanceH5uAddress(original, InovanceArea.Timer, addr, 0x03, 0x06),
                'C' => new InovanceH5uAddress(original, InovanceArea.Counter, addr, 0x03, 0x06),
                'S' => new InovanceH5uAddress(original, InovanceArea.Step, addr, 0x01, 0x05),
                _ => throw new AddressParseException(address, $"不支持的汇川地址前缀: {prefix}")
            };
        }

        public bool TryParse(string address, out InovanceH5uAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
