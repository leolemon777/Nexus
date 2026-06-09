using System;

namespace Nexus.Yaskawa
{
    /// <summary>Memobus 地址区域类型。</summary>
    public enum MemobusArea : byte
    {
        /// <summary>线圈（FC01/FC05）。</summary>
        Coil = 1,
        /// <summary>离散输入（FC02）。</summary>
        DiscreteInput = 2,
        /// <summary>保持寄存器（FC03/FC06/FC16）。</summary>
        HoldingRegister = 3,
        /// <summary>输入寄存器（FC04）。</summary>
        InputRegister = 4,
        /// <summary>扩展保持寄存器（SFC09/0x0B）。</summary>
        ExtendedHolding = 9,
        /// <summary>扩展输入寄存器（SFC0A）。</summary>
        ExtendedInput = 10,
        /// <summary>命名区域 — M（内部继电器）。</summary>
        NamedM = (byte)'M',
        /// <summary>命名区域 — G（全局继电器）。</summary>
        NamedG = (byte)'G',
        /// <summary>命名区域 — I（输入继电器）。</summary>
        NamedI = (byte)'I',
        /// <summary>命名区域 — O（输出继电器）。</summary>
        NamedO = (byte)'O',
        /// <summary>命名区域 — S（步进继电器）。</summary>
        NamedS = (byte)'S',
    }

    /// <summary>YASKAWA PLC 型号。</summary>
    public enum YaskawaModel
    {
        /// <summary>MP2300S 运动控制器。</summary>
        Mp2300S,
        /// <summary>MP2300 运动控制器。</summary>
        Mp2300,
        /// <summary>MP2400 运动控制器。</summary>
        Mp2400,
        /// <summary>MP2000 系列。</summary>
        Mp2000,
        /// <summary>VIPA 系列变频器。</summary>
        Vipa,
        /// <summary>GA700/GA500 变频器。</summary>
        Ga700,
        /// <summary>CH700 变频器。</summary>
        Ch700,
        /// <summary>JE-C 系列伺服。</summary>
        JeC,
        /// <summary>SLIO 安全模块。</summary>
        Slio,
    }

    /// <summary>Memobus 解析后的地址信息。</summary>
    public sealed class MemobusAddress
    {
        /// <summary>原始地址字符串。</summary>
        public string RawAddress { get; }

        /// <summary>是否为命名区域地址（M/G/I/O/S 前缀）。</summary>
        public bool IsNamed { get; }

        /// <summary>区域类型。</summary>
        public MemobusArea Area { get; }

        /// <summary>子功能码（标准地址的 SFC）。</summary>
        public byte SubFunctionCode { get; }

        /// <summary>主功能码。</summary>
        public byte MainFunctionCode { get; }

        /// <summary>数值地址（标准地址）。</summary>
        public ushort AddressValue { get; }

        /// <summary>命名区域的数值地址。</summary>
        public uint NamedAddressValue { get; }

        /// <summary>是否为位访问（命名区域 + MB/.bit 格式）。</summary>
        public bool IsBitAccess { get; }

        /// <summary>位索引（命名区域的位偏移）。</summary>
        public int BoolIndex { get; }

        private MemobusAddress(string raw, bool isNamed, MemobusArea area,
            byte sfc, byte mfc, ushort addrValue, uint namedAddr,
            bool isBitAccess, int boolIndex)
        {
            RawAddress = raw;
            IsNamed = isNamed;
            Area = area;
            SubFunctionCode = sfc;
            MainFunctionCode = mfc;
            AddressValue = addrValue;
            NamedAddressValue = namedAddr;
            IsBitAccess = isBitAccess;
            BoolIndex = boolIndex;
        }

        /// <summary>
        /// 解析 Memobus 地址字符串。
        /// 标准格式: "100"（纯数字）或 "100;x=3"（指定 SFC）
        /// 命名格式: "M100", "MB100.5", "G200", "I0", "O10A", "S50"
        /// 扩展参数: "100;mfc=0x43;x=9"（指定 MFC 和 SFC）
        /// </summary>
        public static MemobusAddress? TryParse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return null;

