// Delta DVP PLC over Modbus ASCII.
// Implements Delta-specific address mapping (X/Y/M/S/T/C/D) on top of standard Modbus ASCII.
// Based on public Delta DVP communication manual.

using System;
using Nexus.Modbus;

namespace Nexus.Delta.Ascii
{
    /// <summary>
    /// 台达 DVP 系列 PLC 客户端 — 基于台达官方 Modbus ASCII 兼容模式实现。
    /// </summary>
    /// <remarks>
    /// <b>实现说明</b>:台达 DVP 系列 PLC(SS2/SA2/SV2/EH3/ES2 等)出厂支持标准 Modbus ASCII 协议
    /// (参考台达公开手册《DVP 应用手册 - 通讯篇》)。本客户端继承 <see cref="ModbusAsciiClient"/>
    /// 获得完整的 Modbus ASCII 能力,并添加台达特定的地址映射。
    /// <para>
    /// <b>地址映射</b>(基于台达手册公开内容,DVP-ES2/SV2 默认配置):
    /// <list type="table">
    ///   <listheader><term>台达地址</term><description>Modbus 类型</description><description>0-based 起始</description></listheader>
    ///   <item><term>X0..X377 (输入,8 进制)</term><description>FC02 Input Status</description><description>0x0000</description></item>
    ///   <item><term>Y0..Y377 (输出,8 进制)</term><description>FC01/FC05 Coil</description><description>0x0000</description></item>
    ///   <item><term>M0..M4095 (辅助继电器)</term><description>FC01/FC05 Coil</description><description>0x0800</description></item>
    ///   <item><term>S0..S1023 (步进继电器)</term><description>FC01/FC05 Coil</description><description>0x2800</description></item>
    ///   <item><term>T0..T255 (定时器触点)</term><description>FC01/FC05 Coil</description><description>0x1800</description></item>
    ///   <item><term>C0..C255 (计数器触点)</term><description>FC01/FC05 Coil</description><description>0x1C00</description></item>
    ///   <item><term>D0..D9999 (数据寄存器)</term><description>FC03/FC06 Holding Register</description><description>0x0000</description></item>
    ///   <item><term>T0..T255 (定时器当前值)</term><description>FC03/FC06 Holding Register</description><description>0x0600</description></item>
    ///   <item><term>C0..C255 (计数器当前值)</term><description>FC03/FC06 Holding Register</description><description>0x0E00</description></item>
    /// </list>
    /// </para>
    /// <para><b>变更说明</b>(Phase C-2):本类从纯 OperateResult.Failed 占位升级为基于
    /// Modbus ASCII 的真实实现。</para>
    /// </remarks>
    public class DeltaAsciiClient : ModbusAsciiClient
    {
        public DeltaAsciiClient(ISerialPort port, byte station = 1, int timeout = 5000)
            : base(port, station, timeout)
        {
            ByteOrder = Endianness.BigEndian;
        }

        // ── 地址映射(使用 Modbus 5 位数字编码,让 ParseAddressEx 自动选 FC)──────

        /// <summary>把台达 X(输入,8 进制)地址转为 Modbus FC02 输入地址字符串。</summary>
        public static string MapInputX(string deltaAddress)
        {
            int oct = ParseDeltaOctal(deltaAddress, 'X');
            return "1" + (oct + 1).ToString("D4");
        }

        /// <summary>把台达 Y(输出,8 进制)地址转为 Modbus FC01/FC05 线圈地址字符串。</summary>
        public static string MapOutputY(string deltaAddress)
        {
            int oct = ParseDeltaOctal(deltaAddress, 'Y');
            return "0" + (oct + 1).ToString("D4");
        }

        /// <summary>把台达 M(辅助继电器)地址转为 Modbus 线圈地址字符串。</summary>
        public static string MapAuxM(string deltaAddress)
        {
            int dec = ParseDeltaDecimal(deltaAddress, 'M');
            return "0" + (0x0800 + dec + 1).ToString("D4");
        }

