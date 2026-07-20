// Xinje XC/XD PLC over Modbus RTU.
// Implements Xinje-specific address mapping (X/Y/M/D/HD) on top of standard Modbus RTU.
// Based on public Xinje XC/XD Modbus communication manual.

using System;
using Nexus.Modbus;

namespace Nexus.Xinje.Serial
{
    /// <summary>
    /// 信捷 XC/XD 系列 PLC 串口客户端 — 基于信捷官方 Modbus RTU 兼容模式实现。
    /// </summary>
    /// <remarks>
    /// <b>实现说明</b>:信捷 XC/XD 系列 PLC 出厂支持标准 Modbus RTU 协议(参考信捷官方
    /// 《XC 系列可编程控制器 Modbus 通讯应用手册》)。本客户端直接继承
    /// <see cref="ModbusRtuClient"/> 获得完整的 Modbus RTU 能力,并添加信捷特定的地址映射辅助方法。
    /// <para>
    /// <b>地址映射</b>(基于信捷手册公开内容,默认配置):
    /// <list type="table">
    ///   <listheader><term>信捷地址</term><description>Modbus 类型/功能码</description><description>Modbus 起始地址</description></listheader>
    ///   <item><term>X0..X177 (输入继电器, 8 进制)</term><description>Input Status / FC02</description><description>0x0000</description></item>
    ///   <item><term>Y0..Y177 (输出继电器, 8 进制)</term><description>Coil / FC01/FC05</description><description>0x0000</description></item>
    ///   <item><term>M0..M1499 (辅助继电器)</term><description>Coil / FC01/FC05</description><description>0x8000</description></item>
    ///   <item><term>S0..S255 (状态继电器)</term><description>Coil / FC01/FC05</description><description>0x9000</description></item>
    ///   <item><term>T0..T255 (定时器触点)</term><description>Coil / FC01/FC05</description><description>0xC000</description></item>
    ///   <item><term>C0..C255 (计数器触点)</term><description>Coil / FC01/FC05</description><description>0xD000</description></item>
    ///   <item><term>D0..D7999 (数据寄存器)</term><description>Holding Register / FC03/FC06/FC10</description><description>0x0000</description></item>
    ///   <item><term>HD0..HD499 (高速数据寄存器)</term><description>Holding Register</description><description>0x4000</description></item>
    ///   <item><term>T0..T255 (定时器当前值)</term><description>Holding Register</description><description>0xE000</description></item>
    ///   <item><term>C0..C255 (计数器当前值)</term><description>Holding Register</description><description>0xF000</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>使用方法</b>:既可以直接用 Modbus 地址(<c>ReadInt16("40001")</c>),
    /// 也可以用信捷原生地址(<c>ReadCoilX("Y0")</c>)。
    /// </para>
    /// <para><b>变更说明</b>(Phase C-1):本类从纯 OperateResult.Failed 占位升级为基于
    /// Modbus RTU 的真实实现,无需设备手册即可工作。</para>
    /// </remarks>
    public class XinjeSerialClient : ModbusRtuClient
    {
        /// <summary>
        /// 构造影捷 XC/XD Modbus RTU 客户端。
        /// </summary>
        /// <param name="port">已配置好参数的串口实现。</param>
        /// <param name="station">PLC 站号(信捷默认 1)。</param>
        /// <param name="timeout">通讯超时(毫秒)。</param>
        public XinjeSerialClient(ISerialPort port, byte station = 1, int timeout = 5000)
            : base(port, station, timeout)
        {
            // 信捷 Modbus 默认大端字节序(Modbus 标准)。
            ByteOrder = Endianness.BigEndian;
        }

