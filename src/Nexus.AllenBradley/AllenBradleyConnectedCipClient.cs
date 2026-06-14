using System;

namespace Nexus.AllenBradley
{
    /// <summary>
    /// Allen-Bradley Connected CIP 客户端 — 使用 Forward Open 建立连接路径后再发送 CIP 消息。
    /// <para>协议层次: TCP → ENIP → CIP (Class 3 Connected)</para>
    /// <para>继承 AllenBradleyCipClient，增加 Forward Open / Forward Close 连接管理。</para>
    /// <para>适用: 大数据量传输、需要确定性连接的场景。</para>
    /// </summary>
    public class AllenBradleyConnectedCipClient : AllenBradleyCipClient
    {
        private uint _connectionIdO2T;
        private uint _connectionIdT2O;
        private ushort _connectionSerialNumber;
        private bool _connectedCipActive;
        private uint _sequenceCount;

        /// <summary>Target to Origin 连接 ID。</summary>
        public uint ConnectionIdT2O => _connectionIdT2O;

        /// <summary>Origin to Target 连接 ID。</summary>
        public uint ConnectionIdO2T => _connectionIdO2T;

        /// <summary>Connected CIP 连接是否已建立。</summary>
        public bool IsConnectedCip => _connectedCipActive;

        /// <summary>连接序列号。</summary>
        public ushort ConnectionSerialNumber => _connectionSerialNumber;

        /// <summary>O→T 连接大小（字节，默认 500）。</summary>
        public ushort ConnectionSizeO2T { get; set; } = 500;

        /// <summary>T→O 连接大小（字节，默认 500）。</summary>
        public ushort ConnectionSizeT2O { get; set; } = 500;

        /// <summary>连接超时倍数（默认 4，单位 250ms，即 1 秒）。</summary>
        public byte TimeoutMultiplier { get; set; } = 4;

        public AllenBradleyConnectedCipClient(string ipAddress, int port = 44818, byte slot = 0, int timeout = 5000)
            : base(ipAddress, port, slot, timeout)
        {
        }

