using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Nexus.Ftp
{
    /// <summary>
    /// 简易 FTP 客户端 — 针对工业场景（PLC 程序上传/下载、日志采集）。
    /// <para>支持连接/登录、目录列表、上传/下载文件、删除文件。</para>
    /// <para>不依赖外部库，纯 Socket 实现，netstandard2.0 兼容。</para>
    /// </summary>
    public class FtpClient
    {
        // ── 属性 ─────────────────────────────────
        protected string Ip { get; }
        protected int Port { get; }
        protected int Timeout { get; set; }
        protected ILogger Log { get; set; }

        /// <summary>FTP 用户名。</summary>
        public string Username { get; set; } = "anonymous";

        /// <summary>FTP 密码。</summary>
        public string Password { get; set; } = "";

        /// <summary>使用被动模式（默认 true）。</summary>
        public bool PassiveMode { get; set; } = true;

        private TcpClient? _controlClient;
        private NetworkStream? _controlStream;
        private StreamReader? _reader;
        private readonly object _lock = new object();

        // ── 事件 ──────────────────────────────────

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        // ── 构造 ────────────────────────────────

        public FtpClient(string ip, int port = 21, int timeout = 10000)
        {
            Ip = ip ?? throw new ArgumentNullException(nameof(ip));
            Port = port;
            Timeout = timeout;
            Log = Nexus.NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? Nexus.NullLogger.Instance;

        /// <summary>是否已连接。</summary>
        public bool IsConnected
        {
            get { lock (_lock) return _controlClient?.Connected == true; }
        }

        // ═══════════════════════════════════════════
        //  连接与登录
        // ═══════════════════════════════════════════

        /// <summary>连接并登录 FTP 服务器。</summary>
        public OperateResult Connect()
        {
            lock (_lock)
            {
                try
                {
                    DisconnectCore();

                    _controlClient = new TcpClient { SendTimeout = Timeout, ReceiveTimeout = Timeout };
                    var result = _controlClient.BeginConnect(Ip, Port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(Timeout, true))
                    {
                        DisconnectCore();
                        return OperateResult.Failed($"FTP 连接超时: {Ip}:{Port} ({Timeout}ms)");
                    }
                    _controlClient.EndConnect(result);

                    _controlStream = _controlClient.GetStream();
                    _controlStream.ReadTimeout = Timeout;
                    _controlStream.WriteTimeout = Timeout;
                    _reader = new StreamReader(_controlStream, Encoding.ASCII, false, 8192, leaveOpen: true);

                    // 读取欢迎消息
                    string welcome = ReadResponse();
                    if (!welcome.StartsWith("220"))
                        return OperateResult.Failed($"FTP 服务器拒绝连接: {welcome}");

                    // 登录
                    var loginResult = SendCommand($"USER {Username}");
                    if (loginResult.StartsWith("331"))
                    {
                        loginResult = SendCommand($"PASS {Password}");
                    }

                    if (!loginResult.StartsWith("230"))
                        return OperateResult.Failed($"FTP 登录失败: {loginResult}");

                    // 设置二进制模式
                    string typeResult = SendCommand("TYPE I");
                    if (!typeResult.StartsWith("200"))
                        Log.Warn($"FTP TYPE I 失败: {typeResult}");

                    Log.Info($"FTP 已连接 {Ip}:{Port}");
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    DisconnectCore();
                    Log.Error($"FTP 连接失败: {ex.Message}");
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult.Failed($"FTP 连接失败: {ex.Message}");
                }
            }
        }

        /// <summary>断开 FTP 连接。</summary>
        public void Disconnect()
        {
            lock (_lock) DisconnectCore();
        }

        private void DisconnectCore()
        {
            try
            {
                if (_reader != null) { _reader.Dispose(); _reader = null; }
                if (_controlStream != null) { _controlStream.Dispose(); _controlStream = null; }
                if (_controlClient != null) { _controlClient.Close(); _controlClient = null; }
            }
            catch { }
        }

        // ═══════════════════════════════════════════
        //  目录操作
        // ═══════════════════════════════════════════

        /// <summary>获取当前工作目录。</summary>
        public OperateResult<string> GetWorkingDirectory()
        {
            EnsureConnected();
            string resp = SendCommand("PWD");
            if (!resp.StartsWith("257"))
                return OperateResult<string>.Failed($"PWD 失败: {resp}");

            // 解析 "257 "/path" ..."
            int q1 = resp.IndexOf('"');
            int q2 = resp.IndexOf('"', q1 + 1);
            if (q1 >= 0 && q2 > q1)
                return OperateResult<string>.Success(resp.Substring(q1 + 1, q2 - q1 - 1));

            return OperateResult<string>.Success(resp);
        }

        /// <summary>切换工作目录。</summary>
        public OperateResult ChangeDirectory(string path)
        {
            EnsureConnected();
            string resp = SendCommand($"CWD {path}");
            return resp.StartsWith("250") ? OperateResult.Success() : OperateResult.Failed($"CWD 失败: {resp}");
        }

        /// <summary>列出当前目录文件。</summary>
        public OperateResult<string[]> ListDirectory(string path = "")
        {
            EnsureConnected();
            string listData;
            if (PassiveMode)
            {
                var pasvResult = EnterPassiveMode();
                if (!pasvResult.IsSuccess) return OperateResult<string[]>.Failed(pasvResult.Message);
                listData = pasvResult.Content;
            }
            else
            {
                return OperateResult<string[]>.Failed("主动模式暂不支持");
            }

            // 发送 LIST 命令
            string cmd = string.IsNullOrEmpty(path) ? "LIST" : $"LIST {path}";
            string resp = SendCommand(cmd);

            if (!resp.StartsWith("150") && !resp.StartsWith("125"))
                return OperateResult<string[]>.Failed($"LIST 失败: {resp}");

            // 通过数据连接获取列表
            var files = new List<string>();
            foreach (string line in listData.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                files.Add(line.Trim());

            // 读取完成响应
            ReadResponse();

            return OperateResult<string[]>.Success(files.ToArray());
        }

        // ═══════════════════════════════════════════
        //  文件传输
        // ═══════════════════════════════════════════

        /// <summary>下载远程文件到本地路径。</summary>
        public OperateResult DownloadFile(string remotePath, string localPath)
        {
            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    var dataConn = GetDataConnection();
                    if (!dataConn.IsSuccess) return OperateResult.Failed(dataConn.Message);

                    string resp = SendCommand($"RETR {remotePath}");
                    if (!resp.StartsWith("150") && !resp.StartsWith("125"))
                        return OperateResult.Failed($"RETR 失败: {resp}");

                    using (var dataStream = dataConn.Content.GetStream())
                    using (var fs = File.Create(localPath))
                    {
                        byte[] buf = new byte[8192];
                        int read;
                        while ((read = dataStream.Read(buf, 0, buf.Length)) > 0)
                            fs.Write(buf, 0, read);
                    }

                    dataConn.Content.Close();
                    string complete = ReadResponse();
                    if (!complete.StartsWith("226") && !complete.StartsWith("250"))
                        return OperateResult.Failed($"下载完成响应异常: {complete}");

                    Log.Info($"FTP 下载完成: {remotePath} → {localPath}");
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult.Failed($"FTP 下载异常: {ex.Message}");
                }
            }
        }

        /// <summary>上传本地文件到远程路径。</summary>
        public OperateResult UploadFile(string localPath, string remotePath)
        {
            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    var dataConn = GetDataConnection();
                    if (!dataConn.IsSuccess) return OperateResult.Failed(dataConn.Message);

                    string resp = SendCommand($"STOR {remotePath}");
                    if (!resp.StartsWith("150") && !resp.StartsWith("125"))
                        return OperateResult.Failed($"STOR 失败: {resp}");

                    using (var dataStream = dataConn.Content.GetStream())
                    using (var fs = File.OpenRead(localPath))
                    {
                        byte[] buf = new byte[8192];
                        int read;
                        while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                            dataStream.Write(buf, 0, read);
                    }

                    dataConn.Content.Close();
                    string complete = ReadResponse();
                    if (!complete.StartsWith("226") && !complete.StartsWith("250"))
                        return OperateResult.Failed($"上传完成响应异常: {complete}");

                    Log.Info($"FTP 上传完成: {localPath} → {remotePath}");
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult.Failed($"FTP 上传异常: {ex.Message}");
                }
            }
        }

        /// <summary>下载远程文件到字节数组。</summary>
        public OperateResult<byte[]> DownloadBytes(string remotePath)
        {
            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    var dataConn = GetDataConnection();
                    if (!dataConn.IsSuccess) return OperateResult<byte[]>.Failed(dataConn.Message);

                    string resp = SendCommand($"RETR {remotePath}");
                    if (!resp.StartsWith("150") && !resp.StartsWith("125"))
                        return OperateResult<byte[]>.Failed($"RETR 失败: {resp}");

                    using (var dataStream = dataConn.Content.GetStream())
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        int read;
                        while ((read = dataStream.Read(buf, 0, buf.Length)) > 0)
                            ms.Write(buf, 0, read);

                        dataConn.Content.Close();
                        ReadResponse();
                        return OperateResult<byte[]>.Success(ms.ToArray());
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult<byte[]>.Failed($"FTP 下载异常: {ex.Message}");
                }
            }
        }

        /// <summary>上传字节数组到远程文件。</summary>
        public OperateResult UploadBytes(string remotePath, byte[] data)
        {
            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    var dataConn = GetDataConnection();
                    if (!dataConn.IsSuccess) return OperateResult.Failed(dataConn.Message);

                    string resp = SendCommand($"STOR {remotePath}");
                    if (!resp.StartsWith("150") && !resp.StartsWith("125"))
                        return OperateResult.Failed($"STOR 失败: {resp}");

                    using (var dataStream = dataConn.Content.GetStream())
                    {
                        dataStream.Write(data, 0, data.Length);
                    }

                    dataConn.Content.Close();
                    ReadResponse();
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult.Failed($"FTP 上传异常: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  文件管理
        // ═══════════════════════════════════════════

        /// <summary>删除远程文件。</summary>
        public OperateResult DeleteFile(string remotePath)
        {
            EnsureConnected();
            string resp = SendCommand($"DELE {remotePath}");
            return resp.StartsWith("250") ? OperateResult.Success() : OperateResult.Failed($"DELE 失败: {resp}");
        }

        /// <summary>创建远程目录。</summary>
        public OperateResult CreateDirectory(string remotePath)
        {
            EnsureConnected();
            string resp = SendCommand($"MKD {remotePath}");
            return resp.StartsWith("257") ? OperateResult.Success() : OperateResult.Failed($"MKD 失败: {resp}");
        }

        /// <summary>删除远程目录。</summary>
        public OperateResult RemoveDirectory(string remotePath)
        {
            EnsureConnected();
            string resp = SendCommand($"RMD {remotePath}");
            return resp.StartsWith("250") ? OperateResult.Success() : OperateResult.Failed($"RMD 失败: {resp}");
        }

        /// <summary>重命名远程文件/目录。</summary>
        public OperateResult Rename(string fromPath, string toPath)
        {
            EnsureConnected();
            string rnfr = SendCommand($"RNFR {fromPath}");
            if (!rnfr.StartsWith("350"))
                return OperateResult.Failed($"RNFR 失败: {rnfr}");

            string rnto = SendCommand($"RNTO {toPath}");
            return rnto.StartsWith("250") ? OperateResult.Success() : OperateResult.Failed($"RNTO 失败: {rnto}");
        }

        /// <summary>获取远程文件大小。</summary>
        public OperateResult<long> GetFileSize(string remotePath)
        {
            EnsureConnected();
            string resp = SendCommand($"SIZE {remotePath}");
            if (resp.StartsWith("213") && long.TryParse(resp.Substring(4).Trim(), out long size))
                return OperateResult<long>.Success(size);
            return OperateResult<long>.Failed($"SIZE 失败: {resp}");
        }

        // ═══════════════════════════════════════════
        //  FTP 内部通讯
        // ═══════════════════════════════════════════

        private string SendCommand(string cmd)
        {
            if (_controlStream == null) throw new InvalidOperationException("FTP 未连接");

            byte[] cmdBytes = Encoding.ASCII.GetBytes(cmd + "\r\n");
            _controlStream.Write(cmdBytes, 0, cmdBytes.Length);
            OnMessageSent?.Invoke(this, cmd);
            Log.Debug($"FTP >> {cmd}");

            string response = ReadResponse();
            return response;
        }

        private string ReadResponse()
        {
            if (_reader == null) throw new InvalidOperationException("FTP 未连接");

            var sb = new StringBuilder();
            string? line;
            while ((line = _reader.ReadLine()) != null)
            {
                sb.AppendLine(line);
                OnMessageReceived?.Invoke(this, line);
                Log.Debug($"FTP << {line}");

                // 多行响应检测: 如果第4个字符是空格，说明是最后一行
                if (line.Length >= 4 && line[3] == ' ')
                    break;
            }

            return sb.ToString().Trim();
        }

        private OperateResult<string> EnterPassiveMode()
        {
            string resp = SendCommand("PASV");
            if (!resp.StartsWith("227"))
                return OperateResult<string>.Failed($"PASV 失败: {resp}");

            return OperateResult<string>.Success(resp);
        }

        private OperateResult<TcpClient> GetDataConnection()
        {
            string pasvResp = SendCommand("PASV");
            if (!pasvResp.StartsWith("227"))
                return OperateResult<TcpClient>.Failed($"PASV 失败: {pasvResp}");

            // 解析 PASV 响应: 227 Entering Passive Mode (h1,h2,h3,h4,p1,p2)
            int left = pasvResp.IndexOf('(');
            int right = pasvResp.IndexOf(')');
            if (left < 0 || right < 0)
                return OperateResult<TcpClient>.Failed($"PASV 解析失败: {pasvResp}");

            string[] parts = pasvResp.Substring(left + 1, right - left - 1).Split(',');
            if (parts.Length != 6)
                return OperateResult<TcpClient>.Failed($"PASV 地址解析失败: {pasvResp}");

            string dataIp = $"{parts[0]}.{parts[1]}.{parts[2]}.{parts[3]}";
            int dataPort = (int.Parse(parts[4]) << 8) | int.Parse(parts[5]);

            try
            {
                var dataClient = new TcpClient { SendTimeout = Timeout, ReceiveTimeout = Timeout };
                var result = dataClient.BeginConnect(dataIp, dataPort, null, null);
                if (!result.AsyncWaitHandle.WaitOne(Timeout, true))
                {
                    dataClient.Close();
                    return OperateResult<TcpClient>.Failed($"FTP 数据连接超时: {dataIp}:{dataPort}");
                }
                dataClient.EndConnect(result);

                return OperateResult<TcpClient>.Success(dataClient);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
                return OperateResult<TcpClient>.Failed($"FTP 数据连接失败: {ex.Message}");
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected) throw new InvalidOperationException("FTP 未连接，请先调用 Connect()");
        }

        public override string ToString() => $"FtpClient[{Ip}:{Port}, User={Username}]";
    }
}
