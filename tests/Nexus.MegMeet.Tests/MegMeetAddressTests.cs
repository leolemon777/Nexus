using Nexus;
using Nexus.MegMeet;
using Xunit;

namespace Nexus.MegMeet.Tests;

public class MegMeetAddressTests
{
    // ── Input X (octal, read-only) ──────────────────────

    [Fact]
    public void Parse_X0_Input_OctalAddress()
    {
        var addr = MegMeetAddress.Parse("X0");
        Assert.Equal(MegMeetArea.Input, addr.Area);
        Assert.Equal(0, addr.Address);
        Assert.Equal(0x02, addr.ReadFunctionCode);
        Assert.Equal(0x00, addr.WriteFunctionCode);
        Assert.True(addr.IsReadOnly);
        Assert.True(addr.IsBitArea);
    }

    [Fact]
    public void Parse_X10_Input_Octal10()
    {
        var addr = MegMeetAddress.Parse("X10");
        Assert.Equal(MegMeetArea.Input, addr.Area);
        Assert.Equal(8, addr.Address);
        Assert.Equal(0x02, addr.ReadFunctionCode);
    }

    [Fact]
    public void Parse_X177_Input_Octal127()
    {
        var addr = MegMeetAddress.Parse("X177");
        Assert.Equal(MegMeetArea.Input, addr.Area);
        Assert.Equal(127, addr.Address);
    }

    // ── Output Y (octal) ───────────────────────────────

    [Fact]
    public void Parse_Y0_Output_OctalAddress()
    {
        var addr = MegMeetAddress.Parse("Y0");
        Assert.Equal(MegMeetArea.Output, addr.Area);
        Assert.Equal(0, addr.Address);
        Assert.Equal(0x01, addr.ReadFunctionCode);
        Assert.Equal(0x05, addr.WriteFunctionCode);
        Assert.False(addr.IsReadOnly);
    }

    [Fact]
    public void Parse_Y10_Output_Octal8()
    {
        var addr = MegMeetAddress.Parse("Y10");
        Assert.Equal(MegMeetArea.Output, addr.Area);
        Assert.Equal(8, addr.Address);
    }

    // ── Internal Relay M ───────────────────────────────

    [Fact]
    public void Parse_M0_LowRange()
    {
        var addr = MegMeetAddress.Parse("M0");
        Assert.Equal(MegMeetArea.InternalRelay, addr.Area);
        Assert.Equal(2000, addr.Address);
        Assert.Equal(0x01, addr.ReadFunctionCode);
        Assert.Equal(0x05, addr.WriteFunctionCode);
    }

    [Fact]
    public void Parse_M2047_HighRangeStart()
    {
        var addr = MegMeetAddress.Parse("M2048");
        Assert.Equal(MegMeetArea.InternalRelay, addr.Area);
        Assert.Equal(12000, addr.Address);
    }

    // ── Special Relay SM ───────────────────────────────

    [Fact]
    public void Parse_SM0_LowRange()
    {
        var addr = MegMeetAddress.Parse("SM0");
        Assert.Equal(MegMeetArea.SpecialRelay, addr.Area);
        Assert.Equal(4400, addr.Address);
        Assert.Equal(0x01, addr.ReadFunctionCode);
    }

    [Fact]
    public void Parse_SM256_HighRange()
    {
        var addr = MegMeetAddress.Parse("SM256");
        Assert.Equal(MegMeetArea.SpecialRelay, addr.Area);
        Assert.Equal(30000, addr.Address);
    }

    // ── Step Relay S ───────────────────────────────────

    [Fact]
    public void Parse_S0_LowRange()
    {
        var addr = MegMeetAddress.Parse("S0");
        Assert.Equal(MegMeetArea.StepRelay, addr.Area);
        Assert.Equal(6000, addr.Address);
    }

    [Fact]
    public void Parse_S1024_HighRange()
    {
        var addr = MegMeetAddress.Parse("S1024");
        Assert.Equal(MegMeetArea.StepRelay, addr.Area);
        Assert.Equal(31000, addr.Address);
    }

    // ── Timer T (bit) ──────────────────────────────────

    [Fact]
    public void Parse_T0_TimerContact_LowRange()
    {
        var addr = MegMeetAddress.Parse("T0");
        Assert.Equal(MegMeetArea.TimerContact, addr.Area);
        Assert.Equal(8000, addr.Address);
        Assert.Equal(0x01, addr.ReadFunctionCode);
    }

