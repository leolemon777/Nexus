// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
//
// Turck BLident RFID reader protocol over TCP.
// Adapted from HSL's Turck.ReaderNet. Frame format:
//   Request:  [0xAA] [total-len] [total-len] [command-bytes] [CRC16(2 bytes)]
//   Response: [0xAA] [invocation] [total-len] [response-bytes] [CRC16(2 bytes)]
// CRC is CRC-16 with polynomial 0x8408 (reflected), init 0xFFFF, final XOR 0xFFFF.

using System;
using System.Collections.Generic;

namespace Nexus.Turck
{
    /// <summary>
    /// 图尔克 BLident RFID 读卡器 TCP 客户端。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>协议格式</b>(参考 HslCommunication.Profinet.Turck.ReaderNet):
    /// <list type="bullet">
    ///   <item>帧头: <c>0xAA</c> + 总长度(1 字节) + 总长度(1 字节,请求中重复)</item>
    ///   <item>命令: 1-2 字节命令码 + 参数</item>
    ///   <item>校验: CRC-16 (poly 0x8408 反射,init 0xFFFF,final XOR)</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>常用命令</b>:
    /// <list type="table">
    ///   <listheader><term>命令字节</term><description>含义</description></listheader>
    ///   <item><term>0x68 (104)</term><description>读数据块</description></item>
    ///   <item><term>0x69 (105)</term><description>写数据块</description></item>
    ///   <item><term>0x70 (112)</term><description>读 UID</description></item>
    ///   <item><term>0x73 (115)</term><description>读工作状态</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public class TurckReaderClient : TcpDeviceBase
    {
        /// <summary>
        /// 设备的 UID(标签唯一标识符)— 在读 UID 命令成功后赋值。
        /// </summary>
        public string? UID { get; private set; }

        /// <summary>当前设备数据块总数量(初始化后赋值)。</summary>
        public byte NumberOfBlock { get; set; } = 1;

        /// <summary>每个数据块的字节数(初始化后赋值;也可手动设置以适配不同型号)。</summary>
        public byte BytesOfBlock { get; set; } = 4;

        /// <summary>构造。</summary>
        public TurckReaderClient(string ip, int port = 10000, int timeout = 5000)
            : base(ip, port, timeout)
        {
            SetPersistentConnection();
        }

        // ── 帧解析 ─────────────────────────────

        protected override int ResponseHeaderLength => 3;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 3) return 0;
            int totalLen = header[2];
            return totalLen <= 3 ? 0 : totalLen - 3;
        }

        // ── CRC-16 (Turck 反射多项式 0x8408) ──────

        /// <summary>
        /// 计算 Turck 协议的 CRC-16 校验。多项式 0x8408(即 0x1021 反射),
        /// 初始值 0xFFFF,最终取反。
        /// </summary>
        public static byte[] CalculateCrc(byte[] data, int length)
        {
            int crc = 0xFFFF;
            const int poly = 0x8408;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
                }
            }
            crc = ~crc & 0xFFFF;
            return new byte[] { (byte)(crc & 0xFF), (byte)((crc >> 8) & 0xFF) };
        }

        // ── 命令打包 ────────────────────────────

        /// <summary>
        /// 把命令 payload 打包成完整 Turck 帧头 + payload + CRC。
        /// </summary>
        public static byte[] PackCommand(byte[] command)
        {
            if (command == null) command = Array.Empty<byte>();
            byte[] frame = new byte[5 + command.Length];
            frame[0] = 0xAA;
            frame[1] = (byte)frame.Length;
            frame[2] = (byte)frame.Length;
            Buffer.BlockCopy(command, 0, frame, 3, command.Length);
            byte[] crc = CalculateCrc(frame, 3 + command.Length);
            frame[3 + command.Length] = crc[0];
            frame[4 + command.Length] = crc[1];
            return frame;
        }

        // ── 高级 API ────────────────────────────

        /// <summary>读 UID — 发命令 [0x70, 0x00]。</summary>
        public OperateResult<string> ReadUid()
        {
            var cmd = PackCommand(new byte[] { 0x70, 0x00 });
            var resp = SendAndReceive(cmd);
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message);

            var r = ParseResponse(resp.Content);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);

            // payload = fullResponse[1..(end-2)] = [invocation] [total-len] [cmd] [status] [6 字节 UID]。
            // UID 从 payload 第 4 字节开始(跳过 invocation/total-len/cmd/status)。
            byte[] payload = r.Content;
            const int uidOffset = 4;
            if (payload.Length < uidOffset + 6) return OperateResult<string>.Failed($"UID 响应负载过短: {payload.Length}");

            string uid = BitConverter.ToString(payload, uidOffset, 6).Replace("-", "");
            UID = uid;
            return OperateResult<string>.Success(uid);
        }

        /// <summary>读数据块。</summary>
        /// <param name="startBlock">起始块(0-based)。</param>
        /// <param name="blockCount">块数量。</param>
        public OperateResult<byte[]> ReadBlocks(byte startBlock, byte blockCount)
        {
            if (blockCount == 0) return OperateResult<byte[]>.Failed("blockCount 必须 > 0");

            var allData = new List<byte>();
            byte currentBlock = startBlock;
            int remaining = blockCount;

            while (remaining > 0)
            {
                int everyLength = BytesOfBlock == 0 ? 16 : 64 / BytesOfBlock;
                int chunk = Math.Min(remaining, everyLength);
                if (chunk <= 0) chunk = 1;

                byte[] cmdPayload = new byte[]
                {
                    0x68,                       // 0x68 = read blocks
                    0x00,
                    currentBlock,
                    (byte)(chunk - 1)
                };

                var resp = SendAndReceive(PackCommand(cmdPayload));
                if (!resp.IsSuccess) return OperateResult<byte[]>.Failed(resp.Message);

                var r = ParseResponse(resp.Content);
                if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);

                // 响应 payload 在第 4 字节后是数据。
                byte[] payload = r.Content;
                if (payload.Length < 4) return OperateResult<byte[]>.Failed("读块响应负载过短");

                allData.AddRange(new ArraySegment<byte>(payload, 4, payload.Length - 4));

                currentBlock += (byte)chunk;
                remaining -= chunk;
            }

            return OperateResult<byte[]>.Success(allData.ToArray());
        }

        /// <summary>写数据块。</summary>
        public OperateResult WriteBlocks(byte startBlock, byte[] data)
        {
            if (data == null || data.Length == 0) return OperateResult.Failed("data 为空");

            int bytesPerBlock = BytesOfBlock == 0 ? 4 : BytesOfBlock;
            int totalBlocks = (data.Length + bytesPerBlock - 1) / bytesPerBlock;
            int offset = 0;
            byte currentBlock = startBlock;

            while (offset < data.Length)
            {
                int everyLength = 64 / bytesPerBlock;
                int chunkBlocks = Math.Min(totalBlocks, everyLength);
                if (chunkBlocks <= 0) chunkBlocks = 1;
                int chunkBytes = chunkBlocks * bytesPerBlock;
                int remaining = data.Length - offset;
                if (chunkBytes > remaining) chunkBytes = remaining;

                byte[] cmdPayload = new byte[4 + chunkBytes];
                cmdPayload[0] = 0x69;  // write blocks
                cmdPayload[1] = 0x00;
                cmdPayload[2] = currentBlock;
                cmdPayload[3] = (byte)(chunkBlocks - 1);
                Buffer.BlockCopy(data, offset, cmdPayload, 4, chunkBytes);

                var resp = SendAndReceive(PackCommand(cmdPayload));
                if (!resp.IsSuccess) return resp;

                offset += chunkBytes;
                currentBlock += (byte)chunkBlocks;
                totalBlocks -= chunkBlocks;
            }

            return OperateResult.Success();
        }

        /// <summary>解析响应,验证头部和 CRC。</summary>
        private OperateResult<byte[]> ParseResponse(byte[] fullResponse)
        {
            if (fullResponse == null || fullResponse.Length < 5)
                return OperateResult<byte[]>.Failed("响应过短");

            if (fullResponse[0] != 0xAA)
                return OperateResult<byte[]>.Failed($"响应帧头错误: 期望 0xAA, 实际 0x{fullResponse[0]:X2}");

            // 检查错误响应([0x07, 0x07] = 错误码)。
            if (fullResponse.Length >= 5 && fullResponse[1] == 0x07 && fullResponse[2] == 0x07)
            {
                int errorCode = fullResponse.Length > 5 ? fullResponse[5] : -1;
                string msg = errorCode >= 0 ? MapErrorCode(errorCode) : "未知错误";
                return OperateResult<byte[]>.Failed($"Turck 错误响应: 0x{errorCode:X2} - {msg}");
            }

            // CRC 校验。
            int dataLen = fullResponse.Length - 2;
            if (dataLen > 0)
            {
                byte[] expected = CalculateCrc(fullResponse, dataLen);
                if (expected[0] != fullResponse[dataLen] || expected[1] != fullResponse[dataLen + 1])
                {
                    return OperateResult<byte[]>.Failed("CRC 校验失败");
                }
            }

            // 返回去掉帧头 0xAA(1 字节)和最后 2 字节 CRC 的负载。
            // payload 长度 = fullResponse.Length - 1(0xAA) - 2(CRC)。
            byte[] payload = new byte[fullResponse.Length - 1 - 2];
            Buffer.BlockCopy(fullResponse, 1, payload, 0, payload.Length);
            return OperateResult<byte[]>.Success(payload);
        }

        /// <summary>Turck 错误码到中文消息的映射。</summary>
        private static string MapErrorCode(int code)
        {
            switch (code)
            {
                case 0x01: return "NO_TAG, 无标签";
                case 0x02: return "TAG_NOT_READABLE, 标签不可读";
                case 0x03: return "ADDR_OVERFLOW, 地址溢出";
                case 0x04: return "WRITE_ERROR, 写入错误";
                case 0x05: return "ADDR_ERR, 地址错误";
                case 0x06: return "CMD_ERR, 命令错误";
                case 0x07: return "CRC_ERR, CRC 校验失败";
                default: return $"未知错误码 0x{code:X2}";
            }
        }

        // ── IReadWriteDevice(简化:基于 ReadBlocks/WriteBlocks 实现)──

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            if (!byte.TryParse(address, out byte startBlock))
                return OperateResult<byte[]>.Failed($"地址无效(应为块号数字): {address}");
            return ReadBlocks(startBlock, (byte)length);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (!byte.TryParse(address, out byte startBlock))
                return OperateResult.Failed($"地址无效(应为块号数字): {address}");
            return WriteBlocks(startBlock, data);
        }

        public override string ToString() => $"TurckReaderClient[{Ip}:{Port}, UID={UID ?? "<未读取>"}]";
    }
}
