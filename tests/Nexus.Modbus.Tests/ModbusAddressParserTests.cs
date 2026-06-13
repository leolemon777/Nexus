using Xunit;
using Nexus;
using Nexus.Modbus;

namespace Nexus.Modbus.Tests;

public class ModbusAddressParserTests
{
    private readonly ModbusAddressParser _parser = new();

    // ── Holding Register (4xxxx) ───────────────────────

    [Fact]
    public void Parse_40001_HoldingRegister_Address1()
    {
        var addr = _parser.Parse("40001");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(1, addr.StartAddress);
        Assert.Equal(0x03, addr.ReadFunctionCode);
        Assert.Equal(0x06, addr.WriteFunctionCode);
    }

    [Fact]
    public void Parse_40010_HoldingRegister_Address10()
    {
        var addr = _parser.Parse("40010");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(10, addr.StartAddress);
    }

    [Fact]
    public void Parse_465535_HoldingRegister_MaxAddress()
    {
        var addr = _parser.Parse("465535");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(65535, addr.StartAddress);
    }

    [Fact]
    public void Parse_40000_HoldingRegister_ZeroAfterPrefix()
    {
        var addr = _parser.Parse("40000");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(0, addr.StartAddress);
    }

    // ── Coil (0xxxx) ──────────────────────────────────

    [Fact]
    public void Parse_00001_Coil_Address1()
    {
        var addr = _parser.Parse("00001");
        Assert.Equal(ModbusArea.Coil, addr.Area);
        Assert.Equal(1, addr.StartAddress);
        Assert.Equal(0x01, addr.ReadFunctionCode);
        Assert.Equal(0x05, addr.WriteFunctionCode);
    }

    [Fact]
    public void Parse_00010_Coil_Address10()
    {
        var addr = _parser.Parse("00010");
        Assert.Equal(ModbusArea.Coil, addr.Area);
        Assert.Equal(10, addr.StartAddress);
    }

    [Fact]
    public void Parse_09999_Coil_Address9999()
    {
        var addr = _parser.Parse("09999");
        Assert.Equal(ModbusArea.Coil, addr.Area);
        Assert.Equal(9999, addr.StartAddress);
    }

    // ── Discrete Input (1xxxx) ─────────────────────────

    [Fact]
    public void Parse_10001_DiscreteInput_Address1()
    {
        var addr = _parser.Parse("10001");
        Assert.Equal(ModbusArea.DiscreteInput, addr.Area);
        Assert.Equal(1, addr.StartAddress);
        Assert.Equal(0x02, addr.ReadFunctionCode);
        Assert.Equal(0x00, addr.WriteFunctionCode);
    }

    [Fact]
    public void Parse_10010_DiscreteInput_Address10()
    {
        var addr = _parser.Parse("10010");
        Assert.Equal(ModbusArea.DiscreteInput, addr.Area);
        Assert.Equal(10, addr.StartAddress);
    }

    // ── Input Register (3xxxx) ─────────────────────────

    [Fact]
    public void Parse_30001_InputRegister_Address1()
    {
        var addr = _parser.Parse("30001");
        Assert.Equal(ModbusArea.InputRegister, addr.Area);
        Assert.Equal(1, addr.StartAddress);
        Assert.Equal(0x04, addr.ReadFunctionCode);
        Assert.Equal(0x00, addr.WriteFunctionCode);
    }

    [Fact]
    public void Parse_30010_InputRegister_Address10()
    {
        var addr = _parser.Parse("30010");
        Assert.Equal(ModbusArea.InputRegister, addr.Area);
        Assert.Equal(10, addr.StartAddress);
    }

    // ── No prefix (default HoldingRegister) ────────────

    [Fact]
    public void Parse_NoPrefix_DefaultHoldingRegister()
    {
        var addr = _parser.Parse("100");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(100, addr.StartAddress);
    }

    [Fact]
    public void Parse_Zero_DefaultHoldingRegister()
    {
        var addr = _parser.Parse("0");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(0, addr.StartAddress);
    }

    [Fact]
    public void Parse_ShortAddress_DefaultHoldingRegister()
    {
        var addr = _parser.Parse("42");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(42, addr.StartAddress);
    }

    [Fact]
    public void Parse_1_DefaultHoldingRegister()
    {
        var addr = _parser.Parse("1");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(1, addr.StartAddress);
    }

    // ── AddressContext integration ─────────────────────

    [Fact]
    public void Parse_WithAddressContext_Prefix()
    {
        var addr = _parser.Parse("unit=1;40001");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(1, addr.StartAddress);
    }

    [Fact]
    public void Parse_WithAddressContext_ByteOrder()
    {
        var addr = _parser.Parse("bo=LittleEndian;40001");
        Assert.Equal(ModbusArea.HoldingRegister, addr.Area);
        Assert.Equal(1, addr.StartAddress);
    }

    // ── Error cases ────────────────────────────────────

    [Fact]
    public void Parse_Empty_Throws()
    {
        Assert.Throws<AddressParseException>(() => _parser.Parse(""));
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<AddressParseException>(() => _parser.Parse(null!));
    }

    [Fact]
    public void Parse_Whitespace_Throws()
    {
        Assert.Throws<AddressParseException>(() => _parser.Parse("   "));
    }

    [Fact]
    public void Parse_InvalidPrefix_Throws()
    {
        Assert.Throws<AddressParseException>(() => _parser.Parse("50001"));
    }

    // ── TryParse ───────────────────────────────────────

    [Fact]
    public void TryParse_Valid_ReturnsTrue()
    {
        Assert.True(_parser.TryParse("40001", out var addr));
        Assert.NotNull(addr);
        Assert.Equal(ModbusArea.HoldingRegister, addr!.Area);
    }

    [Fact]
    public void TryParse_Invalid_ReturnsFalse()
    {
        Assert.False(_parser.TryParse("", out var addr));
        Assert.Null(addr);
    }

    [Fact]
    public void TryParse_InvalidPrefix_ReturnsFalse()
    {
        Assert.False(_parser.TryParse("50001", out var addr));
        Assert.Null(addr);
    }

    // ── ToString ───────────────────────────────────────

    [Fact]
    public void ToString_ContainsAreaAndAddress()
    {
        var addr = _parser.Parse("40001");
        string s = addr.ToString();
        Assert.Contains("HoldingRegister", s);
        Assert.Contains("1", s);
    }

    // ── All 4 areas function code completeness ─────────

    [Fact]
    public void AllAreas_HaveCorrectFunctionCodes()
    {
        var coil = _parser.Parse("00001");
        Assert.Equal(0x01, coil.ReadFunctionCode);
        Assert.Equal(0x05, coil.WriteFunctionCode);

        var di = _parser.Parse("10001");
        Assert.Equal(0x02, di.ReadFunctionCode);
        Assert.Equal(0x00, di.WriteFunctionCode);

        var ir = _parser.Parse("30001");
        Assert.Equal(0x04, ir.ReadFunctionCode);
        Assert.Equal(0x00, ir.WriteFunctionCode);

        var hr = _parser.Parse("40001");
        Assert.Equal(0x03, hr.ReadFunctionCode);
        Assert.Equal(0x06, hr.WriteFunctionCode);
    }

    [Fact]
    public void AllAreas_HaveCorrectEnumValues()
    {
        Assert.Equal(0, (int)ModbusArea.Coil);
        Assert.Equal(1, (int)ModbusArea.DiscreteInput);
        Assert.Equal(2, (int)ModbusArea.InputRegister);
        Assert.Equal(3, (int)ModbusArea.HoldingRegister);
    }
}
