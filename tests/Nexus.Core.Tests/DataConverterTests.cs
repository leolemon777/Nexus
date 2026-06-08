using Xunit;

namespace Nexus.Core.Tests;

public class DataConverterTests
{
    // ── 字节数组 → 值（大端序）─────────────────────

    [Fact]
    public void ToInt16_BigEndian_FromKnownBytes()
    {
        // 0x12 0x34 = 0x1234 = 4660
        Assert.Equal((short)0x1234, DataConverter.ToInt16(new byte[] { 0x12, 0x34 }));
    }

    [Fact]
    public void ToInt16_WithOffset()
    {
        var data = new byte[] { 0x00, 0x00, 0x56, 0x78 };
        Assert.Equal((short)0x5678, DataConverter.ToInt16(data, 2));
    }

    [Fact]
    public void ToInt32_BigEndian_FromKnownBytes()
    {
        // 0x12 0x34 0x56 0x78 = 0x12345678
        Assert.Equal(0x12345678, DataConverter.ToInt32(new byte[] { 0x12, 0x34, 0x56, 0x78 }));
    }

    [Fact]
    public void ToFloat_BigEndian_ReinterpretsBits()
    {
        // 0x40490FDB = IEEE 754 float 3.1415927
        var bytes = new byte[] { 0x40, 0x49, 0x0F, 0xDB };
        Assert.Equal(3.1415927f, DataConverter.ToFloat(bytes), 5);
    }

    [Fact]
    public void ToDouble_BigEndian_ReinterpretsBits()
    {
        // 0x40091EB851EB851F = IEEE 754 double 3.14 (approx)
        var bytes = new byte[] { 0x40, 0x09, 0x1E, 0xB8, 0x51, 0xEB, 0x85, 0x1F };
        Assert.Equal(3.14, DataConverter.ToDouble(bytes), 10);
    }

    [Fact]
    public void ToBool_TrueWhenNonzero()
    {
        Assert.True(DataConverter.ToBool(new byte[] { 0x01 }));
        Assert.True(DataConverter.ToBool(new byte[] { 0xFF }));
    }

    [Fact]
    public void ToBool_FalseWhenZero()
    {
        Assert.False(DataConverter.ToBool(new byte[] { 0x00 }));
    }

    [Fact]
    public void ToString_StripsTrailingNullsAndSpaces()
    {
        var data = new byte[] { (byte)'H', (byte)'i', 0x00, 0x00 };
        Assert.Equal("Hi", DataConverter.ToString(data, 0, 4));
    }

    [Fact]
    public void ToHexString_FormatsUppercaseWithSpaces()
    {
        var hex = DataConverter.ToHexString(new byte[] { 0xAB, 0xCD, 0xEF });
        Assert.Equal("AB CD EF", hex);
    }

    [Fact]
    public void ToHexString_NullSafe_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DataConverter.ToHexString(null!));
    }

    // ── 值 → 字节数组（大端序）─────────────────────

    [Fact]
    public void GetBytes_Bool_ZeroOrOne()
    {
        Assert.Equal(new byte[] { 1 }, DataConverter.GetBytes(true));
        Assert.Equal(new byte[] { 0 }, DataConverter.GetBytes(false));
    }

    [Fact]
    public void GetBytes_Int16_BigEndian()
    {
        var b = DataConverter.GetBytes((short)0x1234);
        Assert.Equal(new byte[] { 0x12, 0x34 }, b);
    }

    [Fact]
    public void GetBytes_Int32_BigEndian()
    {
        var b = DataConverter.GetBytes(0x12345678);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, b);
    }

    [Fact]
    public void GetBytes_Float_BigEndian()
    {
        var b = DataConverter.GetBytes(3.1415927f);
        Assert.Equal(new byte[] { 0x40, 0x49, 0x0F, 0xDB }, b);
    }

    [Fact]
    public void GetBytes_Float_RoundTrip()
    {
        float original = 3.14159f;
        var b = DataConverter.GetBytes(original);
        Assert.Equal(original, DataConverter.ToFloat(b));
    }

    [Fact]
    public void GetBytes_String_Ascii()
    {
        var b = DataConverter.GetBytes("Hi");
        Assert.Equal(new byte[] { (byte)'H', (byte)'i' }, b);
    }

    // ── 字节序枚举 ─────────────────────────────

    [Fact]
    public void Endianness_EnumHasFourDistinctValuesWithAliases()
    {
        var values = Enum.GetValues<Endianness>();
        // 4 underlying values + 4 aliases = 8 named members
        Assert.Equal(8, values.Length);
        Assert.Contains(Endianness.BigEndian, values);
        Assert.Contains(Endianness.LittleEndian, values);
        Assert.Contains(Endianness.MidBigEndian, values);
        Assert.Contains(Endianness.MidLittleEndian, values);
        // aliases
        Assert.Contains(Endianness.Abcd, values);
        Assert.Contains(Endianness.Dcba, values);
        Assert.Contains(Endianness.Badc, values);
        Assert.Contains(Endianness.Cdab, values);
    }
}
