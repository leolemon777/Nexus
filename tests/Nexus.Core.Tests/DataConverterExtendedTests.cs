using Xunit;

namespace Nexus.Core.Tests;

public class DataConverterExtendedTests
{
    // ── UInt16 ─────────────────────────────────────────

    [Fact]
    public void ToUInt16_BigEndian()
    {
        Assert.Equal((ushort)0x1234, DataConverter.ToUInt16(new byte[] { 0x12, 0x34 }));
    }

    [Fact]
    public void ToUInt16_LittleEndian()
    {
        Assert.Equal((ushort)0x1234, DataConverter.ToUInt16(new byte[] { 0x34, 0x12 }, 0, Endianness.LittleEndian));
    }

    [Fact]
    public void ToUInt16_WithOffset()
    {
        var data = new byte[] { 0x00, 0x00, 0xAB, 0xCD };
        Assert.Equal((ushort)0xABCD, DataConverter.ToUInt16(data, 2));
    }

    // ── UInt32 ─────────────────────────────────────────

    [Fact]
    public void ToUInt32_BigEndian()
    {
        Assert.Equal(0x12345678u, DataConverter.ToUInt32(new byte[] { 0x12, 0x34, 0x56, 0x78 }));
    }

    [Fact]
    public void ToUInt32_LittleEndian()
    {
        Assert.Equal(0x12345678u, DataConverter.ToUInt32(new byte[] { 0x78, 0x56, 0x34, 0x12 }, 0, Endianness.LittleEndian));
    }

    [Fact]
    public void ToUInt32_WithOffset()
    {
        var data = new byte[] { 0x00, 0x00, 0x12, 0x34, 0x56, 0x78 };
        Assert.Equal(0x12345678u, DataConverter.ToUInt32(data, 2));
    }

    // ── Int64 ──────────────────────────────────────────

