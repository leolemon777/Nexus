using System;
using Xunit;
using Nexus;
using Nexus.Siemens;

namespace Nexus.Siemens.Tests;

public class S7AddressParseTests
{
    // ── DB addresses ───────────────────────────────────

    [Fact]
    public void Parse_DB_Word()
    {
        var addr = SiemensS7Address.Parse("DB1.DBW0");
        Assert.Equal(S7Area.DB, addr.Area);
        Assert.Equal(1, addr.DBNumber);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(0, addr.BitOffset);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_DB_DWord()
    {
        var addr = SiemensS7Address.Parse("DB10.DBD20");
        Assert.Equal(S7Area.DB, addr.Area);
        Assert.Equal(10, addr.DBNumber);
        Assert.Equal(20, addr.ByteAddress);
        Assert.Equal(4, addr.DataSize);
    }

    [Fact]
    public void Parse_DB_Byte()
    {
        var addr = SiemensS7Address.Parse("DB1.DBB10");
        Assert.Equal(S7Area.DB, addr.Area);
        Assert.Equal(1, addr.DBNumber);
        Assert.Equal(10, addr.ByteAddress);
        Assert.Equal(1, addr.DataSize);
    }

    [Fact]
    public void Parse_DB_Bit()
    {
        var addr = SiemensS7Address.Parse("DB1.DBX5.3");
        Assert.Equal(S7Area.DB, addr.Area);
        Assert.Equal(1, addr.DBNumber);
        Assert.Equal(5, addr.ByteAddress);
        Assert.Equal(3, addr.BitOffset);
        Assert.Equal(1, addr.DataSize);
    }

    [Fact]
    public void Parse_DB_CaseInsensitive()
    {
        var addr = SiemensS7Address.Parse("db1.dbw0");
        Assert.Equal(S7Area.DB, addr.Area);
        Assert.Equal(1, addr.DBNumber);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_DB_LargeNumber()
    {
        var addr = SiemensS7Address.Parse("DB9999.DBW0");
        Assert.Equal(9999, addr.DBNumber);
    }

    // ── Marker (M) addresses ──────────────────────────

    [Fact]
    public void Parse_Marker_Word()
    {
        var addr = SiemensS7Address.Parse("MW10");
        Assert.Equal(S7Area.MK, addr.Area);
        Assert.Equal(10, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_Marker_DWord()
    {
        var addr = SiemensS7Address.Parse("MD20");
        Assert.Equal(S7Area.MK, addr.Area);
        Assert.Equal(20, addr.ByteAddress);
        Assert.Equal(4, addr.DataSize);
    }

    [Fact]
    public void Parse_Marker_BareNumber_DefaultsToWord()
    {
        var addr = SiemensS7Address.Parse("M0");
        Assert.Equal(S7Area.MK, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_Marker_Bit()
    {
        var addr = SiemensS7Address.Parse("M0.5");
        Assert.Equal(S7Area.MK, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(5, addr.BitOffset);
        Assert.Equal(1, addr.DataSize);
    }

    [Fact]
    public void Parse_Marker_WithPrefixB_DefaultsToWord()
    {
        var addr = SiemensS7Address.Parse("MB0");
        Assert.Equal(S7Area.MK, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    // ── Input (I) addresses ───────────────────────────

    [Fact]
    public void Parse_Input_Word()
    {
        var addr = SiemensS7Address.Parse("IW0");
        Assert.Equal(S7Area.PE, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_Input_DWord()
    {
        var addr = SiemensS7Address.Parse("ID4");
        Assert.Equal(S7Area.PE, addr.Area);
        Assert.Equal(4, addr.ByteAddress);
        Assert.Equal(4, addr.DataSize);
    }

    [Fact]
    public void Parse_Input_Bit()
    {
        var addr = SiemensS7Address.Parse("I0.1");
        Assert.Equal(S7Area.PE, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(1, addr.BitOffset);
    }

    [Fact]
    public void Parse_Input_BareNumber_DefaultsToWord()
    {
        var addr = SiemensS7Address.Parse("I0");
        Assert.Equal(S7Area.PE, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    // ── Output (Q) addresses ──────────────────────────

    [Fact]
    public void Parse_Output_Word()
    {
        var addr = SiemensS7Address.Parse("QW0");
        Assert.Equal(S7Area.PA, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_Output_DWord()
    {
        var addr = SiemensS7Address.Parse("QD4");
        Assert.Equal(S7Area.PA, addr.Area);
        Assert.Equal(4, addr.ByteAddress);
        Assert.Equal(4, addr.DataSize);
    }

    [Fact]
    public void Parse_Output_Bit()
    {
        var addr = SiemensS7Address.Parse("Q0.3");
        Assert.Equal(S7Area.PA, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(3, addr.BitOffset);
    }

    [Fact]
    public void Parse_Output_BareNumber_DefaultsToWord()
    {
        var addr = SiemensS7Address.Parse("Q0");
        Assert.Equal(S7Area.PA, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    // ── V area (S7-200) ───────────────────────────────

    [Fact]
    public void Parse_V_Word()
    {
        var addr = SiemensS7Address.Parse("VW100");
        Assert.Equal(S7Area.V, addr.Area);
        Assert.Equal(100, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_V_DWord()
    {
        var addr = SiemensS7Address.Parse("VD200");
        Assert.Equal(S7Area.V, addr.Area);
        Assert.Equal(200, addr.ByteAddress);
        Assert.Equal(4, addr.DataSize);
    }

    [Fact]
    public void Parse_V_Bit()
    {
        var addr = SiemensS7Address.Parse("V10.2");
        Assert.Equal(S7Area.V, addr.Area);
        Assert.Equal(10, addr.ByteAddress);
        Assert.Equal(2, addr.BitOffset);
    }

    // ── Timer (T) and Counter (C) ─────────────────────

    [Fact]
    public void Parse_Timer()
    {
        var addr = SiemensS7Address.Parse("T0");
        Assert.Equal(S7Area.TM, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_Timer_LargeIndex()
    {
        var addr = SiemensS7Address.Parse("T255");
        Assert.Equal(S7Area.TM, addr.Area);
        Assert.Equal(255, addr.ByteAddress);
    }

    [Fact]
    public void Parse_Counter()
    {
        var addr = SiemensS7Address.Parse("C0");
        Assert.Equal(S7Area.CT, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_Counter_LargeIndex()
    {
        var addr = SiemensS7Address.Parse("C255");
        Assert.Equal(S7Area.CT, addr.Area);
        Assert.Equal(255, addr.ByteAddress);
    }

    // ── German aliases (EB/AB) ────────────────────────

    [Fact]
    public void Parse_GermanInput_EB()
    {
        var addr = SiemensS7Address.Parse("EB0");
        Assert.Equal(S7Area.PE, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    [Fact]
    public void Parse_GermanOutput_AB()
    {
        var addr = SiemensS7Address.Parse("AB0");
        Assert.Equal(S7Area.PA, addr.Area);
        Assert.Equal(0, addr.ByteAddress);
        Assert.Equal(2, addr.DataSize);
    }

    // ── TryParse ───────────────────────────────────────

    [Fact]
    public void TryParse_Valid_ReturnsTrue()
    {
        Assert.True(SiemensS7Address.TryParse("DB1.DBW0", out var addr));
        Assert.NotNull(addr);
        Assert.Equal(S7Area.DB, addr!.Area);
    }

    [Fact]
    public void TryParse_Invalid_ReturnsFalse()
    {
        Assert.False(SiemensS7Address.TryParse("", out var addr));
        Assert.Null(addr);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(SiemensS7Address.TryParse(null!, out var addr));
        Assert.Null(addr);
    }

    [Fact]
    public void TryParse_UnsupportedFormat_ReturnsFalse()
    {
        Assert.False(SiemensS7Address.TryParse("XYZ", out var addr));
        Assert.Null(addr);
    }

    // ── Error cases ────────────────────────────────────

    [Fact]
    public void Parse_Empty_Throws()
    {
        Assert.Throws<AddressParseException>(() => SiemensS7Address.Parse(""));
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<AddressParseException>(() => SiemensS7Address.Parse(null!));
    }

    [Fact]
    public void Parse_UnsupportedFormat_Throws()
    {
        Assert.Throws<AddressParseException>(() => SiemensS7Address.Parse("XYZ"));
    }

    [Fact]
    public void Parse_DB_MissingDot_Throws()
    {
        Assert.Throws<AddressParseException>(() => SiemensS7Address.Parse("DB1DBW0"));
    }

    // ── ToString ───────────────────────────────────────

    [Fact]
    public void ToString_ReturnsOriginal()
    {
        var addr = SiemensS7Address.Parse("DB1.DBW0");
        Assert.Equal("DB1.DBW0", addr.ToString());
    }

    [Fact]
    public void ToString_CasePreserved()
    {
        var addr = SiemensS7Address.Parse("db1.dbw0");
        Assert.Equal("db1.dbw0", addr.ToString());
    }
}
