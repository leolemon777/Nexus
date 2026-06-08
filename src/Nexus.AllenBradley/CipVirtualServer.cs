using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.AllenBradley
{
    /// <summary>
    /// CIP/EtherNet/IP 虚拟 PLC 服务器 — 模拟 Allen-Bradley ControlLogix / CompactLogix。
    /// 实现 EtherNet/IP 封装层 + CIP 服务层，用于无硬件测试。
    /// 内存模型: Tag 名称 → 值字典。
    /// </summary>
    public class CipVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        private readonly ConcurrentDictionary<uint, byte> _sessions = new ConcurrentDictionary<uint, byte>();
        private uint _nextSessionId = 1;
        private readonly object _sessionLock = new object();

        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new ConcurrentDictionary<TcpClient, byte>();

        // ── Tag 内存模型 ─────────────────────────
        private readonly ConcurrentDictionary<string, CipTagEntry> _tags = new ConcurrentDictionary<string, CipTagEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _tagLock = new object();

        /// <summary>写入 Tag 接收到事件 — 当客户端写入任何 Tag 时触发。</summary>
        public event EventHandler<CipWriteEventArgs>? OnWriteReceived;

        public int Port { get; }
        public bool IsRunning => _running;
        public string DeviceName { get; set; } = "1756-L83E";
        public string DeviceVendor { get; set; } = "Rockwell Automation";
        public ushort VendorId { get; set; } = 0x0001;
        public uint SerialNumber { get; set; } = 0x12345678;

        // ── CIP/ENIP 常量 ────────────────────────
        private const ushort EnipListIdentity = 0x0063;
        private const ushort EnipRegisterSession = 0x0065;
        private const ushort EnipUnregisterSession = 0x0066;
        private const ushort EnipSendRRData = 0x006F;
        private const ushort EnipSendUnitData = 0x0070;

        private const byte CipReadService = 0x4C;
        private const byte CipWriteService = 0x4D;

        private const ushort CipTypeBool = 0x00C1;
        private const ushort CipTypeSint = 0x00C2;
        private const ushort CipTypeInt = 0x00C3;
        private const ushort CipTypeDint = 0x00C4;
        private const ushort CipTypeLint = 0x00C5;
        private const ushort CipTypeUsint = 0x00C6;
        private const ushort CipTypeUint = 0x00C7;
        private const ushort CipTypeUdint = 0x00C8;
        private const ushort CipTypeReal = 0x00CA;
        private const ushort CipTypeLreal = 0x00CB;
        private const ushort CipTypeString = 0x00D0;

        public CipVirtualServer(int port = 44818) { Port = port; }

        // ── Tag 管理 API ─────────────────────────

        /// <summary>添加一个 Tag（如果已存在则覆盖）。</summary>
        public void AddTag(string name, object value)
        {
            _tags[name] = new CipTagEntry
            {
                Name = name,
                Value = value,
                DataType = GetCipTypeFromValue(value)
            };
        }

        /// <summary>设置已存在 Tag 的值（不存在则添加）。</summary>
        public void SetTagValue(string name, object value)
        {
            _tags.AddOrUpdate(name,
                _ => new CipTagEntry { Name = name, Value = value, DataType = GetCipTypeFromValue(value) },
                (_, existing) => { existing.Value = value; return existing; });
        }

        /// <summary>获取 Tag 值，不存在返回 null。</summary>
        public object? GetTagValue(string name)
        {
            return _tags.TryGetValue(name, out var entry) ? entry.Value : null;
        }

        /// <summary>获取强类型 Tag 值。</summary>
        public T? GetTagValue<T>(string name)
        {
            if (_tags.TryGetValue(name, out var entry) && entry.Value is T typed)
                return typed;
            return default;
        }

        /// <summary>检查 Tag 是否存在。</summary>
        public bool TagExists(string name) => _tags.ContainsKey(name);

        /// <summary>获取所有 Tag 名称。</summary>
        public IReadOnlyCollection<string> GetTagNames() => _tags.Keys.ToList().AsReadOnly();

        /// <summary>移除 Tag。</summary>
        public bool RemoveTag(string name) => _tags.TryRemove(name, out _);

        // ── 服务器控制 ────────────────────────────

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            foreach (var kv in _clients)
            {
                try { kv.Key.Close(); } catch { }
            }
            _clients.Clear();
            _sessions.Clear();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    _clients.TryAdd(client, 0);
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    while (_running && client.Connected)
                    {
                        // ENIP Header: 24 bytes
                        byte[]? header = ReadExact(stream, 24);
                        if (header == null) break;

                        ushort command = (ushort)(header[0] | (header[1] << 8));
                        ushort dataLen = (ushort)(header[2] | (header[3] << 8));
                        uint status = (uint)(header[8] | (header[9] << 8) | (header[10] << 16) | (header[11] << 24));

                        byte[]? data = dataLen > 0 ? ReadExact(stream, dataLen) : new byte[0];
                        if (data == null) break;

                        byte[]? response = ProcessEnipCommand(command, header, data);
                        if (response != null)
                        {
                            stream.Write(response, 0, response.Length);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove(client, out _);
            }
        }

        // ── ENIP 命令处理 ─────────────────────────

        private byte[]? ProcessEnipCommand(ushort command, byte[] header, byte[] data)
        {
            return command switch
            {
                EnipListIdentity => BuildListIdentityResponse(header),
                EnipRegisterSession => BuildRegisterSessionResponse(header, data),
                EnipUnregisterSession => BuildUnregisterSessionResponse(header),
                EnipSendRRData => ProcessSendRRData(header, data),
                EnipSendUnitData => ProcessSendRRData(header, data),
                _ => BuildEnipResponse(command, header, 0x0001, null) // 不支持的命令
            };
        }

        // ── List Identity 响应 ────────────────────

        private byte[] BuildListIdentityResponse(byte[] requestHeader)
        {
            using var ms = new MemoryStream();
            // Item count = 1
            ms.Write(new byte[] { 0x01, 0x00 }, 0, 2);
            // Item type = 0x000C (Identity)
            ms.Write(new byte[] { 0x0C, 0x00 }, 0, 2);

            // Identity item data
            using var idMs = new MemoryStream();
            // Encapsulation version
            idMs.Write(new byte[] { 0x01, 0x00 }, 0, 2);
            // Socket address: sin_family(2) + sin_port(2) + sin_addr(4) + sin_zero(8)
            idMs.Write(new byte[] { 0x02, 0x00 }, 0, 2);
            idMs.WriteByte((byte)((Port >> 8) & 0xFF));
            idMs.WriteByte((byte)(Port & 0xFF));
            idMs.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 12);
            // Vendor ID
            idMs.WriteByte((byte)(VendorId & 0xFF));
            idMs.WriteByte((byte)((VendorId >> 8) & 0xFF));
            // Device Type
            idMs.Write(new byte[] { 0x0C, 0x00 }, 0, 2);
            // Product Code
            idMs.Write(new byte[] { 0x01, 0x00 }, 0, 2);
            // Revision
            idMs.Write(new byte[] { 0x01, 0x00 }, 0, 2);
            // Status
            idMs.Write(new byte[] { 0x00, 0x00 }, 0, 2);
            // Serial Number
            idMs.WriteByte((byte)(SerialNumber & 0xFF));
            idMs.WriteByte((byte)((SerialNumber >> 8) & 0xFF));
            idMs.WriteByte((byte)((SerialNumber >> 16) & 0xFF));
            idMs.WriteByte((byte)((SerialNumber >> 24) & 0xFF));
            // Product Name (byte length + string)
            byte[] nameBytes = Encoding.ASCII.GetBytes(DeviceName);
            idMs.WriteByte((byte)nameBytes.Length);
            idMs.Write(nameBytes, 0, nameBytes.Length);
            // State
            idMs.WriteByte(0x03); // Run

            byte[] idData = idMs.ToArray();
            // Item length
            ms.WriteByte((byte)(idData.Length & 0xFF));
            ms.WriteByte((byte)((idData.Length >> 8) & 0xFF));
            ms.Write(idData, 0, idData.Length);

            byte[] payload = ms.ToArray();
            return BuildEnipResponse(EnipListIdentity, requestHeader, 0, payload);
        }

        // ── Register Session 响应 ────────────────

        private byte[] BuildRegisterSessionResponse(byte[] requestHeader, byte[] data)
        {
            if (data.Length < 4) return BuildEnipResponse(EnipRegisterSession, requestHeader, 0x0064, null);

            uint sessionId;
            lock (_sessionLock)
            {
                sessionId = _nextSessionId++;
            }
            _sessions.TryAdd(sessionId, 0);

            byte[] respData = new byte[4];
            respData[0] = (byte)(sessionId & 0xFF);
            respData[1] = (byte)((sessionId >> 8) & 0xFF);
            respData[2] = (byte)((sessionId >> 16) & 0xFF);
            respData[3] = (byte)((sessionId >> 24) & 0xFF);

            return BuildEnipResponseWithSession(EnipRegisterSession, requestHeader, 0, sessionId, respData);
        }

        // ── Unregister Session 响应 ──────────────

        private byte[] BuildUnregisterSessionResponse(byte[] requestHeader)
        {
            uint sessionId = (uint)(requestHeader[4] | (requestHeader[5] << 8) | (requestHeader[6] << 16) | (requestHeader[7] << 24));
            _sessions.TryRemove(sessionId, out _);
            return null; // 不发送响应
        }

        // ── SendRRData 处理 ──────────────────────

        private byte[]? ProcessSendRRData(byte[] requestHeader, byte[] data)
        {
            // SendRRData payload: InterfaceHandle(4) + Timeout(2) + ItemCount(2) + Items...
            if (data.Length < 8) return null;

            uint sessionId = (uint)(requestHeader[4] | (requestHeader[5] << 8) | (requestHeader[6] << 16) | (requestHeader[7] << 24));

            int itemCount = data[6] | (data[7] << 8);
            int offset = 8;

            // 查找 CIP 数据项 (type 0xB2)
            byte[]? cipData = null;
            for (int i = 0; i < itemCount; i++)
            {
                if (offset + 4 > data.Length) break;
                ushort itemType = (ushort)(data[offset] | (data[offset + 1] << 8));
                ushort itemLen = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
                offset += 4;

                if (itemType == 0xB2 && offset + itemLen <= data.Length)
                {
                    cipData = new byte[itemLen];
                    Buffer.BlockCopy(data, offset, cipData, 0, itemLen);
                }
                offset += itemLen;
            }

            if (cipData == null || cipData.Length < 2) return null;

            // 解析 CIP 服务
            byte service = cipData[0];
            byte[]? cipResponse = service switch
            {
                CipReadService => ProcessCipRead(cipData),
                CipWriteService => ProcessCipWrite(cipData),
                _ => BuildCipError(service, 0x08) // 服务不支持
            };

            if (cipResponse == null) return null;

            // 构建 SendRRData 响应
            byte[] rrData = BuildSendRRDataResponse(cipResponse);
            return BuildEnipResponseWithSession(EnipSendRRData, requestHeader, 0, sessionId, rrData);
        }

        // ── CIP Read Tag ─────────────────────────

        private byte[]? ProcessCipRead(byte[] cipData)
        {
            // CIP Read: Service(1) + PathSize(1) + Path(n) + Elements(2)
            if (cipData.Length < 4) return BuildCipError(CipReadService, 0x05);

            byte pathSize = cipData[1]; // 16-bit words
            int pathBytes = pathSize * 2;
            int dataStart = 2 + pathBytes;

            if (dataStart + 2 > cipData.Length) return BuildCipError(CipReadService, 0x05);

            ushort elements = (ushort)(cipData[dataStart] | (cipData[dataStart + 1] << 8));

            // 解析 Tag 路径
            string? tagName = ParseTagPath(cipData, 2, pathBytes);
            if (tagName == null) return BuildCipError(CipReadService, 0x05);

            if (!_tags.TryGetValue(tagName, out var tag))
                return BuildCipError(CipReadService, 0x14); // Tag 不存在

            // 编码 Tag 值为 CIP 响应数据
            byte[] tagData = EncodeTagValue(tag, elements);
            if (tagData == null) return BuildCipError(CipReadService, 0x15); // 类型不匹配

            // CIP Read 响应: Service(1) + Reserved(1) + Status(1) + ExtStatusSize(1) + Data(n)
            byte[] response = new byte[4 + tagData.Length];
            response[0] = (byte)(CipReadService | 0x80); // 响应服务码
            response[1] = 0x00; // Reserved
            response[2] = 0x00; // Status: Success
            response[3] = 0x00; // Ext status size
            Buffer.BlockCopy(tagData, 0, response, 4, tagData.Length);
            return response;
        }

        // ── CIP Write Tag ────────────────────────

        private byte[]? ProcessCipWrite(byte[] cipData)
        {
            // CIP Write: Service(1) + PathSize(1) + Path(n) + DataType(2) + Elements(2) + Data(n)
            if (cipData.Length < 6) return BuildCipError(CipWriteService, 0x05);

            byte pathSize = cipData[1];
            int pathBytes = pathSize * 2;
            int offset = 2 + pathBytes;

            if (offset + 4 > cipData.Length) return BuildCipError(CipWriteService, 0x05);

            ushort dataType = (ushort)(cipData[offset] | (cipData[offset + 1] << 8));
            ushort elements = (ushort)(cipData[offset + 2] | (cipData[offset + 3] << 8));
            offset += 4;

            // 解析 Tag 路径
            string? tagName = ParseTagPath(cipData, 2, pathBytes);
            if (tagName == null) return BuildCipError(CipWriteService, 0x05);

            // 从请求数据区读取写入值
            int dataLen = cipData.Length - offset;
            if (dataLen <= 0) return BuildCipError(CipWriteService, 0x13);

            byte[] writeData = new byte[dataLen];
            Buffer.BlockCopy(cipData, offset, writeData, 0, dataLen);

            // 解码并存储 Tag 值
            object? value = DecodeTagValue(dataType, writeData, elements);
            if (value == null) return BuildCipError(CipWriteService, 0x15);

            bool isNew = !_tags.ContainsKey(tagName);
            _tags.AddOrUpdate(tagName,
                _ => new CipTagEntry { Name = tagName, Value = value, DataType = dataType },
                (_, existing) => { existing.Value = value; return existing; });

            OnWriteReceived?.Invoke(this, new CipWriteEventArgs
            {
                TagName = tagName,
                DataType = dataType,
                Value = value,
                Elements = elements,
                Timestamp = DateTime.Now
            });

            // CIP Write 响应: Service(1) + Reserved(1) + Status(1) + ExtStatusSize(1)
            byte[] response = new byte[4];
            response[0] = (byte)(CipWriteService | 0x80);
            response[1] = 0x00;
            response[2] = 0x00;
            response[3] = 0x00;
            return response;
        }

        // ── Tag 路径解析 ─────────────────────────

        private string? ParseTagPath(byte[] data, int offset, int pathBytes)
        {
            using var ms = new MemoryStream();
            int pos = offset;
            int end = offset + pathBytes;

            while (pos < end)
            {
                if (pos + 2 > data.Length) return null;

                byte segmentType = data[pos];

                if (segmentType == 0x91) // Symbolic segment
                {
                    if (pos + 2 > data.Length) return null;
                    byte nameLen = data[pos + 1];
                    pos += 2;

                    if (pos + nameLen > data.Length) return null;
                    string name = Encoding.ASCII.GetString(data, pos, nameLen);
                    pos += nameLen;

                    // 跳过填充字节（偶数对齐）
                    if (nameLen % 2 == 0 && pos < end) pos++;

                    if (ms.Length > 0) ms.WriteByte((byte)'.');
                    byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                    ms.Write(nameBytes, 0, nameBytes.Length);
                }
                else if ((segmentType & 0xF0) == 0x20) // 数据段 (8-bit index)
                {
                    if (pos + 2 > data.Length) return null;
                    byte index = data[pos + 1];
                    pos += 2;
                    byte[] idxStr = Encoding.ASCII.GetBytes($"[{index}]");
                    ms.Write(idxStr, 0, idxStr.Length);
                }
                else if ((segmentType & 0xF0) == 0x28) // 数据段 (16-bit index)
                {
                    if (pos + 3 > data.Length) return null;
                    ushort index = (ushort)(data[pos + 1] | (data[pos + 2] << 8));
                    pos += 3;
                    byte[] idxStr = Encoding.ASCII.GetBytes($"[{index}]");
                    ms.Write(idxStr, 0, idxStr.Length);
                }
                else
                {
                    // 未知段类型，跳过
                    pos += 2;
                }
            }

            return ms.Length > 0 ? Encoding.ASCII.GetString(ms.ToArray()) : null;
        }

        // ── Tag 值编码 ───────────────────────────

        private byte[]? EncodeTagValue(CipTagEntry tag, ushort elements)
        {
            object val = tag.Value;

            if (tag.DataType == CipTypeBool)
            {
                bool boolVal = val is bool b ? b : Convert.ToBoolean(val);
                return new byte[] { (byte)(boolVal ? 1 : 0), 0x00 };
            }
            else if (tag.DataType == CipTypeSint)
            {
                sbyte v = val is sbyte sb ? sb : Convert.ToSByte(val);
                return new byte[] { (byte)v };
            }
            else if (tag.DataType == CipTypeInt)
            {
                short v = val is short s ? s : Convert.ToInt16(val);
                return new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF) };
            }
            else if (tag.DataType == CipTypeDint)
            {
                int v = val is int i ? i : Convert.ToInt32(val);
                return new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF) };
            }
            else if (tag.DataType == CipTypeLint)
            {
                long v = val is long l ? l : Convert.ToInt64(val);
                return new byte[] {
                    (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF),
                    (byte)((v >> 32) & 0xFF), (byte)((v >> 40) & 0xFF), (byte)((v >> 48) & 0xFF), (byte)((v >> 56) & 0xFF)
                };
            }
            else if (tag.DataType == CipTypeUsint)
            {
                byte v = val is byte bt ? bt : Convert.ToByte(val);
                return new byte[] { v };
            }
            else if (tag.DataType == CipTypeUint)
            {
                ushort v = val is ushort us ? us : Convert.ToUInt16(val);
                return new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF) };
            }
            else if (tag.DataType == CipTypeUdint)
            {
                uint v = val is uint ui ? ui : Convert.ToUInt32(val);
                return new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF) };
            }
            else if (tag.DataType == CipTypeReal)
            {
                float v = val is float f ? f : Convert.ToSingle(val);
                unsafe
                {
                    int iv = *(int*)&v;
                    return new byte[] { (byte)(iv & 0xFF), (byte)((iv >> 8) & 0xFF), (byte)((iv >> 16) & 0xFF), (byte)((iv >> 24) & 0xFF) };
                }
            }
            else if (tag.DataType == CipTypeLreal)
            {
                double v = val is double d ? d : Convert.ToDouble(val);
                unsafe
                {
                    long lv = *(long*)&v;
                    return new byte[] {
                        (byte)(lv & 0xFF), (byte)((lv >> 8) & 0xFF), (byte)((lv >> 16) & 0xFF), (byte)((lv >> 24) & 0xFF),
                        (byte)((lv >> 32) & 0xFF), (byte)((lv >> 40) & 0xFF), (byte)((lv >> 48) & 0xFF), (byte)((lv >> 56) & 0xFF)
                    };
                }
            }
            else if (tag.DataType == CipTypeString)
            {
                string str = val?.ToString() ?? string.Empty;
                byte[] strBytes = Encoding.ASCII.GetBytes(str);
                byte[] result = new byte[4 + strBytes.Length];
                result[0] = (byte)(strBytes.Length & 0xFF);
                result[1] = (byte)((strBytes.Length >> 8) & 0xFF);
                result[2] = (byte)((strBytes.Length >> 16) & 0xFF);
                result[3] = (byte)((strBytes.Length >> 24) & 0xFF);
                Buffer.BlockCopy(strBytes, 0, result, 4, strBytes.Length);
                return result;
            }

            return null;
        }

        // ── Tag 值解码 ───────────────────────────

        private object? DecodeTagValue(ushort dataType, byte[] data, ushort elements)
        {
            try
            {
                if (dataType == CipTypeBool)
                {
                    return data.Length >= 1 ? data[0] != 0 : false;
                }
                else if (dataType == CipTypeSint)
                {
                    return data.Length >= 1 ? (sbyte)data[0] : (sbyte)0;
                }
                else if (dataType == CipTypeInt)
                {
                    if (data.Length < 2) return (short)0;
                    return (short)(data[0] | (data[1] << 8));
                }
                else if (dataType == CipTypeDint)
                {
                    if (data.Length < 4) return 0;
                    return data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
                }
                else if (dataType == CipTypeLint)
                {
                    if (data.Length < 8) return 0L;
                    uint lo = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                    uint hi = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));
                    return ((long)hi << 32) | lo;
                }
                else if (dataType == CipTypeUsint)
                {
                    return data.Length >= 1 ? data[0] : (byte)0;
                }
                else if (dataType == CipTypeUint)
                {
                    if (data.Length < 2) return (ushort)0;
                    return (ushort)(data[0] | (data[1] << 8));
                }
                else if (dataType == CipTypeUdint)
                {
                    if (data.Length < 4) return 0u;
                    return (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                }
                else if (dataType == CipTypeReal)
                {
                    if (data.Length < 4) return 0f;
                    int iv = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
                    unsafe { float f = *(float*)&iv; return f; }
                }
                else if (dataType == CipTypeLreal)
                {
                    if (data.Length < 8) return 0.0;
                    uint lo = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
                    uint hi = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));
                    long lv = ((long)hi << 32) | lo;
                    unsafe { double d = *(double*)&lv; return d; }
                }
                else if (dataType == CipTypeString)
                {
                    if (data.Length < 4) return string.Empty;
                    int strLen = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
                    if (data.Length < 4 + strLen) return string.Empty;
                    return Encoding.ASCII.GetString(data, 4, strLen);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // ── CIP 辅助 ─────────────────────────────

        private static ushort GetCipTypeFromValue(object value)
        {
            return value switch
            {
                bool => CipTypeBool,
                sbyte => CipTypeSint,
                byte => CipTypeUsint,
                short => CipTypeInt,
                ushort => CipTypeUint,
                int => CipTypeDint,
                uint => CipTypeUdint,
                long => CipTypeLint,
                float => CipTypeReal,
                double => CipTypeLreal,
                string => CipTypeString,
                _ => CipTypeDint
            };
        }

        private byte[] BuildCipError(byte service, byte status)
        {
            return new byte[] { (byte)(service | 0x80), 0x00, status, 0x00 };
        }

        // ── ENIP 响应构建 ────────────────────────

        private byte[] BuildSendRRDataResponse(byte[] cipData)
        {
            int dataLen = cipData.Length;
            int totalLen = 4 + 2 + 2 + 2 + 2 + 2 + 2 + dataLen;
            byte[] result = new byte[totalLen];
            int i = 0;
            // Interface Handle = 0
            result[i++] = 0; result[i++] = 0; result[i++] = 0; result[i++] = 0;
            // Timeout = 0
            result[i++] = 0; result[i++] = 0;
            // Item Count = 2
            result[i++] = 2; result[i++] = 0;
            // Item 1: Null Address
            result[i++] = 0x00; result[i++] = 0x00;
            result[i++] = 0x00; result[i++] = 0x00;
            // Item 2: Unconnected Data
            result[i++] = 0xB2; result[i++] = 0x00;
            result[i++] = (byte)(dataLen & 0xFF); result[i++] = (byte)((dataLen >> 8) & 0xFF);
            Buffer.BlockCopy(cipData, 0, result, i, dataLen);
            return result;
        }

        private byte[] BuildEnipResponse(ushort command, byte[] requestHeader, uint status, byte[]? payload)
        {
            return BuildEnipResponseWithSession(command, requestHeader, status, 0, payload);
        }

        private byte[] BuildEnipResponseWithSession(ushort command, byte[] requestHeader, uint status, uint sessionHandle, byte[]? payload)
        {
            int dataLen = payload?.Length ?? 0;
            byte[] response = new byte[24 + dataLen];

            // Command
            response[0] = (byte)(command & 0xFF);
            response[1] = (byte)((command >> 8) & 0xFF);
            // Length
            response[2] = (byte)(dataLen & 0xFF);
            response[3] = (byte)((dataLen >> 8) & 0xFF);
            // Session Handle
            response[4] = (byte)(sessionHandle & 0xFF);
            response[5] = (byte)((sessionHandle >> 8) & 0xFF);
            response[6] = (byte)((sessionHandle >> 16) & 0xFF);
            response[7] = (byte)((sessionHandle >> 24) & 0xFF);
            // Status
            response[8] = (byte)(status & 0xFF);
            response[9] = (byte)((status >> 8) & 0xFF);
            response[10] = (byte)((status >> 16) & 0xFF);
            response[11] = (byte)((status >> 24) & 0xFF);
            // Sender Context (echo back)
            if (requestHeader.Length >= 20)
            {
                Buffer.BlockCopy(requestHeader, 12, response, 12, 8);
            }
            // Options = 0
            // response[20-23] = 0

            if (payload != null && dataLen > 0)
            {
                Buffer.BlockCopy(payload, 0, response, 24, dataLen);
            }

            return response;
        }

        // ── 辅助 ──────────────────────────────────

        private static byte[]? ReadExact(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buf, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        public void Dispose() => Stop();
    }

    internal class CipTagEntry
    {
        public string Name { get; set; } = string.Empty;
        public object Value { get; set; } = 0;
        public ushort DataType { get; set; }
    }

    /// <summary>CIP 写入事件参数。</summary>
    public class CipWriteEventArgs : EventArgs
    {
        public string TagName { get; set; } = string.Empty;
        public ushort DataType { get; set; }
        public object? Value { get; set; }
        public ushort Elements { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