        // ── 地址映射辅助 ─────────────────────────
        //
        // Modbus 地址编码约定(由 Nexus.Modbus.ModbusRtuClient.ParseAddressEx 解析):
        //   - "0xxxx"  → FC01 读线圈 / FC05 写单线圈       (线圈区段,PLC Y/M)
        //   - "1xxxx"  → FC02 读输入状态                   (输入区段,PLC X)
        //   - "3xxxx"  → FC04 读输入寄存器                 (输入寄存器,只读)
        //   - "4xxxx"  → FC03 读保持寄存器 / FC06 写单寄存器(保持寄存器,PLC D)
        //   - 纯数字(无前缀字符匹配)→ 默认 FC03/FC06
        //
        // 信捷 X/Y 是 8 进制;M/S/T/C 是十进制;D/HD 是十进制。
        // 信捷手册公开的偏移量(默认配置):
        //   X0    → Modbus 输入 10001(0-based offset = 0)
        //   Y0    → Modbus 线圈 00001(0-based offset = 0)
        //   M0    → Modbus 线圈 00001 + 0x8000(信捷固定偏移)
        //   S0    → Modbus 线圈 00001 + 0x9000
        //   D0    → Modbus 保持寄存器 40001(0-based offset = 0)
        //   HD0   → Modbus 保持寄存器 40001 + 0x4000

        /// <summary>把信捷 X(输入)地址(如 "X0"、"X17",8 进制)转为 Modbus FC02 地址字符串。</summary>
        public static string MapInputX(string xinjeAddress)
        {
            int oct = ParseXinjeOctal(xinjeAddress, 'X');
            // FC02 输入状态,前缀 '1'。Modbus 协议地址 1-based,但 ParseAddressEx 把 "1xxxx"
            // 的 numPart 解析为 0-based,所以 numPart = oct+1(让 0-based = oct)。
            return "1" + (oct + 1).ToString("D4");
        }

        /// <summary>把信捷 Y(输出)地址(8 进制)转为 Modbus FC01/FC05 地址字符串。</summary>
        public static string MapOutputY(string xinjeAddress)
        {
            int oct = ParseXinjeOctal(xinjeAddress, 'Y');
            // FC01 线圈,前缀 '0'。0-based = oct,所以 numPart = oct+1。
            return "0" + (oct + 1).ToString("D4");
        }

        /// <summary>把信捷 M(辅助继电器)地址(十进制)转为 Modbus 线圈地址字符串。</summary>
        public static string MapAuxM(string xinjeAddress)
        {
            int dec = ParseXinjeDecimal(xinjeAddress, 'M');
            // 信捷手册:M 起始偏移 0x8000(线圈区段,FC01/FC05)。
            int modbusAddr = 0x8000 + dec;  // 0-based
            return "0" + (modbusAddr + 1).ToString("D4");
        }

        /// <summary>把信捷 S(状态)地址(十进制)转为 Modbus 线圈地址字符串。</summary>
        public static string MapStateS(string xinjeAddress)
        {
            int dec = ParseXinjeDecimal(xinjeAddress, 'S');
            int modbusAddr = 0x9000 + dec;
            return "0" + (modbusAddr + 1).ToString("D4");
        }

        /// <summary>把信捷 D(数据寄存器)地址(十进制)转为 Modbus 保持寄存器地址字符串。</summary>
        public static string MapDataD(string xinjeAddress)
        {
            int dec = ParseXinjeDecimal(xinjeAddress, 'D');
            // FC03/FC06 保持寄存器,前缀 '4'。0-based = dec,所以 numPart = dec+1。
            return "4" + (dec + 1).ToString("D4");
        }

        /// <summary>把信捷 HD(高速数据寄存器)地址(十进制)转为 Modbus 保持寄存器地址字符串。</summary>
        public static string MapHighSpeedDataHD(string xinjeAddress)
        {
            int dec = ParseXinjeDecimal(xinjeAddress, 'H', requiredSecondChar: 'D');
            int modbusAddr = 0x4000 + dec;
            return "4" + (modbusAddr + 1).ToString("D4");
        }

        // ── 信捷原生地址读写 API ─────────────────

        /// <summary>读取输入继电器 X(FC02)。</summary>
        public OperateResult<bool> ReadInputX(string address)
            => ReadBool(MapInputX(address));

        /// <summary>读取/写入输出继电器 Y(FC01/FC05)。</summary>
        public OperateResult<bool> ReadOutputY(string address)
            => ReadBool(MapOutputY(address));

