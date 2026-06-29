using System;
using Nexus;

namespace Nexus.Iec103
{
    public enum Iec103AsduType : byte
    {
        M_IT_NA_1 = 1,     // 时间同步
        M_IT_TA_1 = 2,     // 带时标的时间同步
        M_SP_NA_1 = 1,     // 单点信息
        M_DP_NA_1 = 3,     // 双点信息
        M_ME_NA_1 = 9,     // 测量值，标度化值
        M_ME_NB_1 = 11,    // 测量值，标度化值
        M_ME_NC_1 = 13,    // 测量值，短浮点数
        M_IT_NA_1_2 = 15,  // 计数量
        M_SP_TB_1 = 30,    // 带时标的单点信息
        M_ME_TF_1 = 36,    // 带时标的短浮点数
        C_SC_NA_1 = 45,    // 单命令
        C_DC_NA_1 = 46,    // 双命令
        C_SE_NA_1 = 48,    // 设定值命令
        C_IC_NA_1 = 100,   // 总召唤
        C_CI_NA_1 = 101,   // 计数量召唤
        C_CS_NA_1 = 103,   // 时钟同步
        C_TS_NA_1 = 104,   // 测试命令
        P_ME_NA_1 = 110,   // 参数设定值
        P_ME_NB_1 = 111,   // 参数设定值
        P_AC_NA_1 = 113,   // 参数激活
        F_DR_NA_1 = 126,   // 文件传输
    }

    public sealed class Iec103Address : IDataAddress
    {
        public string Original { get; }
        public Iec103AsduType Type { get; }
        public byte FunctionType { get; }
        public byte InformationNumber { get; }
        public ushort Ca { get; }

        public Iec103Address(string original, Iec103AsduType type, byte functionType, byte informationNumber, ushort ca = 0)
        {
            Original = original;
            Type = type;
            FunctionType = functionType;
            InformationNumber = informationNumber;
            Ca = ca;
        }
    }

    public sealed class Iec103AddressParser : IAddressParser<Iec103Address>
    {
        public Iec103Address Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim().ToUpperInvariant();

            // 格式: Type.FunctionType.InformationNumber[@CA]
            // 例如: M_ME_NC_1.1.1, 13.1.1@0, C_SC_NA_1.1.1
            ushort ca = 0;
            int atIdx = address.IndexOf('@');
            if (atIdx >= 0)
            {
                ca = ushort.Parse(address.Substring(atIdx + 1));
                address = address.Substring(0, atIdx);
            }

            string[] parts = address.Split('.');
            if (parts.Length < 3)
                throw new AddressParseException(address, "IEC 103 地址格式: Type.FunctionType.InformationNumber[@CA]");

            string typePart = parts[0];
            byte functionType = byte.Parse(parts[1]);
            byte infoNumber = byte.Parse(parts[2]);

            Iec103AsduType type;
            if (byte.TryParse(typePart, out byte typeNum))
                type = (Iec103AsduType)typeNum;
            else if (!Enum.TryParse(typePart, true, out type))
                throw new AddressParseException(address, $"未知 ASDU 类型: {typePart}");

            return new Iec103Address(original, type, functionType, infoNumber, ca);
        }

        public bool TryParse(string address, out Iec103Address? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
