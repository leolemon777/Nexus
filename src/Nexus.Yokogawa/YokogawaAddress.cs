using System;

namespace Nexus.Yokogawa
{
    /// <summary>
    /// 横河 PLC 地址解析结果。
    /// 支持继电器类型: X(24), Y(25), I(9), E(5), M(13), T(20), C(3), L(12)
    /// 支持寄存器类型: D(4), B(2), F(6), R(18), V(22), Z(26), W(23), TN(33), CN(49)
    /// </summary>
    public class YokogawaAddress
    {
        /// <summary>数据代码（协议中标识寄存器/继电器类型的编号）。</summary>
        public int DataCode { get; set; }

        /// <summary>起始地址。</summary>
        public int AddressStart { get; set; }

        /// <summary>数据长度。</summary>
        public int Length { get; set; }

        /// <summary>
        /// 获取地址的二进制编码（6 字节，大端序）。
        /// [dataCodeHi, dataCodeLo, addr3, addr2, addr1, addr0]
        /// </summary>
        public byte[] GetAddressBinaryContent()
        {
            return new byte[6]
            {
                (byte)((DataCode >> 8) & 0xFF),
                (byte)(DataCode & 0xFF),
                (byte)((AddressStart >> 24) & 0xFF),
                (byte)((AddressStart >> 16) & 0xFF),
                (byte)((AddressStart >> 8) & 0xFF),
                (byte)(AddressStart & 0xFF)
            };
        }

        /// <summary>
        /// 判断该地址是否为位（继电器）类型。
        /// </summary>
        public bool IsBitType => IsBitDataCode(DataCode);

        /// <summary>
        /// 判断指定 DataCode 是否为继电器（位）类型。
        /// </summary>
        public static bool IsBitDataCode(int dataCode)
        {
            return dataCode == 24 || dataCode == 25 || dataCode == 9 || dataCode == 5 ||
                   dataCode == 13 || dataCode == 20 || dataCode == 3 || dataCode == 12;
        }

        /// <summary>
        /// 从 PLC 地址字符串解析为 YokogawaAddress 对象。
        /// </summary>
        /// <param name="address">PLC 地址，如 D100, X0, Y10, M50 等。</param>
        /// <param name="length">数据长度。</param>
        /// <returns>解析结果。</returns>
        public static OperateResult<YokogawaAddress> ParseFrom(string address, ushort length)
        {
            if (string.IsNullOrWhiteSpace(address))
                return OperateResult<YokogawaAddress>.Failed("地址不能为空");

            try
            {
                int dataCode = 0;
                int addrStart = 0;
                string upper = address.ToUpperInvariant();

                if (upper.StartsWith("CN"))
                {
                    dataCode = 49;
                    addrStart = int.Parse(address.Substring(2));
                }
                else if (upper.StartsWith("TN"))
                {
                    dataCode = 33;
                    addrStart = int.Parse(address.Substring(2));
                }
                else if (upper.StartsWith("X"))
                {
                    dataCode = 24;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("Y"))
                {
                    dataCode = 25;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("I"))
                {
                    dataCode = 9;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("E"))
                {
                    dataCode = 5;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("M"))
                {
                    dataCode = 13;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("T"))
                {
                    dataCode = 20;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("C"))
                {
                    dataCode = 3;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("L"))
                {
                    dataCode = 12;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("D"))
                {
                    dataCode = 4;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("B"))
                {
                    dataCode = 2;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("F"))
                {
                    dataCode = 6;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("R"))
                {
                    dataCode = 18;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("V"))
                {
                    dataCode = 22;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("Z"))
                {
                    dataCode = 26;
                    addrStart = int.Parse(address.Substring(1));
                }
                else if (upper.StartsWith("W"))
                {
                    dataCode = 23;
                    addrStart = int.Parse(address.Substring(1));
                }
                else
                {
                    return OperateResult<YokogawaAddress>.Failed($"不支持的地址类型: {address}");
                }

                return OperateResult<YokogawaAddress>.Success(new YokogawaAddress
                {
                    DataCode = dataCode,
                    AddressStart = addrStart,
                    Length = length
                });
            }
            catch (Exception ex)
            {
                return OperateResult<YokogawaAddress>.Failed($"地址解析失败: {ex.Message}");
            }
        }
    }
}