    [Fact]
    public void ToInt64_BigEndian_Positive()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x12, 0x34, 0x56, 0x78 };
        Assert.Equal(0x12345678L, DataConverter.ToInt64(bytes));
    }

    [Fact]
    public void ToInt64_BigEndian_Negative()
    {
        var bytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Equal(-1L, DataConverter.ToInt64(bytes));
    }

    [Fact]
    public void ToInt64_WithOffset()
    {
        var data = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        Assert.Equal(0x0102030405060708L, DataConverter.ToInt64(data, 8));
    }

    // ── UInt64 ─────────────────────────────────────────

    [Fact]
    public void ToUInt64_BigEndian()
    {
        var bytes = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
        Assert.Equal(0x0123456789ABCDEFUL, DataConverter.ToUInt64(bytes));
    }

    [Fact]
    public void ToUInt64_LittleEndian()
    {
        var bytes = new byte[] { 0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01 };
        Assert.Equal(0x0123456789ABCDEFUL, DataConverter.ToUInt64(bytes, 0, Endianness.LittleEndian));
    }

    // ── Negative values ────────────────────────────────

    [Fact]
    public void ToInt16_Negative()
    {
        // -1 in big-endian = 0xFFFF
        Assert.Equal((short)-1, DataConverter.ToInt16(new byte[] { 0xFF, 0xFF }));
    }

    [Fact]
    public void ToInt32_Negative()
    {
        // -1 in big-endian = 0xFFFFFFFF
        Assert.Equal(-1, DataConverter.ToInt32(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
    }

    [Fact]
    public void GetBytes_Int16_Negative_Roundtrip()
    {
        short value = -12345;
        var bytes = DataConverter.GetBytes(value);
        Assert.Equal(value, DataConverter.ToInt16(bytes));
    }

    [Fact]
    public void GetBytes_Int32_Negative_Roundtrip()
    {
        int value = -1234567890;
        var bytes = DataConverter.GetBytes(value);
        Assert.Equal(value, DataConverter.ToInt32(bytes));
    }

    [Fact]
    public void GetBytes_Int64_Negative_Roundtrip()
    {
        long value = -123456789012345L;
        var bytes = DataConverter.GetBytes(value);
        Assert.Equal(value, DataConverter.ToInt64(bytes));
    }

    // ── GetBytes additional types ──────────────────────

    [Fact]
    public void GetBytes_UInt16_BigEndian()
    {
        var b = DataConverter.GetBytes((ushort)0xABCD);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, b);
    }

    [Fact]
    public void GetBytes_UInt32_BigEndian()
    {
        var b = DataConverter.GetBytes(0x12345678u);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, b);
    }

    [Fact]
    public void GetBytes_Double_BigEndian()
    {
        double value = 3.14;
        var b = DataConverter.GetBytes(value);
        Assert.Equal(8, b.Length);
        Assert.Equal(value, DataConverter.ToDouble(b));
    }

    [Fact]
    public void GetBytes_Int16_AllEndianness_Roundtrip()
    {
        short value = 0x1234;
        foreach (Endianness bo in new[] { Endianness.BigEndian, Endianness.LittleEndian, Endianness.MidBigEndian, Endianness.MidLittleEndian })
        {
            byte[] bytes = DataConverter.GetBytes(value, bo);
            short recovered = DataConverter.ToInt16(bytes, 0, bo);
            Assert.Equal(value, recovered);
        }
    }

    [Fact]
    public void GetBytes_UInt16_AllEndianness_Roundtrip()
    {
        ushort value = 0x1234;
        foreach (Endianness bo in new[] { Endianness.BigEndian, Endianness.LittleEndian, Endianness.MidBigEndian, Endianness.MidLittleEndian })
        {
            byte[] bytes = DataConverter.GetBytes(value, bo);
            ushort recovered = DataConverter.ToUInt16(bytes, 0, bo);
            Assert.Equal(value, recovered);
        }
    }

    [Fact]
    public void GetBytes_Float_AllEndianness_Roundtrip()
    {
        float value = 3.14159f;
        foreach (Endianness bo in new[] { Endianness.BigEndian, Endianness.LittleEndian, Endianness.MidBigEndian, Endianness.MidLittleEndian })
        {
            byte[] bytes = DataConverter.GetBytes(value, bo);
            float recovered = DataConverter.ToFloat(bytes, 0, bo);
            Assert.Equal(value, recovered);
        }
    }

    [Fact]
    public void GetBytes_UInt32_AllEndianness_Roundtrip()
    {
        uint value = 0xDEADBEEF;
        foreach (Endianness bo in new[] { Endianness.BigEndian, Endianness.LittleEndian, Endianness.MidBigEndian, Endianness.MidLittleEndian })
        {
            byte[] bytes = DataConverter.GetBytes(value, bo);
            uint recovered = DataConverter.ToUInt32(bytes, 0, bo);
            Assert.Equal(value, recovered);
        }
    }

    // ── ToHexString overloads ──────────────────────────

    [Fact]
    public void ToHexString_WithOffsetLength()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        Assert.Equal("03 04", DataConverter.ToHexString(data, 2, 2));
    }

    [Fact]
    public void ToHexString_EmptyArray_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DataConverter.ToHexString(new byte[0]));
    }

    // ── Float/Double special values ────────────────────

    [Fact]
    public void ToFloat_Zero()
    {
        Assert.Equal(0f, DataConverter.ToFloat(new byte[] { 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void ToFloat_Negative()
    {
        // -3.14 = 0xC048F5C3
        var bytes = new byte[] { 0xC0, 0x48, 0xF5, 0xC3 };
        Assert.Equal(-3.14f, DataConverter.ToFloat(bytes), 2);
    }

    [Fact]
    public void ToDouble_Zero()
    {
        Assert.Equal(0d, DataConverter.ToDouble(new byte[8]));
    }

    [Fact]
    public void ToDouble_Negative()
    {
        double value = -3.14;
        var bytes = DataConverter.GetBytes(value);
        Assert.Equal(value, DataConverter.ToDouble(bytes), 10);
    }
}
