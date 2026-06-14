using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Siemens
{
    /// <summary>
    /// 西门子 S7-1200/1500 Web API 客户端 — 通过 PLC 内置 HTTP REST API 读写变量。
    /// 不依赖 S7 协议栈，使用 HttpClient 通信。
    /// </summary>
    /// <remarks>
    /// <para>支持的功能：</para>
    /// <list type="bullet">
    ///   <item>基础数据读写（Bool/Int16/UInt16/Int32/UInt32/Int64/UInt64/Float/Double/String/Bytes）</item>
    ///   <item>批量多地址读写</item>
    ///   <item>Basic 认证（用户名/密码）</item>
    ///   <item>PLC 信息查询（型号/订货号/CPU 状态）</item>
    ///   <item>复用 S7 地址解析（DB100.DBW0, M0, I0, Q0 等）</item>
    /// </list>
    /// </remarks>
    public class SiemensWebApiClient : IBatchReadWrite
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly bool _ownsHttpClient;
        private bool _disposed;
        private bool _connected;
        private readonly object _lock = new object();

        public ILogger Log { get; set; } = NullLogger.Instance;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        public bool IsConnected
        {
            get { lock (_lock) return _connected; }
        }

        /// <summary>
        /// 创建 Web API 客户端（自动生成 HttpClient，支持 Basic 认证）。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">HTTP 端口，默认 80。</param>
        /// <param name="username">Basic 认证用户名。</param>
        /// <param name="password">Basic 认证密码。</param>
        /// <param name="timeout">请求超时（毫秒），默认 5000。</param>
        /// <param name="useHttps">是否使用 HTTPS，默认 false。</param>
        public SiemensWebApiClient(string ip, int port = 80,
            string? username = null, string? password = null,
            int timeout = 5000, bool useHttps = false)
        {
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentNullException(nameof(ip));

            string scheme = useHttps ? "https" : "http";
            _baseUrl = $"{scheme}://{ip}:{port}";

            _httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeout) };

            if (!string.IsNullOrEmpty(username))
            {
                string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", credentials);
            }

            _ownsHttpClient = true;
        }

        /// <summary>
        /// 创建 Web API 客户端（外部提供 HttpClient，适用于 DI 或 HttpClientFactory 场景）。
        /// </summary>
        /// <param name="httpClient">外部 HttpClient 实例。</param>
        /// <param name="baseUrl">完整基础 URL（如 http://192.168.1.1）。</param>
        public SiemensWebApiClient(HttpClient httpClient, string baseUrl)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _ownsHttpClient = false;
        }

        // ── 连接管理 ──────────────────────────────

        public OperateResult Connect()
        {
            try
            {
                var result = GetPlcInfo();
                if (!result.IsSuccess)
                    return OperateResult.Failed($"Web API 连接失败: {result.Message}", result.ErrorCode);

                lock (_lock) { _connected = true; }
                Log.Info("Web API 连接成功");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"Web API 连接异常: {ex.Message}");
                return OperateResult.Failed($"Web API 连接异常: {ex.Message}");
            }
        }

        public Task<OperateResult> ConnectAsync()
        {
            return Task.Run(() => Connect());
        }

        public void Disconnect()
        {
            lock (_lock) { _connected = false; }
            Log.Info("Web API 已断开");
        }

        // ── HTTP 请求方法 ────────────────────────

        private string BuildReadUrl(string area, int db, int start, int size, string type)
        {
            return $"{_baseUrl}/api/jsonrpc?op=Read&area={area}&db={db}&start={start}&size={size}&type={type}";
        }

        private string BuildWriteUrl(string area, int db, int start, int size, string type)
        {
            return $"{_baseUrl}/api/jsonrpc?op=Write&area={area}&db={db}&start={start}&size={size}&type={type}";
        }

        private void RaiseMessageSent(string msg) => OnMessageSent?.Invoke(this, msg);
        private void RaiseMessageReceived(string msg) => OnMessageReceived?.Invoke(this, msg);
        private void RaiseError(string msg) => OnError?.Invoke(this, msg);

        private OperateResult<string> SendHttpGet(string url)
        {
            try
            {
                Log.Debug($"GET {url}");
                RaiseMessageSent($"GET {url}");

                var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                Log.Debug($"HTTP {(int)response.StatusCode} {body}");
                RaiseMessageReceived(body);

                if (!response.IsSuccessStatusCode)
                    return OperateResult<string>.Failed(
                        $"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);

                return OperateResult<string>.Success(body);
            }
            catch (Exception ex)
            {
                Log.Error($"HTTP GET 异常: {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<string>.Failed($"HTTP GET 异常: {ex.Message}");
            }
        }

        private async Task<OperateResult<string>> SendHttpGetAsync(string url, CancellationToken ct)
        {
            try
            {
                Log.Debug($"GET {url}");
                RaiseMessageSent($"GET {url}");

                var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                Log.Debug($"HTTP {(int)response.StatusCode} {body}");
                RaiseMessageReceived(body);

                if (!response.IsSuccessStatusCode)
                    return OperateResult<string>.Failed(
                        $"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);

                return OperateResult<string>.Success(body);
            }
            catch (Exception ex)
            {
                Log.Error($"HTTP GET 异常: {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<string>.Failed($"HTTP GET 异常: {ex.Message}");
            }
        }

        private OperateResult<string> SendHttpPost(string url, string jsonBody)
        {
            try
            {
                Log.Debug($"POST {url} body={jsonBody}");
                RaiseMessageSent($"POST {url} body={jsonBody}");

                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                var response = _httpClient.PostAsync(url, content).GetAwaiter().GetResult();
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                Log.Debug($"HTTP {(int)response.StatusCode} {body}");
                RaiseMessageReceived(body);

                if (!response.IsSuccessStatusCode)
                    return OperateResult<string>.Failed(
                        $"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);

                return OperateResult<string>.Success(body);
            }
            catch (Exception ex)
            {
                Log.Error($"HTTP POST 异常: {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<string>.Failed($"HTTP POST 异常: {ex.Message}");
            }
        }

        private async Task<OperateResult<string>> SendHttpPostAsync(string url, string jsonBody, CancellationToken ct)
        {
            try
            {
                Log.Debug($"POST {url} body={jsonBody}");
                RaiseMessageSent($"POST {url} body={jsonBody}");

                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                Log.Debug($"HTTP {(int)response.StatusCode} {body}");
                RaiseMessageReceived(body);

                if (!response.IsSuccessStatusCode)
                    return OperateResult<string>.Failed(
                        $"HTTP {(int)response.StatusCode}: {body}", (int)response.StatusCode);

                return OperateResult<string>.Success(body);
            }
            catch (Exception ex)
            {
                Log.Error($"HTTP POST 异常: {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<string>.Failed($"HTTP POST 异常: {ex.Message}");
            }
        }

        // ── 地址解析与区域映射 ──────────────────

        private static string MapArea(S7Area area)
        {
            switch (area)
            {
                case S7Area.PE: return "PE";
                case S7Area.PA: return "PA";
                case S7Area.MK: return "MK";
                case S7Area.DB: return "DB";
                case S7Area.TM: return "TM";
                case S7Area.CT: return "CT";
                default: return "MK";
            }
        }

        private static string MapDataType(int dataSize, bool isBool)
        {
            if (isBool) return "X";
            switch (dataSize)
            {
                case 1: return "B";
                case 2: return "W";
                case 4: return "D";
                case 8: return "L";
                default: return "B";
            }
        }

        private struct ResolvedAddress
        {
            public string Area;
            public int DBNumber;
            public int ByteAddress;
            public int BitOffset;
            public int DataSize;
            public bool IsBit;
        }

        private static ResolvedAddress ResolveAddress(string address)
        {
            var s7 = SiemensS7Address.Parse(address);
            bool isBit = s7.BitOffset != 0 || (s7.DataSize == 1 && address.ToUpper().Contains("."));
            return new ResolvedAddress
            {
                Area = MapArea(s7.Area),
                DBNumber = s7.DBNumber,
                ByteAddress = s7.ByteAddress,
                BitOffset = s7.BitOffset,
                DataSize = s7.DataSize,
                IsBit = isBit
            };
        }

        // ── 底层读写 ────────────────────────────

        private OperateResult<byte[]> ReadRaw(string address, ushort length)
        {
            var addr = ResolveAddress(address);
            int size = length > 0 ? length : (ushort)addr.DataSize;
            string type = MapDataType(addr.DataSize, false);
            string url = BuildReadUrl(addr.Area, addr.DBNumber, addr.ByteAddress, size, type);

            var resp = SendHttpGet(url);
            if (!resp.IsSuccess) return OperateResult<byte[]>.Failed(resp.Message, resp.ErrorCode);

            return ParseReadResponse(resp.Content, size);
        }

        private async Task<OperateResult<byte[]>> ReadRawAsync(string address, ushort length, CancellationToken ct)
        {
            var addr = ResolveAddress(address);
            int size = length > 0 ? length : (ushort)addr.DataSize;
            string type = MapDataType(addr.DataSize, false);
            string url = BuildReadUrl(addr.Area, addr.DBNumber, addr.ByteAddress, size, type);

            var resp = await SendHttpGetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccess) return OperateResult<byte[]>.Failed(resp.Message, resp.ErrorCode);

            return ParseReadResponse(resp.Content, size);
        }

        private static OperateResult<byte[]> ParseReadResponse(string json, int expectedSize)
        {
            try
            {
                // 解析 JSON: {"jsonrpc":"2.0","result":{"data":[...]},"id":1}
                // 或: {"result":{"value":...},"id":1}
                // 或简单格式: {"data":[0x00,0x01,...]}
                string dataStr = ExtractJsonField(json, "data");
                if (dataStr != null)
                {
                    byte[] bytes = ParseByteArray(dataStr);
                    return OperateResult<byte[]>.Success(bytes);
                }

                // 尝试提取 value 字段
                string valueStr = ExtractJsonField(json, "value");
                if (valueStr != null)
                {
                    byte[] bytes = ParseByteArray(valueStr);
                    return OperateResult<byte[]>.Success(bytes);
                }

                return OperateResult<byte[]>.Failed($"无法解析响应: {json}");
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"解析响应失败: {ex.Message}");
            }
        }

        private static OperateResult<string> WriteRaw(string address, byte[] data, SiemensWebApiClient client)
        {
            var addr = ResolveAddress(address);
            string type = MapDataType(data.Length, false);
            string url = client.BuildWriteUrl(addr.Area, addr.DBNumber, addr.ByteAddress, data.Length, type);

            string hexData = BitConverter.ToString(data).Replace("-", "");
            string jsonBody = $"{{\"data\":\"{hexData}\"}}";

            var resp = client.SendHttpPost(url, jsonBody);
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message, resp.ErrorCode);

            return OperateResult<string>.Success(resp.Content);
        }

        private async Task<OperateResult<string>> WriteRawAsync(string address, byte[] data, CancellationToken ct)
        {
            var addr = ResolveAddress(address);
            string type = MapDataType(data.Length, false);
            string url = BuildWriteUrl(addr.Area, addr.DBNumber, addr.ByteAddress, data.Length, type);

            string hexData = BitConverter.ToString(data).Replace("-", "");
            string jsonBody = $"{{\"data\":\"{hexData}\"}}";

            var resp = await SendHttpPostAsync(url, jsonBody, ct).ConfigureAwait(false);
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message, resp.ErrorCode);

            return OperateResult<string>.Success(resp.Content);
        }

        // ── JSON 辅助方法（netstandard2.0 无 System.Text.Json）────

        private static string? ExtractJsonField(string json, string fieldName)
        {
            string search = $"\"{fieldName}\"";
            int idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + search.Length);
            if (colonIdx < 0) return null;

            int valueStart = colonIdx + 1;
            while (valueStart < json.Length && json[valueStart] == ' ') valueStart++;

            if (valueStart >= json.Length) return null;

            if (json[valueStart] == '"')
            {
                int endQuote = json.IndexOf('"', valueStart + 1);
                if (endQuote < 0) return null;
                return json.Substring(valueStart + 1, endQuote - valueStart - 1);
            }

            if (json[valueStart] == '[')
            {
                int endBracket = json.IndexOf(']', valueStart);
                if (endBracket < 0) return null;
                return json.Substring(valueStart, endBracket - valueStart + 1);
            }

            // 数值或其他
            int end = valueStart;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ' ') end++;
            return json.Substring(valueStart, end - valueStart);
        }

        private static byte[] ParseByteArray(string dataStr)
        {
            dataStr = dataStr.Trim();

            // 数组格式: [0,1,2,3] 或 [0x00,0x01]
            if (dataStr.StartsWith("[") && dataStr.EndsWith("]"))
            {
                string inner = dataStr.Substring(1, dataStr.Length - 2);
                if (string.IsNullOrWhiteSpace(inner)) return new byte[0];
                var parts = inner.Split(',');
                byte[] result = new byte[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    string p = parts[i].Trim().Trim('"');
                    if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        result[i] = Convert.ToByte(p, 16);
                    else
                        result[i] = byte.Parse(p);
                }
                return result;
            }

            // 十六进制字符串: "00010203"
            if (dataStr.Length >= 2 && IsHexString(dataStr))
            {
                byte[] result = new byte[dataStr.Length / 2];
                for (int i = 0; i < result.Length; i++)
                    result[i] = Convert.ToByte(dataStr.Substring(i * 2, 2), 16);
                return result;
            }

            // 单个数值
            if (byte.TryParse(dataStr, out byte singleByte))
                return new byte[] { singleByte };

            return new byte[0];
        }

        private static bool IsHexString(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        // ── 字节序处理 ─────────────────────────────

        private byte[] ApplyByteOrderWrite(byte[] data, int typeSize)
        {
            if (ByteOrder == Endianness.BigEndian || typeSize <= 1) return data;
            byte[] swapped = (byte[])data.Clone();
            if (typeSize == 2)
            {
                if (ByteOrder == Endianness.LittleEndian) { byte t = swapped[0]; swapped[0] = swapped[1]; swapped[1] = t; }
            }
            else if (typeSize == 4)
            {
                switch (ByteOrder)
                {
                    case Endianness.LittleEndian:
                        byte t0 = swapped[0]; swapped[0] = swapped[3]; swapped[3] = t0;
                        byte t1 = swapped[1]; swapped[1] = swapped[2]; swapped[2] = t1;
                        break;
                    case Endianness.MidBigEndian:
                        byte b0 = swapped[0]; swapped[0] = swapped[1]; swapped[1] = b0;
                        byte b2 = swapped[2]; swapped[2] = swapped[3]; swapped[3] = b2;
                        break;
                    case Endianness.MidLittleEndian:
                        byte c0 = swapped[0]; byte c1 = swapped[1];
                        swapped[0] = swapped[2]; swapped[1] = swapped[3];
                        swapped[2] = c0; swapped[3] = c1;
                        break;
                }
            }
            else if (typeSize == 8)
            {
                switch (ByteOrder)
                {
                    case Endianness.LittleEndian:
                        Array.Reverse(swapped);
                        break;
                    case Endianness.MidBigEndian:
                        for (int i = 0; i < 8; i += 2) { byte t = swapped[i]; swapped[i] = swapped[i + 1]; swapped[i + 1] = t; }
                        break;
                    case Endianness.MidLittleEndian:
                        for (int i = 0; i < 4; i++) { byte t = swapped[i]; swapped[i] = swapped[i + 4]; swapped[i + 4] = t; }
                        break;
                }
            }
            return swapped;
        }

        private byte[] ApplyByteOrderRead(byte[] data, int typeSize)
        {
            if (ByteOrder == Endianness.BigEndian || typeSize <= 1) return data;
            return ApplyByteOrderWrite(data, typeSize);
        }

        // ── 同步读取 ──────────────────────────────

        public OperateResult<bool> ReadBool(string address)
        {
            var addr = ResolveAddress(address);
            string url = BuildReadUrl(addr.Area, addr.DBNumber, addr.ByteAddress, 1, "X");

            var resp = SendHttpGet(url);
            if (!resp.IsSuccess) return OperateResult<bool>.Failed(resp.Message, resp.ErrorCode);

            string val = ExtractJsonField(resp.Content, "value") ?? ExtractJsonField(resp.Content, "data") ?? "0";
            val = val.Trim().Trim('[', ']');
            if (int.TryParse(val, out int intVal))
                return OperateResult<bool>.Success(intVal != 0);

            byte[] bytes = ParseByteArray(val);
            return OperateResult<bool>.Success(bytes.Length > 0 && bytes[0] != 0);
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var r = ReadRaw(address, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据长度不足");
            return OperateResult<short>.Success(DataConverter.ToInt16(ApplyByteOrderRead(r.Content, 2), 0));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadRaw(address, 2);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("数据长度不足");
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(ApplyByteOrderRead(r.Content, 2), 0));
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var r = ReadRaw(address, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据长度不足");
            return OperateResult<int>.Success(DataConverter.ToInt32(ApplyByteOrderRead(r.Content, 4), 0));
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var r = ReadRaw(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("数据长度不足");
            return OperateResult<long>.Success(DataConverter.ToInt64(ApplyByteOrderRead(r.Content, 8), 0));
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<float> ReadFloat(string address)
        {
            var r = ReadRaw(address, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据长度不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(ApplyByteOrderRead(r.Content, 4), 0));
        }

        public OperateResult<double> ReadDouble(string address)
        {
            var r = ReadRaw(address, 8);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<double>.Failed("数据长度不足");
            return OperateResult<double>.Success(DataConverter.ToDouble(ApplyByteOrderRead(r.Content, 8), 0));
        }

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadRaw(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            return ReadRaw(address, length);
        }

        // ── 同步写入 ──────────────────────────────

        public OperateResult Write(string address, bool value)
        {
            var addr = ResolveAddress(address);
            string url = BuildWriteUrl(addr.Area, addr.DBNumber, addr.ByteAddress, 1, "X");
            string jsonBody = $"{{\"value\":{(value ? 1 : 0)}}}";

            var resp = SendHttpPost(url, jsonBody);
            if (!resp.IsSuccess) return OperateResult.Failed(resp.Message, resp.ErrorCode);
            return OperateResult.Success();
        }

        public OperateResult Write(string address, short value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 2);
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, ushort value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 2);
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, int value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 4);
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, uint value) => Write(address, (int)value);

        public OperateResult Write(string address, long value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 8);
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, ulong value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(unchecked((long)value)), 8);
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, float value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 4);
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, double value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 8);
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, string value)
        {
            byte[] data = DataConverter.GetBytes(value);
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, byte[] data)
        {
            var r = WriteRaw(address, data, this);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        // ── 异步读取 ──────────────────────────────

        public async Task<OperateResult<bool>> ReadBoolAsync(string address)
        {
            var addr = ResolveAddress(address);
            string url = BuildReadUrl(addr.Area, addr.DBNumber, addr.ByteAddress, 1, "X");

            var resp = await SendHttpGetAsync(url, CancellationToken.None).ConfigureAwait(false);
            if (!resp.IsSuccess) return OperateResult<bool>.Failed(resp.Message, resp.ErrorCode);

            string val = ExtractJsonField(resp.Content, "value") ?? ExtractJsonField(resp.Content, "data") ?? "0";
            val = val.Trim().Trim('[', ']');
            if (int.TryParse(val, out int intVal))
                return OperateResult<bool>.Success(intVal != 0);

            byte[] bytes = ParseByteArray(val);
            return OperateResult<bool>.Success(bytes.Length > 0 && bytes[0] != 0);
        }

        public async Task<OperateResult<short>> ReadInt16Async(string address)
        {
            var r = await ReadRawAsync(address, 2, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据长度不足");
            return OperateResult<short>.Success(DataConverter.ToInt16(ApplyByteOrderRead(r.Content, 2), 0));
        }

        public async Task<OperateResult<ushort>> ReadUInt16Async(string address)
        {
            var r = await ReadRawAsync(address, 2, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("数据长度不足");
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(ApplyByteOrderRead(r.Content, 2), 0));
        }

        public async Task<OperateResult<int>> ReadInt32Async(string address)
        {
            var r = await ReadRawAsync(address, 4, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据长度不足");
            return OperateResult<int>.Success(DataConverter.ToInt32(ApplyByteOrderRead(r.Content, 4), 0));
        }

        public async Task<OperateResult<uint>> ReadUInt32Async(string address)
        {
            var r = await ReadInt32Async(address).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult<long>> ReadInt64Async(string address)
        {
            var r = await ReadRawAsync(address, 8, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("数据长度不足");
            return OperateResult<long>.Success(DataConverter.ToInt64(ApplyByteOrderRead(r.Content, 8), 0));
        }

        public async Task<OperateResult<ulong>> ReadUInt64Async(string address)
        {
            var r = await ReadInt64Async(address).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult<float>> ReadFloatAsync(string address)
        {
            var r = await ReadRawAsync(address, 4, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据长度不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(ApplyByteOrderRead(r.Content, 4), 0));
        }

        public async Task<OperateResult<double>> ReadDoubleAsync(string address)
        {
            var r = await ReadRawAsync(address, 8, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<double>.Failed("数据长度不足");
            return OperateResult<double>.Success(DataConverter.ToDouble(ApplyByteOrderRead(r.Content, 8), 0));
        }

        public async Task<OperateResult<string>> ReadStringAsync(string address, ushort length)
        {
            var r = await ReadRawAsync(address, length, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length));
        }

        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            return ReadRawAsync(address, length, CancellationToken.None);
        }

        // ── 异步写入 ──────────────────────────────

        public async Task<OperateResult> WriteAsync(string address, bool value)
        {
            var addr = ResolveAddress(address);
            string url = BuildWriteUrl(addr.Area, addr.DBNumber, addr.ByteAddress, 1, "X");
            string jsonBody = $"{{\"value\":{(value ? 1 : 0)}}}";

            var resp = await SendHttpPostAsync(url, jsonBody, CancellationToken.None).ConfigureAwait(false);
            if (!resp.IsSuccess) return OperateResult.Failed(resp.Message, resp.ErrorCode);
            return OperateResult.Success();
        }

        public async Task<OperateResult> WriteAsync(string address, short value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 2);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, int value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 4);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, float value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 4);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, string value)
        {
            byte[] data = DataConverter.GetBytes(value);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, byte[] data)
        {
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, ushort value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 2);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, uint value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 4);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, long value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 8);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, ulong value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 8);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult> WriteAsync(string address, double value)
        {
            byte[] data = ApplyByteOrderWrite(DataConverter.GetBytes(value), 8);
            var r = await WriteRawAsync(address, data, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        // ── 批量读写 (IBatchReadWrite) ─────────────

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Success(new Dictionary<string, object?>());

            var dict = new Dictionary<string, object?>();
            foreach (var address in addrList)
            {
                var addr = ResolveAddress(address);
                var r = ReadRaw(address, (ushort)addr.DataSize);
                if (!r.IsSuccess)
                {
                    dict[address] = null;
                    continue;
                }

                byte[] data = r.Content;
                object? val = null;
                if (data.Length > 0)
                {
                    switch (addr.DataSize)
                    {
                        case 1: val = data[0]; break;
                        case 2: val = DataConverter.ToInt16(ApplyByteOrderRead(data, 2), 0); break;
                        case 4: val = DataConverter.ToInt32(ApplyByteOrderRead(data, 4), 0); break;
                        case 8: val = DataConverter.ToInt64(ApplyByteOrderRead(data, 8), 0); break;
                        default: val = data; break;
                    }
                }
                dict[address] = val;
            }

            return OperateResult<Dictionary<string, object?>>.Success(dict);
        }

        public async Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Success(new Dictionary<string, object?>());

            var dict = new Dictionary<string, object?>();
            foreach (var address in addrList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var addr = ResolveAddress(address);
                var r = await ReadRawAsync(address, (ushort)addr.DataSize, cancellationToken).ConfigureAwait(false);
                if (!r.IsSuccess)
                {
                    dict[address] = null;
                    continue;
                }

                byte[] data = r.Content;
                object? val = null;
                if (data.Length > 0)
                {
                    switch (addr.DataSize)
                    {
                        case 1: val = data[0]; break;
                        case 2: val = DataConverter.ToInt16(ApplyByteOrderRead(data, 2), 0); break;
                        case 4: val = DataConverter.ToInt32(ApplyByteOrderRead(data, 4), 0); break;
                        case 8: val = DataConverter.ToInt64(ApplyByteOrderRead(data, 8), 0); break;
                        default: val = data; break;
                    }
                }
                dict[address] = val;
            }

            return OperateResult<Dictionary<string, object?>>.Success(dict);
        }

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Success(new Dictionary<string, byte[]>());

            var dict = new Dictionary<string, byte[]>();
            foreach (var address in addrList)
            {
                var addr = ResolveAddress(address);
                var r = ReadRaw(address, (ushort)addr.DataSize);
                dict[address] = r.IsSuccess ? r.Content : new byte[0];
            }

            return OperateResult<Dictionary<string, byte[]>>.Success(dict);
        }

        public async Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Success(new Dictionary<string, byte[]>());

            var dict = new Dictionary<string, byte[]>();
            foreach (var address in addrList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var addr = ResolveAddress(address);
                var r = await ReadRawAsync(address, (ushort)addr.DataSize, cancellationToken).ConfigureAwait(false);
                dict[address] = r.IsSuccess ? r.Content : new byte[0];
            }

            return OperateResult<Dictionary<string, byte[]>>.Success(dict);
        }

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0) return OperateResult.Success();

            foreach (var kvp in itemList)
            {
                byte[] data = ConvertValueToBytes(kvp.Value);
                var r = WriteRaw(kvp.Key, data, this);
                if (!r.IsSuccess) return OperateResult.Failed($"写入 {kvp.Key} 失败: {r.Message}", r.ErrorCode);
            }

            return OperateResult.Success();
        }

        public async Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0) return OperateResult.Success();

            foreach (var kvp in itemList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] data = ConvertValueToBytes(kvp.Value);
                var r = await WriteRawAsync(kvp.Key, data, cancellationToken).ConfigureAwait(false);
                if (!r.IsSuccess) return OperateResult.Failed($"写入 {kvp.Key} 失败: {r.Message}", r.ErrorCode);
            }

            return OperateResult.Success();
        }

        private byte[] ConvertValueToBytes(object value)
        {
            switch (value)
            {
                case bool bv: return new byte[] { (byte)(bv ? 1 : 0) };
                case short sv: return ApplyByteOrderWrite(DataConverter.GetBytes(sv), 2);
                case ushort usv: return ApplyByteOrderWrite(DataConverter.GetBytes(usv), 2);
                case int iv: return ApplyByteOrderWrite(DataConverter.GetBytes(iv), 4);
                case uint uiv: return ApplyByteOrderWrite(DataConverter.GetBytes((int)uiv), 4);
                case long lv: return ApplyByteOrderWrite(DataConverter.GetBytes(lv), 8);
                case ulong ulv: return ApplyByteOrderWrite(DataConverter.GetBytes(unchecked((long)ulv)), 8);
                case float fv: return ApplyByteOrderWrite(DataConverter.GetBytes(fv), 4);
                case double dv: return ApplyByteOrderWrite(DataConverter.GetBytes(dv), 8);
                case string sv: return DataConverter.GetBytes(sv);
                case byte[] bv: return bv;
                default: return DataConverter.GetBytes(Convert.ToInt32(value));
            }
        }

        // ── PLC 信息查询 ──────────────────────────

        /// <summary>
        /// 获取 PLC 基本信息（型号/订货号/固件版本等）。
        /// </summary>
        public OperateResult<string> GetPlcInfo()
        {
            string url = $"{_baseUrl}/api/jsonrpc?op=GetPlcInfo";
            var resp = SendHttpGet(url);
            return resp.IsSuccess
                ? OperateResult<string>.Success(resp.Content)
                : OperateResult<string>.Failed(resp.Message, resp.ErrorCode);
        }

        /// <summary>异步获取 PLC 信息。</summary>
        public async Task<OperateResult<string>> GetPlcInfoAsync(CancellationToken ct = default)
        {
            string url = $"{_baseUrl}/api/jsonrpc?op=GetPlcInfo";
            var resp = await SendHttpGetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccess
                ? OperateResult<string>.Success(resp.Content)
                : OperateResult<string>.Failed(resp.Message, resp.ErrorCode);
        }

        /// <summary>
        /// 获取 CPU 运行状态（RUN/STOP 等）。
        /// </summary>
        public OperateResult<string> GetCpuState()
        {
            string url = $"{_baseUrl}/api/jsonrpc?op=GetCpuState";
            var resp = SendHttpGet(url);
            return resp.IsSuccess
                ? OperateResult<string>.Success(resp.Content)
                : OperateResult<string>.Failed(resp.Message, resp.ErrorCode);
        }

        /// <summary>异步获取 CPU 状态。</summary>
        public async Task<OperateResult<string>> GetCpuStateAsync(CancellationToken ct = default)
        {
            string url = $"{_baseUrl}/api/jsonrpc?op=GetCpuState";
            var resp = await SendHttpGetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccess
                ? OperateResult<string>.Success(resp.Content)
                : OperateResult<string>.Failed(resp.Message, resp.ErrorCode);
        }

        // ── JSON-RPC 格式支持 ─────────────────────

        /// <summary>
        /// 使用 JSON-RPC 2.0 格式读取变量（适用于固件较新的 PLC）。
        /// </summary>
        public OperateResult<string> JsonRpcRead(string address, ushort length)
        {
            var addr = ResolveAddress(address);
            int size = length > 0 ? length : (ushort)addr.DataSize;
            string type = MapDataType(addr.DataSize, false);

            string jsonBody = $"{{\"jsonrpc\":\"2.0\",\"method\":\"Read\",\"params\":{{\"area\":\"{addr.Area}\",\"dbNumber\":{addr.DBNumber},\"start\":{addr.ByteAddress},\"size\":{size},\"type\":\"{type}\"}},\"id\":1}}";

            string url = $"{_baseUrl}/api/jsonrpc";
            return SendHttpPost(url, jsonBody);
        }

        /// <summary>
        /// 使用 JSON-RPC 2.0 格式写入变量。
        /// </summary>
        public OperateResult<string> JsonRpcWrite(string address, byte[] data)
        {
            var addr = ResolveAddress(address);
            string type = MapDataType(data.Length, false);
            string hexData = BitConverter.ToString(data).Replace("-", "");

            string jsonBody = $"{{\"jsonrpc\":\"2.0\",\"method\":\"Write\",\"params\":{{\"area\":\"{addr.Area}\",\"dbNumber\":{addr.DBNumber},\"start\":{addr.ByteAddress},\"size\":{data.Length},\"type\":\"{type}\",\"data\":\"{hexData}\"}},\"id\":1}}";

            string url = $"{_baseUrl}/api/jsonrpc";
            return SendHttpPost(url, jsonBody);
        }

        // ── Dispose ──────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();

            if (_ownsHttpClient)
                _httpClient.Dispose();
        }
    }
}
