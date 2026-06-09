using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Rkc
{
    /// <summary>
    /// RKC CD/CH 系列数字温度控制器 TCP 通讯客户端。
    /// <para>串口参数: 8-1-N（8 数据位，1 停止位，无校验）。</para>
    /// <para>地址支持站号前缀: s=2;M1</para>
    /// <para>读取帧: EOT(0x04) + 站号(2位ASCII) + 地址(ASCII) + ENQ(0x05)</para>
    /// <para>写入帧: EOT(0x04) + 站号(2位ASCII) + STX(0x02) + 地址(ASCII) + 值(ASCII) + ETX(0x03) + BCC</para>
    /// <para>读响应: STX(0x02) + 站号(2位) + 数据(ASCII) + ETX(0x03) + BCC</para>
    /// <para>写响应: ACK(0x06) 或 NAK(0x15)</para>
    /// </summary>
    public class RkcTemperatureClient : TcpDeviceBase, IBatchReadWrite
    {
        // ── TcpDeviceBase 抽象实现 ───────────────
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ── 帧常量 ──────────────────────────────
        private const byte EOT = 0x04;
        private const byte ENQ = 0x05;
        private const byte STX = 0x02;
        private const byte ETX = 0x03;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;

        // ── 属性 ─────────────────────────────────

        /// <summary>站号（默认 1）。</summary>
        public byte Station { get; set; } = 1;

        // ── 构造 ────────────────────────────────

        public RkcTemperatureClient(string ip, int port = 10001, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  读取
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取温度值。
        /// <para>地址示例: M1(测量值1), M2(测量值2), AA, AB, B1, ER 等。</para>
        /// </summary>
        /// <param name="address">数据地址，支持 s=N; 前缀指定站号。</param>
        public OperateResult<double> ReadDouble(string address)
        {
            byte station = Station;
            address = ExtractStation(ref station, address);

            var cmd = BuildReadCommand(station, address);
            if (!cmd.IsSuccess) return OperateResult<double>.Failed(cmd.Message);

            var recv = SendAndReceiveCustom(cmd.Content);
            if (!recv.IsSuccess) return OperateResult<double>.Failed(recv.Message);

            return ParseReadResponse(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  写入
        // ═══════════════════════════════════════════

        /// <summary>
        /// 写入温度设定值。
        /// </summary>
        /// <param name="address">数据地址，支持 s=N; 前缀指定站号。</param>
        /// <param name="value">设定值（最多 6 个字符）。</param>
        public OperateResult Write(string address, double value)
        {
            byte station = Station;
            address = ExtractStation(ref station, address);

            var cmd = BuildWriteCommand(station, address, value);
            if (!cmd.IsSuccess) return OperateResult.Failed(cmd.Message);

            var recv = SendAndReceiveCustom(cmd.Content);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            // 写响应应为 ACK
            if (recv.Content == null || recv.Content.Length == 0)
                return OperateResult.Failed("无响应");
            if (recv.Content[0] == NAK)
                return OperateResult.Failed("RKC 返回 NAK");
            if (recv.Content[0] != ACK)
                return OperateResult.Failed($"RKC 响应异常: 0x{recv.Content[0]:X2}");

            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  命令构建（公开供测试）
        // ═══════════════════════════════════════════

        /// <summary>构建读取命令。</summary>
        public static OperateResult<byte[]> BuildReadCommand(byte station, string address)
        {
            if (station >= 100)
                return OperateResult<byte[]>.Failed("站号必须小于 100");
            if (string.IsNullOrEmpty(address))
                return OperateResult<byte[]>.Failed("地址不能为空");

            try
            {
                byte[] cmd = new byte[4 + address.Length];
                cmd[0] = EOT;
                Encoding.ASCII.GetBytes(station.ToString("D2")).CopyTo(cmd, 1);
                Encoding.ASCII.GetBytes(address).CopyTo(cmd, 3);
                cmd[cmd.Length - 1] = ENQ;
                return OperateResult<byte[]>.Success(cmd);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        /// <summary>构建写入命令。</summary>
        public static OperateResult<byte[]> BuildWriteCommand(byte station, string address, double value)
        {
            if (station >= 100)
                return OperateResult<byte[]>.Failed("站号必须小于 100");
            if (string.IsNullOrEmpty(address))
                return OperateResult<byte[]>.Failed("地址不能为空");

            string valueStr = value.ToString();
            if (valueStr.Length > 6)
                return OperateResult<byte[]>.Failed("值最多 6 个字符");

            try
            {
                var list = new System.Collections.Generic.List<byte>(20);
                list.Add(EOT);
                list.AddRange(Encoding.ASCII.GetBytes(station.ToString("D2")));
                list.Add(STX);
                list.AddRange(Encoding.ASCII.GetBytes(address));
                list.AddRange(Encoding.ASCII.GetBytes(valueStr));
                list.Add(ETX);

                // BCC: 从 STX 后第一个字节到 ETX 的异或
                byte bcc = list[4]; // 第一个 STX 后的地址字节
                for (int i = 5; i < list.Count; i++)
                    bcc ^= list[i];
                list.Add(bcc);

                return OperateResult<byte[]>.Success(list.ToArray());
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        /// <summary>解析读取响应。</summary>
        public static OperateResult<double> ParseReadResponse(byte[] response)
        {
            if (response == null || response.Length < 3)
                return OperateResult<double>.Failed("响应数据过短");
            if (response[0] != STX)
                return OperateResult<double>.Failed($"STX 校验失败: 0x{response[0]:X2}");

            try
            {
                // 找到 ETX 的位置
                int etxPos = -1;
                for (int i = 1; i < response.Length; i++)
                {
                    if (response[i] == ETX) { etxPos = i; break; }
                }
                if (etxPos < 0) etxPos = response.Length;

                // 数据在 STX 后第2字节开始（跳过站号2字节）到 ETX 之前
                int dataStart = 3;
                int dataLen = etxPos - dataStart;
                if (dataLen <= 0)
                    return OperateResult<double>.Failed("无数据内容");

                string dataStr = Encoding.ASCII.GetString(response, dataStart, dataLen);
                if (double.TryParse(dataStr, out double result))
                    return OperateResult<double>.Success(result);

                return OperateResult<double>.Failed($"数据解析失败: {dataStr}");
            }
            catch (Exception ex)
            {
                return OperateResult<double>.Failed($"解析异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  内部实现
        // ═══════════════════════════════════════════

        private string ExtractStation(ref byte station, string address)
        {
            if (address != null && address.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
            {
                int semiPos = address.IndexOf(';');
                if (semiPos > 2)
                {
                    string stationStr = address.Substring(2, semiPos - 2);
                    if (byte.TryParse(stationStr, out byte s)) station = s;
                    return address.Substring(semiPos + 1);
                }
            }
            return address;
        }

        private OperateResult<byte[]> SendAndReceiveCustom(byte[] request)
        {
            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    RaiseMessageSent(DataConverter.ToHexString(request));

                    _stream!.Write(request, 0, request.Length);

                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[256];
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        if (_stream.DataAvailable)
                        {
                            int read = _stream.Read(buf, 0, buf.Length);
                            if (read > 0) response.AddRange(buf);

                            // 检查响应完整性
                            if (response.Count > 0)
                            {
                                byte first = response[0];
                                if (first == ACK || first == NAK)
                                {
                                    // 单字节响应
                                    System.Threading.Thread.Sleep(20);
                                    if (!_stream.DataAvailable) break;
                                }
                                else if (first == STX)
                                {
                                    // 读取响应，等待 ETX + BCC
                                    bool foundEtx = false;
                                    for (int i = 1; i < response.Count; i++)
                                    {
                                        if (response[i] == ETX && i + 1 < response.Count)
                                        {
                                            foundEtx = true;
                                            break;
                                        }
                                    }
                                    if (foundEtx)
                                    {
                                        System.Threading.Thread.Sleep(20);
                                        if (!_stream.DataAvailable) break;
                                    }
                                }
                            }
                        }
                        else if (response.Count > 0)
                        {
                            System.Threading.Thread.Sleep(20);
                            if (!_stream.DataAvailable) break;
                        }
                        System.Threading.Thread.Sleep(5);
                    }

                    if (response.Count == 0)
                        return OperateResult<byte[]>.Failed("RKC 响应超时");

                    byte[] result = response.ToArray();
                    RaiseMessageReceived(DataConverter.ToHexString(result));
                    return OperateResult<byte[]>.Success(result);
                }
                catch (Exception ex)
                {
                    RaiseError($"RKC 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"RKC 通讯异常: {ex.Message}");
                }
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                var conn = Connect();
                if (!conn.IsSuccess) throw new InvalidOperationException($"RKC 连接失败: {conn.Message}");
            }
        }

        public override string ToString() => $"RkcTemperatureClient[{Ip}:{Port}]";

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
}
