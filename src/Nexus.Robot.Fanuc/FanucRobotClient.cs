using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Robot.Fanuc
{
    /// <summary>
    /// FANUC 机器人 SocketMessage 通讯客户端。
    /// <para>基于 FANUC Socket Messaging 协议（PC ↔ Robot 双向通讯）。</para>
    /// <para>支持读写 IO、读取位置、读取状态、发送字符串指令。</para>
    /// <para>默认端口 60008（Socket Message）。</para>
    /// </summary>
    public class FanucRobotClient : TcpDeviceBase, IBatchReadWrite
    {
        // ── 命令码 ──────────────────────────────
        private const int CMD_READ_NUMERIC_REG = 1;
        private const int CMD_WRITE_NUMERIC_REG = 2;
        private const int CMD_READ_POS_REG = 3;
        private const int CMD_WRITE_POS_REG = 4;
        private const int CMD_READ_STRING_REG = 5;
        private const int CMD_WRITE_STRING_REG = 6;
        private const int CMD_READ_DI = 10;
        private const int CMD_READ_DO = 11;
        private const int CMD_WRITE_DO = 12;
        private const int CMD_READ_GI = 13;
        private const int CMD_READ_GO = 14;
        private const int CMD_WRITE_GO = 15;
        private const int CMD_READ_ROBOT_POS = 20;
        private const int CMD_READ_STATUS = 21;
        private const int CMD_SEND_STRING = 30;

        // ── 响应码 ──────────────────────────────
        private const int RESP_SUCCESS = 0;
        private const int RESP_INVALID_CMD = -1;
        private const int RESP_INVALID_INDEX = -2;
        private const int RESP_WRITE_FAILED = -3;

        // ── 属性 ─────────────────────────────────

        /// <summary>消息 ID（每次请求递增）。</summary>
        private int _messageId;
        private readonly object _idLock = new object();

        // ── TcpDeviceBase 抽象实现 ───────────────

        protected override int ResponseHeaderLength => 8;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 8) return 0;
            // Header: MsgId(4) + Code(4)
            int code = BitConverter.ToInt32(header, 4);
            if (code < 0) return 0; // 错误响应无数据
            return code; // 成功时 code 表示数据长度
        }

        // ── 构造 ────────────────────────────────

        public FanucRobotClient(string ip, int port = 60008, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  寄存器读写
        // ═══════════════════════════════════════════

        /// <summary>读取数值寄存器（R[regIndex]）。</summary>
        public OperateResult<int> ReadNumericRegister(int regIndex)
        {
            var cmd = BuildCommand(CMD_READ_NUMERIC_REG, regIndex, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<int>.Failed(recv.Message);

            var parsed = ParseIntResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult<int>.Failed(parsed.Message);
            return OperateResult<int>.Success(parsed.Content);
        }

        /// <summary>写入数值寄存器（R[regIndex] = value）。</summary>
        public OperateResult WriteNumericRegister(int regIndex, int value)
        {
            var data = BitConverter.GetBytes(value);
            var cmd = BuildCommand(CMD_WRITE_NUMERIC_REG, regIndex, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckSuccessResponse(recv.Content);
        }

        /// <summary>读取位置寄存器（PR[regIndex]）。</summary>
        public OperateResult<double[]> ReadPositionRegister(int regIndex)
        {
            var cmd = BuildCommand(CMD_READ_POS_REG, regIndex, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<double[]>.Failed(recv.Message);

            return ParseDoubleArrayResponse(recv.Content, 6); // 6 轴
        }

        /// <summary>写入位置寄存器（PR[regIndex]）。</summary>
        public OperateResult WritePositionRegister(int regIndex, double[] values)
        {
            byte[] data = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
            {
                byte[] bytes = BitConverter.GetBytes(values[i]);
                Array.Copy(bytes, 0, data, i * 8, 8);
            }
            var cmd = BuildCommand(CMD_WRITE_POS_REG, regIndex, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckSuccessResponse(recv.Content);
        }

        /// <summary>读取字符串寄存器（SR[regIndex]）。</summary>
        public OperateResult<string> ReadStringRegister(int regIndex)
        {
            var cmd = BuildCommand(CMD_READ_STRING_REG, regIndex, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<string>.Failed(recv.Message);

            if (recv.Content.Length <= 8)
                return OperateResult<string>.Success("");

            int strLen = BitConverter.ToInt32(recv.Content, 8);
            if (recv.Content.Length < 12 + strLen)
                return OperateResult<string>.Failed("字符串数据不完整");

            return OperateResult<string>.Success(
                Encoding.ASCII.GetString(recv.Content, 12, strLen));
        }

        /// <summary>写入字符串寄存器（SR[regIndex]）。</summary>
        public OperateResult WriteStringRegister(int regIndex, string value)
        {
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            byte[] data = new byte[4 + strBytes.Length];
            BitConverter.GetBytes(strBytes.Length).CopyTo(data, 0);
            strBytes.CopyTo(data, 4);

            var cmd = BuildCommand(CMD_WRITE_STRING_REG, regIndex, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckSuccessResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  IO 读写
        // ═══════════════════════════════════════════

        /// <summary>读取数字输入（DI[index]）。</summary>
        public OperateResult<bool> ReadDigitalInput(int index)
        {
            var cmd = BuildCommand(CMD_READ_DI, index, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<bool>.Failed(recv.Message);

            var parsed = ParseIntResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult<bool>.Failed(parsed.Message);
            return OperateResult<bool>.Success(parsed.Content != 0);
        }

        /// <summary>读取数字输出（DO[index]）。</summary>
        public OperateResult<bool> ReadDigitalOutput(int index)
        {
            var cmd = BuildCommand(CMD_READ_DO, index, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<bool>.Failed(recv.Message);

            var parsed = ParseIntResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult<bool>.Failed(parsed.Message);
            return OperateResult<bool>.Success(parsed.Content != 0);
        }

        /// <summary>写入数字输出（DO[index] = value）。</summary>
        public OperateResult WriteDigitalOutput(int index, bool value)
        {
            var data = BitConverter.GetBytes(value ? 1 : 0);
            var cmd = BuildCommand(CMD_WRITE_DO, index, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckSuccessResponse(recv.Content);
        }

        /// <summary>读取组输入（GI[index]）。</summary>
        public OperateResult<int> ReadGroupInput(int index)
        {
            var cmd = BuildCommand(CMD_READ_GI, index, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<int>.Failed(recv.Message);

            var parsed = ParseIntResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult<int>.Failed(parsed.Message);
            return parsed;
        }

        /// <summary>读取组输出（GO[index]）。</summary>
        public OperateResult<int> ReadGroupOutput(int index)
        {
            var cmd = BuildCommand(CMD_READ_GO, index, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<int>.Failed(recv.Message);

            var parsed = ParseIntResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult<int>.Failed(parsed.Message);
            return parsed;
        }

        /// <summary>写入组输出（GO[index] = value）。</summary>
        public OperateResult WriteGroupOutput(int index, int value)
        {
            var data = BitConverter.GetBytes(value);
            var cmd = BuildCommand(CMD_WRITE_GO, index, data);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckSuccessResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  机器人状态
        // ═══════════════════════════════════════════

        /// <summary>读取当前机器人关节位置（度）。</summary>
        public OperateResult<double[]> ReadRobotPosition()
        {
            var cmd = BuildCommand(CMD_READ_ROBOT_POS, 0, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<double[]>.Failed(recv.Message);

            return ParseDoubleArrayResponse(recv.Content, 6);
        }

        /// <summary>读取机器人状态。</summary>
        public OperateResult<FanucRobotStatus> ReadRobotStatus()
        {
            var cmd = BuildCommand(CMD_READ_STATUS, 0, null);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<FanucRobotStatus>.Failed(recv.Message);

            if (recv.Content.Length < 16)
                return OperateResult<FanucRobotStatus>.Failed("状态数据不足");

            return OperateResult<FanucRobotStatus>.Success(new FanucRobotStatus
            {
                Mode = BitConverter.ToInt32(recv.Content, 8),
                State = BitConverter.ToInt32(recv.Content, 12)
            });
        }

        /// <summary>发送字符串消息到机器人（Socket Message）。</summary>
        public OperateResult SendString(string message)
        {
            byte[] strBytes = Encoding.ASCII.GetBytes(message ?? string.Empty);
            var cmd = BuildCommand(CMD_SEND_STRING, 0, strBytes);
            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckSuccessResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 基础实现
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            // 地址格式: R10 / PR5 / DI3 / DO5 / GI1 / GO2 / SR3
            var parsed = ParseAddress(address);
            if (!parsed.IsSuccess) return OperateResult<byte[]>.Failed(parsed.Message);

            byte[] cmd;
            switch (parsed.Content.Type)
            {
                case "R":
                    cmd = BuildCommand(CMD_READ_NUMERIC_REG, parsed.Content.Index, null);
                    break;
                case "DI":
                    cmd = BuildCommand(CMD_READ_DI, parsed.Content.Index, null);
                    break;
                case "DO":
                    cmd = BuildCommand(CMD_READ_DO, parsed.Content.Index, null);
                    break;
                default:
                    return OperateResult<byte[]>.Failed($"不支持的地址类型: {parsed.Content.Type}");
            }

            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);
            return OperateResult<byte[]>.Success(recv.Content);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var parsed = ParseAddress(address);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);

            byte[] cmd;
            switch (parsed.Content.Type)
            {
                case "R":
                    cmd = BuildCommand(CMD_WRITE_NUMERIC_REG, parsed.Content.Index, data);
                    break;
                case "DO":
                    cmd = BuildCommand(CMD_WRITE_DO, parsed.Content.Index, data);
                    break;
                default:
                    return OperateResult.Failed($"不支持的地址类型: {parsed.Content.Type}");
            }

            var recv = SendAndReceive(cmd);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            return CheckSuccessResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  命令构建
        // ═══════════════════════════════════════════

        /// <summary>构建 FANUC Socket 命令帧。</summary>
        /// <para>帧格式: MsgId(4) + CmdCode(4) + Index(4) + DataLen(4) + Data</para>
        public byte[] BuildCommand(int cmdCode, int index, byte[]? data)
        {
            int dataLen = data?.Length ?? 0;
            byte[] frame = new byte[16 + dataLen];
            int msgId;
            lock (_idLock) { msgId = ++_messageId; }

            BitConverter.GetBytes(msgId).CopyTo(frame, 0);
            BitConverter.GetBytes(cmdCode).CopyTo(frame, 4);
            BitConverter.GetBytes(index).CopyTo(frame, 8);
            BitConverter.GetBytes(dataLen).CopyTo(frame, 12);
            if (data != null && data.Length > 0)
                data.CopyTo(frame, 16);

            return frame;
        }

        // ═══════════════════════════════════════════
        //  响应解析
        // ═══════════════════════════════════════════

        private static OperateResult<int> ParseIntResponse(byte[] raw)
        {
            if (raw == null || raw.Length < 8)
                return OperateResult<int>.Failed($"响应数据过短 ({raw?.Length ?? 0})");

            int code = BitConverter.ToInt32(raw, 4);
            if (code < 0)
                return OperateResult<int>.Failed($"FANUC 错误码: {code}");

            if (raw.Length < 12)
                return OperateResult<int>.Failed("响应数据不足");

            return OperateResult<int>.Success(BitConverter.ToInt32(raw, 8));
        }

        private static OperateResult<double[]> ParseDoubleArrayResponse(byte[] raw, int expectedAxes)
        {
            if (raw == null || raw.Length < 8)
                return OperateResult<double[]>.Failed($"响应数据过短 ({raw?.Length ?? 0})");

            int code = BitConverter.ToInt32(raw, 4);
            if (code < 0)
                return OperateResult<double[]>.Failed($"FANUC 错误码: {code}");

            int dataLen = raw.Length - 8;
            int axes = Math.Min(expectedAxes, dataLen / 8);
            if (axes == 0)
                return OperateResult<double[]>.Failed("位置数据为空");

            var result = new double[axes];
            for (int i = 0; i < axes; i++)
                result[i] = BitConverter.ToDouble(raw, 8 + i * 8);

            return OperateResult<double[]>.Success(result);
        }

        private static OperateResult CheckSuccessResponse(byte[] raw)
        {
            if (raw == null || raw.Length < 8)
                return OperateResult.Failed($"响应数据过短 ({raw?.Length ?? 0})");

            int code = BitConverter.ToInt32(raw, 4);
            if (code < 0)
            {
                string errText = code switch
                {
                    -1 => "无效命令",
                    -2 => "无效索引",
                    -3 => "写入失败",
                    _ => $"未知错误 ({code})"
                };
                return OperateResult.Failed($"FANUC 错误: {errText}");
            }

            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        private static OperateResult<FanucAddress> ParseAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
                return OperateResult<FanucAddress>.Failed("地址为空");

            string upper = address.ToUpperInvariant().Trim();

            string[] prefixes = { "R", "PR", "SR", "DI", "DO", "GI", "GO" };
            foreach (string prefix in prefixes)
            {
                if (upper.StartsWith(prefix) && int.TryParse(upper.Substring(prefix.Length), out int idx))
                    return OperateResult<FanucAddress>.Success(new FanucAddress { Type = prefix, Index = idx });
            }

            return OperateResult<FanucAddress>.Failed($"无法解析地址: {address}");
        }

        public override string ToString() => $"FanucRobotClient[{Ip}:{Port}]";

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值。</summary>
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
    }

    // ── 辅助类型 ──────────────────────────────

    internal class FanucAddress
    {
        public string Type { get; set; } = "";
        public int Index { get; set; }
    }

    /// <summary>FANUC 机器人状态。</summary>
    public class FanucRobotStatus
    {
        /// <summary>运行模式（1=手动, 2=自动, 3=远程）。</summary>
        public int Mode { get; set; }

        /// <summary>运行状态（0=停止, 1=运行, 2=暂停, 3=急停）。</summary>
        public int State { get; set; }

        public string ModeDescription => Mode switch
        {
            1 => "手动",
            2 => "自动",
            3 => "远程",
            _ => $"未知({Mode})"
        };

        public string StateDescription => State switch
        {
            0 => "停止",
            1 => "运行",
            2 => "暂停",
            3 => "急停",
            _ => $"未知({State})"
        };

        public override string ToString() => $"Mode={ModeDescription}, State={StateDescription}";
    }
}