        /// <summary>把台达 S(步进继电器)地址转为 Modbus 线圈地址字符串。</summary>
        public static string MapStepS(string deltaAddress)
        {
            int dec = ParseDeltaDecimal(deltaAddress, 'S');
            return "0" + (0x2800 + dec + 1).ToString("D4");
        }

        /// <summary>把台达 D(数据寄存器)地址转为 Modbus 保持寄存器地址字符串。</summary>
        public static string MapDataD(string deltaAddress)
        {
            int dec = ParseDeltaDecimal(deltaAddress, 'D');
            return "4" + (dec + 1).ToString("D4");
        }

        /// <summary>把台达 T(定时器当前值)地址转为 Modbus 保持寄存器地址字符串。</summary>
        public static string MapTimerCurrentValueT(string deltaAddress)
        {
            int dec = ParseDeltaDecimal(deltaAddress, 'T');
            return "4" + (0x0600 + dec + 1).ToString("D4");
        }

        /// <summary>把台达 C(计数器当前值)地址转为 Modbus 保持寄存器地址字符串。</summary>
        public static string MapCounterCurrentValueC(string deltaAddress)
        {
            int dec = ParseDeltaDecimal(deltaAddress, 'C');
            return "4" + (0x0E00 + dec + 1).ToString("D4");
        }

        // ── 台达原生 API ────────────────────────

        public OperateResult<bool> ReadInputX(string address) => ReadBool(MapInputX(address));
        public OperateResult<bool> ReadOutputY(string address) => ReadBool(MapOutputY(address));
        public OperateResult WriteOutputY(string address, bool value) => Write(MapOutputY(address), value);
        public OperateResult<bool> ReadAuxM(string address) => ReadBool(MapAuxM(address));
        public OperateResult WriteAuxM(string address, bool value) => Write(MapAuxM(address), value);
        public OperateResult<bool> ReadStepS(string address) => ReadBool(MapStepS(address));
        public OperateResult<short> ReadDataD(string address) => ReadInt16(MapDataD(address));
        public OperateResult WriteDataD(string address, short value) => Write(MapDataD(address), value);
        public OperateResult<int> ReadDataD32(string address) => ReadInt32(MapDataD(address));
        public OperateResult WriteDataD32(string address, int value) => Write(MapDataD(address), value);
        public OperateResult<float> ReadDataDFloat(string address) => ReadFloat(MapDataD(address));
        public OperateResult WriteDataDFloat(string address, float value) => Write(MapDataD(address), value);
        public OperateResult<short> ReadTimerCurrentValue(string address) => ReadInt16(MapTimerCurrentValueT(address));
        public OperateResult<short> ReadCounterCurrentValue(string address) => ReadInt16(MapCounterCurrentValueC(address));

        // ── 内部地址解析 ─────────────────────────

        private static int ParseDeltaOctal(string address, char prefix)
        {
            string digits = StripPrefix(address, prefix);
            try { return Convert.ToInt32(digits, 8); }
            catch (Exception ex)
            {
                throw new FormatException($"台达 {prefix} 地址无效(应为 8 进制数字): {address} — {ex.Message}");
            }
        }

        private static int ParseDeltaDecimal(string address, char prefix)
        {
            string digits = StripPrefix(address, prefix);
            if (!int.TryParse(digits, out int v))
                throw new FormatException($"台达 {prefix} 地址无效(应为十进制数字): {address}");
            return v;
        }

        private static string StripPrefix(string address, char prefix)
        {
            if (string.IsNullOrEmpty(address))
                throw new FormatException("台达地址为空");
            char first = char.ToUpperInvariant(address[0]);
            if (first != char.ToUpperInvariant(prefix))
                throw new FormatException($"台达地址前缀不匹配: 期望 '{prefix}', 实际 '{first}' — {address}");
            return address.Substring(1);
        }
    }
}
