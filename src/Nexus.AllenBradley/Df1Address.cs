using System;

namespace Nexus.AllenBradley
{
    /// <summary>
    /// DF1/SLC 地址解析 — 支持 SLC-500 / PLC-5 数据文件寻址。
    /// <para>地址格式: {文件类型}{文件号}:{元素号}[.{子元素}]</para>
    /// <para>示例: N7:0, F8:1, B3:0/5, T4:0.ACC, C5:0.PRE, ST9:0</para>
    /// </summary>
    public class Df1Address : IDataAddress
    {
        /// <summary>原始地址字符串。</summary>
        public string Original { get; set; } = string.Empty;

        /// <summary>数据文件类型码。</summary>
        public byte FileType { get; set; }

        /// <summary>文件号。</summary>
        public ushort FileNumber { get; set; }

        /// <summary>元素号。</summary>
        public ushort Element { get; set; }

        /// <summary>子元素/位偏移。</summary>
        public ushort SubElement { get; set; }

        public override string ToString()
            => $"Type=0x{FileType:X2} File={FileNumber} Elem={Element} Sub={SubElement}";
    }

    /// <summary>DF1/SLC 数据文件类型码。</summary>
    public enum Df1FileType : byte
    {
        Output = 0x82,
        Input = 0x83,
        Status = 0x84,
        Bit = 0x85,
        Timer = 0x86,
        Counter = 0x87,
        Control = 0x88,
        Integer = 0x89,
        Float = 0x8A,
        String = 0x8D,
        Ascii = 0x8E,
        Long = 0x91,
    }

    /// <summary>
    /// DF1/SLC 地址解析器。
    /// <para>支持: N7:0, B3:0/5, T4:0.ACC, C5:0.PRE, F8:0, ST9:0, I:0, O:0, S:0, R6:0, L10:0</para>
    /// <para>默认文件号: S→2, I→1, O→0, ST→1</para>
    /// </summary>
    public class Df1AddressParser : IAddressParser<Df1Address>
    {
        public Df1Address Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address ?? "", "地址不能为空");

            string addr = address.Trim().ToUpperInvariant();
            var result = new Df1Address { Original = address, SubElement = 0 };

            int numStart = 1;
            char typeChar = addr[0];

            if (typeChar == 'S' && addr.Length > 1 && addr[1] == 'T')
            {
                result.FileType = (byte)Df1FileType.String;
                numStart = 2;
            }
            else
            {
                result.FileType = typeChar switch
                {
                    'N' => (byte)Df1FileType.Integer,
                    'B' => (byte)Df1FileType.Bit,
                    'T' => (byte)Df1FileType.Timer,
                    'C' => (byte)Df1FileType.Counter,
                    'F' => (byte)Df1FileType.Float,
                    'R' => (byte)Df1FileType.Control,
                    'S' => (byte)Df1FileType.Status,
                    'I' => (byte)Df1FileType.Input,
                    'O' => (byte)Df1FileType.Output,
                    'A' => (byte)Df1FileType.Ascii,
                    'L' => (byte)Df1FileType.Long,
                    _ => throw new AddressParseException(address, $"不支持的文件类型: {typeChar}")
                };
            }

            string remainder = addr.Substring(numStart);
            string[] parts = remainder.Split(':');
            if (parts.Length < 2)
                throw new AddressParseException(address, "需要 '文件号:元素号' 格式 (如 N7:0)");

            string filePart = parts[0];
            result.FileNumber = result.FileType switch
            {
                (byte)Df1FileType.Status => (ushort)(filePart.Length == 0 ? 2 : ushort.Parse(filePart)),
                (byte)Df1FileType.Input => (ushort)(filePart.Length == 0 ? 1 : ushort.Parse(filePart)),
                (byte)Df1FileType.Output => (ushort)(filePart.Length == 0 ? 0 : ushort.Parse(filePart)),
                (byte)Df1FileType.String => (ushort)(filePart.Length == 0 ? 1 : ushort.Parse(filePart)),
                _ => ushort.Parse(filePart)
            };

            string elemPart = parts[1];
            int slashIdx = elemPart.IndexOf('/');
            int dotIdx = elemPart.IndexOf('.');

            if (slashIdx >= 0)
            {
                result.Element = ushort.Parse(elemPart.Substring(0, slashIdx));
                result.SubElement = ushort.Parse(elemPart.Substring(slashIdx + 1));
            }
            else if (dotIdx >= 0)
            {
                result.Element = ushort.Parse(elemPart.Substring(0, dotIdx));
                string subPart = elemPart.Substring(dotIdx + 1).ToUpperInvariant();
                result.SubElement = subPart switch
                {
                    "ACC" => 2,
                    "PRE" => 1,
                    "LEN" => 1,
                    "EN" => 0,
                    "DN" => 1,
                    "TT" => 2,
                    "CU" => 0,
                    "CD" => 1,
                    "OV" => 2,
                    "UN" => 3,
                    _ => ushort.Parse(subPart)
                };
            }
            else
            {
                result.Element = ushort.Parse(elemPart);
            }

            return result;
        }

        public bool TryParse(string address, out Df1Address? parsed)
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
    }
}
