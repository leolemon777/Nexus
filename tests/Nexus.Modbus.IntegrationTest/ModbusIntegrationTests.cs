using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.IntegrationTest;

/// <summary>
/// Modbus TCP 端到端集成测试。完全覆盖原 Program.cs 全部断言。
/// </summary>
public class ModbusIntegrationTests : IClassFixture<ModbusServerFixture>
{
    private readonly ModbusServerFixture _fx;
    private ModbusTcpClient NewClient()
    {
        var c = new ModbusTcpClient("127.0.0.1", _fx.Port, station: 1);
        c.SetPersistentConnection();
        return c;
    }

    public ModbusIntegrationTests(ModbusServerFixture fx) => _fx = fx;

    // ── Connection & Events ────────────────────────────

    [Fact]
    public void Connect_Succeeds()
    {
        using var c = NewClient();
        Assert.True(c.Connect().IsSuccess);
        Assert.True(c.IsConnected);
    }

    [Fact]
    public void OnConnected_Event_Fires()
    {
        using var c = NewClient();
        bool fired = false;
        c.OnConnected += (_, _) => fired = true;
        c.Connect();
        Assert.True(fired);
    }

    [Fact]
    public void OnMessageSent_Event_Fires()
    {
        using var c = NewClient();
        bool fired = false;
        c.OnMessageSent += (_, _) => fired = true;
        c.Connect();
        c.ReadInt16("100");
        Assert.True(fired);
    }

    // ── FC03 Read Holding Registers ─────────────────────

    [Fact]
    public void FC03_D100_Equals_1234()
    {
        using var c = NewClient(); c.Connect();
        Assert.Equal(1234, c.ReadInt16("100").Content);
    }

    [Fact]
    public void FC03_D101_Equals_5678()
    {
        using var c = NewClient(); c.Connect();
        Assert.Equal(5678, c.ReadInt16("101").Content);
    }

    [Fact]
    public void FC03_D200_UInt16_Equals_0x1234()
    {
        using var c = NewClient(); c.Connect();
        Assert.Equal((ushort)0x1234, c.ReadUInt16("200").Content);
    }

    [Fact]
    public void FC03_Prefix_4xxxx_Routes_To_Holding()
    {
        using var c = NewClient(); c.Connect();
        // 5-digit prefix "40101" → addr=100 (after -1 convention), matches SetRegister(100, 1234)
        Assert.Equal(1234, c.ReadInt16("40101").Content);
    }

    // ── FC04 Read Input Registers ──────────────────────

    [Fact]
    public void FC04_Input_30021_Equals_9999()
    {
        using var c = NewClient(); c.Connect();
        // 5-digit prefix "30021" → addr=20 (after -1 convention), matches SetInputRegister(20, 9999)
        Assert.Equal(9999, c.ReadInt16("30021").Content);
    }

    [Fact]
    public void FC04_Prefix_3xxxx_5Digit_Routes_To_Input()
    {
        using var c = NewClient(); c.Connect();
        Assert.Equal(9999, c.ReadInt16("30021").Content);
    }

    // ── FC01 Read Coils ────────────────────────────────

    [Fact]
    public void FC01_Coil50_True()
    {
        using var c = NewClient(); c.Connect();
        Assert.True(c.ReadBool("50").Content);
    }

    [Fact]
    public void FC01_Coil51_False()
    {
        using var c = NewClient(); c.Connect();
        Assert.False(c.ReadBool("51").Content);
    }

    [Fact]
    public void FC01_Prefix_0xxxx_Routes_To_Coil()
    {
        using var c = NewClient(); c.Connect();
        Assert.True(c.ReadBool("050").Content);
    }

    // ── FC02 Read Discrete Inputs ──────────────────────

    [Fact]
    public void FC02_DI_10011_5DigitPrefix_True()
    {
        using var c = NewClient(); c.Connect();
        // 5-digit prefix "10011" → addr=10 (after -1 convention), matches SetDiscreteInput(10, true)
        Assert.True(c.ReadBool("10011").Content);
    }

    [Fact]
    public void FC02_DI111_ShortAddress_False()
    {
        using var c = NewClient(); c.Connect();
        Assert.False(c.ReadBool("111").Content);
    }

    // ── FC06 Write Single Register ─────────────────────

    [Fact]
    public void FC06_Write_D102_Negative9999()
    {
        using var c = NewClient(); c.Connect();
        Assert.True(c.Write("102", (short)-9999).IsSuccess);
    }

    [Fact]
    public void FC06_ReadBack_D102_Equals_Negative9999()
    {
        using var c = NewClient(); c.Connect();
        c.Write("102", (short)-9999);
        Assert.Equal(-9999, c.ReadInt16("102").Content);
    }

    // ── FC05 Write Single Coil ─────────────────────────

    [Fact]
    public void FC05_WriteCoil60_True()
    {
        using var c = NewClient(); c.Connect();
        Assert.True(c.Write("60", true).IsSuccess);
    }

