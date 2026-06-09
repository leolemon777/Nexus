using System;
using System.IO;
using System.Text;
using Xunit;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests;

/// <summary>
/// FX 协议整合测试 — 覆盖 FxLinkClient (计算机链接协议) 和 FxFrameBuilder (编程口帧构建)。
/// FxSerialClient 需要真实/模拟串口硬件，此处仅测试帧构建和离线客户端逻辑。
/// </summary>
public sealed class FxSerialFrameTests
{
    // ═══════════════════════════════════════════
    //  FxFrameBuilder — 编程口帧构建基本验证
    // ═══════════════════════════════════════════

    [Fact]
    public void FxFrameBuilder_BuildReadCommand_StartsWithSTX()
    {
        byte[] frame = FxFrameBuilder.BuildReadCommand('D', 100, 2);
        Assert.Equal(0x02, frame[0]); // STX
        Assert.Equal((byte)'0', frame[1]); // Read command
        Assert.Equal((byte)'D', frame[2]); // Device code
        Assert.True(frame.Length >= 8);
    }

    [Fact]
    public void FxFrameBuilder_BuildWriteCommand_StartsWithSTX()
    {
        byte[] frame = FxFrameBuilder.BuildWriteCommand('D', 100, new byte[] { 0x12, 0x34 });
        Assert.Equal(0x02, frame[0]); // STX
        Assert.Equal((byte)'1', frame[1]); // Write command
        Assert.Equal((byte)'D', frame[2]); // Device code
        Assert.True(frame.Length >= 10);
    }

    [Fact]
    public void FxFrameBuilder_BuildReadCommand_ContainsAddressAndCount()
    {
        byte[] frame = FxFrameBuilder.BuildReadCommand('D', 100, 2);
        // STX + "0D010002" + ETX + SUM → ASCII payload starts at index 1
        // Command body is frame[1..^3] (before ETX and SUM)
        int bodyLen = frame.Length - 3; // exclude ETX + SUM(2)
        string body = Encoding.ASCII.GetString(frame, 1, bodyLen);
        Assert.StartsWith("0D0100", body); // Read + D + addr 0100
        Assert.EndsWith("02", body);       // count = 2
    }

    [Fact]
    public void FxFrameBuilder_VerifyResponse_RejectsNak()
    {
        byte[] response = { 0x15 }; // NAK
        bool ok = FxFrameBuilder.VerifyResponse(response, out _);
        Assert.False(ok);
    }

    [Fact]
    public void FxFrameBuilder_VerifyResponse_RejectsTooShort()
    {
        byte[] response = { 0x02 }; // STX only, too short
        bool ok = FxFrameBuilder.VerifyResponse(response, out _);
        Assert.False(ok);
    }

    // ═══════════════════════════════════════════
    //  FxLinkClient — 计算机链接协议客户端 (离线)
    // ═══════════════════════════════════════════

    [Fact]
    public void FxLinkClient_Constructor_SetsDefaults()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms);
        Assert.True(client.IsConnected);
        Assert.Equal((byte)0, client.Station);
        Assert.Equal(5000, client.Timeout);
    }

    [Fact]
    public void FxLinkClient_Constructor_WithStationAndTimeout()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms, station: 5, timeout: 3000);
        Assert.Equal((byte)5, client.Station);
        Assert.Equal(3000, client.Timeout);
    }

    [Fact]
    public void FxLinkClient_SetLogger_DoesNotThrow()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms);
        client.SetLogger(NullLogger.Instance);
    }

    [Fact]
    public void FxLinkClient_Dispose_DoesNotThrow()
    {
        using var ms = new MemoryStream();
        var client = new FxLinkClient(ms);
        client.Dispose();
    }

    [Fact]
    public void FxLinkClient_Connect_ReturnsSuccess()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms);
        var result = client.Connect();
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void FxLinkClient_ReadDouble_ReturnsNotSupported()
    {
        using var ms = new MemoryStream();
        using var client = new FxLinkClient(ms);
        var result = client.ReadDouble("D100");
        Assert.False(result.IsSuccess);
        Assert.Contains("不支持", result.Message);
    }

    [Fact]
    public void FxLinkClient_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FxLinkClient(null!));
    }
}
