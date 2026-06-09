using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// Omron FINS Serial 协议客户端 — 支持 CJ2M/CJ2H/CP1H/CP1L/NJ/NX 串口通讯。
    /// <para>FINS Serial 帧格式: [ICF(1) + RSV(1) + GCT(1) + DNA(1) + DA1(1) + DA2(1) + SNA(1) + SA1(1) + SA2(1) + SID(1)] + [MRC(1) + SRC(1)] + Data</para>
    /// </summary>
    public class FinsSerialClient : IReadWriteDevice
    {
        private readonly Stream _stream;
        private readonly object _lock = new object();
        protected ILogger Log { get; set; }

        /// <summary>目标网络地址。</summary>
        public byte DestNetwork { get; set; } = 0;
        /// <summary>目标节点号。</summary>
        public byte DestNode { get; set; } = 1;
        /// <summary>目标单元号 (0=CPU, 0xFE=系统)。</summary>
        public byte DestUnit { get; set; } = 0;
        /// <summary>源网络地址。</summary>
        public byte SrcNetwork { get; set; } = 0;
        /// <summary>源节点号。</summary>
        public byte SrcNode { get; set; } = 0;
        /// <summary>源单元号。</summary>
        public byte SrcUnit { get; set; } = 0;
        /// <summary>服务 ID。</summary>
        public byte ServiceId { get; set; } = 0;
        /// <summary>超时时间（毫秒）。</summary>
        public int Timeout { get; set; } = 5000;

        public bool IsConnected => _stream?.CanRead == true && _stream?.CanWrite == true;

        public FinsSerialClient(Stream stream, byte destNode = 1)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            DestNode = destNode;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  FINS 帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建 FINS 命令帧头（10 字节）。</summary>
        private byte[] BuildFinsHeader(byte mrc, byte src)
        {
            byte sid = unchecked(++ServiceId);
            return new byte[]
            {
                0x80, // ICF: 信息帧
                0x00, // RSV: 保留
                0x02, // GCT: 网关计数
                DestNetwork,
                DestNode,
                DestUnit,
                SrcNetwork,
                SrcNode,
                SrcUnit,
                sid,
                mrc, src
            };
        }

        /// <summary>构建内存区域读取命令帧。</summary>
        public byte[] BuildReadCommand(FinsMemoryArea area, ushort address, byte bitOffset, ushort count)
        {
            byte[] header = BuildFinsHeader(0x01, 0x01);
            byte[] cmd = new byte[header.Length + 6];
            Buffer.BlockCopy(header, 0, cmd, 0, header.Length);
            cmd[header.Length] = (byte)area;
            cmd[header.Length + 1] = (byte)(address >> 8);
            cmd[header.Length + 2] = (byte)address;
            cmd[header.Length + 3] = bitOffset;
            cmd[header.Length + 4] = (byte)(count >> 8);
            cmd[header.Length + 5] = (byte)count;
            return cmd;
        }

        /// <summary>构建内存区域写入命令帧。</summary>
        public byte[] BuildWriteCommand(FinsMemoryArea area, ushort address, byte bitOffset, byte[] data)
        {
            byte[] header = BuildFinsHeader(0x01, 0x02);
            ushort wordCount = (ushort)(data.Length / 2);
            byte[] cmd = new byte[header.Length + 6 + data.Length];
            Buffer.BlockCopy(header, 0, cmd, 0, header.Length);
            int offset = header.Length;
            cmd[offset] = (byte)area;
            cmd[offset + 1] = (byte)(address >> 8);
            cmd[offset + 2] = (byte)address;
            cmd[offset + 3] = bitOffset;
            cmd[offset + 4] = (byte)(wordCount >> 8);
            cmd[offset + 5] = (byte)wordCount;
            Buffer.BlockCopy(data, 0, cmd, offset + 6, data.Length);
            return cmd;
        }

        // ═══════════════════════════════════════════
        //  串口收发
        // ═══════════════════════════════════════════

        private byte[] SendAndReceiveFrame(byte[] command)
        {
            lock (_lock)
            {
                _stream.Write(command, 0, command.Length);
                _stream.Flush();

                // 读取响应头 (12 字节: ICF+RSV+GCT+DNA+DA1+DA2+SNA+SA1+SA2+SID+MRC+SRC)
                byte[] header = new byte[12];
                int read = ReadExact(header, 0, 12);
                if (read < 12) throw new IOException("FINS 响应头读取不完整");

                // 检查是否为响应帧 (ICF 最高位为 1 表示响应)
                if ((header[0] & 0x40) == 0)
                    throw new IOException("收到的不是 FINS 响应帧");

                // 读取结束码 (2 字节)
                byte[] endCodeBuf = new byte[2];
                read = ReadExact(endCodeBuf, 0, 2);
                if (read < 2) throw new IOException("FINS 结束码读取不完整");

                ushort endCode = (ushort)((endCodeBuf[0] << 8) | endCodeBuf[1]);
                if (endCode != 0x0000)
                    throw new IOException($"FINS 错误: {FinsEndCode.ToMessage(endCode)}");

                // 计算剩余数据长度
                // 对于内存区域读取，MRC=0x01, SRC=0x01
                // 数据长度 = 命令中请求的字数 * 2
                // 由于串口帧没有显式长度字段，读取到超时为止
                using (var ms = new MemoryStream())
                {
                    ms.Write(header, 0, header.Length);
                    ms.Write(endCodeBuf, 0, endCodeBuf.Length);

                    // 尝试读取剩余数据
                    byte[] buf = new byte[1024];
                    int deadline = Environment.TickCount + 500; // 500ms 额外读取窗口
                    while (Environment.TickCount < deadline)
                    {
                        try
                        {
                            int available = _stream.ReadByte();
                            if (available < 0) break;
                            ms.WriteByte((byte)available);
                        }
                        catch (TimeoutException) { break; }
                    }

                    return ms.ToArray();
                }
            }
        }

        private int ReadExact(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            int remaining = count;
            while (remaining > 0)
            {
                int read = _stream.Read(buffer, offset + totalRead, remaining);
                if (read == 0) break;
                totalRead += read;
                remaining -= read;
            }
            return totalRead;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            try
            {
                var parser = new FinsAddressParser();
                FinsAddress finsAddr = parser.Parse(address);
                byte bitOff = finsAddr.BitOffset >= 0 ? (byte)finsAddr.BitOffset : (byte)0;
                byte[] cmd = BuildReadCommand(finsAddr.Area, finsAddr.WordAddress, bitOff, length);
                byte[] response = SendAndReceiveFrame(cmd);

                // 提取数据部分 (跳过 12 字节帧头 + 2 字节结束码)
                if (response.Length < 14)
                    return OperateResult<byte[]>.Failed("FINS 响应数据不足");

                byte[] data = new byte[response.Length - 14];
                Buffer.BlockCopy(response, 14, data, 0, data.Length);
                return OperateResult<byte[]>.Success(data);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        public OperateResult Write(string address, byte[] data)
        {
            try
            {
                var parser = new FinsAddressParser();
                FinsAddress finsAddr = parser.Parse(address);
                byte bitOff = finsAddr.BitOffset >= 0 ? (byte)finsAddr.BitOffset : (byte)0;
                byte[] cmd = BuildWriteCommand(finsAddr.Area, finsAddr.WordAddress, bitOff, data);
                SendAndReceiveFrame(cmd);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        public OperateResult<bool> ReadBool(string address) => ReadBytesSafe<bool>(address, 1,
            (data) => (data[0] & 0x01) != 0);

        public OperateResult<short> ReadInt16(string address) => ReadBytesSafe<short>(address, 1,
            (data) => (short)((data[0] << 8) | data[1]));

        public OperateResult<ushort> ReadUInt16(string address) => ReadBytesSafe<ushort>(address, 1,
            (data) => (ushort)((data[0] << 8) | data[1]));

        public OperateResult<int> ReadInt32(string address) => ReadBytesSafe<int>(address, 2,
            (data) => (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);

        public OperateResult<uint> ReadUInt32(string address) => ReadBytesSafe<uint>(address, 2,
            (data) => (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]));

        public OperateResult<long> ReadInt64(string address) => ReadBytesSafe<long>(address, 4,
            (data) => (long)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]) << 32 |
                      (long)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]));

        public OperateResult<ulong> ReadUInt64(string address) => ReadBytesSafe<ulong>(address, 4,
            (data) => (ulong)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]) << 32 |
                      (ulong)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]));

        public OperateResult<float> ReadFloat(string address) => ReadBytesSafe<float>(address, 2,
            (data) =>
            {
                int bits = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                unsafe { return *(float*)&bits; }
            });

        public OperateResult<double> ReadDouble(string address) => ReadBytesSafe<double>(address, 4,
            (data) =>
            {
                long bits = (long)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]) << 32 |
                            (long)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
                unsafe { return *(double*)&bits; }
            });

        public OperateResult<string> ReadString(string address, ushort length) => ReadBytesSafe<string>(address, length,
            (data) => Encoding.ASCII.GetString(data).TrimEnd('\0'));

        public OperateResult Write(string address, bool value) => Write(address, new byte[] { (byte)(value ? 1 : 0) });
        public OperateResult Write(string address, short value) => Write(address, new byte[] { (byte)(value >> 8), (byte)value });
        public OperateResult Write(string address, int value) => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        public OperateResult Write(string address, float value) { unsafe { int bits = *(int*)&value; return Write(address, bits); } }
        public OperateResult Write(string address, double value) { unsafe { long bits = (long)(*(double*)&value); return Write(address, new byte[] { (byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32), (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits }); } }
        public OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));
        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, (int)value);
        public OperateResult Write(string address, ulong value) => Write(address, (int)value);

        private OperateResult<T> ReadBytesSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  连接管理（串口已预连接）
        // ═══════════════════════════════════════════

        public OperateResult Connect() => OperateResult.Success();
        public Task<OperateResult> ConnectAsync() => Task.FromResult(OperateResult.Success());
        public void Disconnect() { }

        // ═══════════════════════════════════════════
        //  Async 方法 (Task.Run 包装)
        // ═══════════════════════════════════════════

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

        public void Dispose() { GC.SuppressFinalize(this); }
    }
}