            try
            {
                string working = address;
                byte mfc = ExtractByteParam(ref working, "mfc", 0x20);
                byte sfc = ExtractByteParam(ref working, "x", 3);

                // 命名区域地址
                if (IsNamedPrefix(working))
                {
                    byte dataType = (byte)char.ToUpperInvariant(working[0]);
                    bool isBit = IsBitAccessInternal(working);
                    int boolIndex = isBit ? CalculateBoolIndexInternal(working) : 0;
                    string body = working.Substring(1);
                    if (body.Length > 0 && (body[0] == 'B' || body[0] == 'b'))
                        body = body.Substring(1);
                    int dotIdx = body.IndexOf('.');
                    string numPart = dotIdx > 0 ? body.Substring(0, dotIdx) : body;
                    if (numPart.Length > 0 && char.IsLetter(numPart[numPart.Length - 1]))
                        numPart = numPart.Substring(0, numPart.Length - 1);
                    uint namedAddr = numPart.Length > 0 ? Convert.ToUInt32(numPart) : 0;

                    MemobusArea area = (MemobusArea)dataType;

                    return new MemobusAddress(address, true, area,
                        isBit ? (byte)0x41 : (byte)0x49,
                        0x43, 0, namedAddr, isBit, boolIndex);
                }

                // 标准数字地址
                if (!ushort.TryParse(working, out ushort addrValue))
                    return null;

                MemobusArea stdArea;
                if (sfc == 1) stdArea = MemobusArea.Coil;
                else if (sfc == 2) stdArea = MemobusArea.DiscreteInput;
                else if (sfc == 3) stdArea = MemobusArea.HoldingRegister;
                else if (sfc == 4) stdArea = MemobusArea.InputRegister;
                else if (sfc == 9) stdArea = MemobusArea.ExtendedHolding;
                else if (sfc == 10) stdArea = MemobusArea.ExtendedInput;
                else stdArea = MemobusArea.HoldingRegister;

                return new MemobusAddress(address, false, stdArea,
                    sfc, mfc, addrValue, 0, false, 0);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>判断地址是否为命名区域地址。</summary>
        public static bool IsNamedPrefix(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            char c = char.ToUpperInvariant(address[0]);
            return c == 'M' || c == 'G' || c == 'I' || c == 'O' || c == 'S';
        }

        /// <summary>获取命名区域的数据类型编码。</summary>
        public static byte GetDataType(string address)
        {
            if (string.IsNullOrEmpty(address)) return 0;
            return (byte)char.ToUpperInvariant(address[0]);
        }

        /// <summary>判断命名地址是否包含位访问（MB 前缀、点号、尾字母）。</summary>
        public static bool IsBitAccessInternal(string address)
        {
            if (address.Length < 2) return false;
            if (address[1] == 'B' || address[1] == 'b') return true;
            if (address.IndexOf('.') > 1) return true;
            // 尾字母表示位号: M10A, G5F 等
            if (address.Length > 2)
            {
                char last = char.ToUpperInvariant(address[address.Length - 1]);
                if (last >= 'A' && last <= 'F')
                {
                    // 确保前面部分是数字
                    string prefix = address.Substring(1, address.Length - 2);
                    if (prefix.Length > 0 && int.TryParse(prefix, out _))
                        return true;
                }
            }
            return false;
        }

        /// <summary>计算命名区域的布尔索引。</summary>
        public static int CalculateBoolIndexInternal(string address)
        {
            string body = address.Substring(1);
            if (body.Length > 0 && (body[0] == 'B' || body[0] == 'b'))
                body = body.Substring(1);

            int dotIdx = body.IndexOf('.');
            if (dotIdx > 0)
            {
                int wordNo = Convert.ToInt32(body.Substring(0, dotIdx));
                int bitNo = CalculateBitIndex(body.Substring(dotIdx + 1));
                return wordNo * 16 + bitNo;
            }

            if (body.Length > 1 && char.IsLetter(body[body.Length - 1]))
            {
                int wordNo = Convert.ToInt32(body.Substring(0, body.Length - 1));
                int bitNo = CalculateBitIndex(body.Substring(body.Length - 1));
                return wordNo * 16 + bitNo;
            }

            return Convert.ToInt32(body) * 16;
        }

        /// <summary>计算位索引（0-9 或 A-F）。</summary>
        public static int CalculateBitIndex(string bitStr)
        {
            if (bitStr.Length == 0) return 0;
            char c = char.ToUpperInvariant(bitStr[0]);
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return int.Parse(bitStr);
        }

        /// <summary>提取地址字符串中的参数值。</summary>
        public static byte ExtractByteParam(ref string address, string paramName, byte defaultValue)
        {
            string key = paramName + "=";
            int idx = address.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return defaultValue;

            int valueStart = idx + key.Length;
            int semicolon = address.IndexOf(';', valueStart);
            string valueStr = semicolon >= 0
                ? address.Substring(valueStart, semicolon - valueStart)
                : address.Substring(valueStart);

            address = address.Substring(0, idx) + (semicolon >= 0 ? address.Substring(semicolon + 1) : "");
            address = address.TrimEnd(';');

            if (valueStr.StartsWith("0x") || valueStr.StartsWith("0X"))
                return Convert.ToByte(valueStr.Substring(2), 16);
            return Convert.ToByte(valueStr);
        }

        /// <summary>获取区域描述。</summary>
        public string GetAreaDescription()
        {
            if (!IsNamed) return Area.ToString();
            return $"Named_{(char)Area}";
        }
    }
}
