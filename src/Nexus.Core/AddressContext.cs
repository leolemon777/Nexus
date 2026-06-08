using System;
using System.Collections.Generic;

namespace Nexus
{
    /// <summary>
    /// 地址上下文 — 解析运行时参数覆盖格式的地址字符串。
    /// 支持格式：<c>x=3;s=2;D100</c>，其中分号前的 key=value 对为参数，最后一段为核心地址。
    /// </summary>
    public class AddressContext
    {
        /// <summary>核心地址（去除参数后的裸地址）。</summary>
        public string CoreAddress { get; }

        /// <summary>用户输入的原始地址字符串。</summary>
        public string OriginalAddress { get; }

        /// <summary>解析出的参数字典（只读）。</summary>
        public IReadOnlyDictionary<string, string> Parameters { get; }

        /// <param name="originalAddress">原始地址字符串。</param>
        /// <param name="coreAddress">核心地址。</param>
        /// <param name="parameters">参数字典。</param>
        private AddressContext(string originalAddress, string coreAddress, Dictionary<string, string> parameters)
        {
            OriginalAddress = originalAddress;
            CoreAddress = coreAddress;
            Parameters = parameters;
        }

        /// <summary>获取参数值，不存在时返回 null。</summary>
        public string? GetParameter(string key)
        {
            return Parameters.TryGetValue(key, out var value) ? value : null;
        }

        /// <summary>获取参数值并解析为整数，不存在或无法解析时返回 null。</summary>
        public int? GetIntParameter(string key)
        {
            if (!Parameters.TryGetValue(key, out var value))
                return null;

            return int.TryParse(value, out var result) ? (int?)result : null;
        }

        /// <summary>判断是否存在指定参数。</summary>
        public bool HasParameter(string key)
        {
            return Parameters.ContainsKey(key);
        }

        /// <summary>解析地址字符串为 AddressContext。</summary>
        /// <exception cref="AddressParseException">地址格式无效时抛出。</exception>
        public static AddressContext Parse(string address)
        {
            if (address == null)
                throw new AddressParseException("(null)", "地址不能为 null");

            if (string.IsNullOrWhiteSpace(address))
                return new AddressContext(address, string.Empty, new Dictionary<string, string>());

            var parameters = new Dictionary<string, string>();
            string coreAddress = string.Empty;

            // 按分号拆分
            string[] segments = address.Split(';');

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i].Trim();

                if (string.IsNullOrEmpty(segment))
                    continue;

                int eqIndex = segment.IndexOf('=');
                if (eqIndex >= 0)
                {
                    // key=value 格式 → 参数
                    string key = segment.Substring(0, eqIndex).Trim();
                    string value = segment.Substring(eqIndex + 1).Trim();

                    if (string.IsNullOrEmpty(key))
                        throw new AddressParseException(address, $"参数键不能为空: '{segment}'");

                    // 重复键 → 后者覆盖
                    parameters[key] = value;
                }
                else
                {
                    // 无等号 → 核心地址（最后一个无等号的段）
                    coreAddress = segment;
                }
            }

            return new AddressContext(address, coreAddress, parameters);
        }

        /// <summary>尝试解析地址字符串，失败不抛异常。</summary>
        public static bool TryParse(string address, out AddressContext result)
        {
            try
            {
                result = Parse(address);
                return true;
            }
            catch (AddressParseException)
            {
                result = default!;
                return false;
            }
        }

        /// <summary>从地址字符串中提取核心地址（快捷方法）。</summary>
        public static string ExtractCoreAddress(string address)
        {
            return Parse(address).CoreAddress;
        }

        public override string ToString()
        {
            if (Parameters.Count == 0)
                return CoreAddress;

            var parts = new List<string>(Parameters.Count + 1);
            foreach (var kvp in Parameters)
                parts.Add($"{kvp.Key}={kvp.Value}");
            parts.Add(CoreAddress);
            return string.Join(";", parts);
        }
    }
}
