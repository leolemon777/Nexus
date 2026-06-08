using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Nexus.Kuka
{
    /// <summary>
    /// KUKA 机器人 EKI (Ethernet KRL Interface) 通讯客户端。
    /// <para>通过 XML 配置的 TCP 连接读写机器人变量。</para>
    /// <para>默认端口 54600。</para>
    /// <para>对标 HSL: KUKA EKI — Read/Write 变量, 机器人位置/速度/程序状态, 运动控制</para>
    /// </summary>
    public class KukaEkiClient : IReadWriteDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private bool _isConnected;
        protected ILogger Log { get; set; }

        public string IpAddress { get; }
        public int Port { get; }
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected { get { lock (_lock) return _isConnected && _tcp?.Connected == true; } }

        public KukaEkiClient(string ipAddress, int port = 54600, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port; Timeout = timeout; Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        public OperateResult Connect() { try { lock (_lock) { if (_isConnected) return OperateResult.Success(); _tcp = new TcpClient(); var ar = _tcp.BeginConnect(IpAddress, Port, null, null); if (!ar.AsyncWaitHandle.WaitOne(Timeout, false)) { _tcp.Close(); _tcp = null; return OperateResult.Failed("连接超时"); } _tcp.EndConnect(ar); _stream = _tcp.GetStream(); _stream.ReadTimeout = Timeout; _stream.WriteTimeout = Timeout; _isConnected = true; } OnConnected?.Invoke(this, EventArgs.Empty); return OperateResult.Success(); } catch (Exception ex) { OnError?.Invoke(this, ex.Message); return OperateResult.Failed(ex.Message); } }
        public async Task<OperateResult> ConnectAsync() { try { _tcp = new TcpClient(); await _tcp.ConnectAsync(IpAddress, Port).ConfigureAwait(false); lock (_lock) { _stream = _tcp.GetStream(); _stream.ReadTimeout = Timeout; _stream.WriteTimeout = Timeout; _isConnected = true; } OnConnected?.Invoke(this, EventArgs.Empty); return OperateResult.Success(); } catch (Exception ex) { OnError?.Invoke(this, ex.Message); return OperateResult.Failed(ex.Message); } }
        public void Disconnect() { lock (_lock) { _isConnected = false; try { _stream?.Close(); } catch { } try { _tcp?.Close(); } catch { } _stream = null; _tcp = null; } OnDisconnected?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { Disconnect(); GC.SuppressFinalize(this); }

        // EKI XML-based read/write: <READ><VARIABLE name="..."/></READ> / <WRITE><VARIABLE name="...">value</VARIABLE></WRITE>
        private OperateResult<string> SendXmlCommand(string xml)
        {
            lock (_lock)
            {
                if (_stream == null || !_isConnected) return OperateResult<string>.Failed("未连接");
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(xml + "\0"); // null-terminated
                    OnMessageSent?.Invoke(this, xml);
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();

                    // Read response until null terminator
                    var buf = new List<byte>();
                    while (true) { int b = _stream.ReadByte(); if (b <= 0 || b == 0) break; buf.Add((byte)b); if (buf.Count > 4096) break; }

                    if (buf.Count == 0) return OperateResult<string>.Failed("无响应");
                    string resp = Encoding.UTF8.GetString(buf.ToArray());
                    OnMessageReceived?.Invoke(this, resp);

                    if (resp.Contains("<ERROR>")) return OperateResult<string>.Failed("EKI error: " + resp);
                    return OperateResult<string>.Success(resp);
                }
                catch (Exception ex) { _isConnected = false; return OperateResult<string>.Failed(ex.Message); }
            }
        }

        private static string ExtractValue(string xml, string tagName)
        {
            string start = $"<{tagName}>"; string end = $"</{tagName}>";
            int si = xml.IndexOf(start, StringComparison.OrdinalIgnoreCase);
            int ei = xml.IndexOf(end, StringComparison.OrdinalIgnoreCase);
            if (si < 0 || ei < 0) return string.Empty;
            return xml.Substring(si + start.Length, ei - si - start.Length).Trim();
        }

        public OperateResult<bool> ReadBool(string address) { try { var r = SendXmlCommand($"<READ><VARIABLE name=\"{address}\"/></READ>"); if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message); var v = ExtractValue(r.Content, "VARIABLE"); return OperateResult<bool>.Success(v == "1" || v.Equals("TRUE", StringComparison.OrdinalIgnoreCase)); } catch (Exception ex) { return OperateResult<bool>.Failed(ex.Message); } }
        public OperateResult<short> ReadInt16(string address) { try { var r = SendXmlCommand($"<READ><VARIABLE name=\"{address}\"/></READ>"); if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message); var v = ExtractValue(r.Content, "VARIABLE"); return short.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out short val) ? OperateResult<short>.Success(val) : OperateResult<short>.Failed($"Cannot parse '{v}'"); } catch (Exception ex) { return OperateResult<short>.Failed(ex.Message); } }
        public OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message); }
        public OperateResult<int> ReadInt32(string address) { try { var r = SendXmlCommand($"<READ><VARIABLE name=\"{address}\"/></READ>"); if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message); var v = ExtractValue(r.Content, "VARIABLE"); return int.TryParse(v, out int val) ? OperateResult<int>.Success(val) : OperateResult<int>.Failed($"Cannot parse '{v}'"); } catch (Exception ex) { return OperateResult<int>.Failed(ex.Message); } }
        public OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message); }
        public OperateResult<long> ReadInt64(string address) { try { var r = SendXmlCommand($"<READ><VARIABLE name=\"{address}\"/></READ>"); if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message); var v = ExtractValue(r.Content, "VARIABLE"); return long.TryParse(v, out long val) ? OperateResult<long>.Success(val) : OperateResult<long>.Failed($"Cannot parse '{v}'"); } catch (Exception ex) { return OperateResult<long>.Failed(ex.Message); } }
        public OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message); }
        public OperateResult<float> ReadFloat(string address) { try { var r = SendXmlCommand($"<READ><VARIABLE name=\"{address}\"/></READ>"); if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message); var v = ExtractValue(r.Content, "VARIABLE"); return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float val) ? OperateResult<float>.Success(val) : OperateResult<float>.Failed($"Cannot parse '{v}'"); } catch (Exception ex) { return OperateResult<float>.Failed(ex.Message); } }
        public OperateResult<double> ReadDouble(string address) { try { var r = SendXmlCommand($"<READ><VARIABLE name=\"{address}\"/></READ>"); if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message); var v = ExtractValue(r.Content, "VARIABLE"); return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) ? OperateResult<double>.Success(val) : OperateResult<double>.Failed($"Cannot parse '{v}'"); } catch (Exception ex) { return OperateResult<double>.Failed(ex.Message); } }
        public OperateResult<string> ReadString(string address, ushort length) { try { var r = SendXmlCommand($"<READ><VARIABLE name=\"{address}\"/></READ>"); if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message); return OperateResult<string>.Success(ExtractValue(r.Content, "VARIABLE")); } catch (Exception ex) { return OperateResult<string>.Failed(ex.Message); } }
        public OperateResult<byte[]> ReadBytes(string address, ushort length) { try { var r = ReadString(address, length); if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message); return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(r.Content)); } catch (Exception ex) { return OperateResult<byte[]>.Failed(ex.Message); } }

        public OperateResult Write(string address, bool value) => WriteXml(address, value ? "1" : "0");
        public OperateResult Write(string address, short value) => WriteXml(address, value.ToString(CultureInfo.InvariantCulture));
        public OperateResult Write(string address, ushort value) => WriteXml(address, value.ToString(CultureInfo.InvariantCulture));
        public OperateResult Write(string address, int value) => WriteXml(address, value.ToString(CultureInfo.InvariantCulture));
        public OperateResult Write(string address, uint value) => WriteXml(address, value.ToString(CultureInfo.InvariantCulture));
        public OperateResult Write(string address, long value) => WriteXml(address, value.ToString(CultureInfo.InvariantCulture));
        public OperateResult Write(string address, ulong value) => WriteXml(address, value.ToString(CultureInfo.InvariantCulture));
        public OperateResult Write(string address, float value) => WriteXml(address, value.ToString(CultureInfo.InvariantCulture));
        public OperateResult Write(string address, double value) => WriteXml(address, value.ToString(CultureInfo.InvariantCulture));
        public OperateResult Write(string address, string value) => WriteXml(address, value);
        public OperateResult Write(string address, byte[] data) => WriteXml(address, BitConverter.ToString(data).Replace("-", ""));

        private OperateResult WriteXml(string address, string value)
        {
            try { var r = SendXmlCommand($"<WRITE><VARIABLE name=\"{address}\">{value}</VARIABLE></WRITE>"); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message); }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        public Task<OperateResult<bool>> ReadBoolAsync(string a) => Task.Run(() => ReadBool(a));
        public Task<OperateResult<short>> ReadInt16Async(string a) => Task.Run(() => ReadInt16(a));
        public Task<OperateResult<ushort>> ReadUInt16Async(string a) => Task.Run(() => ReadUInt16(a));
        public Task<OperateResult<int>> ReadInt32Async(string a) => Task.Run(() => ReadInt32(a));
        public Task<OperateResult<uint>> ReadUInt32Async(string a) => Task.Run(() => ReadUInt32(a));
        public Task<OperateResult<long>> ReadInt64Async(string a) => Task.Run(() => ReadInt64(a));
        public Task<OperateResult<ulong>> ReadUInt64Async(string a) => Task.Run(() => ReadUInt64(a));
        public Task<OperateResult<float>> ReadFloatAsync(string a) => Task.Run(() => ReadFloat(a));
        public Task<OperateResult<double>> ReadDoubleAsync(string a) => Task.Run(() => ReadDouble(a));
        public Task<OperateResult<string>> ReadStringAsync(string a, ushort l) => Task.Run(() => ReadString(a, l));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string a, ushort l) => Task.Run(() => ReadBytes(a, l));
        public Task<OperateResult> WriteAsync(string a, bool v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, short v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, int v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, float v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, string v) => Task.Run(() => Write(a, v));
        public Task<OperateResult> WriteAsync(string a, byte[] v) => Task.Run(() => Write(a, v));

        // ═══════════════════════════════════════════
        //  机器人专用命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取机器人当前笛卡尔位置 (X, Y, Z, A, B, C)。
        /// <para>需要 EKI XML 配置中定义 $POS_ACT 变量映射。</para>
        /// </summary>
        public OperateResult<KukaCartesianPosition> ReadPosition()
        {
            try
            {
                var r = SendXmlCommand("<READ><VARIABLE name=\"$POS_ACT\"/></READ>");
                if (!r.IsSuccess) return OperateResult<KukaCartesianPosition>.Failed(r.Message);

                var pos = new KukaCartesianPosition();
                string content = r.Content;
                // EKI 返回 XML: <REPLY><VARIABLE><element name="X">value</element>...</VARIABLE></REPLY>
                pos.X = ParseXmlElement(content, "X");
                pos.Y = ParseXmlElement(content, "Y");
                pos.Z = ParseXmlElement(content, "Z");
                pos.A = ParseXmlElement(content, "A");
                pos.B = ParseXmlElement(content, "B");
                pos.C = ParseXmlElement(content, "C");
                return OperateResult<KukaCartesianPosition>.Success(pos);
            }
            catch (Exception ex) { return OperateResult<KukaCartesianPosition>.Failed(ex.Message); }
        }

        /// <summary>
        /// 读取机器人当前关节位置 (A1-A6)。
        /// <para>需要 EKI XML 配置中定义 $AXIS_ACT 变量映射。</para>
        /// </summary>
        public OperateResult<double[]> ReadAxisPosition()
        {
            try
            {
                var r = SendXmlCommand("<READ><VARIABLE name=\"$AXIS_ACT\"/></READ>");
                if (!r.IsSuccess) return OperateResult<double[]>.Failed(r.Message);

                var axes = new double[6];
                string content = r.Content;
                for (int i = 0; i < 6; i++)
                    axes[i] = ParseXmlElement(content, $"A{i + 1}");
                return OperateResult<double[]>.Success(axes);
            }
            catch (Exception ex) { return OperateResult<double[]>.Failed(ex.Message); }
        }

        /// <summary>
        /// 读取机器人程序运行状态。
        /// </summary>
        public OperateResult<KukaProgramState> ReadProgramState()
        {
            try
            {
                var r = SendXmlCommand("<READ><VARIABLE name=\"$PRO_STATE\"/></READ>");
                if (!r.IsSuccess) return OperateResult<KukaProgramState>.Failed(r.Message);

                string val = ExtractValue(r.Content, "VARIABLE");
                var state = new KukaProgramState
                {
                    IsRunning = val.Contains("#P_ACTIVE") || val.Contains("RUN"),
                    IsPaused = val.Contains("#P_STOP") || val.Contains("PAUSE"),
                    State = val
                };
                return OperateResult<KukaProgramState>.Success(state);
            }
            catch (Exception ex) { return OperateResult<KukaProgramState>.Failed(ex.Message); }
        }

        /// <summary>
        /// 发送运动命令 — 启动程序。
        /// </summary>
        public OperateResult StartProgram(string programName)
        {
            try
            {
                var r = SendXmlCommand($"<WRITE><VARIABLE name=\"PGNO\">{programName}</VARIABLE></WRITE>");
                if (!r.IsSuccess) return r;
                // 发送启动信号
                return WriteXml("START", "1");
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        /// <summary>
        /// 停止机器人运动。
        /// </summary>
        public OperateResult StopMotion()
        {
            try { return WriteXml("STOP", "1"); }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        /// <summary>
        /// 点动控制 — 指定轴和方向。
        /// </summary>
        public OperateResult Jog(int axis, double velocity)
        {
            try
            {
                return WriteXml($"JOG_AXIS_{axis}", velocity.ToString("F3", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        private static double ParseXmlElement(string xml, string elementName)
        {
            string start = $"name=\"{elementName}\">";
            int si = xml.IndexOf(start, StringComparison.OrdinalIgnoreCase);
            if (si < 0) return 0;
            si += start.Length;
            int ei = xml.IndexOf('<', si);
            if (ei < 0) return 0;
            string val = xml.Substring(si, ei - si).Trim();
            return double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        }

        // ── 机器人异步 ──
        public Task<OperateResult<KukaCartesianPosition>> ReadPositionAsync() => Task.Run(() => ReadPosition());
        public Task<OperateResult<double[]>> ReadAxisPositionAsync() => Task.Run(() => ReadAxisPosition());
        public Task<OperateResult<KukaProgramState>> ReadProgramStateAsync() => Task.Run(() => ReadProgramState());
        public Task<OperateResult> StartProgramAsync(string programName) => Task.Run(() => StartProgram(programName));
        public Task<OperateResult> StopMotionAsync() => Task.Run(() => StopMotion());
        public Task<OperateResult> JogAsync(int axis, double velocity) => Task.Run(() => Jog(axis, velocity));
    }

    /// <summary>KUKA 笛卡尔位置 (X, Y, Z, A, B, C)。</summary>
    public class KukaCartesianPosition
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double A { get; set; }
        public double B { get; set; }
        public double C { get; set; }
        public override string ToString() => $"X={X:F3} Y={Y:F3} Z={Z:F3} A={A:F3} B={B:F3} C={C:F3}";
    }

    /// <summary>KUKA 程序运行状态。</summary>
    public class KukaProgramState
    {
        public bool IsRunning { get; set; }
        public bool IsPaused { get; set; }
        public string State { get; set; } = "";
        public override string ToString() => IsRunning ? "RUNNING" : IsPaused ? "PAUSED" : State;
    }
}