    // ── Counter C (bit) ────────────────────────────────

    [Fact]
    public void Parse_C0_CounterContact_LowRange()
    {
        var addr = MegMeetAddress.Parse("C0");
        Assert.Equal(MegMeetArea.CounterContact, addr.Area);
        Assert.Equal(9200, addr.Address);
    }

    // ── Data Register D ────────────────────────────────

    [Fact]
    public void Parse_D0_DataRegister()
    {
        var addr = MegMeetAddress.Parse("D0");
        Assert.Equal(MegMeetArea.DataRegister, addr.Area);
        Assert.Equal(0, addr.Address);
        Assert.Equal(0x03, addr.ReadFunctionCode);
        Assert.Equal(0x06, addr.WriteFunctionCode);
        Assert.True(addr.IsWordArea);
    }

    [Fact]
    public void Parse_D100_DataRegister()
    {
        var addr = MegMeetAddress.Parse("D100");
        Assert.Equal(MegMeetArea.DataRegister, addr.Area);
        Assert.Equal(100, addr.Address);
    }

    [Fact]
    public void Parse_BareNumber_DefaultsToDataRegister()
    {
        var addr = MegMeetAddress.Parse("500");
        Assert.Equal(MegMeetArea.DataRegister, addr.Area);
        Assert.Equal(500, addr.Address);
    }

    // ── Special Register SD ────────────────────────────

    [Fact]
    public void Parse_SD0_LowRange()
    {
        var addr = MegMeetAddress.Parse("SD0");
        Assert.Equal(MegMeetArea.SpecialRegister, addr.Area);
        Assert.Equal(8000, addr.Address);
        Assert.Equal(0x03, addr.ReadFunctionCode);
    }

    [Fact]
    public void Parse_SD256_HighRange()
    {
        var addr = MegMeetAddress.Parse("SD256");
        Assert.Equal(MegMeetArea.SpecialRegister, addr.Area);
        Assert.Equal(12000, addr.Address);
    }

    // ── Index Register Z ───────────────────────────────

    [Fact]
    public void Parse_Z0_IndexRegister()
    {
        var addr = MegMeetAddress.Parse("Z0");
        Assert.Equal(MegMeetArea.IndexRegister, addr.Area);
        Assert.Equal(8500, addr.Address);
    }

    // ── File Register R ────────────────────────────────

    [Fact]
    public void Parse_R0_FileRegister()
    {
        var addr = MegMeetAddress.Parse("R0");
        Assert.Equal(MegMeetArea.FileRegister, addr.Area);
        Assert.Equal(13000, addr.Address);
    }

    // ── Timer/Counter Word ─────────────────────────────

    [Fact]
    public void ParseTimerWord_LowRange()
    {
        var addr = MegMeetAddress.ParseTimerWord(0);
        Assert.Equal(MegMeetArea.TimerValue, addr.Area);
        Assert.Equal(9000, addr.Address);
        Assert.Equal(0x03, addr.ReadFunctionCode);
        Assert.True(addr.IsWordArea);
    }

    [Fact]
    public void ParseCounterWord_LowRange()
    {
        var addr = MegMeetAddress.ParseCounterWord(0);
        Assert.Equal(MegMeetArea.CounterValue, addr.Area);
        Assert.Equal(9500, addr.Address);
    }

    // ── TryParse ───────────────────────────────────────

    [Fact]
    public void TryParse_Valid_ReturnsNonNull()
    {
        var addr = MegMeetAddress.TryParse("D100");
        Assert.NotNull(addr);
        Assert.Equal(MegMeetArea.DataRegister, addr!.Area);
    }

    [Fact]
    public void TryParse_Invalid_ReturnsNull()
    {
        var addr = MegMeetAddress.TryParse("");
        Assert.Null(addr);
    }

    [Fact]
    public void TryParse_UnsupportedPrefix_ReturnsNull()
    {
        var addr = MegMeetAddress.TryParse("Q99");
        Assert.Null(addr);
    }

    // ── ToString ───────────────────────────────────────

    [Fact]
    public void ToString_ContainsAreaAndModbusAddress()
    {
        var addr = MegMeetAddress.Parse("D100");
        string s = addr.ToString();
        Assert.Contains("DataRegister", s);
        Assert.Contains("0x0064", s);
    }
}
