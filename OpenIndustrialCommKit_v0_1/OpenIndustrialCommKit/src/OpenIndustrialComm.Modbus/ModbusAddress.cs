using OpenIndustrialComm.Core;

namespace OpenIndustrialComm.Modbus;

public enum ModbusArea
{
    Coil,
    DiscreteInput,
    HoldingRegister,
    InputRegister
}

public sealed record ModbusAddress(string Original, ModbusArea Area, ushort Offset, ushort Count = 1, byte? Bit = null) : IDataAddress;

public sealed class ModbusAddressParser : IAddressParser<ModbusAddress>
{
    public ModbusAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new AddressParseException(address, "Address is empty.");

        var raw = address.Trim().ToLowerInvariant();
        var parts = raw.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            var area = parts[0] switch
            {
                "co" or "coil" or "0x" => ModbusArea.Coil,
                "di" or "discrete" or "1x" => ModbusArea.DiscreteInput,
                "ir" or "input" or "3x" => ModbusArea.InputRegister,
                "hr" or "holding" or "4x" => ModbusArea.HoldingRegister,
                _ => throw new AddressParseException(address, "Unknown Modbus area.")
            };
            if (!ushort.TryParse(parts[1], out var offset))
                throw new AddressParseException(address, "Offset must be UInt16.");
            return new ModbusAddress(address, area, offset);
        }

        if (raw.All(char.IsDigit) && raw.Length >= 2)
        {
            // Legacy 0xxxx/1xxxx/3xxxx/4xxxx style. Convert to zero-based offset.
            // Keep the original string so leading zeros such as 00001 are not lost.
            var first = raw[0];
            if (!int.TryParse(raw[1..], out var number))
                throw new AddressParseException(address, "Legacy address number is invalid.");
            if (number <= 0) throw new AddressParseException(address, "Legacy address offset must be 1-based.");
            if (number - 1 > ushort.MaxValue) throw new AddressParseException(address, "Offset exceeds UInt16 range.");
            var offset = (ushort)(number - 1);
            return first switch
            {
                '0' => new ModbusAddress(address, ModbusArea.Coil, offset),
                '1' => new ModbusAddress(address, ModbusArea.DiscreteInput, offset),
                '3' => new ModbusAddress(address, ModbusArea.InputRegister, offset),
                '4' => new ModbusAddress(address, ModbusArea.HoldingRegister, offset),
                _ => throw new AddressParseException(address, "Legacy Modbus address must start with 0, 1, 3, or 4.")
            };
        }

        throw new AddressParseException(address, "Use formats such as hr:0, ir:10, co:5, di:7, or 40001.");
    }
}
