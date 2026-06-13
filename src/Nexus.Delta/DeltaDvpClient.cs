using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Delta
{
    /// <summary>
    /// 台达 DVP/AS 系列 PLC 通讯客户端 — 基于 Modbus RTU/ASCII 协议。
    /// <para>台达 DVP 系列 PLC 原生支持 Modbus RTU，地址映射为标准 Modbus 格式。</para>
    /// <para>地址映射: Y0=0x0000, X0=0x0400, T0=0x0600, C0=0x0800, D0=0x1000, T0(Timer当前值)=0x1800</para>
    /// <para>对标 HSL: DeltaDvp — Read/Write D/T/C/Y/X/M 寄存器, ReadBools/WriteBools, 大块分割</para>
    /// </summary>
    public class DeltaDvpClient : IReadWriteDevice, IBatchReadWrite, ISubscribeDevice
    {
        private readonly Stream _stream;
        private readonly object _lock = new object();
        protected ILogger Log { get; set; }

        /// <summary>站号。</summary>
        public byte Station { get; set; }
        /// <summary>字节序。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        /// <summary>超时（毫秒）。</summary>
        public int Timeout { get; set; }

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _stream?.CanRead == true && _stream?.CanWrite == true;

        public DeltaDvpClient(Stream stream, byte station = 1, int timeout = 5000)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Station = station;
            Timeout = timeout;
            if (_stream.CanTimeout)
            {
                _stream.ReadTimeout = timeout;
                _stream.WriteTimeout = timeout;
            }
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  台达地址映射
        // ═══════════════════════════════════════════

        /// <summary>
        /// 解析台达地址: "D100", "Y0", "X10", "T0", "C0", "M100"
        /// 返回 Modbus 地址和功能码。
        /// </summary>
        private static (ushort address, byte readFc, byte writeFc) ParseDeltaAddress(string address)
        {
            var parsed = DeltaDvpAddress.Parse(address);
            return (parsed.Address, parsed.ReadFunctionCode, parsed.WriteFunctionCode);
        }

        // ═══════════════════════════════════════════
        //  Modbus RTU 帧收发
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendReceive(byte[] pdu)
        {
            lock (_lock)
            {
                // Build RTU frame: Station + PDU + CRC16
                byte[] frame = new byte[1 + pdu.Length + 2];
                frame[0] = Station;
                Buffer.BlockCopy(pdu, 0, frame, 1, pdu.Length);
                ushort crc = Crc16(frame, 0, frame.Length - 2);
                frame[frame.Length - 2] = (byte)(crc & 0xFF);
                frame[frame.Length - 1] = (byte)((crc >> 8) & 0xFF);

                OnMessageSent?.Invoke(this, BitConverter.ToString(frame));
                _stream.Write(frame, 0, frame.Length);

                // 读取响应
                var r = ReadRtuResponse();
                if (r.IsSuccess)
                    OnMessageReceived?.Invoke(this, BitConverter.ToString(r.Content));
                else
                    OnError?.Invoke(this, r.Message);

                return r;
            }
        }

        private OperateResult<byte[]> ReadRtuResponse()
        {
            try
            {
                byte[] header = new byte[2];
                ReadExact(header, 0, 2);

                if (header[0] != Station)
                    return OperateResult<byte[]>.Failed($"站号不匹配: 期望 {Station}, 实际 {header[0]}");

                byte fc = header[1];
                if ((fc & 0x80) != 0)
                {
                    byte[] rest = new byte[3]; // exception code + CRC
                    ReadExact(rest, 0, rest.Length);
                    var crcCheck = VerifyCrc(header, rest);
                    if (!crcCheck.IsSuccess) return crcCheck;
                    byte exCode = rest[0];
                    return OperateResult<byte[]>.Failed($"Delta Modbus 异常: 0x{exCode:X2}", exCode);
                }

                if (fc == 0x01 || fc == 0x02 || fc == 0x03 || fc == 0x04)
                {
                    byte[] countBuf = new byte[1];
                    ReadExact(countBuf, 0, countBuf.Length);
                    byte byteCount = countBuf[0];
                    byte[] dataAndCrc = new byte[byteCount + 2];
                    ReadExact(dataAndCrc, 0, dataAndCrc.Length);

                    byte[] rest = new byte[1 + dataAndCrc.Length];
                    rest[0] = byteCount;
                    Buffer.BlockCopy(dataAndCrc, 0, rest, 1, dataAndCrc.Length);
                    var crcCheck = VerifyCrc(header, rest);
                    if (!crcCheck.IsSuccess) return crcCheck;

                    byte[] result = new byte[byteCount];
                    Buffer.BlockCopy(dataAndCrc, 0, result, 0, byteCount);
                    return OperateResult<byte[]>.Success(result);
                }

                if (fc == 0x05 || fc == 0x06 || fc == 0x0F || fc == 0x10)
                {
                    byte[] rest = new byte[6]; // addr(2) + value/count(2) + CRC(2)
                    ReadExact(rest, 0, rest.Length);
                    var crcCheck = VerifyCrc(header, rest);
                    if (!crcCheck.IsSuccess) return crcCheck;

                    byte[] result = new byte[4];
                    Buffer.BlockCopy(rest, 0, result, 0, result.Length);
                    return OperateResult<byte[]>.Success(result);
                }

                return OperateResult<byte[]>.Failed($"未知功能码: 0x{fc:X2}");
            }
            catch (TimeoutException ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
            catch (IOException ex)
            {
                return OperateResult<byte[]>.Failed($"读取响应失败: {ex.Message}");
            }
        }

        private static OperateResult<byte[]> VerifyCrc(byte[] header, byte[] rest)
        {
            byte[] frame = new byte[header.Length + rest.Length];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            Buffer.BlockCopy(rest, 0, frame, header.Length, rest.Length);

            if (frame.Length < 4)
                return OperateResult<byte[]>.Failed("响应长度不足，无法校验 CRC");

            ushort expected = Crc16(frame, 0, frame.Length - 2);
            ushort actual = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            if (expected != actual)
                return OperateResult<byte[]>.Failed($"CRC 校验失败: 期望 0x{expected:X4}, 实际 0x{actual:X4}");

            return OperateResult<byte[]>.Success(Array.Empty<byte>());
        }

        private static OperateResult EnsureLength(byte[] data, int expectedLength)
        {
            if (data.Length < expectedLength)
                return OperateResult.Failed($"响应数据不足: 期望 {expectedLength} 字节, 实际 {data.Length} 字节");
            return OperateResult.Success();
        }

        private void ReadExact(byte[] buffer, int offset, int count)
        {
            int deadline = Environment.TickCount + Timeout;
            while (count > 0 && Environment.TickCount <= deadline)
            {
                int n;
                try
                {
                    n = _stream.Read(buffer, offset, count);
                }
                catch (IOException ex)
                {
                    throw new TimeoutException("读取超时", ex);
                }

                if (n <= 0) throw new TimeoutException("读取超时");
                offset += n;
                count -= n;
            }

            if (count > 0)
                throw new TimeoutException("读取超时");
        }

        private static ushort Crc16(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0) { crc >>= 1; crc ^= 0xA001; }
                    else { crc >>= 1; }
                }
            }
            return crc;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 类型化读写
        // ═══════════════════════════════════════════

        public OperateResult<bool> ReadBool(string address)
        {
            var (addr, readFc, _) = ParseDeltaAddress(address);
            byte[] pdu = { readFc, (byte)(addr >> 8), (byte)addr, 0, 1 };
            var r = SendReceive(pdu);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            var length = EnsureLength(r.Content, 1);
            if (!length.IsSuccess) return OperateResult<bool>.Failed(length.Message, length.ErrorCode);
            return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0);
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var (addr, readFc, _) = ParseDeltaAddress(address);
            byte[] pdu = { readFc, (byte)(addr >> 8), (byte)addr, 0, 1 };
            var r = SendReceive(pdu);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            var length = EnsureLength(r.Content, 2);
            if (!length.IsSuccess) return OperateResult<short>.Failed(length.Message, length.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0, ByteOrder));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var (addr, readFc, _) = ParseDeltaAddress(address);
            byte[] pdu = { readFc, (byte)(addr >> 8), (byte)addr, 0, 2 };
            var r = SendReceive(pdu);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            var length = EnsureLength(r.Content, 4);
            if (!length.IsSuccess) return OperateResult<int>.Failed(length.Message, length.ErrorCode);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0, ByteOrder));
        }

        public OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<long> ReadInt64(string address)
        {
            var (addr, readFc, _) = ParseDeltaAddress(address);
            byte[] pdu = { readFc, (byte)(addr >> 8), (byte)addr, 0, 4 };
            var r = SendReceive(pdu);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            var length = EnsureLength(r.Content, 8);
            if (!length.IsSuccess) return OperateResult<long>.Failed(length.Message, length.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0, ByteOrder));
        }

        public OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public unsafe OperateResult<float> ReadFloat(string address) { var r = ReadInt32(address); if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode); int v = r.Content; return OperateResult<float>.Success(*(float*)&v); }
        public OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(DataConverter.GetBytes(r.Content), 0));
        }

        public OperateResult<string> ReadString(string address, ushort length) { var r = ReadBytes(address, length); if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode); return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content).TrimEnd('\0')); }
        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (a, fc, _) = ParseDeltaAddress(address);
            ushort cnt = (ushort)((length + 1) / 2);
            var r = SendReceive(new byte[] { fc, (byte)(a >> 8), (byte)a, (byte)(cnt >> 8), (byte)cnt });
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            var lengthCheck = EnsureLength(r.Content, length);
            if (!lengthCheck.IsSuccess) return OperateResult<byte[]>.Failed(lengthCheck.Message, lengthCheck.ErrorCode);
            byte[] result = new byte[length];
            Buffer.BlockCopy(r.Content, 0, result, 0, length);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 写入 ──
        public OperateResult Write(string address, bool value)
        {
            var (a, _, wfc) = ParseDeltaAddress(address);
            if (wfc == 0) return OperateResult.Failed($"地址 {address} 为只读区域");
            var r = SendReceive(new byte[] { wfc, (byte)(a >> 8), (byte)a, (byte)(value ? 0xFF : 0x00), 0x00 });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, short value)
        {
            var (a, _, _) = ParseDeltaAddress(address);
            var vb = DataConverter.GetBytes(value, ByteOrder);
            var r = SendReceive(new byte[] { 0x06, (byte)(a >> 8), (byte)a, vb[0], vb[1] });
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) { var (a, _, _) = ParseDeltaAddress(address); var vb = DataConverter.GetBytes(value, ByteOrder); byte[] pdu = new byte[10]; pdu[0] = 0x10; pdu[1] = (byte)(a >> 8); pdu[2] = (byte)a; pdu[3] = 0; pdu[4] = 2; pdu[5] = 4; Buffer.BlockCopy(vb, 0, pdu, 6, 4); var r = SendReceive(pdu); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode); }
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) { var (a, _, _) = ParseDeltaAddress(address); var vb = DataConverter.GetBytes(value, ByteOrder); byte[] pdu = new byte[14]; pdu[0] = 0x10; pdu[1] = (byte)(a >> 8); pdu[2] = (byte)a; pdu[3] = 0; pdu[4] = 4; pdu[5] = 8; Buffer.BlockCopy(vb, 0, pdu, 6, 8); var r = SendReceive(pdu); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode); }
        public OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public unsafe OperateResult Write(string address, float value) => Write(address, *(int*)&value);
        public OperateResult Write(string address, double value) => Write(address, DataConverter.ToInt64(DataConverter.GetBytes(value), 0));
        public OperateResult Write(string address, string value) => Write(address, System.Text.Encoding.ASCII.GetBytes(value ?? ""));
        public OperateResult Write(string address, byte[] data) { if (data == null) return OperateResult.Failed("写入数据不能为空"); var (a, _, _) = ParseDeltaAddress(address); if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1); ushort cnt = (ushort)(data.Length / 2); byte[] pdu = new byte[6 + data.Length]; pdu[0] = 0x10; pdu[1] = (byte)(a >> 8); pdu[2] = (byte)a; pdu[3] = (byte)(cnt >> 8); pdu[4] = (byte)cnt; pdu[5] = (byte)data.Length; Buffer.BlockCopy(data, 0, pdu, 6, data.Length); var r = SendReceive(pdu); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode); }

        // ═══════════════════════════════════════════
        //  批量位操作 — ReadBools / WriteBools
        // ═══════════════════════════════════════════

        /// <summary>
        /// 批量读取位地址（FC01/FC02），支持 Y/X/M/T/C 区域，自动分包（每包最多 1968 位）。
        /// </summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            if (count == 0) return OperateResult<bool[]>.Success(Array.Empty<bool>());
            if (count == 1) { var r = ReadBool(address); return r.IsSuccess ? OperateResult<bool[]>.Success(new[] { r.Content }) : OperateResult<bool[]>.Failed(r.Message, r.ErrorCode); }

            try
            {
                var (addr, readFc, _) = ParseDeltaAddress(address);
                const int maxPerRequest = 1968;
                var result = new bool[count];
                int offset = 0;

                while (offset < count)
                {
                    int batch = Math.Min(count - offset, maxPerRequest);
                    ushort batchAddr = (ushort)(addr + offset);
                    byte[] pdu = { readFc, (byte)(batchAddr >> 8), (byte)batchAddr, (byte)(batch >> 8), (byte)(batch & 0xFF) };
                    var r = SendReceive(pdu);
                    if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message, r.ErrorCode);
                    int expectedBytes = (batch + 7) / 8;
                    if (r.Content.Length < expectedBytes)
                        return OperateResult<bool[]>.Failed($"响应数据不足: 期望 {expectedBytes} 字节, 实际 {r.Content.Length} 字节");

                    for (int i = 0; i < batch; i++)
                    {
                        int byteIdx = i / 8;
                        int bitIdx = i % 8;
                        result[offset + i] = (r.Content[byteIdx] & (1 << bitIdx)) != 0;
                    }
                    offset += batch;
                }

                return OperateResult<bool[]>.Success(result);
            }
            catch (Exception ex) { return OperateResult<bool[]>.Failed(ex.Message); }
        }

        /// <summary>
        /// 批量写入位地址（FC0F Write Multiple Coils），自动分包（每包最多 1968 位）。
        /// </summary>
        public OperateResult WriteBools(string address, bool[] values)
        {
            if (values == null || values.Length == 0) return OperateResult.Success();
            if (values.Length == 1) return Write(address, values[0]);

            try
            {
                var (addr, _, _) = ParseDeltaAddress(address);
                const int maxPerRequest = 1968;
                int offset = 0;

                while (offset < values.Length)
                {
                    int batch = Math.Min(values.Length - offset, maxPerRequest);
                    int byteCount = (batch + 7) / 8;
                    byte[] bytes = new byte[byteCount];
                    for (int i = 0; i < batch; i++) { if (values[offset + i]) bytes[i / 8] |= (byte)(1 << (i % 8)); }

                    ushort batchAddr = (ushort)(addr + offset);
                    byte[] pdu = new byte[6 + byteCount];
                    pdu[0] = 0x0F;
                    pdu[1] = (byte)(batchAddr >> 8); pdu[2] = (byte)batchAddr;
                    pdu[3] = (byte)(batch >> 8); pdu[4] = (byte)(batch & 0xFF);
                    pdu[5] = (byte)byteCount;
                    Buffer.BlockCopy(bytes, 0, pdu, 6, byteCount);

                    var r = SendReceive(pdu);
                    if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
                    offset += batch;
                }
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  大块分割读写
        // ═══════════════════════════════════════════

        /// <summary>大块读取寄存器（自动分包，每包最多 125 个寄存器）。</summary>
        public OperateResult<byte[]> ReadBytesLarge(string address, ushort length)
        {
            if (length == 0) return OperateResult<byte[]>.Success(Array.Empty<byte>());
            try
            {
                var (addr, fc, _) = ParseDeltaAddress(address);
                const int maxRegisters = 125;
                var result = new byte[length * 2];
                int wordOffset = 0;
                int remaining = length;

                while (remaining > 0)
                {
                    int batch = Math.Min(remaining, maxRegisters);
                    ushort batchAddr = (ushort)(addr + wordOffset);
                    byte[] pdu = { fc, (byte)(batchAddr >> 8), (byte)batchAddr, (byte)(batch >> 8), (byte)(batch & 0xFF) };
                    var r = SendReceive(pdu);
                    if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
                    int expectedBytes = batch * 2;
                    if (r.Content.Length < expectedBytes)
                        return OperateResult<byte[]>.Failed($"响应数据不足: 期望 {expectedBytes} 字节, 实际 {r.Content.Length} 字节");
                    Buffer.BlockCopy(r.Content, 0, result, wordOffset * 2, expectedBytes);
                    wordOffset += batch;
                    remaining -= batch;
                }
                return OperateResult<byte[]>.Success(result);
            }
            catch (Exception ex) { return OperateResult<byte[]>.Failed(ex.Message); }
        }

        /// <summary>大块写入寄存器（自动分包，每包最多 123 个寄存器）。</summary>
        public OperateResult WriteBytesLarge(string address, byte[] data)
        {
            if (data == null || data.Length == 0) return OperateResult.Success();
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            try
            {
                var (addr, _, _) = ParseDeltaAddress(address);
                const int maxRegisters = 123;
                int totalWords = data.Length / 2;
                int wordOffset = 0;

                while (wordOffset < totalWords)
                {
                    int batchWords = Math.Min(totalWords - wordOffset, maxRegisters);
                    int batchBytes = batchWords * 2;
                    ushort batchAddr = (ushort)(addr + wordOffset);
                    byte[] pdu = new byte[6 + batchBytes];
                    pdu[0] = 0x10;
                    pdu[1] = (byte)(batchAddr >> 8); pdu[2] = (byte)batchAddr;
                    pdu[3] = (byte)(batchWords >> 8); pdu[4] = (byte)(batchWords & 0xFF);
                    pdu[5] = (byte)batchBytes;
                    Buffer.BlockCopy(data, wordOffset * 2, pdu, 6, batchBytes);
                    var r = SendReceive(pdu);
                    if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
                    wordOffset += batchWords;
                }
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>读取 PLC 型号信息（D1121-D1130 寄存器）。</summary>
        public OperateResult<string> ReadPlcModel()
        {
            try
            {
                // DVP 型号字符串在 D1121 (Modbus 地址 0x1000 + 1121 = 0x1461)
                ushort strAddr = 0x1461;
                byte[] pdu = { 0x03, (byte)(strAddr >> 8), (byte)strAddr, 0x00, 0x0A };
                var r = SendReceive(pdu);
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
                string model = System.Text.Encoding.ASCII.GetString(r.Content).TrimEnd('\0', ' ');
                return OperateResult<string>.Success(string.IsNullOrEmpty(model) ? "Unknown DVP" : model);
            }
            catch (Exception ex) { return OperateResult<string>.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  连接
        // ═══════════════════════════════════════════
        public OperateResult Connect() { if (_stream.CanRead && _stream.CanWrite) { OnConnected?.Invoke(this, EventArgs.Empty); return OperateResult.Success(); } return OperateResult.Failed("Stream 不可读写"); }
        public Task<OperateResult> ConnectAsync() => Task.FromResult(Connect());
        public void Disconnect() { try { _stream.Close(); } catch { } OnDisconnected?.Invoke(this, EventArgs.Empty); }
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (disposing) try { _stream?.Close(); } catch { } }

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

        /// <summary>批量读取位（异步）。</summary>
        public Task<OperateResult<bool[]>> ReadBoolsAsync(string address, ushort count) => Task.Run(() => ReadBools(address, count));
        /// <summary>批量写入位（异步）。</summary>
        public Task<OperateResult> WriteBoolsAsync(string address, bool[] values) => Task.Run(() => WriteBools(address, values));
        /// <summary>大块读取（异步）。</summary>
        public Task<OperateResult<byte[]>> ReadBytesLargeAsync(string address, ushort length) => Task.Run(() => ReadBytesLarge(address, length));
        /// <summary>大块写入（异步）。</summary>
        public Task<OperateResult> WriteBytesLargeAsync(string address, byte[] data) => Task.Run(() => WriteBytesLarge(address, data));
        /// <summary>读取 PLC 型号（异步）。</summary>
        public Task<OperateResult<string>> ReadPlcModelAsync() => Task.Run(() => ReadPlcModel());

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值（按区域分组，连续地址合并读取）。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            // 按区域分组
            var groups = addrList.GroupBy(a =>
            {
                var (addr, readFc, _) = ParseDeltaAddress(a);
                return readFc;
            });

            foreach (var group in groups)
            {
                byte fc = group.Key;
                var sorted = group.Select(a => new { Address = a, Parsed = ParseDeltaAddress(a) })
                                  .OrderBy(a => a.Parsed.address)
                                  .ToList();

                ushort minAddr = sorted[0].Parsed.address;
                ushort maxAddr = sorted.Last().Parsed.address;
                ushort range = (ushort)(maxAddr - minAddr + 1);

                if (fc == 0x01 || fc == 0x02)
                {
                    // 位区域
                    byte[] pdu = { fc, (byte)(minAddr >> 8), (byte)minAddr, (byte)(range >> 8), (byte)(range & 0xFF) };
                    var r = SendReceive(pdu);
                    if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);

                    foreach (var item in sorted)
                    {
                        int idx = item.Parsed.address - minAddr;
                        if (idx >= 0 && idx < r.Content.Length * 8)
                            result[item.Address] = (r.Content[idx / 8] & (1 << (idx % 8))) != 0;
                    }
                }
                else
                {
                    // 字区域
                    byte[] pdu = { fc, (byte)(minAddr >> 8), (byte)minAddr, (byte)(range >> 8), (byte)(range & 0xFF) };
                    var r = SendReceive(pdu);
                    if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);

                    foreach (var item in sorted)
                    {
                        int byteOffset = (item.Parsed.address - minAddr) * 2;
                        if (byteOffset >= 0 && byteOffset + 2 <= r.Content.Length)
                            result[item.Address] = DataConverter.ToInt16(r.Content, byteOffset);
                    }
                }
            }

            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <summary>批量读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        /// <summary>随机读取多个不连续地址（返回原始字节）。</summary>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");

            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 1);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <summary>随机读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        /// <summary>批量写入多个地址的值。</summary>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");

            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

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
}
