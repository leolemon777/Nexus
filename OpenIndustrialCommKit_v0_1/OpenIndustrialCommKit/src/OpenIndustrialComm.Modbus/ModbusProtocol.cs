using System.Buffers.Binary;

namespace OpenIndustrialComm.Modbus;

public enum ModbusFunction : byte
{
    ReadCoils = 0x01,
    ReadDiscreteInputs = 0x02,
    ReadHoldingRegisters = 0x03,
    ReadInputRegisters = 0x04,
    WriteSingleCoil = 0x05,
    WriteSingleRegister = 0x06,
    WriteMultipleCoils = 0x0F,
    WriteMultipleRegisters = 0x10
}

public static class ModbusPdu
{
    public static byte[] Read(ModbusFunction function, ushort startAddress, ushort count)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), count);
        return pdu;
    }

    public static byte[] WriteSingleRegister(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)ModbusFunction.WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), value);
        return pdu;
    }

    public static byte[] WriteMultipleRegisters(ushort address, ReadOnlySpan<ushort> values)
    {
        if (values.Length > 123) throw new ArgumentOutOfRangeException(nameof(values), "Modbus allows up to 123 registers per write multiple request.");
        var byteCount = checked((byte)(values.Length * 2));
        var pdu = new byte[6 + byteCount];
        pdu[0] = (byte)ModbusFunction.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), (ushort)values.Length);
        pdu[5] = byteCount;
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2, 2), values[i]);
        return pdu;
    }

    public static ushort[] DecodeRegisters(ReadOnlySpan<byte> pdu)
    {
        EnsureNotException(pdu);
        if (pdu.Length < 2) throw new InvalidDataException("PDU too short.");
        var byteCount = pdu[1];
        if (pdu.Length < 2 + byteCount) throw new InvalidDataException("Register response length mismatch.");
        if (byteCount % 2 != 0) throw new InvalidDataException("Register byte count must be even.");
        var values = new ushort[byteCount / 2];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(2 + i * 2, 2));
        return values;
    }

    public static void EnsureNotException(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length >= 2 && (pdu[0] & 0x80) != 0)
            throw new ModbusException((byte)(pdu[0] & 0x7F), pdu[1]);
    }
}

public sealed class ModbusException : Exception
{
    public ModbusException(byte function, byte exceptionCode) : base($"Modbus exception. Function=0x{function:X2}, Code=0x{exceptionCode:X2}")
    {
        Function = function;
        ExceptionCode = exceptionCode;
    }

    public byte Function { get; }
    public byte ExceptionCode { get; }
}
