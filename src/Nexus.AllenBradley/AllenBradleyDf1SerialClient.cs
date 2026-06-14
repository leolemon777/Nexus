using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.AllenBradley
{
    /// <summary>
    /// Allen-Bradley DF1 串口客户端 — 支持 SLC-500 / PLC-5 / MicroLogix 等传统 AB PLC。
    /// <para>协议层次: RS-232/RS-485 → DF1 Full-Duplex → SLC 命令</para>
    /// <para>DF1 帧格式: DLE+STX + 数据(含 DLE 转义) + DLE+ETX + BCC</para>
    /// <para>支持 Full-Duplex（点对点）和 Half-Duplex（主从）模式。</para>
    /// </summary>
    public class AllenBradleyDf1SerialClient : SerialDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        private int _transactionCounter;
        private readonly Df1AddressParser _addressParser = new Df1AddressParser();

        /// <summary>源节点号（默认 0x01）。</summary>
        public byte SourceNode { get; set; } = 0x01;

        /// <summary>目标节点号（默认 0x00）。</summary>
        public byte DestinationNode { get; set; } = 0x00;

        /// <summary>DF1 模式（默认 FullDuplex）。</summary>
        public Df1Mode Mode { get; set; } = Df1Mode.FullDuplex;

        /// <summary>DF1 数据字节序（默认 LittleEndian）。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.LittleEndian;

        public AllenBradleyDf1SerialClient(ISerialPort serialPort, int timeout = 5000)
            : base(serialPort, timeout)
        {
        }

        // ═══════════════════════════════════════════
        //  DF1 帧常量
        // ═══════════════════════════════════════════

        private const byte DLE = 0x10;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;

        // SLC 命令码
        private const byte SlcCmdRead = 0x00;
        private const byte SlcCmdWrite = 0x01;
        private const byte SlcCmdProtectedWrite = 0x08;

        // ═══════════════════════════════════════════
        //  DF1 帧构建
        // ═══════════════════════════════════════════

        /// <summary>
        /// 构建 DF1 帧 — DLE+STX + 转义数据 + DLE+ETX + BCC。
        /// </summary>
        private byte[] BuildDf1Frame(byte[] payload)
        {
            using var ms = new MemoryStream();

            ms.WriteByte(DLE);
            ms.WriteByte(STX);

            foreach (byte b in payload)
            {
                ms.WriteByte(b);
                if (b == DLE)
                    ms.WriteByte(DLE);
            }

            ms.WriteByte(DLE);
            ms.WriteByte(ETX);

            byte bcc = CalculateBcc(payload, ETX);
            if (bcc == DLE)
            {
                ms.WriteByte(DLE);
                ms.WriteByte(DLE);
            }
            else
            {
                ms.WriteByte(bcc);
            }

            return ms.ToArray();
        }

        private static byte CalculateBcc(byte[] data, byte extra)
        {
            int sum = extra;
            foreach (byte b in data)
                sum += b;
            return (byte)(~sum + 1);
        }

        // ═══════════════════════════════════════════
        //  DF1 帧解析
        // ═══════════════════════════════════════════

        /// <summary>从串口读取一个完整的 DF1 帧，返回负载数据。</summary>
        private OperateResult<byte[]> ReadDf1Frame()
        {
            try
            {
                int start = Environment.TickCount;
                while (unchecked(Environment.TickCount - start) <= Timeout)
                {
                    int b = ReadOneByte();
                    if (b < 0) return OperateResult<byte[]>.Failed("读取超时");
                    if (b == DLE)
                    {
                        int next = ReadOneByte();
                        if (next < 0) return OperateResult<byte[]>.Failed("读取超时");
                        if (next == STX) break;
                        if (next == ACK)
                            return OperateResult<byte[]>.Success(new byte[] { ACK });
                    }
                }

                using var ms = new MemoryStream();
                bool lastWasDle = false;
                while (unchecked(Environment.TickCount - start) <= Timeout)
                {
                    int b = ReadOneByte();
                    if (b < 0) return OperateResult<byte[]>.Failed("读取超时");

                    if (lastWasDle)
                    {
                        if (b == ETX)
                        {
                            int bcc1 = ReadOneByte();
                            if (bcc1 < 0) return OperateResult<byte[]>.Failed("读取 BCC 超时");

                            byte[] data = ms.ToArray();
                            return OperateResult<byte[]>.Success(data);
                        }
                        else if (b == DLE)
                        {
                            ms.WriteByte(DLE);
                            lastWasDle = false;
                        }
                        else
                        {
                            ms.WriteByte(DLE);
                            ms.WriteByte((byte)b);
                            lastWasDle = false;
                        }
                    }
                    else if (b == DLE)
                    {
                        lastWasDle = true;
                    }
                    else
                    {
                        ms.WriteByte((byte)b);
                    }
                }

                return OperateResult<byte[]>.Failed("DF1 帧读取超时");
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"DF1 帧读取异常: {ex.Message}");
            }
        }

        /// <summary>读取单个字节 — 兼容 ISerialPort 接口。</summary>
        private int ReadOneByte()
        {
            byte[] buf = new byte[1];
            int n = Port.Read(buf, 0, 1);
            return n > 0 ? buf[0] : -1;
        }

        // ═══════════════════════════════════════════
        //  SLC 命令构建
        // ═══════════════════════════════════════════

        private byte[] BuildSlcReadCommand(Df1Address addr, byte byteCount)
        {
            int tns = GetNextTns();
            var ms = new MemoryStream();
            ms.WriteByte(SourceNode);
            ms.WriteByte(DestinationNode);
            ms.WriteByte((byte)(tns & 0xFF));
            ms.WriteByte((byte)((tns >> 8) & 0xFF));
            ms.WriteByte(SlcCmdRead);
            ms.WriteByte(byteCount);
            ms.WriteByte(addr.FileType);
            WriteSlcAddress(ms, addr);
            return ms.ToArray();
        }

        private byte[] BuildSlcProtectedWriteCommand(Df1Address addr, byte[] data)
        {
            int tns = GetNextTns();
            var ms = new MemoryStream();
            ms.WriteByte(SourceNode);
            ms.WriteByte(DestinationNode);
            ms.WriteByte((byte)(tns & 0xFF));
            ms.WriteByte((byte)((tns >> 8) & 0xFF));
            ms.WriteByte(SlcCmdProtectedWrite);
            ms.WriteByte((byte)data.Length);
            ms.WriteByte(addr.FileType);
            WriteSlcAddress(ms, addr);
            ms.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static void WriteSlcAddress(MemoryStream ms, Df1Address addr)
        {
            ms.WriteByte((byte)addr.FileNumber);
            ms.WriteByte((byte)addr.Element);
            ms.WriteByte((byte)addr.SubElement);
        }

        private int GetNextTns() => Interlocked.Increment(ref _transactionCounter) & 0xFFFF;

        // ═══════════════════════════════════════════
        //  DF1 发送/接收 — 自定义帧处理
        // ═══════════════════════════════════════════

        /// <summary>DF1 发送并接收 — 发送 DF1 帧，读取 DF1 响应帧。</summary>
        private OperateResult<byte[]> Df1SendAndReceive(byte[] df1Frame)
        {
            try
            {
                lock (_lock)
                {
                    if (!Port.IsOpen)
                        return OperateResult<byte[]>.Failed("串口未打开");

                    Log.Debug($"TX → {DataConverter.ToHexString(df1Frame)}");
                    RaiseMessageSent(DataConverter.ToHexString(df1Frame));

                    Port.Write(df1Frame, 0, df1Frame.Length);

                    if (InterFrameDelay > 0)
                        Thread.Sleep(InterFrameDelay);

                    var result = ReadDf1Frame();
                    if (result.IsSuccess)
                    {
                        Log.Debug($"RX ← {DataConverter.ToHexString(result.Content)}");
                        RaiseMessageReceived(DataConverter.ToHexString(result.Content));
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"DF1 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<byte[]>.Failed($"DF1 通讯异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  SLC 读写
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SlcRead(string address, int byteCount)
        {
            try
            {
                var addr = _addressParser.Parse(address);
                byte[] cmd = BuildSlcReadCommand(addr, (byte)byteCount);
                byte[] frame = BuildDf1Frame(cmd);
                return Df1SendAndReceive(frame);
            }
            catch (Exception ex)
            {
                Log.Error($"SLC Read 异常 ({address}) — {ex.Message}");
                return OperateResult<byte[]>.Failed($"SLC Read 异常: {ex.Message}");
            }
        }

        private OperateResult SlcWrite(string address, byte[] data)
        {
            try
            {
                var addr = _addressParser.Parse(address);
                byte[] cmd = BuildSlcProtectedWriteCommand(addr, data);
                byte[] frame = BuildDf1Frame(cmd);
                var result = Df1SendAndReceive(frame);
                if (!result.IsSuccess)
                    return OperateResult.Failed(result.Message, result.ErrorCode);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"SLC Write 异常 ({address}) — {ex.Message}");
                return OperateResult.Failed($"SLC Write 异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  字节序辅助
        // ═══════════════════════════════════════════

        private static short ToInt16LE(byte[] data, int offset = 0)
            => (short)(data[offset] | (data[offset + 1] << 8));

        private static int ToInt32LE(byte[] data, int offset = 0)
            => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

        private static float ToFloatLE(byte[] data, int offset = 0)
        {
            int v = ToInt32LE(data, offset);
            return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
        }

        private static byte[] GetBytesLE(short value)
            => new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };

        private static byte[] GetBytesLE(int value)
            => new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF) };

        private static byte[] GetBytesLE(float value)
            => GetBytesLE(BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        // ═══════════════════════════════════════════
        //  SerialDeviceBase 重写
        // ═══════════════════════════════════════════

        // DF1 使用自定义 Df1SendAndReceive，不依赖基类的 header+payload 模型
        protected override int ResponseHeaderLength => 0;

        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 读取
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _addressParser.Parse(address);
            if (addr.SubElement > 0)
            {
                var r = SlcRead(address, 2);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
                if (r.Content.Length < 2) return OperateResult<bool>.Failed("响应数据不足");
                short val = ToInt16LE(r.Content, 0);
                return OperateResult<bool>.Success((val & (1 << addr.SubElement)) != 0);
            }
            var rr = SlcRead(address, 1);
            if (!rr.IsSuccess) return OperateResult<bool>.Failed(rr.Message, rr.ErrorCode);
            if (rr.Content.Length < 1) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success(rr.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = SlcRead(address, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足");
            return OperateResult<short>.Success(ToInt16LE(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = SlcRead(address, 2);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("响应数据不足");
            return OperateResult<ushort>.Success((ushort)ToInt16LE(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = SlcRead(address, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success(ToInt32LE(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = SlcRead(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足");
            uint lo = (uint)ToInt32LE(r.Content, 0);
            uint hi = (uint)ToInt32LE(r.Content, 4);
            return OperateResult<long>.Success(((long)hi << 32) | lo);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = SlcRead(address, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足");
            return OperateResult<float>.Success(ToFloatLE(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            if (!string.IsNullOrEmpty(address) && address.TrimStart().ToUpperInvariant().StartsWith("ST"))
            {
                if (length == 0)
                {
                    var lenRead = SlcRead(address, 2);
                    if (!lenRead.IsSuccess) return OperateResult<string>.Failed(lenRead.Message, lenRead.ErrorCode);
                    if (lenRead.Content.Length < 2) return OperateResult<string>.Failed("ST 长度响应不足");
                    int strLen = ToInt16LE(lenRead.Content, 0);
                    int readLen = strLen + 2;
                    if (readLen % 2 != 0) readLen++;

                    var dataRead = SlcRead(address, readLen);
                    if (!dataRead.IsSuccess) return OperateResult<string>.Failed(dataRead.Message, dataRead.ErrorCode);
                    if (dataRead.Content.Length < 2) return OperateResult<string>.Success(string.Empty);
                    int actualLen = Math.Min(strLen, dataRead.Content.Length - 2);
                    return OperateResult<string>.Success(Encoding.ASCII.GetString(dataRead.Content, 2, actualLen));
                }
                else
                {
                    int readLen = length + 2;
                    if (readLen % 2 != 0) readLen++;
                    var r = SlcRead(address, readLen);
                    if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
                    if (r.Content.Length < 2) return OperateResult<string>.Success(string.Empty);
                    int strLen = Math.Min(ToInt16LE(r.Content, 0), r.Content.Length - 2);
                    if (strLen <= 0) return OperateResult<string>.Success(string.Empty);
                    return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content, 2, strLen));
                }
            }

            int byteLen = length > 0 ? length : 82;
            var rr = SlcRead(address, Math.Min(byteLen, 240));
            if (!rr.IsSuccess) return OperateResult<string>.Failed(rr.Message, rr.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(rr.Content));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = SlcRead(address, (int)Math.Min((int)length, 240));
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(r.Content);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 写入
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, bool value)
        {
            var addr = _addressParser.Parse(address);
            if (addr.SubElement > 0)
            {
                var r = SlcRead(address, 2);
                if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
                if (r.Content.Length < 2) return OperateResult.Failed("响应数据不足");
                short current = ToInt16LE(r.Content, 0);
                if (value)
                    current |= (short)(1 << addr.SubElement);
                else
                    current &= (short)~(1 << addr.SubElement);
                return SlcWrite(address, GetBytesLE(current));
            }
            return SlcWrite(address, new byte[] { value ? (byte)1 : (byte)0 });
        }

        public override OperateResult Write(string address, short value)
            => SlcWrite(address, GetBytesLE(value));

        public override OperateResult Write(string address, ushort value)
            => SlcWrite(address, GetBytesLE((short)value));

        public override OperateResult Write(string address, int value)
            => SlcWrite(address, GetBytesLE(value));

        public override OperateResult Write(string address, uint value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, ulong value)
            => Write(address, (int)value);

        public override OperateResult Write(string address, float value)
            => SlcWrite(address, GetBytesLE(value));

        public override OperateResult Write(string address, double value)
            => Write(address, (float)value);

        public override OperateResult Write(string address, string value)
        {
            if (!string.IsNullOrEmpty(address) && address.TrimStart().ToUpperInvariant().StartsWith("ST"))
            {
                byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
                int strLen = strBytes.Length;
                int dataLen = strLen;
                if (dataLen % 2 != 0) dataLen++;
                byte[] data = new byte[2 + dataLen];
                data[0] = (byte)(strLen & 0xFF);
                data[1] = (byte)((strLen >> 8) & 0xFF);
                Buffer.BlockCopy(strBytes, 0, data, 2, strLen);
                return SlcWrite(address, data);
            }
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            return SlcWrite(address, bytes);
        }

        public override OperateResult Write(string address, byte[] data)
            => SlcWrite(address, data);

        // ═══════════════════════════════════════════
        //  Async 方法
        // ═══════════════════════════════════════════

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public override Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public override Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public override Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public override Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public override Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public override Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public override Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public override Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public override Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ═══════════════════════════════════════════
        //  IBatchReadWrite
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

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

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

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

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        // ═══════════════════════════════════════════
        //  ISubscribeDevice
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

        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        {
            lock (_monitorLock)
            {
                _monitors[address] = new MonitorEntry
                {
                    Address = address, DataType = dataType, IntervalMs = intervalMs, LastValue = null
                };
            }
        }

        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

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

    /// <summary>DF1 通讯模式。</summary>
    public enum Df1Mode
    {
        /// <summary>全双工（点对点）。</summary>
        FullDuplex,
        /// <summary>半双工（主从）。</summary>
        HalfDuplex,
    }
}
