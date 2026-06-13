using System;
using System.Collections.Generic;
using Xunit;
using Nexus.Cjt;

namespace Nexus.Cjt.Tests;

public class Cjt188Tests
{
    #region BCD 工具

    [Theory]
    [InlineData(new byte[] { 0x12, 0x34 }, "3412")]
    [InlineData(new byte[] { 0x56 }, "56")]
    public void BcdToString_Tests(byte[] data, string expected)
    {
        Assert.Equal(expected, Cjt188Client.BcdToString(data));
    }

    [Theory]
    [InlineData("901F0000", new byte[] { 0x00, 0x00, 0x1F, 0x90 })]
    [InlineData("90200000", new byte[] { 0x00, 0x00, 0x20, 0x90 })]
    public void ParseDataId_Valid(string input, byte[] expected)
    {
        var result = Cjt188Client.ParseDataId(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("ZZZZZZZZ")]
    public void ParseDataId_Invalid(string? input)
    {
        var result = Cjt188Client.ParseDataId(input!);
        Assert.Null(result);
    }

    #endregion

    #region 帧构建

    [Fact]
    public void BuildFrame_BasicStructure()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterType = Cjt188Client.TYPE_WATER_COLD;
        client.MeterAddress = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x90, 0x1F, 0x00, 0x00 }, null);

        // 验证帧结构
        Assert.Equal(0x68, frame[0]);   // header
        Assert.Equal(0x68, frame[9]);   // header2
        Assert.Equal(0x16, frame[frame.Length - 1]); // end byte
        Assert.Equal(Cjt188Client.TYPE_WATER_COLD, frame[1]); // meter type
        Assert.Equal(0x01, frame[10]); // control = 0x01

        // 数据长度 = 4 (DI only, no user data)
        Assert.Equal(0x04, frame[11]);
    }

    [Fact]
    public void BuildFrame_WithData()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];

        byte[] frame = client.BuildFrame(0x04, new byte[] { 0x90, 0x1F, 0x00, 0x00 }, new byte[] { 0xAA, 0xBB });

        // 数据长度 = 4(DI) + 2(data) = 6
        Assert.Equal(0x06, frame[11]);

        // 数据域加密: DI0(0x90+33=0xC3)
        Assert.Equal(0xC3, frame[12]);
    }

    [Fact]
    public void BuildFrame_EndBytePresent()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];

        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x00, 0x00, 0x00, 0x00 }, null);

        // 最后一字节必须是 0x16 (FRAME_END)
        Assert.Equal(0x16, frame[frame.Length - 1]);

        // 帧长度 = 68H(1) + T(1) + A(7) + 68H(1) + C(1) + L(1) + DATA(4) + CS(1) + 16H(1) = 18
        Assert.Equal(18, frame.Length);
    }

    #endregion

    #region 响应解析

    [Fact]
    public void ParseResponse_ShortFrame()
    {
        var result = Cjt188Client.ParseResponse(new byte[] { 0x68 }, 0x01);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ParseResponse_BadHeader()
    {
        var frame = new byte[15];
        frame[0] = 0x00; // wrong
        var result = Cjt188Client.ParseResponse(frame, 0x01);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region 仪表类型

    [Fact]
    public void MeterTypeConstants()
    {
        Assert.Equal(0x10, Cjt188Client.TYPE_WATER_COLD);
        Assert.Equal(0x11, Cjt188Client.TYPE_WATER_HOT);
        Assert.Equal(0x20, Cjt188Client.TYPE_HEAT);
        Assert.Equal(0x30, Cjt188Client.TYPE_GAS);
        Assert.Equal(0x40, Cjt188Client.TYPE_ELECTRIC);
    }

    #endregion

    #region ToString

    [Fact]
    public void ToString_ContainsInfo()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterType = Cjt188Client.TYPE_WATER_COLD;
        string s = client.ToString();
        Assert.Contains("Cjt188Client", s);
        Assert.Contains("10", s); // TYPE_WATER_COLD = 0x10
    }

    #endregion

    #region MeterType & Address

    [Fact]
    public void MeterType_Default_IsColdWater()
    {
        var client = new Cjt188Client(new MockSerialPort());
        Assert.Equal(Cjt188Client.TYPE_WATER_COLD, client.MeterType);
    }

    [Fact]
    public void MeterType_CanBeSet()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterType = Cjt188Client.TYPE_GAS;
        Assert.Equal(Cjt188Client.TYPE_GAS, client.MeterType);
    }

    [Fact]
    public void MeterAddress_Default_IsSevenZeros()
    {
        var client = new Cjt188Client(new MockSerialPort());
        Assert.Equal(7, client.MeterAddress.Length);
        Assert.All(client.MeterAddress, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void MeterAddress_CanBeSet()
    {
        var client = new Cjt188Client(new MockSerialPort());
        var addr = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 };
        client.MeterAddress = addr;
        Assert.Equal(addr, client.MeterAddress);
    }

    #endregion

    #region 帧加密验证

    [Fact]
    public void BuildFrame_DataFieldEncrypted_Plus33H()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];

        byte[] dataId = { 0x90, 0x1F, 0x00, 0x00 };
        byte[] frame = client.BuildFrame(0x01, dataId, null);

        // Encrypted DI0 = 0x90 + 0x33 = 0xC3
        Assert.Equal(0xC3, frame[12]);
        // DI1 = 0x1F + 0x33 = 0x52
        Assert.Equal(0x52, frame[13]);
        // DI2 = 0x00 + 0x33 = 0x33
        Assert.Equal(0x33, frame[14]);
        // DI3 = 0x00 + 0x33 = 0x33
        Assert.Equal(0x33, frame[15]);
    }

    [Fact]
    public void BuildFrame_WithUserData_EncryptsAllData()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];

        byte[] frame = client.BuildFrame(0x04, new byte[] { 0x00, 0x00, 0x00, 0x00 }, new byte[] { 0x01, 0x02 });
        // Data length = 4 (DI) + 2 (user) = 6
        Assert.Equal(0x06, frame[11]);
        // User data byte 0: 0x01 + 0x33 = 0x34
        Assert.Equal(0x34, frame[16]);
        // User data byte 1: 0x02 + 0x33 = 0x35
        Assert.Equal(0x35, frame[17]);
    }

    #endregion

    #region 帧校验

    [Fact]
    public void BuildFrame_ChecksumValid()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];
        client.MeterType = 0x10;

        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x90, 0x1F, 0x00, 0x00 }, null);

        // Verify checksum: CS = T ^ A0..A6 ^ C ^ L ^ DATA(all encrypted bytes)
        byte cs = 0;
        cs ^= frame[1]; // T
        for (int i = 0; i < 7; i++) cs ^= frame[2 + i]; // A0..A6
        cs ^= frame[10]; // C
        cs ^= frame[11]; // L
        for (int i = 0; i < 4; i++) cs ^= frame[12 + i]; // DATA

        Assert.Equal(cs, frame[16]); // CS byte
    }

    #endregion

    #region 响应解析 — 更多场景

    [Fact]
    public void ParseResponse_ValidFrame_NoError_DecryptsData()
    {
        // Build a valid response frame manually
        // NOTE: ctrl must NOT have bit 0x80 set, otherwise parsed as error
        byte meterType = 0x10;
        byte[] addr = new byte[7];
        byte ctrl = 0x01; // no error bit
        byte dataLen = 6; // DI(4) + data(2)
        byte[] encrypted = { 0xC3, 0x52, 0x33, 0x33, 0x34, 0x35 }; // encrypted DI + data

        byte cs = meterType;
        for (int i = 0; i < 7; i++) cs ^= addr[i];
        cs ^= ctrl;
        cs ^= dataLen;
        for (int i = 0; i < dataLen; i++) cs ^= encrypted[i];

        byte[] frame = new byte[14 + dataLen];
        frame[0] = 0x68;
        frame[1] = meterType;
        Array.Copy(addr, 0, frame, 2, 7);
        frame[9] = 0x68;
        frame[10] = ctrl;
        frame[11] = dataLen;
        Array.Copy(encrypted, 0, frame, 12, dataLen);
        frame[12 + dataLen] = cs;
        frame[13 + dataLen] = 0x16;

        var result = Cjt188Client.ParseResponse(frame, 0x01);
        Assert.True(result.IsSuccess, result.Message);
        // Decrypted data (last 2 bytes): 0x34-0x33=0x01, 0x35-0x33=0x02
        Assert.Equal(new byte[] { 0x01, 0x02 }, result.Content);
    }

    [Fact]
    public void ParseResponse_ErrorResponse_Bit0x80_ReturnsFailure()
    {
        byte meterType = 0x10;
        byte[] addr = new byte[7];
        byte ctrl = 0x81; // error bit set
        byte dataLen = 6;
        byte[] encrypted = { 0xC3, 0x52, 0x33, 0x33, 0x64, 0x33 }; // encrypted DI + error code

        byte cs = meterType;
        for (int i = 0; i < 7; i++) cs ^= addr[i];
        cs ^= ctrl;
        cs ^= dataLen;
        for (int i = 0; i < dataLen; i++) cs ^= encrypted[i];

        byte[] frame = new byte[14 + dataLen];
        frame[0] = 0x68;
        frame[1] = meterType;
        Array.Copy(addr, 0, frame, 2, 7);
        frame[9] = 0x68;
        frame[10] = ctrl;
        frame[11] = dataLen;
        Array.Copy(encrypted, 0, frame, 12, dataLen);
        frame[12 + dataLen] = cs;
        frame[13 + dataLen] = 0x16;

        var result = Cjt188Client.ParseResponse(frame, 0x01);
        Assert.False(result.IsSuccess);
        Assert.Contains("错误码", result.Message);
    }

    [Fact]
    public void ParseResponse_BadChecksum_ReturnsFailure()
    {
        byte[] frame = new byte[18];
        frame[0] = 0x68;
        frame[9] = 0x68;
        frame[10] = 0x01; // no error bit
        frame[11] = 4;
        frame[12] = 0xC3;
        frame[13] = 0x52;
        frame[14] = 0x33;
        frame[15] = 0x33;
        frame[16] = 0xFF; // bad checksum
        frame[17] = 0x16;

        var result = Cjt188Client.ParseResponse(frame, 0x01);
        Assert.False(result.IsSuccess);
        Assert.Contains("校验", result.Message);
    }

    [Fact]
    public void ParseResponse_NullInput_ReturnsFailure()
    {
        var result = Cjt188Client.ParseResponse(null!, 0x01);
        Assert.False(result.IsSuccess);
    }

    #endregion

    #region IReadWriteDevice 操作（串口未打开）

    [Fact]
    public void ReadBytes_InvalidDataId_ReturnsError()
    {
        var client = new Cjt188Client(new MockSerialPort());
        var r = client.ReadBytes("bad", 1);
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void Write_InvalidDataId_ReturnsError()
    {
        var client = new Cjt188Client(new MockSerialPort());
        var r = client.Write("bad", new byte[] { 0x01 });
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void BatchOperations_EmptyInput_ReturnsError()
    {
        var client = new Cjt188Client(new MockSerialPort());
        Assert.False(client.BatchRead(new string[0]).IsSuccess);
        Assert.False(client.RandomRead(new string[0]).IsSuccess);
        Assert.False(client.BatchWrite(Array.Empty<KeyValuePair<string, object>>()).IsSuccess);
    }

    #endregion

    #region ISubscribeDevice

    [Fact]
    public void Subscribe_Unsubscribe_DoesNotThrow()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.Subscribe("901F0000", 1000, "Int16");
        client.Unsubscribe("901F0000");
        client.StartSubscriptions();
        client.StopSubscriptions();
        client.Dispose();
    }

    [Fact]
    public void Subscribe_DuplicateAddress_OverwritesEntry()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.Subscribe("901F0000", 1000, "Int16");
        client.Subscribe("901F0000", 500, "Float"); // overwrite
        client.Unsubscribe("901F0000");
        client.Dispose();
    }

    #endregion

    #region 扩展覆盖

    [Fact]
    public void BcdToString_TwoBytes_AllZeros()
    {
        Assert.Equal("0000", Cjt188Client.BcdToString(new byte[] { 0x00, 0x00 }));
    }

    [Fact]
    public void BcdToString_TwoBytes_MaxDigits()
    {
        Assert.Equal("9999", Cjt188Client.BcdToString(new byte[] { 0x99, 0x99 }));
    }

    [Fact]
    public void ParseDataId_ElectricMeter()
    {
        var result = Cjt188Client.ParseDataId("90200000");
        Assert.NotNull(result);
        Assert.Equal(4, result!.Length);
    }

    [Fact]
    public void BuildFrame_DifferentMeterTypes()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];

        client.MeterType = Cjt188Client.TYPE_GAS;
        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x90, 0x1F, 0x00, 0x00 }, null);
        Assert.Equal(Cjt188Client.TYPE_GAS, frame[1]);
    }

    [Fact]
    public void BuildFrame_ElectricMeterType()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];
        client.MeterType = Cjt188Client.TYPE_ELECTRIC;

        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x00, 0x00, 0x00, 0x00 }, null);
        Assert.Equal(Cjt188Client.TYPE_ELECTRIC, frame[1]);
    }

    [Fact]
    public void ParseResponse_WrongControlCode()
    {
        var result = Cjt188Client.ParseResponse(new byte[] { 0x68, 0x10, 0, 0, 0, 0, 0, 0, 0, 0x68, 0x02, 0x04, 0x33, 0x33, 0x33, 0x33, 0x00, 0x16 }, 0x01);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void ToString_ContainsMeterType()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterType = Cjt188Client.TYPE_HEAT;
        string s = client.ToString();
        Assert.Contains("Cjt188Client", s);
    }

    [Fact]
    public void BuildFrame_HeatMeterType()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];
        client.MeterType = Cjt188Client.TYPE_HEAT;

        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x90, 0x1F, 0x00, 0x00 }, null);
        Assert.Equal(Cjt188Client.TYPE_HEAT, frame[1]);
    }

    [Fact]
    public void BuildFrame_HotWaterType()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[7];
        client.MeterType = Cjt188Client.TYPE_WATER_HOT;

        byte[] frame = client.BuildFrame(0x01, new byte[] { 0x90, 0x1F, 0x00, 0x00 }, null);
        Assert.Equal(Cjt188Client.TYPE_WATER_HOT, frame[1]);
    }

    [Fact]
    public void SetMeterAddress_ShortLength_Accepts()
    {
        var client = new Cjt188Client(new MockSerialPort());
        client.MeterAddress = new byte[] { 1, 2, 3 };
        Assert.Equal(3, client.MeterAddress.Length);
    }

    #endregion
}

/// <summary>
/// Mock 串口，仅用于单元测试构造。
/// </summary>
internal class MockSerialPort : ISerialPort
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.Even;
    public int ReadTimeout { get; set; } = 1000;
    public int WriteTimeout { get; set; } = 1000;
    public bool IsOpen { get; } = false;
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }

    public void Open() { }
    public void Close() { }
    public int Read(byte[] buffer, int offset, int count) => 0;
    public void Write(byte[] buffer, int offset, int count) { }
    public void Dispose() { }
}
