using System;
using Nexus;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

public class ModbusDiagnosticsToolTests
{
    // ═══════════════════════════════════════════
    //  TCP 报文解析
    // ═══════════════════════════════════════════

    [Fact]
    public void ParseMessage_TcpFc01Request_ShowsCoilInfo()
    {
        // FC01: 读线圈, 地址=10, 数量=8
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x01, 0x00, 0x0A, 0x00, 0x08 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("Modbus TCP", result);
        Assert.Contains("事务 ID: 1", result);
        Assert.Contains("单元 ID: 1", result);
        Assert.Contains("0x01", result);
        Assert.Contains("读线圈", result);
        Assert.Contains("起始地址: 10", result);
        Assert.Contains("数量: 8", result);
    }

    [Fact]
    public void ParseMessage_TcpFc03Request_ShowsRegisterInfo()
    {
        // FC03: 读保持寄存器, 地址=0, 数量=2
        byte[] frame = { 0x00, 0x05, 0x00, 0x00, 0x00, 0x06, 0x02, 0x03, 0x00, 0x00, 0x00, 0x02 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("Modbus TCP", result);
        Assert.Contains("事务 ID: 5", result);
        Assert.Contains("单元 ID: 2", result);
        Assert.Contains("读保持寄存器", result);
        Assert.Contains("起始地址: 0", result);
        Assert.Contains("数量: 2", result);
    }

    // ═══════════════════════════════════════════
    //  RTU 报文解析
    // ═══════════════════════════════════════════

    [Fact]
    public void ParseMessage_RtuFc03Request_ShowsStationAndCrc()
    {
        // FC03 RTU: 站号=1, 地址=0, 数量=1
        byte[] stationAndPdu = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };
        byte[] frame = BuildRtuFrame(stationAndPdu);

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Rtu);