        /// <summary>
        /// 发送 Forward Open 请求建立 Connected CIP 连接。
        /// </summary>
        public OperateResult OpenConnection()
        {
            try
            {
                _connectionSerialNumber = (ushort)(new Random().Next(0xFFFF) + 1);
                byte[] forwardOpenReq = BuildForwardOpenRequest();

                byte[] enipData = BuildSendRRData(forwardOpenReq);
                var result = SendEnip(EnipCommand.SendRRData, enipData);
                if (!result.IsSuccess)
                    return OperateResult.Failed($"Forward Open 失败: {result.Message}");

                var parsed = ParseCipResponse(result.Content);
                if (!parsed.IsSuccess)
                    return OperateResult.Failed($"Forward Open CIP 错误: {parsed.Message}");

                if (parsed.Content.Length < 16)
                    return OperateResult.Failed("Forward Open 响应数据不足");

                _connectionIdO2T = (uint)(parsed.Content[0] | (parsed.Content[1] << 8) |
                                          (parsed.Content[2] << 16) | (parsed.Content[3] << 24));
                _connectionIdT2O = (uint)(parsed.Content[4] | (parsed.Content[5] << 8) |
                                          (parsed.Content[6] << 16) | (parsed.Content[7] << 24));

                _connectedCipActive = true;
                _sequenceCount = 0;
                Log.Debug($"Connected CIP 已建立: O2T=0x{_connectionIdO2T:X8}, T2O=0x{_connectionIdT2O:X8}, SN=0x{_connectionSerialNumber:X4}");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"OpenConnection 异常 — {ex.Message}");
                return OperateResult.Failed($"OpenConnection 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送 Forward Close 请求断开 Connected CIP 连接。
        /// </summary>
        public OperateResult CloseConnection()
        {
            if (!_connectedCipActive)
                return OperateResult.Success();

            try
            {
                byte[] forwardCloseReq = BuildForwardCloseRequest();
                byte[] enipData = BuildSendRRData(forwardCloseReq);
                var result = SendEnip(EnipCommand.SendRRData, enipData);
                if (!result.IsSuccess)
                    Log.Debug($"Forward Close 非致命错误: {result.Message}");

                _connectedCipActive = false;
                _connectionIdO2T = 0;
                _connectionIdT2O = 0;
                Log.Debug("Connected CIP 已关闭");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"CloseConnection 异常 — {ex.Message}");
                _connectedCipActive = false;
                return OperateResult.Failed($"CloseConnection 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 通过 Connected CIP 读取 Tag — 使用连接 ID 发送消息。
        /// </summary>
        public OperateResult<byte[]> ReadTagConnected(string tagName, ushort elements = 1)
        {
            if (!_connectedCipActive)
                return OperateResult<byte[]>.Failed("Connected CIP 未建立，请先调用 OpenConnection()");

            byte[] path = EncodeTagPath(tagName);
            int pathWords = (path.Length + 1) / 2;
            byte[] cipReq = new byte[2 + pathWords * 2 + 2];
            cipReq[0] = 0x4C;
            cipReq[1] = (byte)pathWords;
            Buffer.BlockCopy(path, 0, cipReq, 2, path.Length);
            int offset = 2 + pathWords * 2;
            cipReq[offset] = (byte)(elements & 0xFF);
            cipReq[offset + 1] = (byte)((elements >> 8) & 0xFF);

            return SendConnectedCip(cipReq);
        }

        /// <summary>
        /// 通过 Connected CIP 写入 Tag。
        /// </summary>
        public OperateResult WriteTagConnected(string tagName, ushort dataType, byte[] data, ushort elements = 1)
        {
            if (!_connectedCipActive)
                return OperateResult.Failed("Connected CIP 未建立，请先调用 OpenConnection()");

            byte[] path = EncodeTagPath(tagName);
            int pathWords = (path.Length + 1) / 2;
            byte[] cipReq = new byte[2 + pathWords * 2 + 2 + 2 + data.Length];
            cipReq[0] = 0x4D;
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

            var result = SendConnectedCip(cipReq);
            if (!result.IsSuccess)
                return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>发送 Connected CIP 请求并解析响应。</summary>
        private OperateResult<byte[]> SendConnectedCip(byte[] cipData)
        {
            try
            {
                _sequenceCount++;
                byte[] enipData = BuildConnectedMessageData(cipData);
                var result = SendEnip(EnipCommand.SendUnitData, enipData);
                if (!result.IsSuccess)
                    return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

                return ParseCipResponse(result.Content);
            }
            catch (Exception ex)
            {
                Log.Error($"SendConnectedCip 异常 — {ex.Message}");
                return OperateResult<byte[]>.Failed($"SendConnectedCip 异常: {ex.Message}");
            }
        }

        /// <summary>构建 Forward Open CIP 请求。</summary>
        private byte[] BuildForwardOpenRequest()
        {
            var ms = new System.IO.MemoryStream();

            ms.WriteByte(0x54);
            ms.WriteByte(0x02);
            ms.WriteByte(0x20);
            ms.WriteByte(0x06);
            ms.WriteByte(0x24);
            ms.WriteByte(0x01);

            ms.WriteByte(0x0A);
            ms.WriteByte(TimeoutMultiplier);

            ms.Write(new byte[4], 0, 4);
            ms.Write(new byte[4], 0, 4);

            ms.WriteByte((byte)(_connectionSerialNumber & 0xFF));
            ms.WriteByte((byte)((_connectionSerialNumber >> 8) & 0xFF));

            ms.WriteByte(0x01);
            ms.WriteByte(0x00);

            ms.WriteByte(0x01); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x00);

            ms.WriteByte(TimeoutMultiplier);

            ms.WriteByte(0x00); ms.WriteByte(0x00); ms.WriteByte(0x00);

            uint rpi = 100000;
            ms.WriteByte((byte)(rpi & 0xFF));
            ms.WriteByte((byte)((rpi >> 8) & 0xFF));
            ms.WriteByte((byte)((rpi >> 16) & 0xFF));
            ms.WriteByte((byte)((rpi >> 24) & 0xFF));

            ushort connParamO2T = (ushort)(0x4000 | (ConnectionSizeO2T & 0x01FF));
            ms.WriteByte((byte)(connParamO2T & 0xFF));
            ms.WriteByte((byte)((connParamO2T >> 8) & 0xFF));

            ms.WriteByte((byte)(rpi & 0xFF));
            ms.WriteByte((byte)((rpi >> 8) & 0xFF));
            ms.WriteByte((byte)((rpi >> 16) & 0xFF));
            ms.WriteByte((byte)((rpi >> 24) & 0xFF));

            ushort connParamT2O = (ushort)(0x4000 | (ConnectionSizeT2O & 0x01FF));
            ms.WriteByte((byte)(connParamT2O & 0xFF));
            ms.WriteByte((byte)((connParamT2O >> 8) & 0xFF));

            ms.WriteByte(0xA3);

            byte[] connPath = BuildPath(Slot);
            int connPathWords = (connPath.Length + 1) / 2;
            ms.WriteByte((byte)connPathWords);
            ms.Write(connPath, 0, connPath.Length);

            return ms.ToArray();
        }

        /// <summary>构建 Forward Close CIP 请求。</summary>
        private byte[] BuildForwardCloseRequest()
        {
            var ms = new System.IO.MemoryStream();

            ms.WriteByte(0x4E);
            ms.WriteByte(0x02);
            ms.WriteByte(0x20);
            ms.WriteByte(0x06);
            ms.WriteByte(0x24);
            ms.WriteByte(0x01);

            ms.WriteByte(0x0A);
            ms.WriteByte(TimeoutMultiplier);

            ms.WriteByte((byte)(_connectionSerialNumber & 0xFF));
            ms.WriteByte((byte)((_connectionSerialNumber >> 8) & 0xFF));

            ms.WriteByte(0x01);
            ms.WriteByte(0x00);

            ms.WriteByte(0x01); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x00);

            byte[] connPath = BuildPath(Slot);
            int connPathWords = (connPath.Length + 1) / 2;
            ms.WriteByte((byte)connPathWords);
            ms.Write(connPath, 0, connPath.Length);

            return ms.ToArray();
        }

        /// <summary>构建 Connected Message ENIP 数据（SendUnitData 的 payload，不含 ENIP 头）。</summary>
        private byte[] BuildConnectedMessageData(byte[] cipData)
        {
            // B1 修复：原方法手工写入了 24 字节 ENIP 头，但调用方 SendEnip 还会再前置一个头，
            // 导致双重 ENIP 头（48 字节），Connected CIP 通讯必然失败。
            // 正确做法：只返回 SendUnitData 的 payload（InterfaceHandle + Timeout + Items + CIP）。
            int totalLen = 2 + cipData.Length;

            byte[] result = new byte[4 + 2 + 2 + 2 + 4 + 2 + totalLen];
            int i = 0;
            // Interface Handle = 0
            result[i++] = 0; result[i++] = 0; result[i++] = 0; result[i++] = 0;
            // Timeout = 0
            result[i++] = 0; result[i++] = 0;
            // Item Count = 2
            result[i++] = 2; result[i++] = 0;
            // Item 1: Connection Address (0x00A1)
            result[i++] = 0xA1; result[i++] = 0x00;
            result[i++] = 4; result[i++] = 0;
            result[i++] = (byte)(_connectionIdO2T & 0xFF);
            result[i++] = (byte)((_connectionIdO2T >> 8) & 0xFF);
            result[i++] = (byte)((_connectionIdO2T >> 16) & 0xFF);
            result[i++] = (byte)((_connectionIdO2T >> 24) & 0xFF);
            // Item 2: Connected Data (0x00B1)
            result[i++] = 0xB1; result[i++] = 0x00;
            result[i++] = (byte)(totalLen & 0xFF);
            result[i++] = (byte)((totalLen >> 8) & 0xFF);
            // Sequence Count
            result[i++] = (byte)(_sequenceCount & 0xFF);
            result[i++] = (byte)((_sequenceCount >> 8) & 0xFF);
            // CIP Data
            Buffer.BlockCopy(cipData, 0, result, i, cipData.Length);

            return result;
        }

        /// <summary>断开时自动关闭 Connected CIP 连接。</summary>
        public new void Disconnect()
        {
            if (_connectedCipActive)
                CloseConnection();
            base.Disconnect();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _connectedCipActive)
            {
                try { CloseConnection(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
