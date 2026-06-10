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
    public class DeltaDvpClient : IBatchReadWrite, ISubscribeDevice
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
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            char prefix = address[0];
            int num = int.Parse(address.Substring(1));

            return prefix switch
            {
                'Y' => ((ushort)(0x0000 + num), 0x01, 0x05), // Output Coil
                'X' => ((ushort)(0x0000 + num), 0x02, 0x00), // Input Discrete (read-only, use FC02 but address in 1x range)
                'M' => ((ushort)(0x0800 + num), 0x01, 0x05), // Internal Relay → Coil range 2048+
                'T' => ((ushort)(0x0C00 + num), 0x01, 0x05), // Timer Coil → Coil range 3072+
                'C' => ((ushort)(0x1000 + num), 0x01, 0x05), // Counter Coil → Coil range 4096+
                'D' => ((ushort)(0x1000 + num), 0x03, 0x06), // Data Register → Holding Register 4096+
                _   => ((ushort)num, 0x03, 0x06),             // Default: holding register
            };
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

                _stream.Write(frame, 0, frame.Length);

                // 读取响应
                var r = ReadRtuResponse();
                return r;
            }
        }

        private OperateResult<byte[]> ReadRtuResponse()
        {
            // 简化: 读到足够字节
            byte[] header = new byte[2];
            ReadExact(header, 0, 2);

            byte fc = header[1];
            if ((fc & 0x80) != 0)
            {
                byte[] errData = new byte[3];
                ReadExact(errData, 0, 3);
                byte exCode = errData[2];
                return OperateResult<byte[]>.Failed($"Delta Modbus 异常: 0x{exCode:X2}", exCode);
            }

            if (fc == 0x01 || fc == 0x02)
            {
                byte[] rest = new byte[1];
                ReadExact(rest, 0, 1);
                byte byteCount = rest[0];
                byte[] data = new byte[byteCount + 2]; // data + CRC
                ReadExact(data, 0, byteCount + 2);
                byte[] result = new byte[byteCount];
                Buffer.BlockCopy(data, 0, result, 0, byteCount);
                return OperateResult<byte[]>.Success(result);
            }
            else if (fc == 0x03 || fc == 0x04)
            {
                byte[] rest = new byte[1];
                ReadExact(rest, 0, 1);
                byte byteCount = rest[0];
                byte[] data = new byte[byteCount + 2];
                ReadExact(data, 0, byteCount + 2);
                byte[] result = new byte[byteCount];
                Buffer.BlockCopy(data, 0, result, 0, byteCount);
                return OperateResult<byte[]>.Success(result);
            }
            else if (fc == 0x05 || fc == 0x06 || fc == 0x0F || fc == 0x10)
            {
                byte[] rest = new byte[6]; // addr(2) + value(2) + crc(2)
                ReadExact(rest, 0, 6);
                return OperateResult<byte[]>.Success(rest);
            }

            return OperateResult<byte[]>.Failed($"未知功能码: 0x{fc:X2}");
        }

        private void ReadExact(byte[] buffer, int offset, int count)
        {
            int deadline = Environment.TickCount + Timeout;
            while (count > 0 && Environment.TickCount <= deadline)
            {
                int n = _stream.Read(buffer, offset, count);
                if (n <= 0) throw new TimeoutException("读取超时");
                offset += n;
                count -= n;
            }
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
            return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0);
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var (addr, readFc, _) = ParseDeltaAddress(address);
            byte[] pdu = { readFc, (byte)(addr >> 8), (byte)addr, 0, 1 };
            var r = SendReceive(pdu);
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
            var (addr, readFc, _) = ParseDeltaAddress(address);
            byte[] pdu = { readFc, (byte)(addr >> 8), (byte)addr, 0, 2 };
            var r = SendReceive(pdu);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
        }

        public OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<ulong> ReadUInt64(string address) { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public unsafe OperateResult<float> ReadFloat(string address) { var r = ReadInt32(address); if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message); int v = r.Content; return OperateResult<float>.Success(*(float*)&v); }
        public unsafe OperateResult<double> ReadDouble(string address) => OperateResult<double>.Failed("Delta DVP 不支持 64 位浮点");
        public OperateResult<string> ReadString(string address, ushort length) { var r = ReadBytes(address, length); if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message); return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content).TrimEnd('\0')); }
        public OperateResult<byte[]> ReadBytes(string address, ushort length) { var (a, fc, _) = ParseDeltaAddress(address); ushort cnt = (ushort)((length + 1) / 2); var r = SendReceive(new byte[] { fc, (byte)(a >> 8), (byte)a, (byte)(cnt >> 8), (byte)cnt }); if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message); return OperateResult<byte[]>.Success(r.Content); }

        // ── 写入 ──
        public OperateResult Write(string address, bool value) { var (a, _, wfc) = ParseDeltaAddress(address); return SendReceive(new byte[] { wfc, (byte)(a >> 8), (byte)a, (byte)(value ? 0xFF : 0x00), 0x00 }).IsSuccess ? OperateResult.Success() : OperateResult.Failed("写入失败"); }
        public OperateResult Write(string address, short value) { var (a, _, _) = ParseDeltaAddress(address); var vb = DataConverter.GetBytes(value); return SendReceive(new byte[] { 0x06, (byte)(a >> 8), (byte)a, vb[0], vb[1] }).IsSuccess ? OperateResult.Success() : OperateResult.Failed("写入失败"); }
        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) { var (a, _, _) = ParseDeltaAddress(address); var vb = DataConverter.GetBytes(value); byte[] pdu = new byte[9]; pdu[0] = 0x10; pdu[1] = (byte)(a >> 8); pdu[2] = (byte)a; pdu[3] = 0; pdu[4] = 2; pdu[5] = 4; Buffer.BlockCopy(vb, 0, pdu, 6, 4); return SendReceive(pdu).IsSuccess ? OperateResult.Success() : OperateResult.Failed("写入失败"); }
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, (int)value);
        public OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public unsafe OperateResult Write(string address, float value) => Write(address, *(int*)&value);
        public OperateResult Write(string address, double value) => Write(address, (float)value);
        public OperateResult Write(string address, string value) => Write(address, System.Text.Encoding.ASCII.GetBytes(value ?? ""));
        public OperateResult Write(string address, byte[] data) { var (a, _, _) = ParseDeltaAddress(address); if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1); ushort cnt = (ushort)(data.Length / 2); byte[] pdu = new byte[6 + data.Length]; pdu[0] = 0x10; pdu[1] = (byte)(a >> 8); pdu[2] = (byte)a; pdu[3] = (byte)(cnt >> 8); pdu[4] = (byte)cnt; pdu[5] = (byte)data.Length; Buffer.BlockCopy(data, 0, pdu, 6, data.Length); return SendReceive(pdu).IsSuccess ? OperateResult.Success() : OperateResult.Failed("写入失败"); }

        // ═══════════════════════════════════════════
        //  批量位操作 — ReadBools / WriteBools
        // ═══════════════════════════════════════════

        /// <summary>
        /// 批量读取位地址（FC01/FC02），支持 Y/X/M/T/C 区域，自动分包（每包最多 1968 位）。
        /// </summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            if (count == 0) return OperateResult<bool[]>.Success(Array.Empty<bool>());
            if (count == 1) { var r = ReadBool(address); return r.IsSuccess ? OperateResult<bool[]>.Success(new[] { r.Content }) : OperateResult<bool[]>.Failed(r.Message); }

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
                    if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message);

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
                    if (!r.IsSuccess) return OperateResult.Failed(r.Message);
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
                    if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
                    Buffer.BlockCopy(r.Content, 0, result, wordOffset * 2, r.Content.Length);
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
                    if (!r.IsSuccess) return OperateResult.Failed(r.Message);
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
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
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
