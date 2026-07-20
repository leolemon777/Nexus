// S7-1200/1500 Web API client over HTTP.
// Implements read/write via the built-in Web API of S7-1200/1500 PLCs.
// Uses System.Net.WebRequest (built-in to netstandard2.0) + minimal JSON value
// extractor (no external JSON dependency needed).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Nexus.Siemens.WebApi
{
    /// <summary>
    /// 西门子 S7-1200/1500 Web API 客户端 — 通过 PLC 内置的 HTTP/JSON 接口读写变量。
    /// </summary>
    /// <remarks>
    /// <b>实现说明</b>(Phase C-5):S7-1200/1500 PLC 固件内置 Web API(默认端口 80),
    /// 通过 HTTP GET/POST 配合 JSON 响应,可读写 PLC 变量。本客户端实现:
    /// <list type="bullet">
    ///   <item>BASIC 认证(用户名/密码 — S7 Web API 强制要求)。</item>
    ///   <item>读单变量:<c>GET /api/json/reads?var="..."</c>,响应 JSON 提取数字值。</item>
    ///   <item>写单变量:<c>POST /api/json/writes</c>,body JSON。</item>
    ///   <item>极简 JSON 数字提取(支持整数、浮点、布尔;不依赖外部 JSON 库)。</item>
    /// </list>
    /// <para>
    /// <b>地址格式</b>(S7 Web API 原生格式):
    /// <list type="bullet">
    ///   <item><c>"DB1.DBX0.0"</c> — DB1 的第 0 字节第 0 位(布尔)</item>
    ///   <item><c>"DB1.DBW2"</c> — DB1 的第 2 字节开始的字(Int16,大端)</item>
    ///   <item><c>"DB1.DBD4"</c> — DB1 的第 4 字节开始的双字(Int32/Float)</item>
    ///   <item><c>"M0.0"</c>、<c>"MW2"</c>、<c>"MD4"</c> — M 区对应类型</item>
    /// </list>
    /// </para>
    /// <para><b>变更说明</b>:本类从纯 OperateResult.Failed 占位升级为基于
    /// WebRequest + 极简 JSON 提取的真实 HTTP 客户端。</para>
    /// <para><b>注意</b>:本类不再继承 TcpDeviceBase(那是裸 TCP,不适合 HTTP)。
    /// 直接实现 <see cref="IReadWriteDevice"/>。HTTP 连接由 <see cref="HttpWebRequest"/> 管理。</para>
    /// </remarks>
    public class SiemensWebApiClient : IReadWriteDevice, IBatchReadWrite
    {
        /// <summary>PLC IP 或主机名。</summary>
        public string IpAddress { get; }

        /// <summary>HTTP 端口(默认 80)。</summary>
        public int Port { get; }

        /// <summary>认证用户名(S7 Web API 要求 BASIC 认证)。</summary>
        public string UserName { get; }

        /// <summary>认证密码。</summary>
        public string Password { get; }

        /// <summary>请求超时(毫秒)。</summary>
        public int Timeout { get; set; } = 5000;

        private readonly string _baseUrl;
        private string? _sessionCookie;

        /// <summary>构造。</summary>
        public SiemensWebApiClient(string ip, int port = 80, string userName = "admin", string password = "", int timeout = 5000)
        {
            IpAddress = ip ?? throw new ArgumentNullException(nameof(ip));
            Port = port;
            UserName = userName ?? throw new ArgumentNullException(nameof(userName));
            Password = password ?? string.Empty;
            Timeout = timeout;
            _baseUrl = $"http://{ip}:{port}";
        }

        // ── IReadWriteDevice 基础 ───────────────

        /// <inheritdoc />
        public bool IsConnected => true;  // HTTP 是无连接的,只要能发请求就视为"已连接"。

        /// <summary>
        /// 登录并保存 session cookie。S7 Web API 大多数操作需要先登录。
        /// </summary>
        public OperateResult Connect()
        {
            try
            {
                string url = $"{_baseUrl}/api/login?username={WebUtility.UrlEncode(UserName)}&password={WebUtility.UrlEncode(Password)}";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = Timeout;
                req.Credentials = new NetworkCredential(UserName, Password);
                req.Headers[HttpRequestHeader.Authorization] = "Basic " +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(UserName + ":" + Password));

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    // 提取 Set-Cookie(session)。
                    _sessionCookie = resp.Headers["Set-Cookie"];
                    return OperateResult.Success();
                }
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Web API 登录失败: {ex.Message}");
            }
        }

        /// <summary>登出。</summary>
        public OperateResult Disconnect()
        {
            try
            {
                if (_sessionCookie == null) return OperateResult.Success();
                var req = CreateRequest("/api/logout", "GET");
                using (req.GetResponse()) { }
                _sessionCookie = null;
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Web API 登出失败: {ex.Message}");
            }
        }

        Task<OperateResult> IReadWriteDevice.ConnectAsync() => Task.FromResult(Connect());
        void IReadWriteDevice.Disconnect() => Disconnect();

        // ── 核心读写 ───────────────────────────

        /// <summary>读取单个变量,返回原始 JSON 字符串(供高级用户解析)。</summary>
        public OperateResult<string> ReadRaw(string variableName)
        {
            try
            {
                string encoded = WebUtility.UrlEncode("\"" + variableName + "\"");
                var req = CreateRequest($"/api/json/reads?var={encoded}", "GET");
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    // 检查错误响应。
                    if (json.Contains("\"error-code\""))
                    {
                        string err = ExtractJsonValue(json, "error-msg") ?? "未知错误";
                        return OperateResult<string>.Failed($"Web API 返回错误: {err}");
                    }
                    return OperateResult<string>.Success(json);
                }
            }
            catch (Exception ex)
            {
                return OperateResult<string>.Failed($"Web API 读取失败: {ex.Message}");
            }
        }

        /// <summary>读取布尔变量。</summary>
        public OperateResult<bool> ReadBool(string address)
        {
            var r = ReadRaw(address);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            // 响应 JSON 格式: {"DB1.DBX0.0": true} 或 {"DB1.DBX0.0": false}
            string? val = ExtractJsonValue(r.Content, address);
            if (val == null) return OperateResult<bool>.Failed($"JSON 解析失败: 找不到键 '{address}'");
            if (bool.TryParse(val, out bool b)) return OperateResult<bool>.Success(b);
            // 尝试数字 0/1。
            if (int.TryParse(val, out int n)) return OperateResult<bool>.Success(n != 0);
            return OperateResult<bool>.Failed($"无法将 '{val}' 解析为 bool");
        }

        /// <summary>读取 16 位整数(S7 Web API 直接返回数值)。</summary>
        public OperateResult<short> ReadInt16(string address)
        {
            var r = ReadNumber(address);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success((short)r.Content);
        }

        /// <inheritdoc />
        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadNumber(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        /// <inheritdoc />
        public OperateResult<int> ReadInt32(string address)
        {
            var r = ReadNumber(address);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success((int)r.Content);
        }

        /// <inheritdoc />
        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadNumber(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        /// <inheritdoc />
        public OperateResult<long> ReadInt64(string address)
        {
            var r = ReadNumber(address);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success((long)r.Content);
        }

        /// <inheritdoc />
        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadNumber(address);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success((ulong)r.Content);
        }

        /// <inheritdoc />
        public OperateResult<float> ReadFloat(string address)
        {
            var r = ReadNumber(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success((float)r.Content);
        }

        /// <inheritdoc />
        public OperateResult<double> ReadDouble(string address)
        {
            var r = ReadNumber(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success(r.Content);
        }

        /// <inheritdoc />
        public OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadRaw(address);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            string? val = ExtractJsonValue(r.Content, address);
            if (val == null) return OperateResult<string>.Failed($"JSON 解析失败: 找不到键 '{address}'");
            // JSON 字符串值可能带引号。
            return OperateResult<string>.Success(val.Trim('"'));
        }

        /// <inheritdoc />
        public OperateResult<byte[]> ReadBytes(string address, ushort length)
            => OperateResult<byte[]>.Failed("S7 Web API 不支持直接读字节,请用 ReadInt16/ReadBool 等");

        /// <summary>通用数值读取 — 通过 JSON 提取数字。</summary>
        private OperateResult<double> ReadNumber(string address)
        {
            var r = ReadRaw(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            string? val = ExtractJsonValue(r.Content, address);
            if (val == null) return OperateResult<double>.Failed($"JSON 解析失败: 找不到键 '{address}'");
            if (double.TryParse(val, out double d)) return OperateResult<double>.Success(d);
            return OperateResult<double>.Failed($"无法将 '{val}' 解析为数字");
        }

        // ── 写入 ───────────────────────────────

        public OperateResult Write(string address, bool value) => WriteValue(address, value ? "true" : "false");
        public OperateResult Write(string address, short value) => WriteValue(address, value.ToString());
        public OperateResult Write(string address, ushort value) => WriteValue(address, value.ToString());
        public OperateResult Write(string address, int value) => WriteValue(address, value.ToString());
        public OperateResult Write(string address, uint value) => WriteValue(address, value.ToString());
        public OperateResult Write(string address, long value) => WriteValue(address, value.ToString());
        public OperateResult Write(string address, ulong value) => WriteValue(address, value.ToString());
        public OperateResult Write(string address, float value) => WriteValue(address, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public OperateResult Write(string address, double value) => WriteValue(address, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        public OperateResult Write(string address, string value) => WriteValue(address, "\"" + value.Replace("\"", "\\\"") + "\"");
        public OperateResult Write(string address, byte[] data) => OperateResult.Failed("S7 Web API 不支持写字节数组");

        /// <summary>POST /api/json/writes with JSON body {"var": value}。</summary>
        private OperateResult WriteValue(string address, string jsonValue)
        {
            try
            {
                var req = CreateRequest("/api/json/writes", "POST");
                req.ContentType = "application/json";
                string body = "{\"" + address + "\": " + jsonValue + "}";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bodyBytes.Length;
                using (var s = req.GetRequestStream())
                {
                    s.Write(bodyBytes, 0, bodyBytes.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    if (json.Contains("\"error-code\""))
                    {
                        string err = ExtractJsonValue(json, "error-msg") ?? "未知错误";
                        return OperateResult.Failed($"Web API 写入错误: {err}");
                    }
                    return OperateResult.Success();
                }
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Web API 写入失败: {ex.Message}");
            }
        }

        // ── IBatchReadWrite ────────────────────

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var addr in addresses)
            {
                var r = ReadRaw(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed($"读取 {addr} 失败: {r.Message}");
                string? val = ExtractJsonValue(r.Content, addr);
                dict[addr] = val;
            }
            return OperateResult<Dictionary<string, object?>>.Success(dict);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
            => OperateResult<Dictionary<string, byte[]>>.Failed("S7 Web API 不支持 RandomRead(字节数组)");

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default)
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kvp in items)
            {
                var r = Write(kvp.Key, kvp.Value?.ToString() ?? "");
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default)
            => Task.FromResult(BatchWrite(items));

        // ── 异步 ────────────────────────────────

        public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.FromResult(ReadBool(address));
        public Task<OperateResult<short>> ReadInt16Async(string address) => Task.FromResult(ReadInt16(address));
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.FromResult(ReadUInt16(address));
        public Task<OperateResult<int>> ReadInt32Async(string address) => Task.FromResult(ReadInt32(address));
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.FromResult(ReadUInt32(address));
        public Task<OperateResult<long>> ReadInt64Async(string address) => Task.FromResult(ReadInt64(address));
        public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.FromResult(ReadUInt64(address));
        public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.FromResult(ReadFloat(address));
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.FromResult(ReadDouble(address));
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.FromResult(ReadString(address, length));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.FromResult(ReadBytes(address, length));
        public Task<OperateResult> WriteAsync(string address, bool value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, short value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, int value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, float value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, string value) => Task.FromResult(Write(address, value));
        public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.FromResult(Write(address, data));

        // ── 内部:创建 HTTP 请求 + 认证 + session ────────────

        private HttpWebRequest CreateRequest(string path, string method)
        {
            var req = (HttpWebRequest)WebRequest.Create(_baseUrl + path);
            req.Method = method;
            req.Timeout = Timeout;
            req.Credentials = new NetworkCredential(UserName, Password);
            req.Headers[HttpRequestHeader.Authorization] = "Basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(UserName + ":" + Password));
            if (!string.IsNullOrEmpty(_sessionCookie))
                req.Headers[HttpRequestHeader.Cookie] = _sessionCookie;
            return req;
        }

        /// <summary>
        /// 极简 JSON 值提取 — 找到 <c>"key": value</c> 模式并返回 value 字符串(无引号)。
        /// 只支持 S7 Web API 的扁平 JSON 格式。不支持嵌套对象/数组/转义字符。
        /// </summary>
        /// <example>
        /// ExtractJsonValue("{\"DB1.DBW2\": 1234}", "DB1.DBW2") → "1234"
        /// ExtractJsonValue("{\"result\": \"hello\"}", "result") → "hello"
        /// </example>
        public static string? ExtractJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            // 在 JSON 字符串里找 "key"(带引号)。key 中的特殊字符(DB1.DBW2 中的点)直接匹配。
            string pattern = "\"" + key + "\"";
            int keyIdx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int colonIdx = json.IndexOf(':', keyIdx + pattern.Length);
            if (colonIdx < 0) return null;

            int i = colonIdx + 1;
            // 跳过空格。
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length) return null;

            // 字符串值?
            if (json[i] == '"')
            {
                int end = json.IndexOf('"', i + 1);
                if (end < 0) return null;
                return json.Substring(i + 1, end - i - 1);
            }
            // 数值 / true / false / null。
            int start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']' && json[i] != ' ' && json[i] != '\n' && json[i] != '\r')
                i++;
            return json.Substring(start, i - start);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try { Disconnect(); } catch { }
        }
    }
}
