using System;
using Nexus;
using Nexus.Modbus;

namespace Nexus.Modbus.Ascii.Serial
{
    /// <summary>
    /// Modbus ASCII 串口客户端 — 通过 RS-232/485 串口传输 ASCII 格式报文。
    /// <para>与 ModbusAsciiClient 相同功能，但提供独立的 NuGet 包入口。</para>
    /// <para>ASCII 帧格式: ':' + HexChars + LRC + CR + LF</para>
    /// <para>地址格式: D100, 40001, M0, 00001 等标准 Modbus 格式</para>
    /// </summary>
    public class ModbusAsciiSerialClient : ModbusAsciiClient
    {
        /// <summary>
        /// 创建 Modbus ASCII 串口客户端。
        /// </summary>
        /// <param name="port">串口实现。</param>
        /// <param name="station">从站地址（默认 1）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public ModbusAsciiSerialClient(ISerialPort port, byte station = 1, int timeout = 5000)
            : base(port, station, timeout) { }
    }
}
