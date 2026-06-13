using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// Omron EtherNet/IP Connected CIP 客户端 — 支持 NJ/NX/CJ 系列 PLC Class 3 连接消息。
    /// <para>协议层次: TCP → ENIP → CIP Connected (Forward Open/Close) → Tag 读写</para>
    /// <para>相比 OmronCipClient（Class 0 无连接），Connected CIP 建立 CIP 连接后使用 SendUnitData，
    /// 适合高频读写场景，减少每次请求的路由开销。</para>
    /// <para>地址格式: TagName, TagName.member, TagName[index], TagName.member[index]</para>
    /// </summary>
    public class OmronConnectedCipClient : OmronCipClient
    {
        private ushort _connectionSerial;
        private uint _originatingConnectionId;
        private uint _targetConnectionId;
        private bool _connected;
        private Timer? _keepAliveTimer;
        private readonly object _connLock = new object();

        /// <summary>Omron 厂商 ID。</summary>
        private const ushort VendorId = 47;
        /// <summary>连接序列号初始值（由客户端分配）。</summary>
        private const ushort DefaultConnectionSerialBase = 0x0001;
        /// <summary>Target-to-Origin 连接 ID。</summary>
        private const uint DefaultT2OConnectionId = 0x20000001;
        /// <summary>Origin-to-Target 连接 ID。</summary>
        private const uint DefaultO2TConnectionId = 0x20000002;
        /// <summary>连接超时倍数（Ticks per second）。</summary>
        private const uint ConnectionTimeoutMultiplier = 4000;
        /// <summary>Forward Open 超时（秒）。</summary>
        private const int ForwardOpenTimeoutSec = 10;

        /// <summary>是否已建立 CIP 连接。</summary>
        public bool IsConnectedMessaging => _connected;

        /// <summary>Keep-alive 间隔（毫秒），默认 10000。</summary>
        public int KeepAliveIntervalMs { get; set; } = 10000;

        /// <summary>
        /// 创建 Omron Connected CIP 客户端。
        /// </summary>
        /// <param name="ipAddress">PLC IP 地址</param>
        /// <param name="port">端口号（默认 44818）</param>
        /// <param name="slot">机架/单元号（默认 0）</param>
        /// <param name="timeout">超时毫秒（默认 5000）</param>
        public OmronConnectedCipClient(string ipAddress, int port = 44818, byte slot = 0, int timeout = 5000)
            : base(ipAddress, port, slot, timeout)
        {
        }

        // ═══════════════════════════════════════════
        //  CIP Connection Manager 服务
        // ═══════════════════════════════════════════

        private const byte CmForwardOpen = 0x54;
        private const byte CmForwardClose = 0x4E;
        private const byte CmGetConnectionOwner = 0x5A;

        /// <summary>
        /// 执行 CIP Forward Open（建立 Class 3 连接）。
        /// <para>参考 CIP Vol 1, Section 3-5.5 — Connection Manager</para>
        /// </summary>
        private OperateResult CipForwardOpen()
        {
            try
            {
                // 连接序列号
                _connectionSerial = (ushort)(DefaultConnectionSerialBase + (ushort)(Environment.TickCount & 0xFFFF));

                // Forward Open Request:
                //   Service(1) + PathSize(1) + Path(2) + Priority(1) + Ticks(1) + Timeout(2)
                //   + O2T ConnId(4) + T2O ConnId(4) + ConnSerial(2) + VendorId(2) + OriginSn(4)
                //   + TimeoutMult(4) + O2T RPI(4) + O2T ConnParams(2) + T2O RPI(4) + T2O ConnParams(2)
                //   + TransportClass(1) + PathSize(1) + Path(n)

                byte[] connPath = BuildConnectionPath();
                int connPathWords = (connPath.Length + 1) / 2;

                // CM Object path: Class 0x06, Instance 0x01
                byte[] cmPath = new byte[] { 0x20, 0x06, 0x24, 0x01 };
                int cmPathWords = (cmPath.Length + 1) / 2;

                // Build the full CIP request
                int foDataLen = 2 + cmPathWords * 2  // Path
                    + 2                                // Priority + Ticks + Timeout
                    + 4 + 4                           // O2T ConnId + T2O ConnId
                    + 2 + 2 + 4                       // ConnSerial + VendorId + OriginSn
                    + 4                               // TimeoutMult
                    + 4 + 2                           // O2T RPI + O2T ConnParams
                    + 4 + 2                           // T2O RPI + T2O ConnParams
                    + 1                               // TransportClass
                    + 1 + connPathWords * 2;          // ConnPathSize + ConnPath

                byte[] cipReq = new byte[foDataLen];
                int pos = 0;

                // Service: Forward Open
                cipReq[pos++] = CmForwardOpen;
                // Path size (in words)
                cipReq[pos++] = (byte)cmPathWords;
                // Path: CM Object
                Buffer.BlockCopy(cmPath, 0, cipReq, pos, cmPath.Length);
                pos += cmPathWords * 2;

                // Priority / Tick / Timeout
                cipReq[pos++] = 0x07; // Priority=Low(0), Tick=7
                cipReq[pos++] = (byte)(ForwardOpenTimeoutSec & 0xFF); // Timeout

                // O2T Connection ID (assigned by us)
                cipReq[pos++] = (byte)(DefaultO2TConnectionId & 0xFF);
                cipReq[pos++] = (byte)((DefaultO2TConnectionId >> 8) & 0xFF);
                cipReq[pos++] = (byte)((DefaultO2TConnectionId >> 16) & 0xFF);
                cipReq[pos++] = (byte)((DefaultO2TConnectionId >> 24) & 0xFF);

                // T2O Connection ID (assigned by us, will be overridden by target)
                cipReq[pos++] = (byte)(DefaultT2OConnectionId & 0xFF);
                cipReq[pos++] = (byte)((DefaultT2OConnectionId >> 8) & 0xFF);
                cipReq[pos++] = (byte)((DefaultT2OConnectionId >> 16) & 0xFF);
                cipReq[pos++] = (byte)((DefaultT2OConnectionId >> 24) & 0xFF);

                // Connection Serial Number
                cipReq[pos++] = (byte)(_connectionSerial & 0xFF);
                cipReq[pos++] = (byte)((_connectionSerial >> 8) & 0xFF);

                // Vendor ID (Omron = 47)
                cipReq[pos++] = (byte)(VendorId & 0xFF);
                cipReq[pos++] = (byte)((VendorId >> 8) & 0xFF);

                // Originator Serial Number
                uint originSn = (uint)(Environment.TickCount & 0x7FFFFFFF);
                cipReq[pos++] = (byte)(originSn & 0xFF);
                cipReq[pos++] = (byte)((originSn >> 8) & 0xFF);
                cipReq[pos++] = (byte)((originSn >> 16) & 0xFF);
                cipReq[pos++] = (byte)((originSn >> 24) & 0xFF);

                // Connection Timeout Multiplier
                cipReq[pos++] = 0x03; // multiplier = 3

                // O2T RPI (Requested Packet Interval) in microseconds
                uint o2tRpi = 100000; // 100ms
                cipReq[pos++] = (byte)(o2tRpi & 0xFF);
                cipReq[pos++] = (byte)((o2tRpi >> 8) & 0xFF);
                cipReq[pos++] = (byte)((o2tRpi >> 16) & 0xFF);
                cipReq[pos++] = (byte)((o2tRpi >> 24) & 0xFF);

                // O2T Connection Parameters
                // Class 3, P2P, Variable, 500 bytes
                ushort o2tParams = 0x4200; // Variable size, Class 3
                cipReq[pos++] = (byte)(o2tParams & 0xFF);
                cipReq[pos++] = (byte)((o2tParams >> 8) & 0xFF);

                // T2O RPI
                uint t2oRpi = 100000; // 100ms
                cipReq[pos++] = (byte)(t2oRpi & 0xFF);
                cipReq[pos++] = (byte)((t2oRpi >> 8) & 0xFF);
                cipReq[pos++] = (byte)((t2oRpi >> 16) & 0xFF);
                cipReq[pos++] = (byte)((t2oRpi >> 24) & 0xFF);

                // T2O Connection Parameters
                ushort t2oParams = 0x4200;
                cipReq[pos++] = (byte)(t2oParams & 0xFF);
                cipReq[pos++] = (byte)((t2oParams >> 8) & 0xFF);

                // Transport Class / Trigger
                cipReq[pos++] = 0xA3; // Class 3, Cyclic, Application

                // Connection Path Size (in words) + Connection Path
                cipReq[pos++] = (byte)connPathWords;
                Buffer.BlockCopy(connPath, 0, cipReq, pos, connPath.Length);

                byte[] enipData = BuildSendRRData(cipReq);
                var result = SendEnip(EnipCommand.SendRRData, enipData);
                if (!result.IsSuccess)
                    return OperateResult.Failed($"Forward Open 失败: {result.Message}");

                var parsed = ParseCipResponse(result.Content);
                if (!parsed.IsSuccess)
                    return OperateResult.Failed($"Forward Open CIP 错误: {parsed.Message}");

                // Parse Forward Open response
                byte[] foResp = parsed.Content;
                if (foResp.Length >= 16)
                {
                    _originatingConnectionId = ToUInt32LE(foResp, 0);
                    _targetConnectionId = ToUInt32LE(foResp, 4);

                    // Connection Serial Number (may be modified by target)
                    _connectionSerial = ToUInt16LE(foResp, 8);
                }

                _connected = true;
                Log.Debug($"Forward Open 成功 — O2T=0x{_originatingConnectionId:X8}, T2O=0x{_targetConnectionId:X8}, Serial=0x{_connectionSerial:X4}");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"CipForwardOpen 异常 — {ex.Message}");
                return OperateResult.Failed($"Forward Open 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 执行 CIP Forward Close（关闭 Class 3 连接）。
        /// </summary>
        private OperateResult CipForwardClose()
        {
            try
            {
                byte[] connPath = BuildConnectionPath();
                int connPathWords = (connPath.Length + 1) / 2;

                byte[] cmPath = new byte[] { 0x20, 0x06, 0x24, 0x01 };
                int cmPathWords = (cmPath.Length + 1) / 2;

                int fcDataLen = 2 + cmPathWords * 2 + 2 + 2 + 2 + 1 + connPathWords * 2;
                byte[] cipReq = new byte[fcDataLen];
                int pos = 0;

                cipReq[pos++] = CmForwardClose;
                cipReq[pos++] = (byte)cmPathWords;
                Buffer.BlockCopy(cmPath, 0, cipReq, pos, cmPath.Length);
                pos += cmPathWords * 2;

                // Priority / Tick
                cipReq[pos++] = 0x07;

                // Connection Serial Number
                cipReq[pos++] = (byte)(_connectionSerial & 0xFF);
                cipReq[pos++] = (byte)((_connectionSerial >> 8) & 0xFF);

                // Vendor ID
                cipReq[pos++] = (byte)(VendorId & 0xFF);
                cipReq[pos++] = (byte)((VendorId >> 8) & 0xFF);

                // Originator Serial Number (use 0 for close)
                cipReq[pos++] = 0; cipReq[pos++] = 0;
                cipReq[pos++] = 0; cipReq[pos++] = 0;

                // Connection Path
                cipReq[pos++] = (byte)connPathWords;
                Buffer.BlockCopy(connPath, 0, cipReq, pos, connPath.Length);

                byte[] enipData = BuildSendRRData(cipReq);
                var result = SendEnip(EnipCommand.SendRRData, enipData);
                if (!result.IsSuccess)
                    return OperateResult.Failed($"Forward Close 失败: {result.Message}");

                var parsed = ParseCipResponse(result.Content);
                if (!parsed.IsSuccess)
                    return OperateResult.Failed($"Forward Close CIP 错误: {parsed.Message}");

                _connected = false;
                Log.Debug("Forward Close 成功");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"CipForwardClose 异常 — {ex.Message}");
                return OperateResult.Failed($"Forward Close 异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  Connected 读写（SendUnitData）
        // ═══════════════════════════════════════════

        /// <summary>
        /// 通过 Connected 连接读取 Tag（使用 SendUnitData）。
        /// </summary>
        private OperateResult<byte[]> ReadTagRawConnected(string tagName, ushort elements = 1)
        {
            if (!_connected)
                return OperateResult<byte[]>.Failed("未建立 CIP 连接，请先调用 Connect()");

            byte[] path = EncodeTagPath(tagName);

            int pathWords = (path.Length + 1) / 2;
            byte[] cipReq = new byte[2 + pathWords * 2 + 2];
            cipReq[0] = CipReadService;
            cipReq[1] = (byte)pathWords;
            Buffer.BlockCopy(path, 0, cipReq, 2, path.Length);
            int offset = 2 + pathWords * 2;
            cipReq[offset] = (byte)(elements & 0xFF);
            cipReq[offset + 1] = (byte)((elements >> 8) & 0xFF);

            byte[] enipData = BuildSendUnitData(cipReq);
            var result = SendEnip(EnipCommand.SendUnitData, enipData);
            if (!result.IsSuccess) return result;

            return ParseCipResponse(result.Content);
        }

        /// <summary>
        /// 通过 Connected 连接写入 Tag（使用 SendUnitData）。
        /// </summary>
        private OperateResult WriteTagRawConnected(string tagName, ushort dataType, byte[] data, ushort elements = 1)
        {
            if (!_connected)
                return OperateResult.Failed("未建立 CIP 连接，请先调用 Connect()");

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

            byte[] enipData = BuildSendUnitData(cipReq);
            var result = SendEnip(EnipCommand.SendUnitData, enipData);
            if (!result.IsSuccess) return result;

            return ParseCipResponse(result.Content);
        }

        /// <summary>
        /// 构建 ENIP SendUnitData 数据包（Connected 消息）。
        /// <para>格式: InterfaceHandle(4) + Timeout(2) + ItemCount(2) + ConnectedAddressItem + ConnectedDataItem</para>
        /// </summary>
        private byte[] BuildSendUnitData(byte[] cipData)
        {
            int dataLen = cipData.Length;
            // Header: InterfaceHandle(4) + Timeout(2) + ItemCount(2) = 8
            // Item 1: Connected Address (Type=0x00A1, Length=4, ConnectionId=4)
            // Item 2: Connected Data (Type=0x00B1, Length=dataLen, Data=dataLen)
            int totalLen = 8 + 8 + 4 + dataLen;

            byte[] result = new byte[totalLen];
            int i = 0;

            // Interface Handle = 0
            result[i++] = 0; result[i++] = 0; result[i++] = 0; result[i++] = 0;
            // Timeout = 0
            result[i++] = 0; result[i++] = 0;
            // Item Count = 2
            result[i++] = 2; result[i++] = 0;

            // Item 1: Connected Address (0x00A1)
            result[i++] = 0xA1; result[i++] = 0x00;
            result[i++] = 4; result[i++] = 0; // Length = 4
            // Connection ID (T2O, as seen by target)
            result[i++] = (byte)(_targetConnectionId & 0xFF);
            result[i++] = (byte)((_targetConnectionId >> 8) & 0xFF);
            result[i++] = (byte)((_targetConnectionId >> 16) & 0xFF);
            result[i++] = (byte)((_targetConnectionId >> 24) & 0xFF);

            // Item 2: Connected Data (0x00B1)
            result[i++] = 0xB1; result[i++] = 0x00;
            result[i++] = (byte)(dataLen & 0xFF);
            result[i++] = (byte)((dataLen >> 8) & 0xFF);
            Buffer.BlockCopy(cipData, 0, result, i, dataLen);

            return result;
        }

        // ═══════════════════════════════════════════
        //  Keep-Alive（连接保活）
        // ═══════════════════════════════════════════

        private void StartKeepAlive()
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = new Timer(KeepAliveTick, null, KeepAliveIntervalMs, KeepAliveIntervalMs);
        }

        private void StopKeepAlive()
        {
            _keepAliveTimer?.Dispose();
            _keepAliveTimer = null;
        }

        private void KeepAliveTick(object? state)
        {
            if (!_connected) return;
            try
            {
                // Send a NOP as keep-alive
                SendEnip(EnipCommand.Nop, Array.Empty<byte>());
            }
            catch
            {
                Log.Warn("Keep-alive 失败，连接可能已断开");
            }
        }

        // ═══════════════════════════════════════════
        //  连接管理（Override）
        // ═══════════════════════════════════════════

        /// <summary>
        /// 建立 TCP 连接并执行 CIP Forward Open。
        /// </summary>
        public override OperateResult Connect()
        {
            // Step 1: TCP + RegisterSession
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;

            // Step 2: Forward Open (建立 CIP Connected 连接)
            var foResult = CipForwardOpen();
            if (!foResult.IsSuccess)
            {
                Log.Error($"Forward Open 失败: {foResult.Message}");
                base.Disconnect();
                return foResult;
            }

            // Step 3: Start keep-alive
            StartKeepAlive();

            Log.Debug($"Omron Connected CIP 已连接 — {IpAddress}:{Port}");
            return OperateResult.Success();
        }

        /// <summary>
        /// 关闭 CIP 连接（Forward Close）并断开 TCP。
        /// </summary>
        public override void Disconnect()
        {
            StopKeepAlive();

            lock (_connLock)
            {
                if (_connected)
                {
                    try { CipForwardClose(); } catch { }
                    _connected = false;
                }
            }

            base.Disconnect();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopKeepAlive();
            }
            base.Dispose(disposing);
        }

        // ═══════════════════════════════════════════
        //  Connected Tag 读写（Override 基类方法）
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadTagRawSmart(string tagName, ushort elements = 1)
        {
            return _connected ? ReadTagRawConnected(tagName, elements) : ReadTagRaw(tagName, elements);
        }

        private OperateResult WriteTagRawSmart(string tagName, ushort dataType, byte[] data, ushort elements = 1)
        {
            return _connected ? WriteTagRawConnected(tagName, dataType, data, elements) : WriteTagRaw(tagName, dataType, data, elements);
        }

        /// <summary>读取 CIP Tag 值（自动选择 Connected 或 Unconnected 模式）。</summary>
        public OperateResult<bool> ReadBoolConnected(string address)
        {
            var r = ReadTagRawSmart(address);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            int offset = r.Content.Length >= 3 ? 2 : 0;
            if (r.Content.Length < offset + 1) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success(r.Content[offset] != 0);
        }

        /// <summary>读取 CIP Tag 值（Connected 模式，Int16）。</summary>
        public OperateResult<short> ReadInt16Connected(string address)
        {
            var r = ReadTagRawSmart(address);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            int offset = r.Content.Length >= 4 ? 2 : 0;
            if (r.Content.Length < offset + 2) return OperateResult<short>.Failed("响应数据不足");
            return OperateResult<short>.Success((short)(r.Content[offset] | (r.Content[offset + 1] << 8)));
        }

        /// <summary>读取 CIP Tag 值（Connected 模式，Int32）。</summary>
        public OperateResult<int> ReadInt32Connected(string address)
        {
            var r = ReadTagRawSmart(address);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            int offset = r.Content.Length >= 6 ? 2 : 0;
            if (r.Content.Length < offset + 4) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success(r.Content[offset] | (r.Content[offset + 1] << 8) | (r.Content[offset + 2] << 16) | (r.Content[offset + 3] << 24));
        }

        /// <summary>读取 CIP Tag 值（Connected 模式，Float）。</summary>
        public unsafe OperateResult<float> ReadFloatConnected(string address)
        {
            var r = ReadTagRawSmart(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            int offset = r.Content.Length >= 6 ? 2 : 0;
            if (r.Content.Length < offset + 4) return OperateResult<float>.Failed("响应数据不足");
            int v = r.Content[offset] | (r.Content[offset + 1] << 8) | (r.Content[offset + 2] << 16) | (r.Content[offset + 3] << 24);
            return OperateResult<float>.Success(*(float*)&v);
        }

        /// <summary>写入 Tag（Connected 模式，Int32）。</summary>
        public OperateResult WriteInt32Connected(string address, int value)
        {
            var data = new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF) };
            return WriteTagRawSmart(address, CipTypeDint, data);
        }

        /// <summary>写入 Tag（Connected 模式，Float）。</summary>
        public unsafe OperateResult WriteFloatConnected(string address, float value)
        {
            int v = *(int*)&value;
            var data = new byte[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF) };
            return WriteTagRawSmart(address, CipTypeReal, data);
        }
    }
}
