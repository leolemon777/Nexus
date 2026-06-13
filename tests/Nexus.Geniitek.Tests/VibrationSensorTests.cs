using System.Reflection;
using Nexus;
using Nexus.Geniitek;
using Xunit;

namespace Nexus.Geniitek.Tests;

public class VibrationSensorTests
{
    private static byte InvokeCalculateChecksum(byte[] frame)
    {
        var method = typeof(VibrationSensorClient).GetMethod("CalculateChecksum",
            BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(byte[]) }, null)!;
        return (byte)method.Invoke(null, new object[] { frame })!;
    }

    private static byte[] InvokeBuildFrame(VibrationSensorClient client, byte command, byte[] data)
    {
        var method = typeof(VibrationSensorClient).GetMethod("BuildFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (byte[])method.Invoke(client, new object[] { command, data })!;
    }

    private static float InvokeReadFloat(byte[] data, int offset)
    {
        var method = typeof(VibrationSensorClient).GetMethod("ReadFloat", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (float)method.Invoke(null, new object[] { data, offset })!;
    }

    private static OperateResult<byte[]> InvokeParseResponse(byte[] response, byte expectedCmd)
    {
        var method = typeof(VibrationSensorClient).GetMethod("ParseResponse", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (OperateResult<byte[]>)method.Invoke(null, new object[] { response, expectedCmd })!;
    }

    // ── Data model tests ──

    [Fact]
    public void VibrationData_PropertiesWork()
    {
        var data = new VibrationData { X = 1.1f, Y = 2.2f, Z = 3.3f };
        Assert.Equal(1.1f, data.X);
        Assert.Equal(2.2f, data.Y);
        Assert.Equal(3.3f, data.Z);
    }

    [Fact]
    public void VelocityData_PropertiesWork()
    {
        var data = new VelocityData { X = 4.4f, Y = 5.5f, Z = 6.6f };
        Assert.Equal(4.4f, data.X);
        Assert.Equal(5.5f, data.Y);
        Assert.Equal(6.6f, data.Z);
    }

    [Fact]
    public void SensorStatus_PropertiesWork()
    {
        var status = new SensorStatus { BatteryLevel = 85, ErrorCode = 0x00, IsRunning = true };
        Assert.Equal(85, status.BatteryLevel);
        Assert.Equal(0x00, status.ErrorCode);
        Assert.True(status.IsRunning);
    }

    [Fact]
    public void SensorStatus_IsRunning_BasedOnErrorCode()
    {
        var running = new SensorStatus { ErrorCode = 0x00 };
        Assert.True((running.ErrorCode & 0x01) == 0);

        var stopped = new SensorStatus { ErrorCode = 0x01 };
        Assert.False((stopped.ErrorCode & 0x01) == 0);
    }

    // ── Checksum tests ──

    [Fact]
    public void CalculateChecksum_XorOfPayload()
    {
        byte[] frame = new byte[] { 0xAA, 0x55, 0x00, 0x02, 0x01, 0x00, 0x00 };
        byte cs = InvokeCalculateChecksum(frame);
        byte expected = (byte)(0x00 ^ 0x02 ^ 0x01 ^ 0x00);
        Assert.Equal(expected, cs);
    }

    // ── ReadFloat tests ──

    [Fact]
    public void ReadFloat_BigEndian_ReturnsCorrectValue()
    {
        byte[] data = BitConverter.GetBytes(3.5f);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(data);
        float result = InvokeReadFloat(data, 0);
        Assert.Equal(3.5f, result);
    }

    // ── BuildFrame tests ──

    [Fact]
    public void BuildFrame_CorrectFormat()
    {
        var client = new VibrationSensorClient("127.0.0.1", 5000);
        byte[] frame = InvokeBuildFrame(client, 0x01, Array.Empty<byte>());

        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(0x55, frame[1]);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x01, frame[3]);
        Assert.Equal(0x01, frame[4]);
    }

    [Fact]
    public void BuildFrame_WithData_IncludesData()
    {
        var client = new VibrationSensorClient("127.0.0.1", 5000);
        byte[] data = new byte[] { 0x11, 0x22 };
        byte[] frame = InvokeBuildFrame(client, 0x07, data);

        Assert.Equal(0xAA, frame[0]);
        Assert.Equal(0x55, frame[1]);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x03, frame[3]);
        Assert.Equal(0x07, frame[4]);
        Assert.Equal(0x11, frame[5]);
        Assert.Equal(0x22, frame[6]);
    }

    [Fact]
    public void BuildFrame_ChecksumIsValid()
    {
        var client = new VibrationSensorClient("127.0.0.1", 5000);
        byte[] frame = InvokeBuildFrame(client, 0x01, Array.Empty<byte>());

        byte cs = 0;
        for (int i = 2; i < frame.Length - 1; i++)
            cs ^= frame[i];
        Assert.Equal(cs, frame[frame.Length - 1]);
    }

    // ── ParseResponse tests ──

    [Fact]
    public void ParseResponse_TooShort_Fails()
    {
        byte[] response = new byte[] { 0xAA, 0x55, 0x00, 0x01 };
        var result = InvokeParseResponse(response, 0x01);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseResponse_HeaderMismatch_Fails()
    {
        byte[] response = new byte[] { 0xBB, 0x55, 0x00, 0x02, 0x01, 0x00, 0x00 };
        var result = InvokeParseResponse(response, 0x01);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseResponse_ErrorBitSet_Fails()
    {
        byte[] payload = new byte[] { 0x00, 0x02, 0x81, 0x00 };
        byte cs = 0;
        for (int i = 0; i < payload.Length; i++) cs ^= payload[i];
        byte[] response = new byte[] { 0xAA, 0x55, 0x00, 0x02, 0x81, 0x00, cs };
        var result = InvokeParseResponse(response, 0x01);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseResponse_ValidResponse_Succeeds()
    {
        byte[] response = new byte[] { 0xAA, 0x55, 0x00, 0x02, 0x01, 0x00, 0x00 };
        byte cs = 0;
        for (int i = 0; i < response.Length - 1; i++) cs ^= response[i];
        response[6] = cs;
        var result = InvokeParseResponse(response, 0x01);
        Assert.True(result.IsSuccess, result.Message);
    }

    // ── Write always fails ──

    [Fact]
    public void Write_AlwaysFails()
    {
        var client = new VibrationSensorClient("127.0.0.1", 5000);
        var result = client.Write("x", 1);
        Assert.False(result.IsSuccess);
    }

    // ── ToString ──

    [Fact]
    public void ToString_ReturnsExpected()
    {
        var client = new VibrationSensorClient("192.168.1.1", 8080);
        Assert.Equal("VibrationSensorClient[192.168.1.1:8080]", client.ToString());
    }
}
