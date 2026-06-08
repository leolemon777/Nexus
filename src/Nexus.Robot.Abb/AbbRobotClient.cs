using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Nexus.Robot.Abb
{
    /// <summary>
    /// ABB 机器人 WebAPI 通讯客户端。
    /// <para>基于 ABB IRC5 / OmniCore 控制器的 REST API。</para>
    /// <para>支持读写 IO 信号、读取机器人状态、读取关节位置、控制执行。</para>
    /// </summary>
    public class AbbRobotClient
    {
        // ── 属性 ─────────────────────────────────
        protected string Ip { get; }
        protected int Port { get; }
        protected int Timeout { get; set; }
        protected ILogger Log { get; set; }

        /// <summary>用户名（默认空 = 无需认证）。</summary>
        public string? Username { get; set; }

        /// <summary>密码。</summary>
        public string? Password { get; set; }

        private readonly object _lock = new object();

        // ── 事件 ──────────────────────────────────

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        // ── 构造 ────────────────────────────────

        public AbbRobotClient(string ip, int port = 80, int timeout = 5000)
        {
            Ip = ip ?? throw new ArgumentNullException(nameof(ip));
            Port = port;
            Timeout = timeout;
            Log = Nexus.NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? Nexus.NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  HTTP 请求
        // ═══════════════════════════════════════════

        private OperateResult<string> HttpGet(string path)
        {
            try
            {
                string url = $"http://{Ip}:{Port}{path}";
                Log.Info($"GET {url}");
                OnMessageSent?.Invoke(this, $"GET {path}");

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = Timeout;
                request.ContentType = "application/json";

                if (!string.IsNullOrEmpty(Username))
                {
                    string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
                    request.Headers["Authorization"] = $"Basic {auth}";
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();
                    OnMessageReceived?.Invoke(this, body);
                    return OperateResult<string>.Success(body);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"GET {path} 失败: {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<string>.Failed($"HTTP GET 失败: {ex.Message}");
            }
        }

        private OperateResult<string> HttpPost(string path, string jsonBody)
        {
            try
            {
                string url = $"http://{Ip}:{Port}{path}";
                Log.Info($"POST {url}");
                OnMessageSent?.Invoke(this, $"POST {path} [{jsonBody.Length}B]");

                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.Timeout = Timeout;
                request.ContentType = "application/json";

                if (!string.IsNullOrEmpty(Username))
                {
                    string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
                    request.Headers["Authorization"] = $"Basic {auth}";
                }

                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
                request.ContentLength = bodyBytes.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(bodyBytes, 0, bodyBytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();
                    OnMessageReceived?.Invoke(this, body);
                    return OperateResult<string>.Success(body);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"POST {path} 失败: {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<string>.Failed($"HTTP POST 失败: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  IO 信号读写
        // ═══════════════════════════════════════════

        /// <summary>读取数字输入信号。</summary>
        /// <param name="name">信号名称，如 "di01"。</param>
        public OperateResult<int> ReadDigitalInput(string name)
        {
            var r = HttpGet($"/rw/iosystem/signals/DI/{name}?json=1");
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return ParseSignalValue(r.Content);
        }

        /// <summary>读取数字输出信号。</summary>
        public OperateResult<int> ReadDigitalOutput(string name)
        {
            var r = HttpGet($"/rw/iosystem/signals/DO/{name}?json=1");
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return ParseSignalValue(r.Content);
        }

        /// <summary>写入数字输出信号。</summary>
        public OperateResult WriteDigitalOutput(string name, int value)
        {
            string json = $"{{\"lvalue\":\"{value}\"}}";
            var r = HttpPost($"/rw/iosystem/signals/DO/{name}?action=set", json);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message);
            return OperateResult.Success();
        }

        /// <summary>写入数字输出信号（布尔）。</summary>
        public OperateResult WriteDigitalOutput(string name, bool value)
            => WriteDigitalOutput(name, value ? 1 : 0);

        /// <summary>读取模拟输入信号。</summary>
        public OperateResult<double> ReadAnalogInput(string name)
        {
            var r = HttpGet($"/rw/iosystem/signals/AI/{name}?json=1");
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return ParseSignalDoubleValue(r.Content);
        }

        /// <summary>读取模拟输出信号。</summary>
        public OperateResult<double> ReadAnalogOutput(string name)
        {
            var r = HttpGet($"/rw/iosystem/signals/AO/{name}?json=1");
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return ParseSignalDoubleValue(r.Content);
        }

        // ═══════════════════════════════════════════
        //  机器人状态
        // ═══════════════════════════════════════════

        /// <summary>读取机器人控制器状态。</summary>
        public OperateResult<AbbControllerState> ReadControllerState()
        {
            var r = HttpGet("/rw/panel/ctrl-state?json=1");
            if (!r.IsSuccess) return OperateResult<AbbControllerState>.Failed(r.Message);

            // 简化解析: 查找 "ctrl-state" 字段
            string stateStr = ExtractJsonValue(r.Content, "ctrl-state");
            return OperateResult<AbbControllerState>.Success(new AbbControllerState
            {
                State = stateStr,
                RawJson = r.Content
            });
        }

        /// <summary>读取机器人运行模式。</summary>
        public OperateResult<AbbOperationMode> ReadOperationMode()
        {
            var r = HttpGet("/rw/panel/opmode?json=1");
            if (!r.IsSuccess) return OperateResult<AbbOperationMode>.Failed(r.Message);

            string modeStr = ExtractJsonValue(r.Content, "opmode");
            return OperateResult<AbbOperationMode>.Success(new AbbOperationMode
            {
                Mode = modeStr,
                RawJson = r.Content
            });
        }

        /// <summary>读取当前机械臂关节角度（度）。</summary>
        public OperateResult<double[]> ReadJointTargets()
        {
            var r = HttpGet("/rw/motionsystem/mechunits/ROB_1/jointtargets?json=1");
            if (!r.IsSuccess) return OperateResult<double[]>.Failed(r.Message);
            return ParseDoubleArray(r.Content, "rax");
        }

        /// <summary>读取当前 TCP 笛卡尔坐标 (X,Y,Z,Quaternion)。</summary>
        public OperateResult<double[]> ReadTcpPosition()
        {
            var r = HttpGet("/rw/motionsystem/mechunits/ROB_1/robtargets?json=1");
            if (!r.IsSuccess) return OperateResult<double[]>.Failed(r.Message);
            return ParseDoubleArray(r.Content, "x");
        }

        /// <summary>读取当前速度。</summary>
        public OperateResult<double> ReadSpeedRatio()
        {
            var r = HttpGet("/rw/panel/speedratio?json=1");
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);

            string speedStr = ExtractJsonValue(r.Content, "speedratio");
            if (double.TryParse(speedStr, out double speed))
                return OperateResult<double>.Success(speed);

            return OperateResult<double>.Failed($"速度解析失败: {speedStr}");
        }

        // ═══════════════════════════════════════════
        //  控制命令
        // ═══════════════════════════════════════════

        /// <summary>请求电机上电（Motors On）。</summary>
        public OperateResult MotorsOn()
        {
            var r = HttpPost("/rw/panel/motors?action=on", "");
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
        }

        /// <summary>请求电机下电（Motors Off）。</summary>
        public OperateResult MotorsOff()
        {
            var r = HttpPost("/rw/panel/motors?action=off", "");
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
        }

        /// <summary>启动执行（Start）。</summary>
        public OperateResult StartExecution()
        {
            var r = HttpPost("/rw/panel/exec?action=start", "");
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
        }

        /// <summary>停止执行（Stop）。</summary>
        public OperateResult StopExecution()
        {
            var r = HttpPost("/rw/panel/exec?action=stop", "");
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
        }

        /// <summary>复位执行（Reset）。</summary>
        public OperateResult ResetExecution()
        {
            var r = HttpPost("/rw/panel/exec?action=reset", "");
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
        }

        /// <summary>设置速度比例（0-100）。</summary>
        public OperateResult SetSpeedRatio(int speed)
        {
            if (speed < 0 || speed > 100) return OperateResult.Failed("速度比例范围 0-100");
            var r = HttpPost($"/rw/panel/speedratio?action=set&speedratio={speed}", "");
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
        }

        // ═══════════════════════════════════════════
        //  RAPID 程序
        // ═══════════════════════════════════════════

        /// <summary>读取 RAPID 程序执行状态。</summary>
        public OperateResult<AbbExecutionState> ReadExecutionState()
        {
            var r = HttpGet("/rw/rapid/execution?json=1");
            if (!r.IsSuccess) return OperateResult<AbbExecutionState>.Failed(r.Message);

            string stateStr = ExtractJsonValue(r.Content, "ctrlexecstate");
            return OperateResult<AbbExecutionState>.Success(new AbbExecutionState
            {
                State = stateStr,
                RawJson = r.Content
            });
        }

        /// <summary>加载 RAPID 模块到控制器。</summary>
        public OperateResult LoadModule(string taskName, string moduleName, string program)
        {
            string json = $"{{\"module\":\"{moduleName}\", \"program\":\"{EscapeJson(program)}\"}}";
            var r = HttpPost($"/rw/rapid/tasks/{taskName}/modules?action=load", json);
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message);
        }

        // ═══════════════════════════════════════════
        //  文件操作
        // ═══════════════════════════════════════════

        /// <summary>列出控制器上的文件目录。</summary>
        public OperateResult<string[]> ListFiles(string path = "/")
        {
            var r = HttpGet($"/fileservice/{path.TrimStart('/')}?json=1");
            if (!r.IsSuccess) return OperateResult<string[]>.Failed(r.Message);
            return OperateResult<string[]>.Success(ParseFileList(r.Content));
        }

        /// <summary>下载控制器文件到本地。</summary>
        public OperateResult DownloadFile(string remotePath, string localPath)
        {
            try
            {
                string url = $"http://{Ip}:{Port}/fileservice/{remotePath.TrimStart('/')}";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = Timeout;

                if (!string.IsNullOrEmpty(Username))
                {
                    string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
                    request.Headers["Authorization"] = $"Basic {auth}";
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var fs = System.IO.File.Create(localPath))
                {
                    stream.CopyTo(fs);
                }

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"文件下载失败: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  JSON 解析（轻量级，不依赖外部库）
        // ═══════════════════════════════════════════

        private static OperateResult<int> ParseSignalValue(string json)
        {
            string value = ExtractJsonValue(json, "lvalue");
            if (int.TryParse(value, out int v))
                return OperateResult<int>.Success(v);
            return OperateResult<int>.Failed($"信号值解析失败: {value}");
        }

        private static OperateResult<double> ParseSignalDoubleValue(string json)
        {
            string value = ExtractJsonValue(json, "lvalue");
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
                return OperateResult<double>.Success(v);
            return OperateResult<double>.Failed($"信号值解析失败: {value}");
        }

        /// <summary>从 JSON 字符串中提取指定键的值（简单实现）。</summary>
        public static string ExtractJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";

            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return "";

            // 找冒号后的值
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return "";

            int start = colon + 1;
            // 跳过空白
            while (start < json.Length && json[start] == ' ') start++;
            if (start >= json.Length) return "";

            if (json[start] == '"')
            {
                // 字符串值
                int end = json.IndexOf('"', start + 1);
                return end > start ? json.Substring(start + 1, end - start - 1) : "";
            }
            else
            {
                // 数字或布尔值
                int end = start;
                while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']')
                    end++;
                return json.Substring(start, end - start).Trim();
            }
        }

        private static OperateResult<double[]> ParseDoubleArray(string json, string keyPrefix)
        {
            try
            {
                var values = new List<double>();
                // 查找 "rax_1": "123.456" 或 "x": "100.0" 模式
                for (int i = 1; i <= 6; i++)
                {
                    string key = $"{keyPrefix}_{i}";
                    string val = ExtractJsonValue(json, key);
                    if (string.IsNullOrEmpty(val)) continue;
                    if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d))
                        values.Add(d);
                }

                if (values.Count == 0)
                {
                    // 尝试直接 "x", "y", "z" 模式
                    foreach (string k in new[] { "x", "y", "z", "q1", "q2", "q3", "q4" })
                    {
                        string val = ExtractJsonValue(json, k);
                        if (string.IsNullOrEmpty(val)) continue;
                        if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double d))
                            values.Add(d);
                    }
                }

                return values.Count > 0
                    ? OperateResult<double[]>.Success(values.ToArray())
                    : OperateResult<double[]>.Failed("未找到坐标数据");
            }
            catch (Exception ex)
            {
                return OperateResult<double[]>.Failed($"坐标解析异常: {ex.Message}");
            }
        }

        private static string[] ParseFileList(string json)
        {
            var files = new List<string>();
            int idx = 0;
            while ((idx = json.IndexOf("\"name\"", idx, StringComparison.Ordinal)) >= 0)
            {
                int colon = json.IndexOf(':', idx + 6);
                if (colon < 0) break;
                int q1 = json.IndexOf('"', colon + 1);
                if (q1 < 0) break;
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                files.Add(json.Substring(q1 + 1, q2 - q1 - 1));
                idx = q2 + 1;
            }
            return files.ToArray();
        }

        private static string EscapeJson(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        public override string ToString() => $"AbbRobotClient[{Ip}:{Port}]";
    }

    // ── 辅助类型 ──────────────────────────────

    /// <summary>ABB 控制器状态。</summary>
    public class AbbControllerState
    {
        public string State { get; set; } = "";
        public string RawJson { get; set; } = "";
        public override string ToString() => $"ControllerState: {State}";
    }

    /// <summary>ABB 操作模式。</summary>
    public class AbbOperationMode
    {
        public string Mode { get; set; } = "";
        public string RawJson { get; set; } = "";
        public override string ToString() => $"OperationMode: {Mode}";
    }

    /// <summary>ABB 执行状态。</summary>
    public class AbbExecutionState
    {
        public string State { get; set; } = "";
        public string RawJson { get; set; } = "";
        public override string ToString() => $"ExecutionState: {State}";
    }
}
