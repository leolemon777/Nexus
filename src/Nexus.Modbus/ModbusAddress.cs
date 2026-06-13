using System;
using Nexus;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus 强类型地址 — 解析后的内部表示。
    /// </summary>
    public sealed class ModbusAddress : IDataAddress
    {
        /// <summary>用户输入的原始地址字符串。</summary>
        public string Original { get; }

        /// <summary>Modbus 数据区域。</summary>
        public ModbusArea Area { get; }

        /// <summary>起始地址（0-based）。</summary>
        public ushort StartAddress { get; }

        /// <summary>读取时的功能码。</summary>
        public byte ReadFunctionCode { get; }

        /// <summary>写入时的功能码（只读区域为 0）。</summary>
        public byte WriteFunctionCode { get; }

        public ModbusAddress(string original, ModbusArea area, ushort startAddress,
            byte readFc, byte writeFc)
        {
            Original = original;
            Area = area;
            StartAddress = startAddress;
            ReadFunctionCode = readFc;
            WriteFunctionCode = writeFc;
        }

        public override string ToString() => $"{Area}:{StartAddress} (from '{Original}')";
    }

    /// <summary>Modbus 数据区域。</summary>
    public enum ModbusArea
    {
        /// <summary>线圈 (0xxxx) — FC01 读 / FC05,15 写</summary>
        Coil,
        /// <summary>离散输入 (1xxxx) — FC02 只读</summary>
        DiscreteInput,
        /// <summary>输入寄存器 (3xxxx) — FC04 只读</summary>
        InputRegister,
        /// <summary>保持寄存器 (4xxxx) — FC03 读 / FC06,16 写</summary>
        HoldingRegister,
    }

    /// <summary>
    /// Modbus 地址解析器 — 实现 IAddressParser。
    /// 支持前缀模式 (0xxxx/1xxxx/3xxxx/4xxxx) 和无前缀 (默认保持寄存器)。
    /// </summary>
    public sealed class ModbusAddressParser : IAddressParser<ModbusAddress>
    {
        public ModbusAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = AddressContext.ExtractCoreAddress(address).Trim();
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(original, "地址不能为空");

            char prefix = address[0];
            string numPart = address.Substring(1);

            // 至少5位数字才算前缀模式：0xxxx, 1xxxx, 3xxxx, 4xxxx
            if (address.Length >= 5 && char.IsDigit(prefix))
            {
                return prefix switch
                {
                    '0' => new ModbusAddress(address, ModbusArea.Coil,
                        ParseUshort(numPart), 0x01, 0x05),
                    '1' => new ModbusAddress(address, ModbusArea.DiscreteInput,
                        ParseUshort(numPart), 0x02, 0x00),
                    '3' => new ModbusAddress(address, ModbusArea.InputRegister,
                        ParseUshort(numPart), 0x04, 0x00),
                    '4' => new ModbusAddress(address, ModbusArea.HoldingRegister,
                        ParseUshort(numPart), 0x03, 0x06),
                    _ => throw new AddressParseException(address, $"不支持的前缀 '{prefix}'")
                };
            }

            // 无前缀或短地址 — 默认保持寄存器
            return new ModbusAddress(address, ModbusArea.HoldingRegister,
                ParseUshort(address), 0x03, 0x06);
        }

        public bool TryParse(string address, out ModbusAddress? parsed)
        {
            try
            {
                parsed = Parse(address);
                return true;
            }
            catch
            {
                parsed = null;
                return false;
            }
        }

        private static ushort ParseUshort(string s)
            => ushort.Parse(s.TrimStart('0').Length == 0 ? "0" : s.TrimStart('0'));
    }
}
