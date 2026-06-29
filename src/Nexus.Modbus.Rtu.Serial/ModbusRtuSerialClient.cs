using System;
using Nexus;
using Nexus.Modbus;

namespace Nexus.Modbus.Rtu.Serial
{
    /// <summary>
    /// Modbus RTU 串口客户端 — 通过 RS-232/485 串口传输 RTU 格式报文。
    /// <para>与 ModbusRtuClient 相同功能，但提供独立的 NuGet 包入口。</para>
    /// <para>支持功能码: FC01, FC02, FC03, FC04, FC05, FC06, FC15, FC16, FC22, FC23</para>
    /// <para>地址格式: D100, 40001, M0, 00001 等标准 Modbus 格式</para>
    /// </summary>
    public class ModbusRtuSerialClient : ModbusRtuClient
    {
        /// <summary>
        /// 创建 Modbus RTU 串口客户端。
        /// </summary>
        /// <param name="port">串口实现。</param>
        /// <param name="station">从站地址（默认 1）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public ModbusRtuSerialClient(ISerialPort port, byte station = 1, int timeout = 5000)
            : base(port, station, timeout) { }
    }
}
