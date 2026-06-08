using System;
using System.Text;
using Xunit;
using Nexus.Secs;

namespace Nexus.Secs.Tests;

public class SecsHsmsTests
{
    #region 帧构建

    [Fact]
    public void BuildFrame_HeaderOnly()
    {
        var header = new byte[10];
        header[0] = 0x00; // DevId Hi
        header[1] = 0x00; // DevId Lo
        header[2] = 0x05; // SType = Linktest
        header[9] = 0x05; // PType = Linktest

        byte[] frame = SecsHsmsClient.BuildFrame(header, null);

        // Length(4) + Header(10) = 14 bytes, length field = 10
        Assert.Equal(14, frame.Length);
        Assert.Equal(0x00, frame[0]);
        Assert.Equal(0x00, frame[1]);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x0A, frame[3]); // msgLen = 10
    }

    [Fact]
    public void BuildFrame_WithData()
    {
        var header = new byte[10];
        byte[] data = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        byte[] frame = SecsHsmsClient.BuildFrame(header, data);

        // Length(4) + Header(10) + Data(4) = 18 bytes
        Assert.Equal(18, frame.Length);
        // msgLen = 14
        Assert.Equal(0x00, frame[0]);
        Assert.Equal(0x00, frame[1]);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x0E, frame[3]); // 14

        // 数据在 offset 14
        Assert.Equal(0x01, frame[14]);
        Assert.Equal(0x04, frame[17]);
    }

    #endregion

    #region 消息解析

    [Fact]
    public void ParseSecsMessage_S1F1()
    {
        var client = new SecsHsmsClient("127.0.0.1");
        client.DeviceId = 0x0001;

        // 构建 S1F1 (Are You There) 帧
        var header = new byte[10];
        header[0] = 0x00; // DevId Hi = 0
        header[1] = 0x01; // DevId Lo = 1
        header[3] = (byte)((1 << 1) | 1); // Stream=1, ReplyExpected=1
        header[4] = 0x02; // Function=2 (reply)
        header[5] = 0x00; header[6] = 0x00; header[7] = 0x00; header[8] = 0x01; // SysBytes=1
        header[9] = 0x00; // PType=SECS2

        byte[] frame = SecsHsmsClient.BuildFrame(header, null);
        var result = SecsHsmsClient.ParseSecsMessage(frame);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((ushort)0x0001, result.Content.DeviceId);
        Assert.Equal((byte)1, result.Content.Stream);
        Assert.Equal((byte)2, result.Content.Function);
        Assert.Equal((uint)1, result.Content.SystemBytes);
        Assert.True(result.Content.ReplyExpected);
    }

    [Fact]
    public void ParseSecsMessage_WithData()
    {
        var header = new byte[10];
        header[3] = (byte)((1 << 1) | 0); // Stream=1, ReplyExpected=0
        header[4] = 0x02; // Function=2
        byte[] data = new byte[] { 0xAA, 0xBB, 0xCC };

        byte[] frame = SecsHsmsClient.BuildFrame(header, data);
        var result = SecsHsmsClient.ParseSecsMessage(frame);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(3, result.Content.Data!.Length);
        Assert.Equal(0xAA, result.Content.Data[0]);
    }

    [Fact]
    public void ParseSecsMessage_ShortData()
    {
        var result = SecsHsmsClient.ParseSecsMessage(new byte[5]);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseSecsMessage_IncompleteData()
    {
        // 长度字段说14字节，但实际只有 header
        var raw = new byte[14];
        raw[0] = 0; raw[1] = 0; raw[2] = 0; raw[3] = 0x14; // msgLen=20 but no data
        var result = SecsHsmsClient.ParseSecsMessage(raw);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region SecsMessage

    [Fact]
    public void SecsMessage_ToString()
    {
        var msg = new SecsMessage { Stream = 1, Function = 1, SystemBytes = 0x00000001, Data = new byte[10] };
        string s = msg.ToString();
        Assert.Contains("S1F1", s);
        Assert.Contains("10B", s);
    }

    #endregion

    #region Client 属性

    [Fact]
    public void Constructor_Defaults()
    {
        var client = new SecsHsmsClient("192.168.1.1");
        Assert.Equal(0, client.DeviceId);

        string s = client.ToString();
        Assert.Contains("SecsHsmsClient", s);
        Assert.Contains("192.168.1.1", s);
    }

    [Fact]
    public void Constructor_CustomPort()
    {
        var client = new SecsHsmsClient("192.168.1.1", 5001);
        Assert.Contains("5001", client.ToString());
    }

    #endregion

    #region GetResponsePayloadLength (通过帧解析间接验证)

    [Fact]
    public void BuildFrame_HeaderOnly_NoData()
    {
        var header = new byte[10];
        byte[] frame = SecsHsmsClient.BuildFrame(header, null);
        Assert.Equal(14, frame.Length);
    }

    [Fact]
    public void BuildFrame_4ByteData()
    {
        var header = new byte[10];
        byte[] data = new byte[4];
        byte[] frame = SecsHsmsClient.BuildFrame(header, data);
        Assert.Equal(18, frame.Length);
        // msgLen = 14 (header 10 + data 4)
        Assert.Equal(0x00, frame[0]);
        Assert.Equal(0x00, frame[1]);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x0E, frame[3]);
    }

    #endregion
}
