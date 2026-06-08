using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Yokogawa
{
    /// <summary>
    /// 横河 PLC 二进制链接协议虚拟服务器，用于单元测试。
    /// 支持字/继电器读写、随机读写、PLC 启停。
    /// </summary>
    public class YokogawaVirtualServer : IDisposable
    {
        #region 常量

        private const byte CMD_READ_RELAY = 0x01;
        private const byte CMD_WRITE_RELAY = 0x02;
        private const byte CMD_RANDOM_READ_RELAY = 0x04;
        private const byte CMD_RANDOM_WRITE_RELAY = 0x05;
        private const byte CMD_READ_WORD = 0x11;
        private const byte CMD_WRITE_WORD = 0x12;
        private const byte CMD_RANDOM_READ_WORD = 0x14;
        private const byte CMD_RANDOM_WRITE_WORD = 0x15;
        private const byte CMD_START = 0x45;
        private const byte CMD_STOP = 0x46;

        private const int RESPONSE_HEADER_LEN = 4;

        #endregion

        #region 字段

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private volatile bool _isRunning;

        /// <summary>PLC 运行状态。</summary>
        public bool IsPlcRunning { get; private set; } = true;

        /// <summary>CPU 编号（默认 1）。</summary>
        public byte CpuNumber { get; set; } = 1;

        /// <summary>监听端口。</summary>
        public int Port { get; }

        /// <summary>数据存储: dataCode → byte[]。</summary>
        private readonly Dictionary<int, byte[]> _storage = new Dictionary<int, byte[]>();

        #endregion

        #region 构造

        /// <summary>
        /// 创建虚拟服务器。
        /// </summary>
        /// <param name="port">监听端口。</param>
        public YokogawaVirtualServer(int port)
        {
            Port = port;
            InitializeStorage();
        }

        private void InitializeStorage()
        {
            // 字类型（每地址 2 字节）
            _storage[4] = new byte[10000];  // D
            _storage[2] = new byte[10000];  // B
            _storage[6] = new byte[10000];  // F
            _storage[18] = new byte[10000]; // R
            _storage[22] = new byte[10000]; // V
            _storage[26] = new byte[200];   // Z
            _storage[23] = new byte[10000]; // W
            _storage[33] = new byte[2000];  // TN
            _storage[49] = new byte[2000];  // CN

            // 位类型（每地址 1 字节，0x00 或 0x01）
            _storage[24] = new byte[2000];  // X
            _storage[25] = new byte[2000];  // Y
            _storage[9] = new byte[2000];   // I
            _storage[5] = new byte[2000];   // E
            _storage[13] = new byte[2000];  // M
            _storage[20] = new byte[2000];  // T
            _storage[3] = new byte[2000];   // C
            _storage[12] = new byte[2000];  // L
        }

        #endregion

        #region 数据设置

        /// <summary>设置字数据（大端序）。</summary>
        public void SetWord(int dataCode, int address, short value)
        {
            if (!_storage.TryGetValue(dataCode, out byte[]? store)) return;
            int byteAddr = address * 2;
            if (byteAddr + 2 > store.Length) return;
            store[byteAddr] = (byte)(value >> 8);
            store[byteAddr + 1] = (byte)value;
        }

        /// <summary>设置字数据（原始字节）。</summary>
        public void SetWordBytes(int dataCode, int address, byte[] data)
        {
            if (!_storage.TryGetValue(dataCode, out byte[]? store)) return;
            int byteAddr = address * 2;
            if (byteAddr + data.Length > store.Length) return;
            Buffer.BlockCopy(data, 0, store, byteAddr, data.Length);
        }

        /// <summary>设置继电器状态。</summary>
        public void SetRelay(int dataCode, int address, bool value)
        {
            if (!_storage.TryGetValue(dataCode, out byte[]? store)) return;
            if (address < 0 || address >= store.Length) return;
            store[address] = value ? (byte)0x01 : (byte)0x00;
        }

        /// <summary>设置 Int32 (CDAB 格式)。</summary>
        public void SetWord32(int dataCode, int address, int value)
        {
            // CDAB: [C,D,A,B] = [value>>8, value, value>>24, value>>16]
            byte[] data = new byte[]
            {
                (byte)(value >> 8),   (byte)value,
                (byte)(value >> 24),  (byte)(value >> 16)
            };
            SetWordBytes(dataCode, address, data);
        }

        /// <summary>设置 Float (CDAB 格式)。</summary>
        public void SetFloat(int dataCode, int address, float value)
        {
            byte[] intBytes = BitConverter.GetBytes(value);
            int intValue = BitConverter.ToInt32(intBytes, 0);
            SetWord32(dataCode, address, intValue);
        }

        /// <summary>设置 Int64 (CDAB 格式)。</summary>
        public void SetWord64(int dataCode, int address, long value)
        {
            byte[] data = new byte[]
            {
                (byte)(value >> 40), (byte)(value >> 32),
                (byte)(value >> 56), (byte)(value >> 48),
                (byte)(value >> 8),  (byte)value,
                (byte)(value >> 24), (byte)(value >> 16)
            };
            SetWordBytes(dataCode, address, data);
        }

        /// <summary>设置 Double (CDAB 格式)。</summary>
        public void SetDouble(int dataCode, int address, double value)
        {
            long longValue = BitConverter.DoubleToInt64Bits(value);
            SetWord64(dataCode, address, longValue);
        }

        #endregion

        #region 服务器生命周期

        /// <summary>启动虚拟服务器。</summary>
        public void Start()
        {
            if (_isRunning) return;

            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _isRunning = true;
            _acceptTask = AcceptLoop(_cts.Token);
        }

        /// <summary>停止虚拟服务器。</summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();
            _listener?.Stop();
            try { _acceptTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            _cts?.Dispose();
            _cts = null;
            _acceptTask = null;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = HandleClient(client, ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                catch (OperationCanceledException) { break; }
                catch { if (_isRunning) continue; else break; }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        // 读取 4 字节头
                        var header = await ReadExactAsync(stream, RESPONSE_HEADER_LEN, ct).ConfigureAwait(false);
                        if (header == null) break;

                        int payloadLen = header[2] * 256 + header[3];
                        byte[]? payload = payloadLen > 0
                            ? await ReadExactAsync(stream, payloadLen, ct).ConfigureAwait(false)
                            : new byte[0];
                        if (payloadLen > 0 && payload == null) break;

                        // 组合完整请求
                        byte[] request = new byte[RESPONSE_HEADER_LEN + (payload?.Length ?? 0)];
                        Buffer.BlockCopy(header, 0, request, 0, RESPONSE_HEADER_LEN);
                        if (payload != null && payload.Length > 0)
                            Buffer.BlockCopy(payload, 0, request, RESPONSE_HEADER_LEN, payload.Length);

                        // 处理命令
                        byte[] response = ProcessCommand(request);

                        // 发送响应
                        await stream.WriteAsync(response, 0, response.Length, ct).ConfigureAwait(false);
                    }
                }
            }
            catch { }
        }

        private static async Task<byte[]?> ReadExactAsync(NetworkStream ns, int count, CancellationToken ct)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await ns.ReadAsync(buf, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        #endregion

        #region 命令处理

        private byte[] ProcessCommand(byte[] request)
        {
            if (request == null || request.Length < 4)
                return BuildErrorResponse(0xFF, 2);

            byte icf = request[0];
            byte cpu = request[1];

            if (cpu != CpuNumber && cpu != 0)
                return BuildErrorResponse(icf, 0x43);

            try
            {
                switch (icf)
                {
                    case CMD_READ_WORD:
                        return ProcessReadWord(request);
                    case CMD_WRITE_WORD:
                        return ProcessWriteWord(request);
                    case CMD_READ_RELAY:
                        return ProcessReadRelay(request);
                    case CMD_WRITE_RELAY:
                        return ProcessWriteRelay(request);
                    case CMD_RANDOM_READ_WORD:
                        return ProcessRandomReadWord(request);
                    case CMD_RANDOM_WRITE_WORD:
                        return ProcessRandomWriteWord(request);
                    case CMD_RANDOM_READ_RELAY:
                        return ProcessRandomReadRelay(request);
                    case CMD_RANDOM_WRITE_RELAY:
                        return ProcessRandomWriteRelay(request);
                    case CMD_START:
                        IsPlcRunning = true;
                        return BuildSuccessResponse(CMD_START, new byte[0]);
                    case CMD_STOP:
                        IsPlcRunning = false;
                        return BuildSuccessResponse(CMD_STOP, new byte[0]);
                    default:
                        return BuildErrorResponse(icf, 1);
                }
            }
            catch
            {
                return BuildErrorResponse(icf, 7);
            }
        }

        /// <summary>
        /// 从请求中解析 6 字节地址编码。
        /// </summary>
        private static (int dataCode, int address) ParseAddress(byte[] request, int offset)
        {
            int dataCode = (request[offset] << 8) | request[offset + 1];
            int address = (request[offset + 2] << 24) | (request[offset + 3] << 16) |
                          (request[offset + 4] << 8) | request[offset + 5];
            return (dataCode, address);
        }

        #endregion

        #region 字读写

        private byte[] ProcessReadWord(byte[] request)
        {
            // 请求: [ICF, cpu, payloadLenHi, payloadLenLo, dataCode(6), countHi, countLo]
            if (request.Length < 12)
                return BuildErrorResponse(CMD_READ_WORD, 2);

            var (dataCode, address) = ParseAddress(request, 4);
            ushort count = (ushort)((request[10] << 8) | request[11]);

            if (!_storage.TryGetValue(dataCode, out byte[]? store))
                return BuildErrorResponse(CMD_READ_WORD, 5);

            int byteAddr = address * 2;
            int byteCount = count * 2;

            if (byteAddr + byteCount > store.Length)
                return BuildErrorResponse(CMD_READ_WORD, 5);

            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(store, byteAddr, data, 0, byteCount);
            return BuildSuccessResponse(CMD_READ_WORD, data);
        }

        private byte[] ProcessWriteWord(byte[] request)
        {
            // 请求: [ICF, cpu, payloadLenHi, payloadLenLo, dataCode(6), countHi, countLo, data...]
            if (request.Length < 14) // 4+6+2+2 = 最少写 1 个字
                return BuildErrorResponse(CMD_WRITE_WORD, 2);

            var (dataCode, address) = ParseAddress(request, 4);
            ushort wordCount = (ushort)((request[10] << 8) | request[11]);
            int dataLen = wordCount * 2;

            if (request.Length < 12 + dataLen)
                return BuildErrorResponse(CMD_WRITE_WORD, 4);

            if (!_storage.TryGetValue(dataCode, out byte[]? store))
                return BuildErrorResponse(CMD_WRITE_WORD, 5);

            int byteAddr = address * 2;
            if (byteAddr + dataLen > store.Length)
                return BuildErrorResponse(CMD_WRITE_WORD, 5);

            Buffer.BlockCopy(request, 12, store, byteAddr, dataLen);
            return BuildSuccessResponse(CMD_WRITE_WORD, new byte[0]);
        }

        #endregion

        #region 继电器读写

        private byte[] ProcessReadRelay(byte[] request)
        {
            // 请求: [ICF, cpu, payloadLenHi, payloadLenLo, dataCode(6), countHi, countLo]
            if (request.Length < 12)
                return BuildErrorResponse(CMD_READ_RELAY, 2);

            var (dataCode, address) = ParseAddress(request, 4);
            ushort count = (ushort)((request[10] << 8) | request[11]);

            if (!_storage.TryGetValue(dataCode, out byte[]? store))
                return BuildErrorResponse(CMD_READ_RELAY, 5);

            if (address + count > store.Length)
                return BuildErrorResponse(CMD_READ_RELAY, 5);

            byte[] data = new byte[count];
            Buffer.BlockCopy(store, address, data, 0, count);
            return BuildSuccessResponse(CMD_READ_RELAY, data);
        }

        private byte[] ProcessWriteRelay(byte[] request)
        {
            // 请求: [ICF, cpu, payloadLenHi, payloadLenLo, dataCode(6), countHi, countLo, bits...]
            if (request.Length < 13) // 4+6+2+1 = 最少写 1 个继电器
                return BuildErrorResponse(CMD_WRITE_RELAY, 2);

            var (dataCode, address) = ParseAddress(request, 4);
            ushort count = (ushort)((request[10] << 8) | request[11]);

            if (request.Length < 12 + count)
                return BuildErrorResponse(CMD_WRITE_RELAY, 4);

            if (!_storage.TryGetValue(dataCode, out byte[]? store))
                return BuildErrorResponse(CMD_WRITE_RELAY, 5);

            if (address + count > store.Length)
                return BuildErrorResponse(CMD_WRITE_RELAY, 5);

            Buffer.BlockCopy(request, 12, store, address, count);
            return BuildSuccessResponse(CMD_WRITE_RELAY, new byte[0]);
        }

        #endregion

        #region 随机字读写

        private byte[] ProcessRandomReadWord(byte[] request)
        {
            // 请求: [ICF, cpu, payloadLenHi, payloadLenLo, countHi, countLo, addr1(6), addr2(6), ...]
            if (request.Length < 6)
                return BuildErrorResponse(CMD_RANDOM_READ_WORD, 2);

            ushort count = (ushort)((request[4] << 8) | request[5]);
            if (request.Length < 6 + count * 6)
                return BuildErrorResponse(CMD_RANDOM_READ_WORD, 3);

            byte[] data = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                var (dataCode, address) = ParseAddress(request, 6 + i * 6);

                if (!_storage.TryGetValue(dataCode, out byte[]? store))
                    return BuildErrorResponse(CMD_RANDOM_READ_WORD, 5);

                int byteAddr = address * 2;
                if (byteAddr + 2 > store.Length)
                    return BuildErrorResponse(CMD_RANDOM_READ_WORD, 5);

                data[i * 2] = store[byteAddr];
                data[i * 2 + 1] = store[byteAddr + 1];
            }
            return BuildSuccessResponse(CMD_RANDOM_READ_WORD, data);
        }

        private byte[] ProcessRandomWriteWord(byte[] request)
        {
            // 请求: [ICF, cpu, payloadLenHi, payloadLenLo, countHi, countLo, [addr(6)+data(2)]...]
            if (request.Length < 6)
                return BuildErrorResponse(CMD_RANDOM_WRITE_WORD, 2);

            ushort count = (ushort)((request[4] << 8) | request[5]);
            if (request.Length < 6 + count * 8)
                return BuildErrorResponse(CMD_RANDOM_WRITE_WORD, 3);

            for (int i = 0; i < count; i++)
            {
                int entryOffset = 6 + i * 8;
                var (dataCode, address) = ParseAddress(request, entryOffset);

                if (!_storage.TryGetValue(dataCode, out byte[]? store))
                    return BuildErrorResponse(CMD_RANDOM_WRITE_WORD, 5);

                int byteAddr = address * 2;
                if (byteAddr + 2 > store.Length)
                    return BuildErrorResponse(CMD_RANDOM_WRITE_WORD, 5);

                store[byteAddr] = request[entryOffset + 6];
                store[byteAddr + 1] = request[entryOffset + 7];
            }
            return BuildSuccessResponse(CMD_RANDOM_WRITE_WORD, new byte[0]);
        }

        #endregion

        #region 随机继电器读写

        private byte[] ProcessRandomReadRelay(byte[] request)
        {
            if (request.Length < 6)
                return BuildErrorResponse(CMD_RANDOM_READ_RELAY, 2);

            ushort count = (ushort)((request[4] << 8) | request[5]);
            if (request.Length < 6 + count * 6)
                return BuildErrorResponse(CMD_RANDOM_READ_RELAY, 3);

            byte[] data = new byte[count];
            for (int i = 0; i < count; i++)
            {
                var (dataCode, address) = ParseAddress(request, 6 + i * 6);

                if (!_storage.TryGetValue(dataCode, out byte[]? store))
                    return BuildErrorResponse(CMD_RANDOM_READ_RELAY, 5);

                if (address < 0 || address >= store.Length)
                    return BuildErrorResponse(CMD_RANDOM_READ_RELAY, 5);

                data[i] = store[address];
            }
            return BuildSuccessResponse(CMD_RANDOM_READ_RELAY, data);
        }

        private byte[] ProcessRandomWriteRelay(byte[] request)
        {
            if (request.Length < 6)
                return BuildErrorResponse(CMD_RANDOM_WRITE_RELAY, 2);

            ushort count = (ushort)((request[4] << 8) | request[5]);
            if (request.Length < 6 + count * 7)
                return BuildErrorResponse(CMD_RANDOM_WRITE_RELAY, 3);

            for (int i = 0; i < count; i++)
            {
                int entryOffset = 6 + i * 7;
                var (dataCode, address) = ParseAddress(request, entryOffset);

                if (!_storage.TryGetValue(dataCode, out byte[]? store))
                    return BuildErrorResponse(CMD_RANDOM_WRITE_RELAY, 5);

                if (address < 0 || address >= store.Length)
                    return BuildErrorResponse(CMD_RANDOM_WRITE_RELAY, 5);

                store[address] = request[entryOffset + 6];
            }
            return BuildSuccessResponse(CMD_RANDOM_WRITE_RELAY, new byte[0]);
        }

        #endregion

        #region 响应构建

        /// <summary>
        /// 构建成功响应。
        /// 响应: [ICF, cpu, payloadLenHi, payloadLenLo, cmdEcho, 0x00, 0x00, 0x00, data...]
        /// </summary>
        private byte[] BuildSuccessResponse(byte icf, byte[] data)
        {
            int payloadLen = 4 + (data?.Length ?? 0);
            byte[] response = new byte[RESPONSE_HEADER_LEN + payloadLen];
            response[0] = icf;
            response[1] = CpuNumber;
            response[2] = (byte)((payloadLen >> 8) & 0xFF);
            response[3] = (byte)(payloadLen & 0xFF);
            response[4] = icf;  // cmdEcho
            response[5] = 0x00; // errorCode = success
            response[6] = 0x00; // reserved
            response[7] = 0x00; // reserved
            if (data != null && data.Length > 0)
                Buffer.BlockCopy(data, 0, response, 8, data.Length);
            return response;
        }

        /// <summary>
        /// 构建错误响应。
        /// </summary>
        private byte[] BuildErrorResponse(byte icf, byte errorCode)
        {
            byte[] response = new byte[8];
            response[0] = icf;
            response[1] = CpuNumber;
            response[2] = 0x00;
            response[3] = 0x04; // payloadLen = 4
            response[4] = icf;  // cmdEcho
            response[5] = errorCode;
            response[6] = 0x00;
            response[7] = 0x00;
            return response;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Stop();
        }

        #endregion
    }
}
