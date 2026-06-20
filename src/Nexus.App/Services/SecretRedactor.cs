using System;
using System.Text.RegularExpressions;

namespace Nexus.App.Services;

/// <summary>
/// 把日志/异常文本中可能包含的敏感信息（IP、密码、Token、连接串口令）替换为 <c>***</c>。
/// </summary>
/// <remarks>
/// 纯静态 + 仅依赖 <see cref="System.Text.RegularExpressions.Regex"/>，
/// <b>netstandard2.0 安全</b>（无 Span / MemoryExtensions / 现代重载），便于单元测试。
/// 正则在静态构造期编译一次，热路径只做 <c>Regex.Replace</c>。
/// </remarks>
public static class SecretRedactor
{
    /// <summary>统一脱敏占位符。</summary>
    public const string Mask = "***";

    // 1) IPv4 — 全 0-255 校验留给调用方上下文，这里只做形态匹配（避免把版本号误伤过多，要求四段点分十进制）。
    //    \b 在像 192.168.1.1:502 的冒号边界仍能正确触发。
    private static readonly Regex Ipv4Pattern = new Regex(
        @"\b\d{1,3}(\.\d{1,3}){3}\b",
        RegexOptions.Compiled);

    // 2) JSON 值 —— "password": "x" / "apiKey": "x"（键名忽略大小写、允许单双引号、允许前后空白）。
    //    命中的键：password | passwd | secret | token | apikey | api_key | credential。
    //    显式匹配 JSON 值分隔符，避免把形如 "the password is foo" 的散文误脱敏。
    private static readonly Regex JsonSecretPattern = new Regex(
        @"(?i)""?(?:password|passwd|secret|token|apikey|api_key|credential)""?\s*[:=]\s*""?([^"",}\s]+)""",
        RegexOptions.Compiled);

    // 3) 连接串 —— Password=...; / Pwd=...; （键名忽略大小写；值持续到下一个分号或串尾）。
    private static readonly Regex ConnStrSecretPattern = new Regex(
        @"(?i)(Password|Pwd)\s*=\s*[^;]+",
        RegexOptions.Compiled);

    // 4) Basic Authorization 头 / Bearer Token —— 顺手覆盖，免得明文 token 进日志。
    private static readonly Regex AuthHeaderPattern = new Regex(
        @"(?i)(Authorization\s*:\s*(?:Basic|Bearer)\s+)([A-Za-z0-9._~+/=\-]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// 对输入文本做一次完整的脱敏。返回新字符串（输入为 null 时返回 string.Empty）。
    /// </summary>
    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        // 先脱敏结构化字段（值会被整体替换），再做 IPv4——已脱敏的 *** 不会被 IPv4 命中。
        string s = JsonSecretPattern.Replace(input, ReplaceJsonValue);
        s = ConnStrSecretPattern.Replace(s, ReplaceConnStrValue);
        s = AuthHeaderPattern.Replace(s, ReplaceAuthHeader);
        s = Ipv4Pattern.Replace(s, Mask);

        return s;
    }

    // JSON：保留键与分隔符，只替换捕获组 2（值）。
    private static string ReplaceJsonValue(Match m)
    {
        if (!m.Groups[2].Success) return m.Value;
        int valStart = m.Groups[2].Index - m.Index;
        int valLen = m.Groups[2].Length;
        return m.Value.Substring(0, valStart) + Mask + m.Value.Substring(valStart + valLen);
    }

    // 连接串：保留键名、替换值。重写为 "Pwd=***"（不保留原分号以免泄露串结构长度）。
    private static string ReplaceConnStrValue(Match m)
    {
        string key = m.Groups[1].Value;
        return $"{key}={Mask}";
    }

    // Authorization 头：保留 scheme，替换凭证。
    private static string ReplaceAuthHeader(Match m) =>
        m.Groups[1].Value + Mask;
}
