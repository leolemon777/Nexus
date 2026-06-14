using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// Omron EtherNet/IP CIP 客户端 — 支持 NJ/NX/CJ 系列 PLC Tag 读写（Class 0 无连接消息）。
    /// <para>协议层次: TCP → ENIP (Encapsulation) → CIP (Common Industrial Protocol)</para>
    /// <para>对标 HSL: OmronCipNet — Read/Write Tag</para>
    /// <para>地址格式: TagName, TagName.member, TagName[index], TagName.member[index]</para>
    /// </summary>
    public class OmronCipClient : IReadWriteDevice, IBatchReadWrite, ISubscribeDevice
    {
        private readonly object _lock = new object();
        private TcpClient? _tcp;
        private Stream? _stream;
        private uint _sessionHandle;
        private bool _isConnected;
        protected ILogger Log { get; set; }

        private const int DefaultMaxPduSize = 508;

        /// <summary>远程 IP 地址。</summary>
        public string IpAddress { get; }
        /// <summary>端口号（默认 44818，EtherNet/IP 标准端口）。</summary>
        public int Port { get; }
        /// <summary>目标机架号 / 单元号（默认 0）。</summary>
        public byte Slot { get; set; }
        /// <summary>超时（毫秒）。</summary>
        public int Timeout { get; set; }
        /// <summary>单次 CIP 读写最大字节数（PDU 限制，默认 508）。</summary>
        public int MaxPduSize { get; set; } = DefaultMaxPduSize;
        /// <summary>CIP 数据字节序（默认 LittleEndian，CIP 协议标准）。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.LittleEndian;

        public event EventHandler? OnConnected;
        public event EventHandler? OnDisconnected;
        public event EventHandler<string>? OnError;
        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;

        public bool IsConnected => _isConnected && _tcp?.Connected == true;

        /// <summary>
        /// 创建 Omron CIP 客户端。
        /// </summary>
        /// <param name="ipAddress">PLC IP 地址</param>
        /// <param name="port">端口号（默认 44818）</param>
        /// <param name="slot">机架/单元号（默认 0，NJ/NX 系列通常为 0）</param>
        /// <param name="timeout">超时毫秒（默认 5000）</param>
        public OmronCipClient(string ipAddress, int port = 44818, byte slot = 0, int timeout = 5000)
        {
            IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            Port = port;
            Slot = slot;
            Timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  字节序辅助
        // ═══════════════════════════════════════════

        private bool IsLittleEndian => ByteOrder == Endianness.LittleEndian;

        protected short ToInt16LE(byte[] data, int offset = 0)
            => (short)(data[offset] | (data[offset + 1] << 8));

        protected ushort ToUInt16LE(byte[] data, int offset = 0)
            => (ushort)(data[offset] | (data[offset + 1] << 8));

        protected int ToInt32LE(byte[] data, int offset = 0)
            => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

        protected uint ToUInt32LE(byte[] data, int offset = 0)
            => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        protected unsafe float ToFloatLE(byte[] data, int offset = 0)
        {
            int v = ToInt32LE(data, offset);
            return *(float*)&v;
        }

        protected unsafe double ToDoubleLE(byte[] data, int offset = 0)
        {
            long lo = (long)ToUInt32LE(data, offset);
            long hi = (long)ToUInt32LE(data, offset + 4);
            long v = lo | (hi << 32);
            return *(double*)&v;
        }

        private static byte[] GetBytesLE(short value)
            => new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };

        private static byte[] GetBytesLE(ushort value)
            => new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };

        private static byte[] GetBytesLE(int value)
            => new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF) };

        private static byte[] GetBytesLE(uint value)
            => new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF) };

        private static byte[] GetBytesLE(long value)
            => new byte[]
            {
                (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF),
                (byte)((value >> 32) & 0xFF), (byte)((value >> 40) & 0xFF), (byte)((value >> 48) & 0xFF), (byte)((value >> 56) & 0xFF)
            };

        private static byte[] GetBytesLE(ulong value)
            => new byte[]
            {
                (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF),
                (byte)((value >> 32) & 0xFF), (byte)((value >> 40) & 0xFF), (byte)((value >> 48) & 0xFF), (byte)((value >> 56) & 0xFF)
            };

        private static unsafe byte[] GetBytesLE(float value)
        {
            int v = *(int*)&value;
            return GetBytesLE(v);
        }

        private static unsafe byte[] GetBytesLE(double value)
        {
            long v = *(long*)&value;
            return new byte[]
            {
                (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF),
                (byte)((v >> 32) & 0xFF), (byte)((v >> 40) & 0xFF), (byte)((v >> 48) & 0xFF), (byte)((v >> 56) & 0xFF)
            };
        }

        // ═══════════════════════════════════════════
        //  ENIP Encapsulation 层
        // ═══════════════════════════════════════════

        internal enum EnipCommand : ushort
        {
            Nop = 0x0000,
            ListIdentity = 0x0063,
            RegisterSession = 0x0065,
            UnregisterSession = 0x0066,
            SendRRData = 0x006F,
            SendUnitData = 0x0070,
        }

        internal OperateResult<byte[]> SendEnip(EnipCommand command, byte[] data)
        {
            try
            {
                lock (_lock)
                {
                    if (_stream == null) return OperateResult<byte[]>.Failed("未连接");

                    byte[] header = new byte[24];
                    header[0] = (byte)((ushort)command & 0xFF);
                    header[1] = (byte)(((ushort)command >> 8) & 0xFF);
                    int dataLen = data?.Length ?? 0;
                    header[2] = (byte)(dataLen & 0xFF);
                    header[3] = (byte)((dataLen >> 8) & 0xFF);
                    header[4] = (byte)(_sessionHandle & 0xFF);
                    header[5] = (byte)((_sessionHandle >> 8) & 0xFF);
                    header[6] = (byte)((_sessionHandle >> 16) & 0xFF);
                    header[7] = (byte)((_sessionHandle >> 24) & 0xFF);

                    byte[] frame = new byte[24 + dataLen];
                    Buffer.BlockCopy(header, 0, frame, 0, 24);
                    if (data != null && dataLen > 0)
                        Buffer.BlockCopy(data, 0, frame, 24, dataLen);

                    Log.Debug($"ENIP TX → Cmd={command} Len={dataLen}");
                    OnMessageSent?.Invoke(this, $"ENIP {command} [{dataLen}B]");
                    _stream.Write(frame, 0, frame.Length);

                    byte[]? respHeader = ReadExact(24);
                    if (respHeader == null)
                        return OperateResult<byte[]>.Failed("读取 ENIP 响应头超时");

                    ushort respCmd = (ushort)(respHeader[0] | (respHeader[1] << 8));
                    ushort respLen = (ushort)(respHeader[2] | (respHeader[3] << 8));
                    uint respStatus = (uint)(respHeader[8] | (respHeader[9] << 8) | (respHeader[10] << 16) | (respHeader[11] << 24));

                    if (command == EnipCommand.RegisterSession && respStatus == 0)
                    {
                        _sessionHandle = (uint)(respHeader[4] | (respHeader[5] << 8) | (respHeader[6] << 16) | (respHeader[7] << 24));
                    }

                    byte[]? respData = respLen > 0 ? ReadExact(respLen) : new byte[0];
                    if (respLen > 0 && respData == null)
                        return OperateResult<byte[]>.Failed("读取 ENIP 响应数据超时");

                    Log.Debug($"ENIP RX ← Cmd={respCmd} Status={respStatus} Len={respLen}");
                    OnMessageReceived?.Invoke(this, $"ENIP Status={respStatus} [{respLen}B]");

                    if (respStatus != 0)
                        return OperateResult<byte[]>.Failed($"ENIP 错误: Status={respStatus}", (byte)respStatus);

                    return OperateResult<byte[]>.Success(respData ?? new byte[0]);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"ENIP 通讯异常 — {ex.Message}");
                OnError?.Invoke(this, ex.Message);
                return OperateResult<byte[]>.Failed($"ENIP 通讯异常: {ex.Message}");
            }
        }

        internal byte[]? ReadExact(int count)
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
        //  CIP 连接路径（Omron 路由）
        // ═══════════════════════════════════════════

        /// <summary>构建 CIP 路径: 背板路径到目标槽号。</summary>
        internal byte[] BuildPath(byte slot)
        {
            return new byte[] { 0x01, slot };
        }

        /// <summary>
        /// 构建 EIP 连接路径。
        /// <para>Omron NJ/NX 系列: 使用 Port 1, Backplane, Slot 路由。</para>
        /// <para>CJ 系列通过 EtherNet/IP 单元: Port 2, 0x01 (EtherNet/IP Unit), 节点号。</para>
        /// </summary>
        protected virtual byte[] BuildConnectionPath()
        {
            return new byte[] { 0x01, 0x00, 0x01, 0x00, 0x20, 0x02, 0x24, Slot };
        }

        // ═══════════════════════════════════════════
        //  CIP Tag 地址编码
        // ═══════════════════════════════════════════

        /// <summary>
        /// 将 Tag 名称编码为 CIP 路径段。
        /// <para>支持: "MyTag", "MyTag[3]", "MyTag.SubTag", "Program:Main.MyTag"</para>
        /// </summary>
        protected virtual byte[] EncodeTagPath(string tagName)
        {
            using var ms = new MemoryStream();
            bool isProgram = false;
            string programName = "";
            string actualTag = tagName;

            if (tagName.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
            {
                int dot = tagName.IndexOf('.');
                if (dot > 0)
                {
                    programName = tagName.Substring(8, dot - 8);
                    actualTag = tagName.Substring(dot + 1);
                }
                else
                {
                    programName = tagName.Substring(8);
                    actualTag = "";
                }
                isProgram = true;
            }

            if (isProgram)
            {
                WriteSymbolSegment(ms, programName);
            }

            if (!string.IsNullOrEmpty(actualTag))
            {
                string[] parts = actualTag.Split('.');
                foreach (string part in parts)
                {
                    string remaining = part;
                    int nameEnd = remaining.IndexOf('[');
                    string name = nameEnd >= 0 ? remaining.Substring(0, nameEnd) : remaining;

                    if (!string.IsNullOrEmpty(name))
                        WriteSymbolSegment(ms, name);

                    while (nameEnd >= 0)
                    {
                        int closeBracket = remaining.IndexOf(']', nameEnd);
                        if (closeBracket < 0) break;
                        string idxStr = remaining.Substring(nameEnd + 1, closeBracket - nameEnd - 1);
                        int index = int.Parse(idxStr);

                        if (index < 256)
                        {
                            ms.WriteByte(0x28);
                            ms.WriteByte((byte)index);
                        }
                        else
                        {
                            ms.WriteByte(0x29);
                            ms.WriteByte((byte)(index & 0xFF));
                            ms.WriteByte((byte)((index >> 8) & 0xFF));
                        }

                        nameEnd = remaining.IndexOf('[', closeBracket);
                    }
                }
            }

            return ms.ToArray();
        }

        private static void WriteSymbolSegment(Stream ms, string name)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            ms.WriteByte(0x91);
            ms.WriteByte((byte)nameBytes.Length);
            ms.Write(nameBytes, 0, nameBytes.Length);
            if (nameBytes.Length % 2 == 0)
                ms.WriteByte(0x00);
        }

        // ═══════════════════════════════════════════
        //  CIP 服务代码与数据类型
        // ═══════════════════════════════════════════

        internal const byte CipReadService = 0x4C;
        internal const byte CipReadFragmented = 0x52;
        internal const byte CipWriteService = 0x4D;
        internal const byte CipWriteFragmented = 0x53;
        internal const byte CipMultipleService = 0x0A;

        internal const ushort CipTypeBool = 0x00C1;
        internal const ushort CipTypeSint = 0x00C2;
        internal const ushort CipTypeInt = 0x00C3;
        internal const ushort CipTypeDint = 0x00C4;
        internal const ushort CipTypeLint = 0x00C5;
        internal const ushort CipTypeUsint = 0x00C6;
        internal const ushort CipTypeUint = 0x00C7;
        internal const ushort CipTypeUdint = 0x00C8;
        internal const ushort CipTypeUlint = 0x00C9;
        internal const ushort CipTypeReal = 0x00CA;
        internal const ushort CipTypeLreal = 0x00CB;
        internal const ushort CipTypeString = 0x00D0;
        internal const ushort CipTypeStruct = 0x02A0;

        // ═══════════════════════════════════════════
        //  CIP 读写基础方法
        // ═══════════════════════════════════════════

        internal OperateResult<byte[]> ReadTagRaw(string tagName, ushort elements = 1)
        {
            byte[] path = EncodeTagPath(tagName);

            int pathWords = (path.Length + 1) / 2;
            byte[] cipReq = new byte[2 + pathWords * 2 + 2];
            cipReq[0] = CipReadService;
            cipReq[1] = (byte)pathWords;
            Buffer.BlockCopy(path, 0, cipReq, 2, path.Length);
            int offset = 2 + pathWords * 2;
            cipReq[offset] = (byte)(elements & 0xFF);
            cipReq[offset + 1] = (byte)((elements >> 8) & 0xFF);

            byte[] enipData = BuildSendRRData(cipReq);
            var result = SendEnip(EnipCommand.SendRRData, enipData);
            if (!result.IsSuccess) return result;

            return ParseCipResponse(result.Content);
        }

        internal OperateResult WriteTagRaw(string tagName, ushort dataType, byte[] data, ushort elements = 1)
        {
            byte[] path = EncodeTagPath(tagName);

            int pathWords = (path.Length + 1) / 2;
            byte[] cipReq = new byte[2 + pathWords * 2 + 2 + 2 + data.Length];
            cipReq[0] = CipWriteService;
            cipReq[1] = (byte)pathWords;
            Buffer.BlockCopy(path, 0, cipReq, 2, path.Length);
            int pos = 2 + pathWords * 2;
            cipReq[pos] = (byte)(dataType & 0xFF);
            cipReq[pos + 1] = (byte)((dataType >> 8) & 0xFF);
            pos += 2;
            cipReq[pos] = (byte)(elements & 0xFF);
            cipReq[pos + 1] = (byte)((elements >> 8) & 0xFF);
            pos += 2;
            Buffer.BlockCopy(data, 0, cipReq, pos, data.Length);

            byte[] enipData = BuildSendRRData(cipReq);
            var result = SendEnip(EnipCommand.SendRRData, enipData);
            if (!result.IsSuccess) return result;

            return ParseCipResponse(result.Content);
        }

        // ═══════════════════════════════════════════
        //  分段读写
        // ═══════════════════════════════════════════

        /// <summary>分段读取大 Tag 数据（CIP Read Fragmented, 0x52）。</summary>
        public OperateResult<byte[]> ReadTagFragmented(string tagName, uint offset = 0, uint count = 0)
        {
            try
            {
                using var ms = new MemoryStream();
                uint currentOffset = offset;
                uint totalRead = 0;

                while (true)
                {
                    byte[] path = EncodeTagPath(tagName);
                    int pathWords = (path.Length + 1) / 2;

                    byte[] cipReq = new byte[2 + pathWords * 2 + 4];
                    cipReq[0] = CipReadFragmented;
                    cipReq[1] = (byte)pathWords;
                    Buffer.BlockCopy(path, 0, cipReq, 2, path.Length);
                    int pos = 2 + pathWords * 2;
                    cipReq[pos] = (byte)(currentOffset & 0xFF);
                    cipReq[pos + 1] = (byte)((currentOffset >> 8) & 0xFF);
                    cipReq[pos + 2] = (byte)((currentOffset >> 16) & 0xFF);
                    cipReq[pos + 3] = (byte)((currentOffset >> 24) & 0xFF);

                    byte[] enipData = BuildSendRRData(cipReq);
                    var result = SendEnip(EnipCommand.SendRRData, enipData);
                    if (!result.IsSuccess) return result;

                    var parsed = ParseCipResponse(result.Content);
                    if (!parsed.IsSuccess) return parsed;

                    byte[] fragData = parsed.Content;

                    if (currentOffset == offset)
                    {
                        if (fragData.Length < 6)
                            return OperateResult<byte[]>.Failed("分段读取响应数据不足");
                        uint dataCount = ToUInt32LE(fragData, 2);
                        int headerSize = 6;
                        int dataLen = fragData.Length - headerSize;
                        if (dataLen > 0)
                            ms.Write(fragData, headerSize, dataLen);
                        totalRead += (uint)dataLen;
                        currentOffset += (uint)dataLen;

                        if (count > 0 && totalRead >= count)
                        {
                            byte[] result2 = new byte[count];
                            Buffer.BlockCopy(ms.ToArray(), 0, result2, 0, (int)count);
                            return OperateResult<byte[]>.Success(result2);
                        }

                        if (dataLen == 0 || dataLen < MaxPduSize - 16)
                            break;
                    }
                    else
                    {
                        if (fragData.Length > 0)
                            ms.Write(fragData, 0, fragData.Length);
                        totalRead += (uint)fragData.Length;
                        currentOffset += (uint)fragData.Length;

                        if (count > 0 && totalRead >= count)
                        {
                            byte[] result2 = new byte[count];
                            Buffer.BlockCopy(ms.ToArray(), 0, result2, 0, (int)count);
                            return OperateResult<byte[]>.Success(result2);
                        }

                        if (fragData.Length == 0 || fragData.Length < MaxPduSize - 16)
                            break;
                    }
                }

                return OperateResult<byte[]>.Success(ms.ToArray());
            }
            catch (Exception ex)
            {
                Log.Error($"ReadTagFragmented 异常 — {ex.Message}");
                return OperateResult<byte[]>.Failed($"ReadTagFragmented 异常: {ex.Message}");
            }
        }

        /// <summary>分段写入大 Tag 数据（CIP Write Fragmented, 0x53）。</summary>
        public OperateResult WriteTagFragmented(string tagName, byte[] data, uint offset = 0, ushort dataType = CipTypeDint)
        {
            try
            {
                int maxPayload = MaxPduSize - 20;
                int dataOffset = 0;
                uint currentOffset = offset;
                bool isFirst = true;

                while (dataOffset < data.Length)
                {
                    int chunkSize = Math.Min(maxPayload, data.Length - dataOffset);
                    byte[] chunk = new byte[chunkSize];
                    Buffer.BlockCopy(data, dataOffset, chunk, 0, chunkSize);

                    byte[] path = EncodeTagPath(tagName);
                    int pathWords = (path.Length + 1) / 2;

                    byte[] cipReq = new byte[2 + pathWords * 2 + 2 + 4 + 4 + chunkSize];
                    cipReq[0] = CipWriteFragmented;
                    cipReq[1] = (byte)pathWords;
                    Buffer.BlockCopy(path, 0, cipReq, 2, path.Length);
                    int pos = 2 + pathWords * 2;
                    cipReq[pos] = (byte)(dataType & 0xFF);
                    cipReq[pos + 1] = (byte)((dataType >> 8) & 0xFF);
                    pos += 2;

                    if (isFirst)
                    {
                        uint totalCount = (uint)data.Length;
                        cipReq[pos] = (byte)(totalCount & 0xFF);
                        cipReq[pos + 1] = (byte)((totalCount >> 8) & 0xFF);
                        cipReq[pos + 2] = (byte)((totalCount >> 16) & 0xFF);
                        cipReq[pos + 3] = (byte)((totalCount >> 24) & 0xFF);
                        isFirst = false;
                    }
                    else
                    {
                        cipReq[pos] = 0; cipReq[pos + 1] = 0;
                        cipReq[pos + 2] = 0; cipReq[pos + 3] = 0;
                    }
                    pos += 4;

                    cipReq[pos] = (byte)(currentOffset & 0xFF);
                    cipReq[pos + 1] = (byte)((currentOffset >> 8) & 0xFF);
                    cipReq[pos + 2] = (byte)((currentOffset >> 16) & 0xFF);
                    cipReq[pos + 3] = (byte)((currentOffset >> 24) & 0xFF);
                    pos += 4;

                    Buffer.BlockCopy(chunk, 0, cipReq, pos, chunkSize);

                    byte[] enipData = BuildSendRRData(cipReq);
                    var result = SendEnip(EnipCommand.SendRRData, enipData);
                    if (!result.IsSuccess) return result;

                    var parsed = ParseCipResponse(result.Content);
                    if (!parsed.IsSuccess) return parsed;

                    dataOffset += chunkSize;
                    currentOffset += (uint)chunkSize;
                }

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"WriteTagFragmented 异常 — {ex.Message}");
                return OperateResult.Failed($"WriteTagFragmented 异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  批量 Tag 读取（Multiple Service Packet）
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个 Tag（CIP Multiple Service Packet, 0x0A）。</summary>
        public OperateResult<Dictionary<string, byte[]>> BatchReadTags(IEnumerable<string> tagNames)
        {
            var tagList = tagNames.ToList();
            if (tagList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Success(new Dictionary<string, byte[]>());

            try
            {
                var serviceRequests = new List<byte[]>();
                foreach (string tag in tagList)
                {
                    byte[] path = EncodeTagPath(tag);
                    int pathWords = (path.Length + 1) / 2;
                    byte[] req = new byte[2 + pathWords * 2 + 2];
                    req[0] = CipReadService;
                    req[1] = (byte)pathWords;
                    Buffer.BlockCopy(path, 0, req, 2, path.Length);
                    int p = 2 + pathWords * 2;
                    req[p] = 0x01;
                    req[p + 1] = 0x00;
                    serviceRequests.Add(req);
                }

                int serviceCount = serviceRequests.Count;
                int offsetsSize = serviceCount * 2;
                int totalDataSize = serviceRequests.Sum(r => r.Length);
                int alignedTotal = (totalDataSize + 1) & ~1;

                byte[] path2 = BuildPath(Slot);
                int pathWords2 = (path2.Length + 1) / 2;
                byte[] cipReq = new byte[2 + pathWords2 * 2 + 2 + offsetsSize + alignedTotal];
                cipReq[0] = CipMultipleService;
                cipReq[1] = (byte)pathWords2;
                Buffer.BlockCopy(path2, 0, cipReq, 2, path2.Length);

                int pos2 = 2 + pathWords2 * 2;
                cipReq[pos2] = (byte)(serviceCount & 0xFF);
                cipReq[pos2 + 1] = (byte)((serviceCount >> 8) & 0xFF);
                pos2 += 2;

                int dataStart = pos2 + offsetsSize;
                int currentDataPos = 0;
                for (int i = 0; i < serviceCount; i++)
                {
                    int absoluteOffset = dataStart + currentDataPos;
                    cipReq[pos2 + i * 2] = (byte)(absoluteOffset & 0xFF);
                    cipReq[pos2 + i * 2 + 1] = (byte)((absoluteOffset >> 8) & 0xFF);
                    currentDataPos += serviceRequests[i].Length;
                }

                int copyPos = dataStart;
                foreach (byte[] req in serviceRequests)
                {
                    Buffer.BlockCopy(req, 0, cipReq, copyPos, req.Length);
                    copyPos += req.Length;
                }

                byte[] enipData = BuildSendRRData(cipReq);
                var result = SendEnip(EnipCommand.SendRRData, enipData);
                if (!result.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(result.Message, result.ErrorCode);

                var parsed = ParseCipResponse(result.Content);
                if (!parsed.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(parsed.Message, parsed.ErrorCode);

                byte[] respData = parsed.Content;
                if (respData.Length < 2)
                    return OperateResult<Dictionary<string, byte[]>>.Failed("批量读取响应数据不足");

                int respCount = respData[0] | (respData[1] << 8);
                int respOffsetStart = 2;
                var results = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < respCount && i < tagList.Count; i++)
                {
                    int svcOffset = respData[respOffsetStart + i * 2] | (respData[respOffsetStart + i * 2 + 1] << 8);
                    int nextOffset = (i + 1 < respCount)
                        ? (respData[respOffsetStart + (i + 1) * 2] | (respData[respOffsetStart + (i + 1) * 2 + 1] << 8))
                        : respData.Length;

                    int svcLen = nextOffset - svcOffset;
                    if (svcOffset + svcLen > respData.Length)
                        svcLen = respData.Length - svcOffset;

                    if (svcLen >= 4)
                    {
                        byte svcStatus = respData[svcOffset + 2];
                        if (svcStatus == 0)
                        {
                            int extSize = respData[svcOffset + 3];
                            int svcDataStart = svcOffset + 4 + extSize * 2;
                            int svcDataLen = nextOffset - svcDataStart;
                            if (svcDataLen > 0 && svcDataStart + svcDataLen <= respData.Length)
                            {
                                byte[] svcData = new byte[svcDataLen];
                                Buffer.BlockCopy(respData, svcDataStart, svcData, 0, svcDataLen);
                                results[tagList[i]] = svcData;
                            }
                        }
                    }
                }

                return OperateResult<Dictionary<string, byte[]>>.Success(results);
            }
            catch (Exception ex)
            {
                Log.Error($"BatchReadTags 异常 — {ex.Message}");
                return OperateResult<Dictionary<string, byte[]>>.Failed($"BatchReadTags 异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  String Tag 读写
        // ═══════════════════════════════════════════

        /// <summary>读取 CIP STRING 类型 Tag（长度前缀格式）。</summary>
        public OperateResult<string> ReadTagString(string tagName)
        {
            var r = ReadTagRaw(tagName);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 6) return OperateResult<string>.Failed("STRING 数据不足");
            int strLen = ToInt32LE(r.Content, 2);
            if (r.Content.Length < 6 + strLen) return OperateResult<string>.Failed("STRING 数据不完整");
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content, 6, strLen));
        }

        /// <summary>写入 CIP STRING 类型 Tag。</summary>
        public OperateResult WriteTagString(string tagName, string value)
        {
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            byte[] data = new byte[4 + strBytes.Length];
            byte[] lenBytes = GetBytesLE(strBytes.Length);
            Buffer.BlockCopy(lenBytes, 0, data, 0, 4);
            Buffer.BlockCopy(strBytes, 0, data, 4, strBytes.Length);
            return WriteTagRaw(tagName, CipTypeString, data);
        }

        // ═══════════════════════════════════════════
        //  SendRRData 包装
        // ═══════════════════════════════════════════

        internal byte[] BuildSendRRData(byte[] cipData)
        {
            int dataLen = cipData.Length;
            int totalLen = 4 + 2 + 2 + 2 + 2 + 2 + 2 + dataLen;

            byte[] result = new byte[totalLen];
            int i = 0;
            result[i++] = 0; result[i++] = 0; result[i++] = 0; result[i++] = 0;
            result[i++] = 0; result[i++] = 0;
            result[i++] = 2; result[i++] = 0;
            result[i++] = 0x00; result[i++] = 0x00;
            result[i++] = 0x00; result[i++] = 0x00;
            result[i++] = 0xB2; result[i++] = 0x00;
            result[i++] = (byte)(dataLen & 0xFF); result[i++] = (byte)((dataLen >> 8) & 0xFF);
            Buffer.BlockCopy(cipData, 0, result, i, dataLen);

            return result;
        }

        // ═══════════════════════════════════════════
        //  CIP 响应解析
        // ═══════════════════════════════════════════

        internal OperateResult<byte[]> ParseCipResponse(byte[] enipPayload)
        {
            int offset = 6;
            if (offset + 2 > enipPayload.Length)
                return OperateResult<byte[]>.Failed("CIP 响应太短");

            offset += 2;
            if (offset + 4 > enipPayload.Length)
                return OperateResult<byte[]>.Failed("CIP 响应 Item1 缺失");
            offset += 4;
            if (offset + 4 > enipPayload.Length)
                return OperateResult<byte[]>.Failed("CIP 响应 Item2 缺失");
            ushort item2Len = (ushort)(enipPayload[offset + 2] | (enipPayload[offset + 3] << 8));
            offset += 4;

            if (offset + item2Len > enipPayload.Length)
                return OperateResult<byte[]>.Failed("CIP 响应数据不完整");

            byte[] cipReply = new byte[item2Len];
            Buffer.BlockCopy(enipPayload, offset, cipReply, 0, item2Len);

            if (cipReply.Length < 4)
                return OperateResult<byte[]>.Failed("CIP Reply 太短");

            byte status = cipReply[2];
            if (status != 0)
            {
                string msg = CipStatusMessage(status);
                return OperateResult<byte[]>.Failed($"CIP 错误 0x{status:X2}: {msg}", status);
            }

            int extSize = cipReply[3];
            int dataOffset = 4 + extSize * 2;
            byte[] data = new byte[cipReply.Length - dataOffset];
            Buffer.BlockCopy(cipReply, dataOffset, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        internal static string CipStatusMessage(byte status) => status switch
        {
            0x01 => "连接失败",
            0x02 => "资源不可用",
            0x03 => "无效参数值",
            0x04 => "路径段错误",
            0x05 => "路径目的地未知",
            0x06 => "部分转移",
            0x07 => "连接丢失",
            0x08 => "服务不支持",
            0x09 => "无效属性值",
            0x0A => "属性列表错误",
            0x0B => "数据太多",
            0x0C => "对象不支持此属性",
            0x0D => "属性列表获取失败",
            0x0E => "属性列表设置失败",
            0x0F => "属性列表中不可设置",
            0x10 => "属性列表中不可获取",
            0x13 => "提供的数据量不足",
            0x14 => "数据属性列表中没有此属性",
            0x15 => "数据类型不匹配",
            0x16 => "数据超出范围",
            _ => $"未知 CIP 错误 0x{status:X2}"
        };

        // ═══════════════════════════════════════════
        //  设备标识
        // ═══════════════════════════════════════════

        /// <summary>Omron 厂商 ID。</summary>
        public const ushort OmronVendorId = 47;

        /// <summary>读取控制器标识 (ListIdentity) — 获取 PLC 设备信息。</summary>
        public OperateResult<CipIdentity> ReadDeviceIdentity()
        {
            var r = SendEnip(EnipCommand.ListIdentity, Array.Empty<byte>());
            if (!r.IsSuccess) return OperateResult<CipIdentity>.Failed(r.Message, r.ErrorCode);

            if (r.Content.Length < 34)
                return OperateResult<CipIdentity>.Failed("ListIdentity 响应不足");

            var id = new CipIdentity();
            int off = 6;

            if (r.Content.Length >= off + 2)
                id.EncapsulationVersion = ToUInt16LE(r.Content, off);
            off += 2;

            off += 16; // Socket address

            if (r.Content.Length >= off + 2)
                id.VendorId = ToUInt16LE(r.Content, off);
            off += 2;

            if (r.Content.Length >= off + 2)
                id.DeviceType = ToUInt16LE(r.Content, off);
            off += 2;

            if (r.Content.Length >= off + 2)
                id.ProductCode = ToUInt16LE(r.Content, off);
            off += 2;

            if (r.Content.Length >= off + 2)
            {
                id.RevisionMajor = r.Content[off];
                id.RevisionMinor = r.Content[off + 1];
            }
            off += 2;

            if (r.Content.Length >= off + 2)
                id.Status = ToUInt16LE(r.Content, off);
            off += 2;

            if (r.Content.Length >= off + 4)
                id.SerialNumber = ToUInt32LE(r.Content, off);
            off += 4;

            if (r.Content.Length > off)
            {
                byte nameLen = r.Content[off];
                off++;
                if (r.Content.Length >= off + nameLen)
                    id.ProductName = Encoding.ASCII.GetString(r.Content, off, nameLen);
            }

            return OperateResult<CipIdentity>.Success(id);
        }

        public Task<OperateResult<CipIdentity>> ReadDeviceIdentityAsync()
            => Task.Run(() => ReadDeviceIdentity());

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        public virtual OperateResult Connect()
        {
            try
            {
                _tcp = new TcpClient(IpAddress, Port);
                _tcp.SendTimeout = Timeout;
                _tcp.ReceiveTimeout = Timeout;
                _stream = _tcp.GetStream();
                _sessionHandle = 0;

                byte[] regData = new byte[4];
                regData[0] = 0x01; regData[1] = 0x00;
                var regResult = SendEnip(EnipCommand.RegisterSession, regData);
                if (!regResult.IsSuccess)
                {
                    _stream.Close();
                    _tcp.Close();
                    _tcp = null;
                    _stream = null;
                    return OperateResult.Failed($"RegisterSession 失败: {regResult.Message}");
                }

                _isConnected = true;
                OnConnected?.Invoke(this, EventArgs.Empty);
                Log.Debug($"已连接到 {IpAddress}:{Port}, Session=0x{_sessionHandle:X8}");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"连接失败 — {ex.Message}");
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
        }

        public Task<OperateResult> ConnectAsync() => Task.Run(() => Connect());

        public virtual void Disconnect()
        {
            lock (_lock)
            {
                if (_sessionHandle != 0 && _stream != null)
                {
                    try { SendEnip(EnipCommand.UnregisterSession, new byte[0]); } catch { }
                }
                try { _stream?.Close(); } catch { }
                try { _tcp?.Close(); } catch { }
                _tcp = null;
                _stream = null;
                _sessionHandle = 0;
                _isConnected = false;
            }
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing) Disconnect();
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — Tag 读写
        // ═══════════════════════════════════════════

        private static bool HasCipTypeHeader(byte[] data)
        {
            if (data.Length < 2) return false;
            ushort type = (ushort)(data[0] | (data[1] << 8));
            return type >= CipTypeBool && type <= CipTypeStruct;
        }

        private static int GetTagDataOffset(byte[] data, int expectedDataBytes)
            => data.Length >= expectedDataBytes + 2 && HasCipTypeHeader(data) ? 2 : 0;

        public OperateResult<bool> ReadBool(string address)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 1);
            if (r.Content.Length < offset + 1) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success(r.Content[offset] != 0);
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 2);
            if (r.Content.Length < offset + 2) return OperateResult<short>.Failed("响应数据不足");
            return OperateResult<short>.Success(IsLittleEndian ? ToInt16LE(r.Content, offset) : DataConverter.ToInt16(r.Content, offset));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 2);
            if (r.Content.Length < offset + 2) return OperateResult<ushort>.Failed("响应数据不足");
            return OperateResult<ushort>.Success(IsLittleEndian ? ToUInt16LE(r.Content, offset) : DataConverter.ToUInt16(r.Content, offset));
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 4);
            if (r.Content.Length < offset + 4) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success(IsLittleEndian ? ToInt32LE(r.Content, offset) : DataConverter.ToInt32(r.Content, offset));
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 4);
            if (r.Content.Length < offset + 4) return OperateResult<uint>.Failed("响应数据不足");
            return OperateResult<uint>.Success(IsLittleEndian ? ToUInt32LE(r.Content, offset) : DataConverter.ToUInt32(r.Content, offset));
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 8);
            if (r.Content.Length < offset + 8) return OperateResult<long>.Failed("响应数据不足");
            if (IsLittleEndian)
            {
                uint lo = ToUInt32LE(r.Content, offset);
                uint hi = ToUInt32LE(r.Content, offset + 4);
                return OperateResult<long>.Success(((long)hi << 32) | lo);
            }
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, offset));
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public unsafe OperateResult<float> ReadFloat(string address)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 4);
            if (r.Content.Length < offset + 4) return OperateResult<float>.Failed("响应数据不足");
            return OperateResult<float>.Success(IsLittleEndian ? ToFloatLE(r.Content, offset) : DataConverter.ToFloat(r.Content, offset));
        }

        public unsafe OperateResult<double> ReadDouble(string address)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 8);
            if (r.Content.Length < offset + 8) return OperateResult<double>.Failed("响应数据不足");
            return OperateResult<double>.Success(IsLittleEndian ? ToDoubleLE(r.Content, offset) : DataConverter.ToDouble(r.Content, offset));
        }

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            int offset = GetTagDataOffset(r.Content, 4);
            if (r.Content.Length < offset + 4) return OperateResult<string>.Failed("响应数据不足");
            int strLen = IsLittleEndian ? ToInt32LE(r.Content, offset) : DataConverter.ToInt32(r.Content, offset);
            int dataOffset = offset + 4;
            if (r.Content.Length < dataOffset + strLen) return OperateResult<string>.Failed("字符串数据不完整");
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content, dataOffset, strLen));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadTagRaw(address);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            int offset = HasCipTypeHeader(r.Content) ? 2 : 0;
            if (r.Content.Length < offset) return OperateResult<byte[]>.Failed("响应数据不足");
            byte[] data = new byte[r.Content.Length - offset];
            Buffer.BlockCopy(r.Content, offset, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        // ── 写入 ──

        public OperateResult Write(string address, bool value)
        {
            return WriteTagRaw(address, CipTypeBool, new byte[] { (byte)(value ? 1 : 0), 0x00 });
        }

        public OperateResult Write(string address, short value)
        {
            var data = IsLittleEndian ? GetBytesLE(value) : DataConverter.GetBytes(value);
            return WriteTagRaw(address, CipTypeInt, data);
        }

        public OperateResult Write(string address, ushort value)
        {
            var data = IsLittleEndian ? GetBytesLE(value) : DataConverter.GetBytes(value);
            return WriteTagRaw(address, CipTypeUint, data);
        }

        public OperateResult Write(string address, int value)
        {
            var data = IsLittleEndian ? GetBytesLE(value) : DataConverter.GetBytes(value);
            return WriteTagRaw(address, CipTypeDint, data);
        }

        public OperateResult Write(string address, uint value) => Write(address, (int)value);

        public OperateResult Write(string address, long value)
        {
            var data = IsLittleEndian ? GetBytesLE(value) : DataConverter.GetBytes(value);
            return WriteTagRaw(address, CipTypeLint, data);
        }

        public OperateResult Write(string address, ulong value)
        {
            var data = IsLittleEndian ? GetBytesLE(value) : DataConverter.GetBytes(value);
            return WriteTagRaw(address, CipTypeUlint, data);
        }

        public unsafe OperateResult Write(string address, float value)
        {
            var data = IsLittleEndian ? GetBytesLE(value) : DataConverter.GetBytes(value);
            return WriteTagRaw(address, CipTypeReal, data);
        }

        public OperateResult Write(string address, double value)
        {
            var data = IsLittleEndian ? GetBytesLE(value) : DataConverter.GetBytes(value);
            return WriteTagRaw(address, CipTypeLreal, data);
        }

        public OperateResult Write(string address, string value)
        {
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            byte[] data = new byte[4 + strBytes.Length];
            byte[] lenBytes = IsLittleEndian ? GetBytesLE(strBytes.Length) : DataConverter.GetBytes(strBytes.Length);
            Buffer.BlockCopy(lenBytes, 0, data, 0, 4);
            Buffer.BlockCopy(strBytes, 0, data, 4, strBytes.Length);
            return WriteTagRaw(address, CipTypeString, data);
        }

        public OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            return WriteTagRaw(address, CipTypeDint, data);
        }

        // ── Async 方法 ──

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

        public Task<OperateResult<byte[]>> ReadTagFragmentedAsync(string tagName, uint offset = 0, uint count = 0)
            => Task.Run(() => ReadTagFragmented(tagName, offset, count));
        public Task<OperateResult> WriteTagFragmentedAsync(string tagName, byte[] data, uint offset = 0, ushort dataType = CipTypeDint)
            => Task.Run(() => WriteTagFragmented(tagName, data, offset, dataType));
        public Task<OperateResult<Dictionary<string, byte[]>>> BatchReadTagsAsync(IEnumerable<string> tagNames)
            => Task.Run(() => BatchReadTags(tagNames));

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            var raw = BatchReadTags(addressList);
            if (!raw.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(raw.Message, raw.ErrorCode);

            var result = new Dictionary<string, object?>();
            foreach (var kv in raw.Content)
            {
                byte[] d = kv.Value;
                if (d == null || d.Length < 4)
                {
                    result[kv.Key] = d;
                    continue;
                }

                ushort dataType = (ushort)((d[0] << 8) | d[1]);
                int dataOff = 4;
                if (d.Length <= dataOff) { result[kv.Key] = null; continue; }

                byte[] tagData = new byte[d.Length - dataOff];
                Buffer.BlockCopy(d, dataOff, tagData, 0, tagData.Length);

                object? value = dataType switch
                {
                    CipTypeBool => tagData[0] != 0,
                    CipTypeSint => (sbyte)tagData[0],
                    CipTypeInt => tagData.Length >= 2 ? (short)((tagData[0] << 8) | tagData[1]) : (object?)null,
                    CipTypeUint => tagData.Length >= 2 ? (ushort)((tagData[0] << 8) | tagData[1]) : (object?)null,
                    CipTypeDint => tagData.Length >= 4 ? (int)((tagData[0] << 24) | (tagData[1] << 16) | (tagData[2] << 8) | tagData[3]) : (object?)null,
                    CipTypeLint => tagData.Length >= 8 ? BitConverter.ToInt64(tagData, 0) : (object?)null,
                    CipTypeReal => tagData.Length >= 4 ? BitConverter.ToSingle(tagData, 0) : (object?)null,
                    _ => tagData
                };
                result[kv.Key] = value;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");

            return BatchReadTags(addressList);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

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
                    long l => Write(kv.Key, l),
                    ulong ul => Write(kv.Key, ul),
                    float f => Write(kv.Key, f),
                    double d => Write(kv.Key, d),
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

    /// <summary>CIP 设备标识信息。</summary>
    public sealed class CipIdentity
    {
        /// <summary>ENIP 封装版本。</summary>
        public ushort EncapsulationVersion { get; set; }
        /// <summary>厂商 ID（Omron = 47）。</summary>
        public ushort VendorId { get; set; }
        /// <summary>设备类型。</summary>
        public ushort DeviceType { get; set; }
        /// <summary>产品代码。</summary>
        public ushort ProductCode { get; set; }
        /// <summary>固件版本主号。</summary>
        public byte RevisionMajor { get; set; }
        /// <summary>固件版本次号。</summary>
        public byte RevisionMinor { get; set; }
        /// <summary>设备状态。</summary>
        public ushort Status { get; set; }
        /// <summary>序列号。</summary>
        public uint SerialNumber { get; set; }
        /// <summary>产品名称。</summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>厂商名称。</summary>
        public string VendorName => VendorId switch
        {
            1 => "Rockwell Automation / Allen-Bradley",
            47 => "Omron Corporation",
            _ => $"Vendor #{VendorId}"
        };

        /// <summary>固件版本字符串。</summary>
        public string FirmwareVersion => $"{RevisionMajor}.{RevisionMinor}";

        public override string ToString() => $"{ProductName} ({VendorName}) v{FirmwareVersion}";
    }
}
