using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 SR-2000 条码阅读器串口客户端。
    /// <para>简单文本协议 over 串口，命令以 CR(\r) 终止。</para>
    /// <para>支持: LON(连续读取)、LOFF(停止)、TGIN(触发)、RESET(复位)、LED 控制等。</para>
    /// <para>响应: 数据 + CR(OK) 或 ER + 错误码 + CR(错误)</para>
    /// </summary>
    public class KeyenceSR2000SerialClient : IReadWriteDevice
    {
        private readonly object _lock = new object();
        private ISerialPort? _serialPort;
        private Stream? _stream;
        protected ILogger Log { get; set; }

        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _serialPort?.IsOpen == true || (_stream != null && _serialPort == null);

        public KeyenceSR2000SerialClient(ISerialPort serialPort, int timeout = 5000)
        {
            _serialPort = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public KeyenceSR2000SerialClient(Stream stream, int timeout = 5000)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  命令发送与响应解析
        // ═══════════════════════════════════════════

        private OperateResult<string> SendTextCommand(string command)
        {
            try
            {
                lock (_lock)
                {
                    if (_stream == null && _serialPort == null)
                        return OperateResult<string>.Failed("未连接");

                    string frame = command + "\r";
                    Log.Debug($"TX → {frame.TrimEnd()}");
                    OnMessageSent?.Invoke(this, frame.TrimEnd());

                    byte[] txBytes = Encoding.ASCII.GetBytes(frame);
                    if (_serialPort != null)
                        _serialPort.Write(txBytes, 0, txBytes.Length);
                    else
                        _stream!.Write(txBytes, 0, txBytes.Length);

                    string? response = ReadLine();
                    if (response == null)
                        return OperateResult<string>.Failed("读取响应超时");

                    Log.Debug($"RX ← {response.TrimEnd()}");
                    OnMessageReceived?.Invoke(this, response.TrimEnd());

                    if (response.StartsWith("ER"))
                    {
                        string errCode = response.Length > 2 ? response.Substring(2).Trim() : "??";
                        return OperateResult<string>.Failed($"SR-2000 错误: {ParseErrorCode(errCode)}");
                    }

                    return OperateResult<string>.Success(response.TrimEnd('\r', '\n'));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"SR-2000 串口通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<string>.Failed($"通讯异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  文本行读取
        // ═══════════════════════════════════════════

        private string? ReadLine()
        {
            var sb = new StringBuilder(256);
            int deadline = Environment.TickCount + Timeout;

            while (Environment.TickCount <= deadline)
            {
                int b = ReadByteWithTimeout(deadline);
                if (b < 0) return null;
                if (b == '\r')
                {
                    int next = ReadByteWithTimeout(Math.Min(deadline, Environment.TickCount + 200));
                    if (next >= 0 && next != '\n')
                        sb.Append((char)next);
                    return sb.ToString();
                }
                if (b == '\n')
                    return sb.ToString();
                sb.Append((char)b);
            }
            return null;
        }

        private int ReadByteWithTimeout(int deadline)
        {
            while (Environment.TickCount <= deadline)
            {
                try
                {
                    if (_serialPort != null)
                    {
                        byte[] buf = new byte[1];
                        int read = _serialPort.Read(buf, 0, 1);
                        if (read > 0) return buf[0];
                    }
                    else if (_stream != null)
                    {
                        return _stream.ReadByte();
                    }
                }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        // ═══════════════════════════════════════════
        //  条码读取命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 开始连续读取 (LON)。SR-2000 将持续读取条码并返回结果。
        /// </summary>
        public OperateResult<string> LON() => SendTextCommand("LON");

        /// <summary>异步开始连续读取。</summary>
        public Task<OperateResult<string>> LONAsync() => Task.Run(() => LON());

        /// <summary>
        /// 停止连续读取 (LOFF)。
        /// </summary>
        public OperateResult<string> LOFF() => SendTextCommand("LOFF");

        /// <summary>异步停止连续读取。</summary>
        public Task<OperateResult<string>> LOFFAsync() => Task.Run(() => LOFF());

        /// <summary>
        /// 触发单次读取 (TGIN)。返回条码数据或错误。
        /// </summary>
        public OperateResult<string> TriggerRead() => SendTextCommand("TGIN");

        /// <summary>异步触发单次读取。</summary>
        public Task<OperateResult<string>> TriggerReadAsync() => Task.Run(() => TriggerRead());

        /// <summary>
        /// 复位设备 (RESET)。
        /// </summary>
        public OperateResult<string> Reset() => SendTextCommand("RESET");

        /// <summary>异步复位设备。</summary>
        public Task<OperateResult<string>> ResetAsync() => Task.Run(() => Reset());

        /// <summary>
        /// 打开 LED 指示灯。
        /// </summary>
        public OperateResult<string> LedOn() => SendTextCommand("LED ON");

        /// <summary>异步打开 LED。</summary>
        public Task<OperateResult<string>> LedOnAsync() => Task.Run(() => LedOn());

        /// <summary>
        /// 关闭 LED 指示灯。
        /// </summary>
        public OperateResult<string> LedOff() => SendTextCommand("LED OFF");

        /// <summary>异步关闭 LED。</summary>
        public Task<OperateResult<string>> LedOffAsync() => Task.Run(() => LedOff());

        /// <summary>
        /// 发送自定义命令并返回响应。
        /// </summary>
        public OperateResult<string> SendCustomCommand(string command) => SendTextCommand(command);

        /// <summary>异步发送自定义命令。</summary>
        public Task<OperateResult<string>> SendCustomCommandAsync(string command) => Task.Run(() => SendTextCommand(command));

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 简化实现（读取条码作为字符串）
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
            => OperateResult<bool>.Failed("SR-2000 不支持布尔读取，请使用 TriggerRead() 读取条码");

        public OperateResult<short> ReadInt16(string address)
            => OperateResult<short>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public OperateResult<ushort> ReadUInt16(string address)
            => OperateResult<ushort>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public OperateResult<int> ReadInt32(string address)
            => OperateResult<int>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public OperateResult<uint> ReadUInt32(string address)
            => OperateResult<uint>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public OperateResult<long> ReadInt64(string address)
            => OperateResult<long>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public OperateResult<ulong> ReadUInt64(string address)
            => OperateResult<ulong>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public OperateResult<float> ReadFloat(string address)
            => OperateResult<float>.Failed("SR-2000 不支持浮点读取，请使用 TriggerRead() 读取条码");

        public OperateResult<double> ReadDouble(string address)
            => OperateResult<double>.Failed("SR-2000 不支持浮点读取，请使用 TriggerRead() 读取条码");

        /// <summary>
        /// 读取条码数据。address 参数被忽略，始终触发单次读取。
        /// </summary>
        public OperateResult<string> ReadString(string address, ushort length)
        {
            var r = TriggerRead();
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content);
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = TriggerRead();
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(r.Content));
        }

        public OperateResult Write(string address, bool value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, short value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, ushort value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, int value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, uint value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, long value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, ulong value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, float value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, double value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, string value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        public OperateResult Connect()
        {
            if (_serialPort != null)
            {
                try
                {
                    _serialPort.ReadTimeout = Timeout;
                    _serialPort.WriteTimeout = Timeout;
                    _serialPort.Open();
                    _stream = null;
                    Log.Info($"串口已打开 {_serialPort.PortName}");
                    OnConnected?.Invoke(this, EventArgs.Empty);
                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    Log.Error($"串口打开失败 — {ex.Message}");
                    OnError?.Invoke(this, ex.Message);
                    return OperateResult.Failed($"串口打开失败: {ex.Message}");
                }
            }

            if (_stream != null)
            {
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }

            return OperateResult.Failed("未配置串口或流");
        }

        public Task<OperateResult> ConnectAsync() => Task.FromResult(Connect());

        public void Disconnect()
        {
            try { _serialPort?.Close(); } catch { }
            if (_serialPort == null)
            {
                try { _stream?.Close(); } catch { }
                _stream = null;
            }
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (disposing) Disconnect(); }

        // ── Async ──

        public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));

        // ═══════════════════════════════════════════
        //  错误码解析
        // ═══════════════════════════════════════════

        private static string ParseErrorCode(string code) => code.Trim() switch
        {
            "0" => "无错误",
            "1" => "命令错误",
            "2" => "参数错误",
            "3" => "超时",
            "4" => "设备忙",
            _ => $"未知错误 {code}"
        };

        public override string ToString() => $"Keyence SR-2000 Serial";
    }
}