    [Fact]
    public void FC05_ReadBack_Coil60_True()
    {
        using var c = NewClient(); c.Connect();
        c.Write("60", true);
        Assert.True(c.ReadBool("60").Content);
    }

    [Fact]
    public void FC05_WriteCoil60_False()
    {
        using var c = NewClient(); c.Connect();
        Assert.True(c.Write("60", false).IsSuccess);
    }

    [Fact]
    public void FC05_ReadBack_Coil60_False()
    {
        using var c = NewClient(); c.Connect();
        c.Write("60", false);
        Assert.False(c.ReadBool("60").Content);
    }

    // ── FC16 Write Multiple Registers ──────────────────

    [Fact]
    public void FC16_WriteInt32_100000_To_D110()
    {
        using var c = NewClient(); c.Connect();
        Assert.True(c.Write("110", 100000).IsSuccess);
    }

    [Fact]
    public void FC16_ReadBack_D110_Equals_100000()
    {
        using var c = NewClient(); c.Connect();
        c.Write("110", 100000);
        Assert.Equal(100000, c.ReadInt32("110").Content);
    }

    [Fact]
    public void FC16_WriteFloat_3_14_To_D120()
    {
        using var c = NewClient(); c.Connect();
        Assert.True(c.Write("120", 3.14f).IsSuccess);
    }

    [Fact]
    public void FC16_ReadBack_D120_Float_Approximately_3_14()
    {
        using var c = NewClient(); c.Connect();
        c.Write("120", 3.14f);
        Assert.InRange(c.ReadFloat("120").Content, 3.139f, 3.141f);
    }

    // ── FC15 Write Multiple Coils ──────────────────────

    [Fact]
    public void FC15_WriteMultipleCoils_TFT()
    {
        using var c = NewClient(); c.Connect();
        Assert.True(c.WriteMultipleCoils(70, new[] { true, false, true }).IsSuccess);
    }

    [Fact]
    public void FC15_ReadBools_70_3_Equals_TFT()
    {
        using var c = NewClient(); c.Connect();
        c.WriteMultipleCoils(70, new[] { true, false, true });
        var r = c.ReadBools("70", 3);
        Assert.True(r.Content[0]);
        Assert.False(r.Content[1]);
        Assert.True(r.Content[2]);
    }

    // ── Batch Read ─────────────────────────────────────

    [Fact]
    public void BatchRead_TwoRegisters_ReturnsFourBytes()
    {
        using var c = NewClient(); c.Connect();
        var r = c.ReadRegistersBatch(100, 2);
        Assert.Equal(4, r.Content.Length);
    }

    // ── FC23 Read/Write Multiple (Atomic) ──────────────

    [Fact]
    public void FC23_ReadWriteMultiple_AtomicOperation()
    {
        using var c = NewClient(); c.Connect();
        c.Write("130", (short)0);
        var writeData = DataConverter.GetBytes((short)42);
        var rw = c.ReadWriteMultipleRegisters(100, 1, 130, writeData);
        Assert.True(rw.IsSuccess);
        Assert.Equal(2, rw.Content.Length);
        Assert.Equal(1234, DataConverter.ToInt16(rw.Content, 0));
        Assert.Equal((ushort)42, _fx.Server.GetRegister(130));
    }

    // ── Endianness ─────────────────────────────────────

    [Fact]
    public void LittleEndian_RoundTrip()
    {
        using var c = NewClient(); c.Connect();
        c.ByteOrder = Endianness.LittleEndian;
        c.Write("140", (short)0x1234);
        Assert.Equal((short)0x1234, c.ReadInt16("140").Content);
    }

    [Fact]
    public void BigEndian_RoundTrip()
    {
        using var c = NewClient(); c.Connect();
        c.ByteOrder = Endianness.BigEndian;
        c.Write("140", (short)0x1234);
        Assert.Equal((short)0x1234, c.ReadInt16("140").Content);
    }

    // ── Custom Message ─────────────────────────────────

    [Fact]
    public void SendCustomMessage_RawFC03_ReturnsValidResponse()
    {
        using var c = NewClient(); c.Connect();
        byte[] msg = { 0, 1, 0, 0, 0, 6, 1, 0x03, 0, 100, 0, 1 };
        var r = c.SendCustomMessage(msg);
        Assert.True(r.IsSuccess);
        Assert.True(r.Content.Length >= 11);
    }

    // ── OperateResult ──────────────────────────────────

    [Fact]
    public void OperateResult_Success_IsSuccessTrue()
    {
        Assert.True(OperateResult.Success().IsSuccess);
    }

    [Fact]
    public void OperateResult_Failed_IsSuccessFalse()
    {
        Assert.False(OperateResult.Failed("err").IsSuccess);
    }

    [Fact]
    public void OperateResult_Failed_PreservesMessage()
    {
        Assert.Equal("err", OperateResult.Failed("err").Message);
    }
}
