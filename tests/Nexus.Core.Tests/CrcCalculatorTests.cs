using Xunit;

namespace Nexus.Core.Tests;

public class CrcCalculatorTests
{
    // ── CRC16-Modbus ───────────────────────────────────

    [Fact]
    public void ComputeCrc16_EmptyArray_ReturnsInitValue()
    {
        ushort crc = CrcCalculator.ComputeCrc16(new byte[0]);
        Assert.Equal(0xFFFF, crc);
    }

    [Fact]
    public void ComputeCrc16_SingleByte_NonZero()
    {
        ushort crc = CrcCalculator.ComputeCrc16(new byte[] { 0x01 });
        Assert.NotEqual(0, crc);
    }

    [Fact]
    public void ComputeCrc16_Deterministic_SameInputSameOutput()
    {
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        ushort first = CrcCalculator.ComputeCrc16(data);
        ushort second = CrcCalculator.ComputeCrc16(data);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeCrc16_DifferentInputs_DifferentOutput()
    {
        ushort crc1 = CrcCalculator.ComputeCrc16(new byte[] { 0x01, 0x03 });
        ushort crc2 = CrcCalculator.ComputeCrc16(new byte[] { 0x01, 0x04 });
        Assert.NotEqual(crc1, crc2);
    }

    [Fact]
    public void ComputeCrc16_ModbusFrame_FC03_Read()
    {
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        ushort crc = CrcCalculator.ComputeCrc16(data);
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, (byte)(crc & 0xFF), (byte)(crc >> 8) };
        Assert.True(CrcCalculator.VerifyCrc16(frame));
    }

    [Fact]
    public void ComputeCrc16_ModbusFrame_FC06_Write()
    {
        byte[] data = { 0x01, 0x06, 0x00, 0x10, 0x12, 0x34 };
        ushort crc = CrcCalculator.ComputeCrc16(data);
        byte[] frame = { 0x01, 0x06, 0x00, 0x10, 0x12, 0x34, (byte)(crc & 0xFF), (byte)(crc >> 8) };
        Assert.True(CrcCalculator.VerifyCrc16(frame));
    }

    [Fact]
    public void ComputeCrc16_ModbusFrame_FC16_WriteMultiple()
    {
        byte[] data = { 0x01, 0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x00, 0x64, 0x00, 0xC8 };
        ushort crc = CrcCalculator.ComputeCrc16(data);
        Assert.NotEqual(0, crc);
        byte[] frame = new byte[data.Length + 2];
        Array.Copy(data, frame, data.Length);
        frame[data.Length] = (byte)(crc & 0xFF);
        frame[data.Length + 1] = (byte)(crc >> 8);
        Assert.True(CrcCalculator.VerifyCrc16(frame));
    }

    [Fact]
    public void ComputeCrc16_WithOffsetAndLength()
    {
        byte[] data = { 0xFF, 0xFF, 0x01, 0x03, 0x00, 0x00, 0xFF, 0xFF };
        ushort partial = CrcCalculator.ComputeCrc16(data, 2, 4);
        ushort direct = CrcCalculator.ComputeCrc16(new byte[] { 0x01, 0x03, 0x00, 0x00 });
        Assert.Equal(direct, partial);
    }

    [Fact]
    public void VerifyCrc16_ValidFrame_ReturnsTrue()
    {
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };
        ushort crc = CrcCalculator.ComputeCrc16(data);
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(crc & 0xFF), (byte)(crc >> 8) };
        Assert.True(CrcCalculator.VerifyCrc16(frame));
    }

    [Fact]
    public void VerifyCrc16_InvalidFrame_ReturnsFalse()
    {
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00 };
        Assert.False(CrcCalculator.VerifyCrc16(frame));
    }

    [Fact]
    public void VerifyCrc16_Null_ReturnsFalse()
    {
        Assert.False(CrcCalculator.VerifyCrc16(null!));
    }

    [Fact]
    public void VerifyCrc16_TooShort_ReturnsFalse()
    {
        Assert.False(CrcCalculator.VerifyCrc16(new byte[] { 0x01 }));
        Assert.False(CrcCalculator.VerifyCrc16(new byte[] { 0x01, 0x02 }));
    }

    // ── LRC ────────────────────────────────────────────

    [Fact]
    public void ComputeLrc_EmptyArray_ReturnsZero()
    {
        byte lrc = CrcCalculator.ComputeLrc(new byte[0]);
        Assert.Equal(0, lrc);
    }

    [Fact]
    public void ComputeLrc_SingleByte()
    {
        // LRC of {0x01} = (-1) & 0xFF = 0xFF
        byte lrc = CrcCalculator.ComputeLrc(new byte[] { 0x01 });
        Assert.Equal(0xFF, lrc);
    }

    [Fact]
    public void ComputeLrc_ModbusAscii_Frame()
    {
        // Station=1, FC03, Address=0x0000, Count=0x000A
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        byte lrc = CrcCalculator.ComputeLrc(data);
        // Sum = 0x0E, LRC = (-0x0E) & 0xFF = 0xF2
        Assert.Equal(0xF2, lrc);
    }

    [Fact]
    public void ComputeLrc_WithOffsetAndLength()
    {
        byte[] data = { 0xFF, 0xFF, 0x01, 0x03 };
        byte partial = CrcCalculator.ComputeLrc(data, 2, 2);
        byte direct = CrcCalculator.ComputeLrc(new byte[] { 0x01, 0x03 });
        Assert.Equal(direct, partial);
    }

    [Fact]
    public void ComputeLrc_AllZeros_ReturnsZero()
    {
        byte lrc = CrcCalculator.ComputeLrc(new byte[] { 0x00, 0x00, 0x00 });
        Assert.Equal(0, lrc);
    }

    [Fact]
    public void VerifyLrc_ValidFrame_ReturnsTrue()
    {
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        byte lrc = CrcCalculator.ComputeLrc(data);
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, lrc };
        Assert.True(CrcCalculator.VerifyLrc(frame));
    }

    [Fact]
    public void VerifyLrc_InvalidFrame_ReturnsFalse()
    {
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0x00 };
        Assert.False(CrcCalculator.VerifyLrc(frame));
    }

    [Fact]
    public void VerifyLrc_Null_ReturnsFalse()
    {
        Assert.False(CrcCalculator.VerifyLrc(null!));
    }

    [Fact]
    public void VerifyLrc_TooShort_ReturnsFalse()
    {
        Assert.False(CrcCalculator.VerifyLrc(new byte[] { 0x01 }));
    }

    [Fact]
    public void ComputeLrc_Deterministic()
    {
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        Assert.Equal(CrcCalculator.ComputeLrc(data), CrcCalculator.ComputeLrc(data));
    }
}
