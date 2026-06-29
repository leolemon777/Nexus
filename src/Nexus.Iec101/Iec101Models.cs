using System;
using Nexus;

namespace Nexus.Iec101
{
    /// <summary>IEC 60870-5-101 ASDU 类型标识。</summary>
    public enum AsduType : byte
    {
        M_SP_NA_1 = 1,    // 单点信息
        M_SP_TA_1 = 2,    // 带时标的单点信息
        M_DP_NA_1 = 3,    // 双点信息
        M_ME_NA_1 = 9,    // 测量值，标度化值
        M_ME_NB_1 = 11,   // 测量值，标度化值
        M_ME_NC_1 = 13,   // 测量值，短浮点数
        M_IT_NA_1 = 15,   // 计数量
        M_SP_TB_1 = 30,   // 带时标CP56Time2a的单点信息
        M_ME_TF_1 = 36,   // 带时标CP56Time2a的短浮点数
        C_SC_NA_1 = 45,   // 单命令
        C_DC_NA_1 = 46,   // 双命令
        C_SE_NA_1 = 48,   // 设定值命令，标度化值
        C_SE_NB_1 = 49,   // 设定值命令，标度化值
        C_SE_NC_1 = 50,   // 设定值命令，短浮点数
        C_IC_NA_1 = 100,  // 总召唤
        C_CI_NA_1 = 101,  // 计数量召唤
        C_CS_NA_1 = 103,  // 时钟同步
    }

    /// <summary>传送原因 (COT)。</summary>
    public enum CauseOfTransmission : byte
    {
        NotUsed = 0,
        Periodic = 1,
        Background = 2,
        Spontaneous = 3,
        Initialized = 4,
        Request = 5,
        Activation = 6,
        ActivationCon = 7,
        Deactivation = 8,
        DeactivationCon = 9,
        ActivationTerm = 10,
        RemoteCommand = 11,
        LocalCommand = 12,
        FileTransfer = 13,
        Interrogation = 20,
        CounterInterrogation = 21,
        ClockSync = 24,
    }

    /// <summary>可变结构限定词。</summary>
    public struct VariableStructQualifier
    {
        public byte Value;
        public int Count => Value & 0x7F;
        public bool IsSequence => (Value & 0x80) != 0;
    }

    /// <summary>IEC 101 地址 — 用于标识信息对象。</summary>
    public sealed class Iec101Address : IDataAddress
    {
        public string Original { get; }
        public AsduType Type { get; }
        public uint Ioa { get; }  // 信息对象地址 (1-3 字节)
        public ushort Ca { get; } // 公共地址

        public Iec101Address(string original, AsduType type, uint ioa, ushort ca = 0)
        {
            Original = original;
            Type = type;
            Ioa = ioa;
            Ca = ca;
        }
    }

    /// <summary>IEC 101 地址解析器。</summary>
    public sealed class Iec101AddressParser : IAddressParser<Iec101Address>
    {
        public Iec101Address Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim().ToUpperInvariant();

            // 格式: Type.IOA 或 Type.IOA@CA
            // 例如: M_ME_NC_1.100, C_SC_NA_1.1@0, 13.100
            string typePart;
            string rest;
            ushort ca = 0;

            int atIdx = address.IndexOf('@');
            if (atIdx >= 0)
            {
                ca = ushort.Parse(address.Substring(atIdx + 1));
                address = address.Substring(0, atIdx);
            }

            int dotIdx = address.IndexOf('.');
            if (dotIdx < 0) throw new AddressParseException(address, "格式: Type.IOA[@CA]");

            typePart = address.Substring(0, dotIdx);
            rest = address.Substring(dotIdx + 1);
            uint ioa = uint.Parse(rest);

            AsduType type;
            if (byte.TryParse(typePart, out byte typeNum))
                type = (AsduType)typeNum;
            else if (!Enum.TryParse(typePart, true, out type))
                throw new AddressParseException(address, $"未知 ASDU 类型: {typePart}");

            return new Iec101Address(original, type, ioa, ca);
        }

        public bool TryParse(string address, out Iec101Address? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
