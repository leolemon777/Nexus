using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Beckhoff
{
    /// <summary>
    /// 倍福 TwinCAT ADS 协议客户端 — 支持变量名读写、Handle 管理。
    /// <para>协议层次: TCP → AMS (Automation Message Specification) → ADS</para>
    /// <para>默认端口: 48898 (TC2) / 851 (TC3 TCP)</para>
    /// <para>对标 HSL: BeckhoffAdsNet — Read/Write Symbol, Handle</para>
    /// </summary>
    public class BeckhoffAdsClient : IReadWriteDevice, IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private bool _isConnected;
        private uint _invokeId;
        protected ILogger Log { get; set; }

        public string IpAddress { get; }
        public int Port { get; }
        /// <summary>本地 AMS NetId (格式 "x.x.x.x.x.x")。</summary>
        public string LocalNetId { get; set; }
        /// <summary>本地 AMS Port。</summary>
        public ushort LocalPort { get; set; } = 32768;
        /// <summary>目标 AMS NetId。</summary>
        public string TargetNetId { get; set; }
        /// <summary>目标 AMS Port (851 = TC3 Runtime 1)。</summary>
        public ushort TargetPort { get; set; } = 851;
        /// <summary>超时（毫秒）。</summary>
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _isConnected && _tcp?.Connected == true;

        public BeckhoffAdsClient(string ipAddress, int port = 48898, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
            Timeout = timeout;
            LocalNetId = "127.0.0.1.1.1";
            TargetNetId = ipAddress + ".1.1";
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  AMS/ADS 帧结构
        // ═══════════════════════════════════════════

        // AMS Command IDs
        private const ushort CmdAdsReadDeviceInfo = 0x0001;
        private const ushort CmdAdsRead = 0x0002;
        private const ushort CmdAdsWrite = 0x0003;
        private const ushort CmdAdsReadWrite = 0x0009;
        private const ushort CmdAdsReadState = 0x0004;
        private const ushort CmdAdsWriteControl = 0x0005;
        private const ushort CmdAdsAddDeviceNotification = 0x0006;
        private const ushort CmdAdsDeleteDeviceNotification = 0x0007;
        private const ushort CmdAdsDeviceNotification = 0x0008;
        private const ushort CmdAdsWriteRead = 0x000C;

        /// <summary>发送 AMS/ADS 请求并接收响应。</summary>
        private OperateResult<byte[]> SendAds(ushort command, byte[] adsData)
        {
            try
            {
                lock (_lock)
                {
                    if (_stream == null) return OperateResult<byte[]>.Failed("未连接");

                    uint invokeId = ++_invokeId;

                    // AMS Header: TargetNetId(6) + TargetPort(2) + SourceNetId(6) + SourcePort(2) +
                    //             Command(2) + StateFlags(2) + DataLength(4) + ErrorCode(4) + InvokeId(4)
                    // Total: 32 bytes
                    byte[] targetId = NetIdToBytes(TargetNetId);
                    byte[] sourceId = NetIdToBytes(LocalNetId);

                    using var ms = new MemoryStream();
                    // AMS Header
                    ms.Write(targetId, 0, 6);       // Target NetId
                    WriteU16(ms, TargetPort);        // Target Port
                    ms.Write(sourceId, 0, 6);       // Source NetId
                    WriteU16(ms, LocalPort);         // Source Port
                    WriteU16(ms, command);           // Command
                    WriteU16(ms, 0x0004);            // StateFlags = ADS Command
                    WriteU32(ms, (uint)adsData.Length); // Data Length
                    WriteU32(ms, 0);                 // Error Code = 0
                    WriteU32(ms, invokeId);          // Invoke Id
                    ms.Write(adsData, 0, adsData.Length);

                    // TCP AMS Frame: AMS Length(4) + AMS Data
                    byte[] amsData = ms.ToArray();
                    byte[] tcpFrame = new byte[4 + amsData.Length];
                    tcpFrame[0] = (byte)(amsData.Length & 0xFF);
                    tcpFrame[1] = (byte)((amsData.Length >> 8) & 0xFF);
                    tcpFrame[2] = (byte)((amsData.Length >> 16) & 0xFF);
                    tcpFrame[3] = (byte)((amsData.Length >> 24) & 0xFF);
                    Buffer.BlockCopy(amsData, 0, tcpFrame, 4, amsData.Length);

                    Log.Debug($"ADS TX → Cmd=0x{command:X4} Invoke={invokeId} Len={adsData.Length}");
                    OnMessageSent?.Invoke(this, $"ADS Cmd=0x{command:X4}");
                    _stream.Write(tcpFrame, 0, tcpFrame.Length);

                    // 读取响应
                    byte[] lenBuf = ReadExact(4);
                    if (lenBuf == null) return OperateResult<byte[]>.Failed("读取 AMS 长度超时");
                    int respLen = BitConverter.ToInt32(lenBuf, 0);
                    if (respLen <= 0 || respLen > 1024 * 1024)
                        return OperateResult<byte[]>.Failed($"AMS 响应长度异常: {respLen}");

                    byte[] respAms = ReadExact(respLen);
                    if (respAms == null) return OperateResult<byte[]>.Failed("读取 AMS 响应超时");

                    // 解析 AMS Header
                    if (respAms.Length < 32)
                        return OperateResult<byte[]>.Failed("AMS 响应头不完整");

                    uint adsError = BitConverter.ToUInt32(respAms, 24);
                    if (adsError != 0)
                        return OperateResult<byte[]>.Failed($"ADS 错误: 0x{adsError:X8}", (byte)(adsError & 0xFF));

                    // 提取 ADS 数据 (after 32-byte AMS header)
                    int adsDataLen = respAms.Length - 32;
                    byte[] result = new byte[adsDataLen];
                    Buffer.BlockCopy(respAms, 32, result, 0, adsDataLen);

                    Log.Debug($"ADS RX ← Error=0x{adsError:X8} Len={adsDataLen}");
                    OnMessageReceived?.Invoke(this, $"ADS Response [{adsDataLen}B]");
                    return OperateResult<byte[]>.Success(result);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"ADS 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private byte[] ReadExact(int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            int start = Environment.TickCount;
            while (offset < count && unchecked(Environment.TickCount - start) <= Timeout)
            {
                int n = _stream!.Read(buffer, offset, count - offset);
                if (n <= 0) return null!;
                offset += n;
            }
            return offset >= count ? buffer : null!;
        }

        // ── AMS 辅助 ──

        private static byte[] NetIdToBytes(string netId)
        {
            string[] parts = netId.Split('.');
            byte[] result = new byte[6];
            for (int i = 0; i < Math.Min(parts.Length, 6); i++)
                result[i] = byte.Parse(parts[i]);
            return result;
        }

        private static void WriteU16(Stream s, ushort v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)((v >> 8) & 0xFF)); }
        private static void WriteU32(Stream s, uint v) { s.WriteByte((byte)(v & 0xFF)); s.WriteByte((byte)((v >> 8) & 0xFF)); s.WriteByte((byte)((v >> 16) & 0xFF)); s.WriteByte((byte)((v >> 24) & 0xFF)); }

        // ═══════════════════════════════════════════
        //  ADS 读写操作
        // ═══════════════════════════════════════════

        /// <summary>ADS Read: IndexGroup(4) + IndexOffset(4) + ReadLength(4)</summary>
        private OperateResult<byte[]> AdsRead(uint indexGroup, uint indexOffset, uint readLength)
        {
            byte[] req = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(indexGroup), 0, req, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(indexOffset), 0, req, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(readLength), 0, req, 8, 4);

            var r = SendAds(CmdAdsRead, req);
            if (!r.IsSuccess) return r;

            // ADS Read Response: Result(4) + DataLength(4) + Data
            if (r.Content.Length < 8)
                return OperateResult<byte[]>.Failed("ADS Read 响应不完整");

            uint result = BitConverter.ToUInt32(r.Content, 0);
            if (result != 0)
                return OperateResult<byte[]>.Failed($"ADS Read 错误: 0x{result:X8}", (byte)(result & 0xFF));

            uint dataLen = BitConverter.ToUInt32(r.Content, 4);
            byte[] data = new byte[dataLen];
            if (dataLen > 0)
                Buffer.BlockCopy(r.Content, 8, data, 0, (int)dataLen);
            return OperateResult<byte[]>.Success(data);
        }

        /// <summary>ADS Write: IndexGroup(4) + IndexOffset(4) + DataLength(4) + Data</summary>
        private OperateResult AdsWrite(uint indexGroup, uint indexOffset, byte[] data)
        {
            byte[] req = new byte[12 + data.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(indexGroup), 0, req, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(indexOffset), 0, req, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)data.Length), 0, req, 8, 4);
            Buffer.BlockCopy(data, 0, req, 12, data.Length);

            var r = SendAds(CmdAdsWrite, req);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>ADS ReadWrite: IndexGroup(4) + IndexOffset(4) + ReadLength(4) + WriteLength(4) + WriteData</summary>
        private OperateResult<byte[]> AdsReadWrite(uint indexGroup, uint indexOffset, uint readLength, byte[] writeData)
        {
            byte[] req = new byte[16 + writeData.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(indexGroup), 0, req, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(indexOffset), 0, req, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(readLength), 0, req, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)writeData.Length), 0, req, 12, 4);
            Buffer.BlockCopy(writeData, 0, req, 16, writeData.Length);

            var r = SendAds(CmdAdsReadWrite, req);
            if (!r.IsSuccess) return r;

            if (r.Content.Length < 8)
                return OperateResult<byte[]>.Failed("ADS ReadWrite 响应不完整");

            uint result = BitConverter.ToUInt32(r.Content, 0);
            if (result != 0)
                return OperateResult<byte[]>.Failed($"ADS ReadWrite 错误: 0x{result:X8}", (byte)(result & 0xFF));

            uint dataLen = BitConverter.ToUInt32(r.Content, 4);
            byte[] data = new byte[dataLen];
            if (dataLen > 0)
                Buffer.BlockCopy(r.Content, 8, data, 0, (int)dataLen);
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  Handle 管理 (变量名读写)
        // ═══════════════════════════════════════════

        /// <summary>通过变量名获取 Handle。IndexGroup=0xF003, IndexOffset=0</summary>
        private OperateResult<uint> GetHandle(string variableName)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(variableName + "\0");
            var r = AdsReadWrite(0xF003, 0, 4, nameBytes);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<uint>.Failed("Handle 响应不足");
            return OperateResult<uint>.Success(BitConverter.ToUInt32(r.Content, 0));
        }

        /// <summary>释放 Handle。</summary>
        private OperateResult ReleaseHandle(uint handle)
        {
            byte[] data = BitConverter.GetBytes(handle);
            return AdsWrite(0xF004, 0, data);
        }

        /// <summary>通过 Handle 读取变量数据。</summary>
        private OperateResult<byte[]> ReadByHandle(uint handle, uint length)
        {
            return AdsReadWrite(0xF005, handle, length, new byte[0]);
        }

        /// <summary>通过 Handle 写入变量数据。</summary>
        private OperateResult WriteByHandle(uint handle, byte[] data)
        {
            return AdsWrite(0xF005, handle, data);
        }

        // ═══════════════════════════════════════════
        //  ADS 设备信息 / 状态 / 控制
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取 ADS 设备信息 (Command=0x0001)。
        /// <para>返回: MajorVersion(1) + MinorVersion(1) + VersionBuild(2) + DeviceName(16)。</para>
        /// </summary>
        public OperateResult<AdsDeviceInfo> ReadDeviceInfo()
        {
            var r = SendAds(CmdAdsReadDeviceInfo, Array.Empty<byte>());
            if (!r.IsSuccess) return OperateResult<AdsDeviceInfo>.Failed(r.Message, r.ErrorCode);

            if (r.Content.Length < 20)
                return OperateResult<AdsDeviceInfo>.Failed("ADS 设备信息响应不足");

            var info = new AdsDeviceInfo
            {
                MajorVersion = r.Content[0],
                MinorVersion = r.Content[1],
                VersionBuild = BitConverter.ToUInt16(r.Content, 2),
                DeviceName = Encoding.ASCII.GetString(r.Content, 4, Math.Min(16, r.Content.Length - 4)).TrimEnd('\0')
            };
            return OperateResult<AdsDeviceInfo>.Success(info);
        }

        /// <summary>
        /// 读取 ADS 设备状态 (Command=0x0004)。
        /// <para>返回: ADSState(2) + DeviceState(2)。</para>
        /// </summary>
        public OperateResult<AdsState> ReadState()
        {
            var r = SendAds(CmdAdsReadState, Array.Empty<byte>());
            if (!r.IsSuccess) return OperateResult<AdsState>.Failed(r.Message, r.ErrorCode);

            if (r.Content.Length < 4)
                return OperateResult<AdsState>.Failed("ADS 状态响应不足");

            return OperateResult<AdsState>.Success(new AdsState
            {
                AdsStateValue = BitConverter.ToUInt16(r.Content, 0),
                DeviceStateValue = BitConverter.ToUInt16(r.Content, 2)
            });
        }

        /// <summary>
        /// 写入 ADS 控制命令 (Command=0x0005) — 用于 Run/Stop PLC。
        /// </summary>
        /// <param name="adsState">ADS 状态值 (5=Run, 6=Stop)。</param>
        /// <param name="deviceState">设备状态值 (通常为0)。</param>
        /// <param name="deviceData">附加数据 (可为空)。</param>
        public OperateResult WriteControl(ushort adsState, ushort deviceState, byte[] deviceData)
        {
            byte[] req = new byte[8 + (deviceData?.Length ?? 0)];
            Buffer.BlockCopy(BitConverter.GetBytes(adsState), 0, req, 0, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(deviceState), 0, req, 2, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)(deviceData?.Length ?? 0)), 0, req, 4, 4);
            if (deviceData != null && deviceData.Length > 0)
                Buffer.BlockCopy(deviceData, 0, req, 8, deviceData.Length);

            var r = SendAds(CmdAdsWriteControl, req);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>启动 PLC (ADS State = 5 = Run)。</summary>
        public OperateResult Run() => WriteControl(5, 0, Array.Empty<byte>());

        /// <summary>停止 PLC (ADS State = 6 = Stop)。</summary>
        public OperateResult Stop() => WriteControl(6, 0, Array.Empty<byte>());

        /// <summary>异步读取 ADS 设备信息。</summary>
        public Task<OperateResult<AdsDeviceInfo>> ReadDeviceInfoAsync() => Task.Run(() => ReadDeviceInfo());

        /// <summary>异步读取 ADS 设备状态。</summary>
        public Task<OperateResult<AdsState>> ReadStateAsync() => Task.Run(() => ReadState());

        /// <summary>异步启动 PLC。</summary>
        public Task<OperateResult> RunAsync() => Task.FromResult(Run());

        /// <summary>异步停止 PLC。</summary>
        public Task<OperateResult> StopAsync() => Task.FromResult(Stop());

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 连接
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
            catch (Exception ex)
            {
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
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

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 读写 (address = variable name or group:offset)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 解析地址: "MyVariable" (by name) 或 "group:offset" (by index, hex)
        /// </summary>
        private static (uint group, uint offset, bool isByName) ParseAddress(string address)
        {
            if (address.Contains(':'))
            {
                string[] parts = address.Split(':');
                return (Convert.ToUInt32(parts[0], 16), Convert.ToUInt32(parts[1], 16), false);
            }
            return (0, 0, true);
        }

        private OperateResult<byte[]> ReadRaw(string address, uint length)
        {
            var (group, offset, isByName) = ParseAddress(address);
            if (isByName)
            {
                var h = GetHandle(address);
                if (!h.IsSuccess) return OperateResult<byte[]>.Failed(h.Message, h.ErrorCode);
                try { return ReadByHandle(h.Content, length); }
                finally { ReleaseHandle(h.Content); }
            }
            return AdsRead(group, offset, length);
        }

        private OperateResult WriteRaw(string address, byte[] data)
        {
            var (group, offset, isByName) = ParseAddress(address);
            if (isByName)
            {
                var h = GetHandle(address);
                if (!h.IsSuccess) return OperateResult.Failed(h.Message, h.ErrorCode);
                try { return WriteByHandle(h.Content, data); }
                finally { ReleaseHandle(h.Content); }
            }
            return AdsWrite(group, offset, data);
        }

        // ── 类型化读写 ──

        public OperateResult<bool> ReadBool(string address)
        {
            var r = ReadRaw(address, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content[0] != 0);
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var r = ReadRaw(address, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(BitConverter.ToInt16(r.Content, 0));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadRaw(address, 2);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(BitConverter.ToUInt16(r.Content, 0));
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var r = ReadRaw(address, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(BitConverter.ToInt32(r.Content, 0));
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadRaw(address, 4);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            return OperateResult<uint>.Success(BitConverter.ToUInt32(r.Content, 0));
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var r = ReadRaw(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(BitConverter.ToInt64(r.Content, 0));
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadRaw(address, 8);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ulong>.Success(BitConverter.ToUInt64(r.Content, 0));
        }

        public unsafe OperateResult<float> ReadFloat(string address)
        {
            var r = ReadRaw(address, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(BitConverter.ToSingle(r.Content, 0));
        }

        public unsafe OperateResult<double> ReadDouble(string address)
        {
            var r = ReadRaw(address, 8);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(BitConverter.ToDouble(r.Content, 0));
        }

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadRaw(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            return ReadRaw(address, length);
        }

        public OperateResult Write(string address, bool value) => WriteRaw(address, new byte[] { (byte)(value ? 1 : 0) });
        public OperateResult Write(string address, short value) => WriteRaw(address, BitConverter.GetBytes(value));
        public OperateResult Write(string address, ushort value) => WriteRaw(address, BitConverter.GetBytes(value));
        public OperateResult Write(string address, int value) => WriteRaw(address, BitConverter.GetBytes(value));
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => WriteRaw(address, BitConverter.GetBytes(value));
        public OperateResult Write(string address, ulong value) => WriteRaw(address, BitConverter.GetBytes(value));
        public unsafe OperateResult Write(string address, float value) => WriteRaw(address, BitConverter.GetBytes(value));
        public OperateResult Write(string address, double value) => WriteRaw(address, BitConverter.GetBytes(value));
        public OperateResult Write(string address, string value) => WriteRaw(address, Encoding.ASCII.GetBytes(value ?? string.Empty));
        public OperateResult Write(string address, byte[] data) => data == null ? OperateResult.Failed("写入数据不能为空") : WriteRaw(address, data);

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
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            var result = new Dictionary<string, object?>();
            foreach (string addr in addressList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = (object?)r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");

            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addressList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");

            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool v => Write(kv.Key, v),
                    short v => Write(kv.Key, v),
                    ushort v => Write(kv.Key, v),
                    int v => Write(kv.Key, v),
                    uint v => Write(kv.Key, v),
                    long v => Write(kv.Key, v),
                    ulong v => Write(kv.Key, v),
                    float v => Write(kv.Key, v),
                    double v => Write(kv.Key, v),
                    string v => Write(kv.Key, v),
                    byte[] v => Write(kv.Key, v),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <inheritdoc/>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

        // ═══════════════════════════════════════════
        //  ISubscribeDevice — 数据订阅接口
        // ═══════════════════════════════════════════

        private readonly object _monitorLock = new object();
        private readonly Dictionary<string, MonitorEntry> _monitors = new Dictionary<string, MonitorEntry>();
        private bool _monitoring;
        private Timer? _monitorTimer;

        private class MonitorEntry
        {
            public string Address = "";
            public string DataType = "Int16";
            public int IntervalMs = 1000;
            public object? LastValue;
        }

        /// <summary>数据变化事件。</summary>
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        /// <summary>订阅指定地址的数据变化。</summary>
        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        {
            lock (_monitorLock)
            {
                _monitors[address] = new MonitorEntry
                {
                    Address = address,
                    DataType = dataType,
                    IntervalMs = intervalMs,
                    LastValue = null
                };
            }
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        /// <summary>启动所有订阅。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

        /// <summary>停止所有订阅。</summary>
        public void StopSubscriptions()
        {
            _monitoring = false;
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        private void PollMonitors(object? state)
        {
            if (!_monitoring) return;
            try
            {
                List<MonitorEntry> entries;
                lock (_monitorLock) { entries = new List<MonitorEntry>(_monitors.Values); }

                foreach (var entry in entries)
                {
                    try
                    {
                        object? current = entry.DataType switch
                        {
                            "Int16" => ReadInt16(entry.Address).Content,
                            "UInt16" => ReadUInt16(entry.Address).Content,
                            "Int32" => ReadInt32(entry.Address).Content,
                            "Float" => ReadFloat(entry.Address).Content,
                            "Bool" => ReadBool(entry.Address).Content,
                            "String" => ReadString(entry.Address, 10).Content,
                            _ => null
                        };

                        if (current != null && !Equals(current, entry.LastValue))
                        {
                            if (entry.LastValue == null) { entry.LastValue = current; continue; }
                            var args = new DataChangeEventArgs
                            {
                                Address = entry.Address,
                                OldValue = entry.LastValue,
                                NewValue = current,
                                Timestamp = DateTime.Now,
                                Quality = "Good"
                            };
                            entry.LastValue = current;
                            OnDataChanged?.Invoke(this, args);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    // ═══════════════════════════════════════════
    //  ADS 数据模型
    // ═══════════════════════════════════════════

    /// <summary>ADS 设备信息。</summary>
    public sealed class AdsDeviceInfo
    {
        /// <summary>主版本号。</summary>
        public byte MajorVersion { get; set; }
        /// <summary>次版本号。</summary>
        public byte MinorVersion { get; set; }
        /// <summary>版本构建号。</summary>
        public ushort VersionBuild { get; set; }
        /// <summary>设备名称。</summary>
        public string DeviceName { get; set; } = string.Empty;

        public override string ToString() => $"{DeviceName} v{MajorVersion}.{MinorVersion}.{VersionBuild}";
    }

    /// <summary>ADS 设备状态。</summary>
    public sealed class AdsState
    {
        /// <summary>ADS 状态值 (5=Run, 6=Stop)。</summary>
        public ushort AdsStateValue { get; set; }
        /// <summary>设备状态值。</summary>
        public ushort DeviceStateValue { get; set; }

        /// <summary>PLC 是否在运行状态。</summary>
        public bool IsRunning => AdsStateValue == 5;
        /// <summary>状态的可读描述。</summary>
        public string StateName => AdsStateValue switch
        {
            0 => "Idle",
            1 => "Reset",
            2 => "Init",
            3 => "Start",
            4 => "Run (preparing)",
            5 => "Run",
            6 => "Stop",
            7 => "SaveConfig",
            8 => "LoadConfig",
            9 => "PowerFailure",
            10 => "PowerGood",
            11 => "Error",
            12 => "Shutdown",
            _ => $"Unknown ({AdsStateValue})"
        };

        public override string ToString() => $"AdsState={StateName}, DeviceState={DeviceStateValue}";
    }
}
