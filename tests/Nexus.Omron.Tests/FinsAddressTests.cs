using System;
using Xunit;
using Nexus.Omron;

namespace Nexus.Omron.Tests;

/// <summary>
/// FINS 地址解析器测试。
/// </summary>
public class FinsAddressTests
{
    private readonly FinsAddressParser _parser = new FinsAddressParser();

    // ── DM 区域 ──────────────────────────────

    [Fact]
    public void Parse_D100_ReturnsDMArea()
    {
        var addr = _parser.Parse("D100");
        Assert.Equal(FinsMemoryArea.DM, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
        Assert.Equal(-1, addr.BitOffset);
    }

    [Fact]
    public void Parse_DM200_ReturnsDMArea()
    {
        var addr = _parser.Parse("DM200");
        Assert.Equal(FinsMemoryArea.DM, addr.Area);
        Assert.Equal((ushort)200, addr.WordAddress);
    }

    [Fact]
    public void Parse_D0_ReturnsDMAreaZero()
    {
        var addr = _parser.Parse("D0");
        Assert.Equal(FinsMemoryArea.DM, addr.Area);
        Assert.Equal((ushort)0, addr.WordAddress);
    }

    // ── CIO 区域 ──────────────────────────────

    [Fact]
    public void Parse_CIO100_ReturnsCIOArea()
    {
        var addr = _parser.Parse("CIO100");
        Assert.Equal(FinsMemoryArea.CIO, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
    }

    [Fact]
    public void Parse_CIO0_ReturnsCIOAreaZero()
    {
        var addr = _parser.Parse("CIO0");
        Assert.Equal(FinsMemoryArea.CIO, addr.Area);
        Assert.Equal((ushort)0, addr.WordAddress);
    }

    // ── WR 区域 ──────────────────────────────

    [Fact]
    public void Parse_W100_ReturnsWRArea()
    {
        var addr = _parser.Parse("W100");
        Assert.Equal(FinsMemoryArea.WR, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
    }

    [Fact]
    public void Parse_WR50_ReturnsWRArea()
    {
        var addr = _parser.Parse("WR50");
        Assert.Equal(FinsMemoryArea.WR, addr.Area);
        Assert.Equal((ushort)50, addr.WordAddress);
    }

    // ── HR 区域 ──────────────────────────────

    [Fact]
    public void Parse_H100_ReturnsHRArea()
    {
        var addr = _parser.Parse("H100");
        Assert.Equal(FinsMemoryArea.HR, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
    }

    [Fact]
    public void Parse_HR50_ReturnsHRArea()
    {
        var addr = _parser.Parse("HR50");
        Assert.Equal(FinsMemoryArea.HR, addr.Area);
        Assert.Equal((ushort)50, addr.WordAddress);
    }

    // ── AR 区域 ──────────────────────────────

    [Fact]
    public void Parse_A100_ReturnsARArea()
    {
        var addr = _parser.Parse("A100");
        Assert.Equal(FinsMemoryArea.AR, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
    }

    [Fact]
    public void Parse_AR50_ReturnsARArea()
    {
        var addr = _parser.Parse("AR50");
        Assert.Equal(FinsMemoryArea.AR, addr.Area);
        Assert.Equal((ushort)50, addr.WordAddress);
    }

    // ── EM 区域 ──────────────────────────────

    [Fact]
    public void Parse_E0_100_ReturnsEMArea()
    {
        var addr = _parser.Parse("E0_100");
        Assert.Equal(FinsMemoryArea.EM, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
        Assert.Equal((byte)0, addr.EmBank);
    }

    [Fact]
    public void Parse_E1_200_ReturnsEMAreaBank1()
    {
        var addr = _parser.Parse("E1_200");
        Assert.Equal(FinsMemoryArea.EM, addr.Area);
        Assert.Equal((ushort)200, addr.WordAddress);
        Assert.Equal((byte)1, addr.EmBank);
    }

    // ── 位偏移 ──────────────────────────────

    [Fact]
    public void Parse_D100_03_ReturnsBitOffset3()
    {
        var addr = _parser.Parse("D100.03");
        Assert.Equal(FinsMemoryArea.DM, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
        Assert.Equal(3, addr.BitOffset);
    }

    [Fact]
    public void Parse_CIO100_15_ReturnsBitOffset15()
    {
        var addr = _parser.Parse("CIO100.15");
        Assert.Equal(FinsMemoryArea.CIO, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
        Assert.Equal(15, addr.BitOffset);
    }

    [Fact]
    public void Parse_D0_00_ReturnsBitOffset0()
    {
        var addr = _parser.Parse("D0.00");
        Assert.Equal(FinsMemoryArea.DM, addr.Area);
        Assert.Equal((ushort)0, addr.WordAddress);
        Assert.Equal(0, addr.BitOffset);
    }

    // ── 纯数字 → 默认 DM ──────────────────────

    [Fact]
    public void Parse_PlainNumber_DefaultsToDM()
    {
        var addr = _parser.Parse("100");
        Assert.Equal(FinsMemoryArea.DM, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
    }

    [Fact]
    public void Parse_PlainZero_DefaultsToDM()
    {
        var addr = _parser.Parse("0");
        Assert.Equal(FinsMemoryArea.DM, addr.Area);
        Assert.Equal((ushort)0, addr.WordAddress);
    }

    // ── 大小写不敏感 ──────────────────────────

    [Fact]
    public void Parse_Lowercase_d100_Works()
    {
        var addr = _parser.Parse("d100");
        Assert.Equal(FinsMemoryArea.DM, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
    }

    [Fact]
    public void Parse_MixedCase_Cio100_Works()
    {
        var addr = _parser.Parse("Cio100");
        Assert.Equal(FinsMemoryArea.CIO, addr.Area);
        Assert.Equal((ushort)100, addr.WordAddress);
    }

    // ── TryParse 错误场景 ──────────────────────

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(_parser.TryParse(null!, out _));
    }

    [Fact]
    public void TryParse_Empty_ReturnsFalse()
    {
        Assert.False(_parser.TryParse("", out _));
    }

    [Fact]
    public void TryParse_Whitespace_ReturnsFalse()
    {
        Assert.False(_parser.TryParse("   ", out _));
    }

    [Fact]
    public void TryParse_InvalidPrefix_ReturnsFalse()
    {
        Assert.False(_parser.TryParse("XYZ100", out _));
    }

    [Fact]
    public void TryParse_BitOffsetOutOfRange_ReturnsFalse()
    {
        Assert.False(_parser.TryParse("D100.16", out _));
    }

    [Fact]
    public void TryParse_NegativeBitOffset_ReturnsFalse()
    {
        Assert.False(_parser.TryParse("D100.-1", out _));
    }

    // ── Parse 异常 ────────────────────────────

    [Fact]
    public void Parse_Null_ThrowsAddressParseException()
    {
        Assert.Throws<AddressParseException>(() => _parser.Parse(null!));
    }

    [Fact]
    public void Parse_Empty_ThrowsAddressParseException()
    {
        Assert.Throws<AddressParseException>(() => _parser.Parse(""));
    }

    [Fact]
    public void Parse_InvalidFormat_ThrowsAddressParseException()
    {
        Assert.Throws<AddressParseException>(() => _parser.Parse("XYZ"));
    }

    // ── ToString ──────────────────────────────

    [Fact]
    public void ToString_DM100_NoBit()
    {
        var addr = _parser.Parse("D100");
        Assert.Equal("D100", addr.ToString());
    }

    [Fact]
    public void ToString_DM100_Bit3()
    {
        var addr = _parser.Parse("D100.03");
        Assert.Equal("D100.03", addr.ToString());
    }

    [Fact]
    public void ToString_CIO100_NoBit()
    {
        var addr = _parser.Parse("CIO100");
        Assert.Equal("CIO100", addr.ToString());
    }

    [Fact]
    public void ToString_EM0_100()
    {
        var addr = _parser.Parse("E0_100");
        Assert.Equal("E0_100", addr.ToString());
    }

    // ── Original 属性 ────────────────────────

    [Fact]
    public void Parse_PreservesOriginal_WithSpaces()
    {
        var addr = _parser.Parse("  D100  ");
        Assert.Equal("D100", addr.Original); // Trimmed
    }

    // ── T/C 区域 ──────────────────────────────

    [Fact]
    public void Parse_T10_ReturnsTimerPV()
    {
        var addr = _parser.Parse("T10");
        Assert.Equal(FinsMemoryArea.TimerPV, addr.Area);
        Assert.Equal((ushort)10, addr.WordAddress);
    }

    [Fact]
    public void Parse_C5_ReturnsCounterPV()
    {
        var addr = _parser.Parse("C5");
        Assert.Equal(FinsMemoryArea.CounterPV, addr.Area);
        Assert.Equal((ushort)5, addr.WordAddress);
    }
}