        Assert.Contains("Modbus RTU", result);
        Assert.Contains("从站地址: 1", result);
        Assert.Contains("读保持寄存器", result);
        Assert.Contains("起始地址: 0", result);
        Assert.Contains("数量: 1", result);
        Assert.DoesNotContain("CRC 校验失败", result);
    }

    [Fact]
    public void ParseMessage_RtuInvalidCrc_ShowsWarning()
    {
        // 人为篡改 CRC
        byte[] frame = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Rtu);

        Assert.Contains("CRC 校验失败", result);
    }

    // ═══════════════════════════════════════════
    //  ASCII 报文解析
    // ═══════════════════════════════════════════

    [Fact]
    public void ParseMessage_AsciiFc16Request_ShowsRegisterData()
    {
        // FC16 ASCII: 站号=1, 地址=16, 数量=2, 数据=0x1234,0x5678
        byte[] stationAndPdu = { 0x01, 0x10, 0x00, 0x10, 0x00, 0x02, 0x04, 0x12, 0x34, 0x56, 0x78 };
        string asciiFrame = BuildAsciiFrame(stationAndPdu);

        string result = ModbusDiagnostics.ParseMessage(asciiFrame, ModbusProtocol.Ascii);

        Assert.Contains("Modbus ASCII", result);
        Assert.Contains("从站地址: 1", result);
        Assert.Contains("写多寄存器", result);
        Assert.Contains("起始地址: 16", result);
        Assert.Contains("数量: 2", result);
        Assert.DoesNotContain("LRC 校验失败", result);
    }

    // ═══════════════════════════════════════════
    //  异常响应解析
    // ═══════════════════════════════════════════

    [Fact]
    public void ParseMessage_TcpExceptionResponse_ShowsExceptionInfo()
    {
        // FC03 异常响应: 异常码=02 (非法数据地址)
        byte[] frame = { 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x01, 0x83, 0x02 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("异常响应", result);
        Assert.Contains("非法数据地址", result);
    }

    [Fact]
    public void ParseMessage_ExceptionCode01_IllegalFunction()
    {
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x01, 0x81, 0x01 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("非法功能码", result);
    }

    // ═══════════════════════════════════════════
    //  异常码翻译
    // ═══════════════════════════════════════════

    [Theory]
    [InlineData(0x01, "非法功能码")]
    [InlineData(0x02, "非法数据地址")]
    [InlineData(0x03, "非法数据值")]
    [InlineData(0x04, "从站设备故障")]
    [InlineData(0x05, "确认")]
    [InlineData(0x06, "从站设备忙")]
    [InlineData(0x08, "存储奇偶性差错")]
    [InlineData(0x0A, "不可用网关路径")]
    [InlineData(0x0B, "网关目标设备响应失败")]
    public void TranslateException_KnownCodes_ReturnsChineseDescription(byte code, string expectedSubstring)
    {
        string result = ModbusDiagnostics.TranslateException(code);
        Assert.Contains(expectedSubstring, result);
    }

    [Fact]
    public void TranslateException_UnknownCode_ReturnsUnknownMessage()
    {
        string result = ModbusDiagnostics.TranslateException(0xFF);
        Assert.Contains("未知异常码", result);
        Assert.Contains("0xFF", result);
    }

    // ═══════════════════════════════════════════
    //  事务格式化
    // ═══════════════════════════════════════════

    [Fact]
    public void FormatTransaction_RequestAndResponse_ShowsBothParts()
    {
        byte[] request = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };
        byte[] response = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34 };

        string result = ModbusDiagnostics.FormatTransaction(request, response, ModbusProtocol.Tcp);

        Assert.Contains("Modbus 事务", result);
        Assert.Contains("[请求]", result);
        Assert.Contains("[响应]", result);
        Assert.Contains("读保持寄存器", result);
    }

    [Fact]
    public void FormatTransaction_NullResponse_ShowsTimeout()
    {
        byte[] request = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };

        string result = ModbusDiagnostics.FormatTransaction(request, null!, ModbusProtocol.Tcp);

        Assert.Contains("无响应/超时", result);
    }

    // ═══════════════════════════════════════════
    //  边界情况
    // ═══════════════════════════════════════════

    [Fact]
    public void ParseMessage_EmptyData_ReturnsEmptyMessage()
    {
        Assert.Contains("空报文", ModbusDiagnostics.ParseMessage(new byte[0], ModbusProtocol.Tcp));
        Assert.Contains("空报文", ModbusDiagnostics.ParseMessage("", ModbusProtocol.Tcp));
    }

    [Fact]
    public void ParseMessage_TcpTooShort_ReturnsShortMessage()
    {
        byte[] frame = { 0x00, 0x01, 0x00 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("过短", result);
    }

    [Fact]
    public void ParseMessage_RtuTooShort_ReturnsShortMessage()
    {
        byte[] frame = { 0x01, 0x03 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Rtu);

        Assert.Contains("过短", result);
    }

    [Fact]
    public void ParseMessage_HexString_ParsesCorrectly()
    {
        string hex = "0001 0000 0006 01 03 0000 0001";

        string result = ModbusDiagnostics.ParseMessage(hex, ModbusProtocol.Tcp);

        Assert.Contains("Modbus TCP", result);
        Assert.Contains("读保持寄存器", result);
    }

    // ═══════════════════════════════════════════
    //  FC05/FC06/FC15/FC22/FC23 解析
    // ═══════════════════════════════════════════

    [Fact]
    public void ParseMessage_TcpFc05_ShowsCoilValue()
    {
        // FC05: 写单线圈, 地址=5, 值=ON
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x05, 0x00, 0x05, 0xFF, 0x00 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("写单线圈", result);
        Assert.Contains("线圈地址: 5", result);
        Assert.Contains("ON", result);
    }

    [Fact]
    public void ParseMessage_TcpFc06_ShowsRegisterValue()
    {
        // FC06: 写单寄存器, 地址=10, 值=12345
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x06, 0x00, 0x0A, 0x30, 0x39 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("写单寄存器", result);
        Assert.Contains("寄存器地址: 10", result);
        Assert.Contains("12345", result);
    }

    [Fact]
    public void ParseMessage_TcpFc22_ShowsMasks()
    {
        // FC22: 掩码写寄存器, 地址=16, AND=0xFF00, OR=0x00F0
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x01, 0x16, 0x00, 0x10, 0xFF, 0x00, 0x00, 0xF0 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("掩码写寄存器", result);
        Assert.Contains("寄存器地址: 16", result);
        Assert.Contains("0xFF00", result);
        Assert.Contains("0x00F0", result);
    }

    [Fact]
    public void ParseMessage_TcpFc23_ShowsReadWriteInfo()
    {
        // FC23: 读地址=0/数量=2, 写地址=10/数量=1, 数据=0x1234
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x0D, 0x01, 0x17,
                          0x00, 0x00, 0x00, 0x02, 0x00, 0x0A, 0x00, 0x01, 0x02, 0x12, 0x34 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("读写多寄存器", result);
        Assert.Contains("读起始地址: 0", result);
        Assert.Contains("读数量: 2", result);
        Assert.Contains("写起始地址: 10", result);
        Assert.Contains("写数量: 1", result);
    }

    [Fact]
    public void ParseMessage_TcpFc08_ShowsDiagnosticsInfo()
    {
        // FC08: 子功能=0x0000 (返回查询数据), 数据=0xA5A5
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x08, 0x00, 0x00, 0xA5, 0xA5 };

        string result = ModbusDiagnostics.ParseMessage(frame, ModbusProtocol.Tcp);

        Assert.Contains("诊断", result);
        Assert.Contains("0x0000", result);
        Assert.Contains("返回查询数据", result);
    }

    // ═══════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════

    private static byte[] BuildRtuFrame(byte[] stationAndPdu)
    {
        ushort crc = CrcCalculator.ComputeCrc16(stationAndPdu);
        byte[] frame = new byte[stationAndPdu.Length + 2];
        Buffer.BlockCopy(stationAndPdu, 0, frame, 0, stationAndPdu.Length);
        frame[stationAndPdu.Length] = (byte)(crc & 0xFF);
        frame[stationAndPdu.Length + 1] = (byte)((crc >> 8) & 0xFF);
        return frame;
    }

    private static string BuildAsciiFrame(byte[] stationAndPdu)
    {
        byte lrc = CrcCalculator.ComputeLrc(stationAndPdu);
        byte[] frame = new byte[stationAndPdu.Length + 1];
        Buffer.BlockCopy(stationAndPdu, 0, frame, 0, stationAndPdu.Length);
        frame[stationAndPdu.Length] = lrc;

        var builder = new System.Text.StringBuilder(frame.Length * 2 + 3);
        builder.Append(':');
        for (int i = 0; i < frame.Length; i++)
            builder.Append(frame[i].ToString("X2"));
        builder.Append("\r\n");
        return builder.ToString();
    }
}
