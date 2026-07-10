using System;
using Nexus;

namespace Nexus.BrPowerlink
{
    /// <summary>
    /// B&amp;R POWERLINK 对象字典地址。
    /// <para>地址格式: [node.]index.subindex，省略 node 时默认 1（MN 默认操作的 CN 编号）。</para>
    /// <para>index 支持 0x 十六进制（如 0x6000）或十进制（如 24576）；subIndex 同样支持 0x。</para>
    /// <para>示例: "1.6000.0"、"6000.0x00"、"0x6000.1"、"0x6000.0x0A"。</para>
    /// </summary>
    public sealed class BrPowerlinkAddress : IDataAddress
    {
        /// <summary>用户输入的原始地址字符串。</summary>
        public string Original { get; }

        /// <summary>节点 ID（CN 编号，1..239）。</summary>
        public byte NodeId { get; }

        /// <summary>对象字典索引（16 位）。</summary>
        public ushort Index { get; }

        /// <summary>子索引（8 位）。</summary>
        public byte SubIndex { get; }

        /// <summary>
        /// 构造对象字典地址。
        /// </summary>
        /// <param name="original">原始地址字符串。</param>
        /// <param name="index">对象字典索引。</param>
        /// <param name="subIndex">子索引。</param>
        /// <param name="nodeId">节点 ID（默认 1）。</param>
        public BrPowerlinkAddress(string original, ushort index, byte subIndex, byte nodeId = BrPowerlinkConstants.DefaultNodeId)
        {
            Original = original;
            Index = index;
            SubIndex = subIndex;
            NodeId = nodeId;
        }

        /// <inheritdoc/>
        public override string ToString()
            => $"{NodeId}.{Index}.{SubIndex}";

        // ═══════════════════════════════════════════
        //  静态解析
        // ═══════════════════════════════════════════

        /// <summary>
        /// 解析对象字典地址字符串。
        /// </summary>
        /// <param name="address">地址字符串，格式 [node.]index.subindex。</param>
        /// <returns>解析后的 <see cref="BrPowerlinkAddress"/>。</returns>
        /// <exception cref="AddressParseException">地址格式无效时抛出。</exception>
        public static BrPowerlinkAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address ?? "", "地址不能为空");

            string original = address;
            address = address.Trim();

            string[] parts = address.Split('.');

            if (parts.Length == 3)
            {
                byte node = ParseByte(parts[0], "node");
                ushort index = ParseUShort(parts[1], "index");
                byte subIndex = ParseByte(parts[2], "subindex");
                return new BrPowerlinkAddress(original, index, subIndex, node);
            }

            if (parts.Length == 2)
            {
                ushort index = ParseUShort(parts[0], "index");
                byte subIndex = ParseByte(parts[1], "subindex");
                return new BrPowerlinkAddress(original, index, subIndex);
            }

            throw new AddressParseException(original, "B&R POWERLINK 地址格式: [node.]index.subindex（如 1.6000.0 或 0x6000.0x01）");
        }

        /// <summary>尝试解析地址，失败返回 null。</summary>
        public static BrPowerlinkAddress? TryParse(string address)
        {
            try { return Parse(address); }
            catch { return null; }
        }

        private static ushort ParseUShort(string s, string field)
        {
            try
            {
                return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToUInt16(s, 16)
                    : ushort.Parse(s);
            }
            catch
            {
                throw new AddressParseException(s, $"无效的 {field} 值: '{s}'");
            }
        }

        private static byte ParseByte(string s, string field)
        {
            try
            {
                return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToByte(s, 16)
                    : byte.Parse(s);
            }
            catch
            {
                throw new AddressParseException(s, $"无效的 {field} 值: '{s}'");
            }
        }
    }

    /// <summary>
    /// B&amp;R POWERLINK 地址解析器 — 实现 <see cref="IAddressParser{TAddress}"/>。
    /// </summary>
    public sealed class BrPowerlinkAddressParser : IAddressParser<BrPowerlinkAddress>
    {
        /// <inheritdoc/>
        public BrPowerlinkAddress Parse(string address) => BrPowerlinkAddress.Parse(address);

        /// <inheritdoc/>
        public bool TryParse(string address, out BrPowerlinkAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
