using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Nexus.LsElectric
{
    /// <summary>
    /// LS 产电 XGT 协议客户端 — 支持 XGB/XBC/XECS 系列。
    /// <para>帧格式 (二进制模式): ENQ(0x05) + Company(10B) + CPUInfo(10B) + PLCInfo(6B) + Data</para>
    /// <para>简化帧格式 (XGT专用): ENQ(1) + Header(12) + Command(1) + DataType(1) + Reserve(2) + BlockInfo(2) + Data + EOT(0x04)</para>
    /// <para>对标 HSL: LSLip — Read/Write P/M/K/D/C/T/N 寄存器</para>
    /// </summary>
    public class LsXgtClient : IReadWriteDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private bool _isConnected;
        protected ILogger Log { get; set; }

        public string IpAddress { get; }
        public int Port { get; }
        public int Timeout { get; set; }
        public byte CpuType { get; set; } = 0xA0; // XGB

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _isConnected && _tcp?.Connected == true;

        public LsXgtClient(string ipAddress, int port = 2004, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  XGT 帧构建与收发
        // ═══════════════════════════════════════════

        // Command
        private const byte CmdRead = 0x54;    // Read
        private const byte CmdWrite = 0x58;   // Write
        private const byte CmdRequest = 0x52; // Request (状态/信息)
        private const byte CmdControl = 0x63; // Control (Run/Stop)

        // Data Type
        private const byte TypeBit = 0x00;
        private const byte TypeByte = 0x01;
        private const byte TypeWord = 0x02;
        private const byte TypeDWord = 0x03;
        private const byte TypeLWord = 0x04;

        // Block Info
        private const ushort BlockContinuous = 0x0000;
        private const ushort BlockRandom = 0x0001;

        private OperateResult<byte[]> SendReceive(byte command, byte dataType, byte[] data)
        {
            try
            {
                lock (_lock)
                {
                    if (_stream == null) return OperateResult<byte[]>.Failed("未连接");

                    // XGT Frame: ENQ(1) + Header(Company=10+CPU=10+PLCCPUInfo=6) + Cmd(1) + DataType(1) + Reserve(2) + BlockInfo(2) + Data + EOT(1)
                    byte[] company = Encoding.ASCII.GetBytes("LSIS-XGT\0\0");
                    byte[] cpuInfo = new byte[10]; // zeros
                    byte[] plcInfo = new byte[6];   // zeros

                    int dataLen = data?.Length ?? 0;
                    int frameLen = 1 + 10 + 10 + 6 + 1 + 1 + 2 + 2 + dataLen + 1;
                    byte[] frame = new byte[frameLen];
                    int i = 0;

                    frame[i++] = 0x05; // ENQ
                    Buffer.BlockCopy(company, 0, frame, i, 10); i += 10;
                    // CPU info (10 bytes of zeros)
                    i += 10;
                    // PLC info (6 bytes)
                    frame[i++] = CpuType;
                    i += 5;
                    frame[i++] = command;
                    frame[i++] = dataType;
                    frame[i++] = 0x00; frame[i++] = 0x00; // Reserve
                    frame[i++] = 0x00; frame[i++] = 0x00; // BlockInfo (continuous)
                    if (data != null && dataLen > 0)
                    {
                        Buffer.BlockCopy(data, 0, frame, i, dataLen);
                        i += dataLen;
                    }
                    frame[i] = 0x04; // EOT

                    Log.Debug($"XGT TX → Cmd=0x{command:X2} DataType={dataType} Len={frameLen}");
                    OnMessageSent?.Invoke(this, $"XGT Cmd=0x{command:X2}");
                    _stream.Write(frame, 0, frameLen);

                    // 读取响应
                    byte[] respHeader = ReadExact(1 + 10 + 10 + 6 + 1 + 1 + 2 + 2);
                    if (respHeader == null) return OperateResult<byte[]>.Failed("读取响应头超时");

                    if (respHeader[0] != 0x06) // ACK
                    {
                        if (respHeader[0] == 0x15) // NAK
                        {
                            // Error response
                            byte errCmd = respHeader[28];
                            byte errType = respHeader[29];
                            ushort errCode = (ushort)(respHeader[32] | (respHeader[33] << 8));
                            return OperateResult<byte[]>.Failed($"XGT 错误: Cmd=0x{errCmd:X2} Type={errType} Code=0x{errCode:X4}");
                        }
                        return OperateResult<byte[]>.Failed($"XGT 响应异常: Header=0x{respHeader[0]:X2}");
                    }

                    byte respCmd = respHeader[28];
                    byte respDataType = respHeader[29];
                    ushort respDataLen = (ushort)(respHeader[32] | (respHeader[33] << 8));

                    byte[] respData = respDataLen > 0 ? ReadExact(respDataLen) : new byte[0];
                    byte eot = ReadExact(1)?[0] ?? 0;

                    Log.Debug($"XGT RX ← Cmd=0x{respCmd:X2} Len={respDataLen}");
                    OnMessageReceived?.Invoke(this, $"XGT Response [{respDataLen}B]");
                    return OperateResult<byte[]>.Success(respData ?? new byte[0]);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"XGT 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private byte[]? ReadExact(int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            int deadline = Environment.TickCount + Timeout;
            while (offset < count && Environment.TickCount <= deadline)
            {
                int n = _stream!.Read(buffer, offset, count - offset);
                if (n <= 0) return null;
                offset += n;
            }
            return offset >= count ? buffer : null;
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        /// <summary>
        /// XGT 地址: "D100" (Data Register), "M100" (Internal Relay), "P100" (I/O),
        /// "K100" (Keep Relay), "T100" (Timer), "C100" (Counter), "N100" (File Register)
        /// </summary>
        private static (byte areaCode, int address) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            char prefix = address[0];
            int addr = int.Parse(address.Substring(1));

            return prefix switch
            {
                'P' => (0x00, addr),  // I/O
                'M' => (0x01, addr),  // Internal Relay
                'L' => (0x02, addr),  // Link Relay
                'K' => (0x03, addr),  // Keep Relay
                'F' => (0x04, addr),  // Special Relay
                'T' => (0x05, addr),  // Timer
                'C' => (0x06, addr),  // Counter
                'D' => (0x07, addr),  // Data Register
                'N' => (0x08, addr),  // File Register
                _ => (0x07, int.Parse(address)) // Default: D register
            };
        }

        // ═══════════════════════════════════════════
        //  读寄存器
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadRegisters(byte area, int startAddr, int count)
        {
            // Request: VariableCount(2) + VariableType(1) + Area(1) + Offset(2) + Size(2)
            byte[] req = new byte[8];
            req[0] = 0x01; req[1] = 0x00; // 1 variable
            req[2] = TypeWord;
            req[3] = area;
            req[4] = (byte)(startAddr & 0xFF); req[5] = (byte)((startAddr >> 8) & 0xFF);
            req[6] = (byte)(count & 0xFF); req[7] = (byte)((count >> 8) & 0xFF);

            var r = SendReceive(CmdRead, TypeWord, req);
            if (!r.IsSuccess) return r;

            // Response: Error(1) + Count(2) + Data
            if (r.Content.Length < 3)
                return OperateResult<byte[]>.Failed("XGT 响应数据不足");
            byte err = r.Content[0];
            if (err != 0)
                return OperateResult<byte[]>.Failed($"XGT 读错误: 0x{err:X2}");

            byte[] data = new byte[r.Content.Length - 3];
            Buffer.BlockCopy(r.Content, 3, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  写寄存器
        // ═══════════════════════════════════════════

        private OperateResult WriteRegisters(byte area, int startAddr, byte[] data)
        {
            int count = data.Length / 2;
            // Request: VariableCount(2) + VariableType(1) + Area(1) + Offset(2) + Size(2) + Data
            byte[] req = new byte[8 + data.Length];
            req[0] = 0x01; req[1] = 0x00;
            req[2] = TypeWord;
            req[3] = area;
            req[4] = (byte)(startAddr & 0xFF); req[5] = (byte)((startAddr >> 8) & 0xFF);
            req[6] = (byte)(count & 0xFF); req[7] = (byte)((count >> 8) & 0xFF);
            Buffer.BlockCopy(data, 0, req, 8, data.Length);

            var r = SendReceive(CmdWrite, TypeWord, req);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);

            if (r.Content.Length < 1) return OperateResult.Failed("XGT 写响应不足");
            if (r.Content[0] != 0) return OperateResult.Failed($"XGT 写错误: 0x{r.Content[0]:X2}");
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取 PLC 运行状态 (XGT Request 命令)。
        /// <para>返回: 0x00=Stop, 0x01=Run, 0x02=Debug, 0x03=Error。</para>
        /// </summary>
        public OperateResult<byte> ReadPlcStatus()
        {
            // XGT 状态请求: 请求类型 = 0x0000 (CPU状态)
            byte[] req = { 0x00, 0x00 };
            var r = SendReceive(CmdRequest, TypeByte, req);
            if (!r.IsSuccess) return OperateResult<byte>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 1) return OperateResult<byte>.Failed("状态响应不足");
            return OperateResult<byte>.Success(r.Content[0]);
        }

        /// <summary>
        /// 运行 PLC (XGT Control 命令, 模式=0x01)。
        /// </summary>
        public OperateResult Run()
        {
            byte[] req = { 0x01 };
            var r = SendReceive(CmdControl, TypeByte, req);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>
        /// 停止 PLC (XGT Control 命令, 模式=0x00)。
        /// </summary>
        public OperateResult Stop()
        {
            byte[] req = { 0x00 };
            var r = SendReceive(CmdControl, TypeByte, req);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>异步运行 PLC。</summary>
        public Task<OperateResult> RunAsync() => Task.FromResult(Run());

        /// <summary>异步停止 PLC。</summary>
        public Task<OperateResult> StopAsync() => Task.FromResult(Stop());

        /// <summary>异步读取 PLC 状态。</summary>
        public Task<OperateResult<byte>> ReadPlcStatusAsync() => Task.FromResult(ReadPlcStatus());

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 数据类型读写
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
        {
            var (area, addr) = ParseAddress(address);
            // 读 bit: 用 TypeBit
            byte[] req = { 0x01, 0x00, TypeBit, area, (byte)(addr & 0xFF), (byte)((addr >> 8) & 0xFF), 0x01, 0x00 };
            var r = SendReceive(CmdRead, TypeBit, req);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<bool>.Failed("响应不足");
            return OperateResult<bool>.Success(r.Content[3] != 0);
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public unsafe OperateResult<float> ReadFloat(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public unsafe OperateResult<double> ReadDouble(string address)
        {
            var (area, addr) = ParseAddress(address);
            var r = ReadRegisters(area, addr, 4);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0));
        }

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var (area, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(area, addr, regCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, Math.Min(length, r.Content.Length)));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (area, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(area, addr, regCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Array.Copy(r.Content, data, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        // ── 写入 ──

        public OperateResult Write(string address, bool value)
        {
            var (area, addr) = ParseAddress(address);
            byte[] req = { 0x01, 0x00, TypeBit, area, (byte)(addr & 0xFF), (byte)((addr >> 8) & 0xFF), 0x01, 0x00, (byte)(value ? 1 : 0) };
            var r = SendReceive(CmdWrite, TypeBit, req);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        public OperateResult Write(string address, short value)
        {
            var (area, addr) = ParseAddress(address);
            return WriteRegisters(area, addr, DataConverter.GetBytes(value));
        }

        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) { var (a, o) = ParseAddress(address); return WriteRegisters(a, o, DataConverter.GetBytes(value)); }
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, (int)value);
        public OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public unsafe OperateResult Write(string address, float value) { var (a, o) = ParseAddress(address); return WriteRegisters(a, o, DataConverter.GetBytes(value)); }
        public OperateResult Write(string address, double value) => Write(address, (float)value);
        public OperateResult Write(string address, string value) { var (a, o) = ParseAddress(address); return WriteRegisters(a, o, DataConverter.GetBytes(value ?? "")); }
        public OperateResult Write(string address, byte[] data) { var (a, o) = ParseAddress(address); return WriteRegisters(a, o, data); }

        // ═══════════════════════════════════════════
        //  连接
        // ═══════════════════════════════════════════

        public OperateResult Connect()
        {
            try
            {
                _tcp = new TcpClient(IpAddress, Port);
                _tcp.SendTimeout = Timeout;
                _tcp.ReceiveTimeout = Timeout;
                _stream = _tcp.GetStream();
                _isConnected = true;
                OnConnected?.Invoke(this, EventArgs.Empty);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed($"连接失败: {ex.Message}"); }
        }

        public Task<OperateResult> ConnectAsync() => Task.Run(() => Connect());

        public void Disconnect()
        {
            _isConnected = false;
            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _tcp = null; _stream = null;
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
    }
}