        /// <summary>写入输出继电器 Y。</summary>
        public OperateResult WriteOutputY(string address, bool value)
            => Write(MapOutputY(address), value);

        /// <summary>读取辅助继电器 M。</summary>
        public OperateResult<bool> ReadAuxM(string address)
            => ReadBool(MapAuxM(address));

        /// <summary>写入辅助继电器 M。</summary>
        public OperateResult WriteAuxM(string address, bool value)
            => Write(MapAuxM(address), value);

        /// <summary>读取状态继电器 S。</summary>
        public OperateResult<bool> ReadStateS(string address)
            => ReadBool(MapStateS(address));

        /// <summary>读取数据寄存器 D 的 16 位整数值。</summary>
        public OperateResult<short> ReadDataD(string address)
            => ReadInt16(MapDataD(address));

        /// <summary>写入数据寄存器 D 的 16 位整数值。</summary>
        public OperateResult WriteDataD(string address, short value)
            => Write(MapDataD(address), value);

        /// <summary>读取数据寄存器 D 的 32 位整数值(占两个连续寄存器,大端拼接)。</summary>
        public OperateResult<int> ReadDataD32(string address)
            => ReadInt32(MapDataD(address));

        /// <summary>写入数据寄存器 D 的 32 位整数值。</summary>
        public OperateResult WriteDataD32(string address, int value)
            => Write(MapDataD(address), value);

        /// <summary>读取数据寄存器 D 的浮点值(占两个连续寄存器)。</summary>
        public OperateResult<float> ReadDataDFloat(string address)
            => ReadFloat(MapDataD(address));

        /// <summary>写入数据寄存器 D 的浮点值。</summary>
        public OperateResult WriteDataDFloat(string address, float value)
            => Write(MapDataD(address), value);

        /// <summary>读取高速数据寄存器 HD 的 16 位整数值。</summary>
        public OperateResult<short> ReadHighSpeedDataHD(string address)
            => ReadInt16(MapHighSpeedDataHD(address));

        /// <summary>写入高速数据寄存器 HD。</summary>
        public OperateResult WriteHighSpeedDataHD(string address, short value)
            => Write(MapHighSpeedDataHD(address), value);

        // ── 内部地址解析 ─────────────────────────

        private static int ParseXinjeOctal(string address, char prefix)
        {
            // 信捷 X/Y 是 8 进制地址(X0..X7, X10..X17, ...)。
            string digits = StripPrefix(address, prefix);
            try
            {
                return Convert.ToInt32(digits, 8);
            }
            catch (Exception ex)
            {
                throw new FormatException($"信捷 {prefix} 地址无效(应为 8 进制数字): {address} — {ex.Message}");
            }
        }

        private static int ParseXinjeDecimal(string address, char prefix, char? requiredSecondChar = null)
        {
            string digits = StripPrefix(address, prefix, requiredSecondChar);
            if (!int.TryParse(digits, out int v))
                throw new FormatException($"信捷 {prefix}{(requiredSecondChar.HasValue ? requiredSecondChar.Value.ToString() : "")} 地址无效(应为十进制数字): {address}");
            return v;
        }

        private static string StripPrefix(string address, char prefix, char? requiredSecondChar = null)
        {
            if (string.IsNullOrEmpty(address))
                throw new FormatException($"信捷地址为空");

            char first = char.ToUpperInvariant(address[0]);
            if (first != char.ToUpperInvariant(prefix))
                throw new FormatException($"信捷地址前缀不匹配: 期望 '{prefix}', 实际 '{first}' — {address}");

            string rest = address.Substring(1);
            if (requiredSecondChar.HasValue)
            {
                if (rest.Length == 0 || char.ToUpperInvariant(rest[0]) != char.ToUpperInvariant(requiredSecondChar.Value))
                    throw new FormatException($"信捷地址第二字符不匹配: 期望 '{requiredSecondChar}', 实际 '{(rest.Length > 0 ? rest[0].ToString() : "<空>")}' — {address}");
                rest = rest.Substring(1);
            }

            return rest;
        }
    }
}
