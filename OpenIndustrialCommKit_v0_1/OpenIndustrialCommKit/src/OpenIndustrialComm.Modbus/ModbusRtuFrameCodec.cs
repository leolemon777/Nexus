namespace OpenIndustrialComm.Modbus;

public static class ModbusRtuFrameCodec
{
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                var lsb = (crc & 0x0001) != 0;
                crc >>= 1;
                if (lsb) crc ^= 0xA001;
            }
        }
        return crc;
    }

    public static byte[] Encode(byte unitId, ReadOnlySpan<byte> pdu)
    {
        var frame = new byte[1 + pdu.Length + 2];
        frame[0] = unitId;
        pdu.CopyTo(frame.AsSpan(1));
        var crc = Crc16(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    public static byte[] Decode(ReadOnlySpan<byte> frame, byte? expectedUnitId = null)
    {
        if (frame.Length < 4) throw new InvalidDataException("RTU frame too short.");
        if (expectedUnitId.HasValue && frame[0] != expectedUnitId.Value)
            throw new InvalidDataException($"Unexpected unit id. Expected={expectedUnitId.Value}, Actual={frame[0]}.");

        var expected = Crc16(frame[..^2]);
        var actual = (ushort)(frame[^2] | (frame[^1] << 8));
        if (expected != actual)
            throw new InvalidDataException($"CRC mismatch. Expected=0x{expected:X4}, Actual=0x{actual:X4}.");

        return frame.Slice(1, frame.Length - 3).ToArray();
    }
}
