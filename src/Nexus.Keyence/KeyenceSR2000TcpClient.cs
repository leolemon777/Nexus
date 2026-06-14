using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 SR-2000 条码阅读器 TCP 客户端。
    /// <para>简单文本协议 over TCP，命令以 CR(\r) 终止。</para>
    /// <para>支持: LON(连续读取)、LOFF(停止)、TGIN(触发)、RESET(复位)、LED 控制等。</para>
    /// <para>响应: 数据 + CR(OK) 或 ER + 错误码 + CR(错误)</para>
    /// </summary>
    public class KeyenceSR2000TcpClient : TcpDeviceBase
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public KeyenceSR2000TcpClient(string ip, int port = 9004, int timeout = 5000)
            : base(ip, port, timeout)
        {
        }

        // ═══════════════════════════════════════════
        //  文本行收发
        // ═══════════════════════════════════════════

        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                if (!IsConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                _asyncLock.Wait();
                try { ns = _stream; }
                finally { _asyncLock.Release(); }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                string txText = Encoding.ASCII.GetString(request);
                Log.Debug($"TX → {txText.TrimEnd()}");
                RaiseMessageSent(txText);

                ns.Write(request, 0, request.Length);

                string? response = ReadLine(ns);
                if (response == null)
                    return OperateResult<byte[]>.Failed("读取响应超时");

                Log.Debug($"RX ← {response}");
                RaiseMessageReceived(response);

                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }

                return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(response));
            }
            catch (Exception ex)
            {
                Log.Error($"SR-2000 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private async Task<OperateResult<byte[]>> SendTextAsync(
            byte[] request, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!IsConnected)
                {
                    var conn = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { ns = _stream; }
                finally { _asyncLock.Release(); }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                string txText = Encoding.ASCII.GetString(request);
                Log.Debug($"TX → {txText.TrimEnd()}");
                RaiseMessageSent(txText);

                await ns.WriteAsync(request, 0, request.Length, cancellationToken).ConfigureAwait(false);

                string? response = await ReadLineAsync(ns, cancellationToken).ConfigureAwait(false);
                if (response == null)
                    return OperateResult<byte[]>.Failed("读取响应超时");

                Log.Debug($"RX ← {response}");
                RaiseMessageReceived(response);

                if (!_persistentMode)
                {
                    await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }

                return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(response));
            }
            catch (OperationCanceledException)
            {
                return OperateResult<byte[]>.Failed("操作已取消");
            }
            catch (Exception ex)
            {
                Log.Error($"SR-2000 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  文本行读取
        // ═══════════════════════════════════════════

        private string? ReadLine(NetworkStream ns)
        {
            var sb = new StringBuilder(256);
            int start = Environment.TickCount;

            while (unchecked(Environment.TickCount - start) <= Timeout)
            {
                int remaining = Timeout - unchecked(Environment.TickCount - start);
                if (remaining < 0) return null;
                int b = ReadByteWithTimeout(ns, remaining);
                if (b < 0) return null;
                if (b == '\r')
                {
                    int rem2 = Timeout - unchecked(Environment.TickCount - start);
                    int next = ReadByteWithTimeout(ns, Math.Min(rem2 < 0 ? 0 : rem2, 200));
                    if (next >= 0 && next != '\n')
                        sb.Append((char)next);
                    return sb.ToString();
                }
                if (b == '\n')
                    return sb.ToString();
                sb.Append((char)b);
            }
            return null;
        }

        private async Task<string?> ReadLineAsync(NetworkStream ns, CancellationToken ct)
        {
            var sb = new StringBuilder(256);
            byte[] single = new byte[1];
            var deadline = DateTime.UtcNow.AddMilliseconds(Timeout);

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                int read = await ns.ReadAsync(single, 0, 1, ct).ConfigureAwait(false);
                if (read == 0) return null;
                int b = single[0];
                if (b == '\r')
                {
                    int next = await ReadByteWithTimeoutAsync(ns, 200, ct).ConfigureAwait(false);
                    if (next >= 0 && next != '\n')
                        sb.Append((char)next);
                    return sb.ToString();
                }
                if (b == '\n')
                    return sb.ToString();
                sb.Append((char)b);
            }
            return null;
        }

        private int ReadByteWithTimeout(NetworkStream ns, int remainingMs)
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) <= remainingMs)
            {
                try { return ns.ReadByte(); }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        private async Task<int> ReadByteWithTimeoutAsync(NetworkStream ns, int timeoutMs, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                byte[] buf = new byte[1];
                int read = await ns.ReadAsync(buf, 0, 1, cts.Token).ConfigureAwait(false);
                return read == 0 ? -1 : buf[0];
            }
            catch (OperationCanceledException) { return -1; }
        }

        // ═══════════════════════════════════════════
        //  命令发送与响应解析
        // ═══════════════════════════════════════════

        private OperateResult<string> SendTextCommand(string command)
        {
            byte[] txBytes = Encoding.ASCII.GetBytes(command + "\r");
            var r = SendAndReceive(txBytes);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            string response = Encoding.ASCII.GetString(r.Content).TrimEnd('\r', '\n');

            if (response.StartsWith("ER"))
            {
                string errCode = response.Length > 2 ? response.Substring(2).Trim() : "??";
                return OperateResult<string>.Failed($"SR-2000 错误: {ParseErrorCode(errCode)}");
            }

            return OperateResult<string>.Success(response);
        }

        private async Task<OperateResult<string>> SendTextCommandAsync(string command, CancellationToken ct)
        {
            byte[] txBytes = Encoding.ASCII.GetBytes(command + "\r");
            var r = await SendTextAsync(txBytes, ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            string response = Encoding.ASCII.GetString(r.Content).TrimEnd('\r', '\n');

            if (response.StartsWith("ER"))
            {
                string errCode = response.Length > 2 ? response.Substring(2).Trim() : "??";
                return OperateResult<string>.Failed($"SR-2000 错误: {ParseErrorCode(errCode)}");
            }

            return OperateResult<string>.Success(response);
        }

        // ═══════════════════════════════════════════
        //  条码读取命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 开始连续读取 (LON)。SR-2000 将持续读取条码并返回结果。
        /// </summary>
        public OperateResult<string> LON() => SendTextCommand("LON");

        /// <summary>异步开始连续读取。</summary>
        public Task<OperateResult<string>> LONAsync(CancellationToken ct = default)
            => SendTextCommandAsync("LON", ct);

        /// <summary>
        /// 停止连续读取 (LOFF)。
        /// </summary>
        public OperateResult<string> LOFF() => SendTextCommand("LOFF");

        /// <summary>异步停止连续读取。</summary>
        public Task<OperateResult<string>> LOFFAsync(CancellationToken ct = default)
            => SendTextCommandAsync("LOFF", ct);

        /// <summary>
        /// 触发单次读取 (TGIN)。返回条码数据或错误。
        /// </summary>
        public OperateResult<string> TriggerRead() => SendTextCommand("TGIN");

        /// <summary>异步触发单次读取。</summary>
        public Task<OperateResult<string>> TriggerReadAsync(CancellationToken ct = default)
            => SendTextCommandAsync("TGIN", ct);

        /// <summary>
        /// 复位设备 (RESET)。
        /// </summary>
        public OperateResult<string> Reset() => SendTextCommand("RESET");

        /// <summary>异步复位设备。</summary>
        public Task<OperateResult<string>> ResetAsync(CancellationToken ct = default)
            => SendTextCommandAsync("RESET", ct);

        /// <summary>
        /// 打开 LED 指示灯。
        /// </summary>
        public OperateResult<string> LedOn() => SendTextCommand("LED ON");

        /// <summary>异步打开 LED。</summary>
        public Task<OperateResult<string>> LedOnAsync(CancellationToken ct = default)
            => SendTextCommandAsync("LED ON", ct);

        /// <summary>
        /// 关闭 LED 指示灯。
        /// </summary>
        public OperateResult<string> LedOff() => SendTextCommand("LED OFF");

        /// <summary>异步关闭 LED。</summary>
        public Task<OperateResult<string>> LedOffAsync(CancellationToken ct = default)
            => SendTextCommandAsync("LED OFF", ct);

        /// <summary>
        /// 发送自定义命令并返回响应。
        /// </summary>
        public OperateResult<string> SendCustomCommand(string command) => SendTextCommand(command);

        /// <summary>异步发送自定义命令。</summary>
        public Task<OperateResult<string>> SendCustomCommandAsync(string command, CancellationToken ct = default)
            => SendTextCommandAsync(command, ct);

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 简化实现（读取条码作为字符串）
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
            => OperateResult<bool>.Failed("SR-2000 不支持布尔读取，请使用 TriggerRead() 读取条码");

        public override OperateResult<short> ReadInt16(string address)
            => OperateResult<short>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public override OperateResult<ushort> ReadUInt16(string address)
            => OperateResult<ushort>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public override OperateResult<int> ReadInt32(string address)
            => OperateResult<int>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public override OperateResult<uint> ReadUInt32(string address)
            => OperateResult<uint>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public override OperateResult<long> ReadInt64(string address)
            => OperateResult<long>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public override OperateResult<ulong> ReadUInt64(string address)
            => OperateResult<ulong>.Failed("SR-2000 不支持整数读取，请使用 TriggerRead() 读取条码");

        public override OperateResult<float> ReadFloat(string address)
            => OperateResult<float>.Failed("SR-2000 不支持浮点读取，请使用 TriggerRead() 读取条码");

        public override OperateResult<double> ReadDouble(string address)
            => OperateResult<double>.Failed("SR-2000 不支持浮点读取，请使用 TriggerRead() 读取条码");

        /// <summary>
        /// 读取条码数据。address 参数被忽略，始终触发单次读取。
        /// </summary>
        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = TriggerRead();
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content);
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = TriggerRead();
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(r.Content));
        }

        public override OperateResult Write(string address, bool value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, short value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, ushort value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, int value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, uint value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, long value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, ulong value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, float value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, double value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("SR-2000 不支持写入操作");

        // ── Async Core (true async) ──

        protected override async Task<OperateResult<string>> ReadStringCoreAsync(string address, ushort length, CancellationToken ct)
        {
            var r = await TriggerReadAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content);
        }

        protected override async Task<OperateResult<byte[]>> ReadBytesCoreAsync(string address, ushort length, CancellationToken ct)
        {
            var r = await TriggerReadAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(r.Content));
        }

        // ═══════════════════════════════════════════
        //  错误码解析
        // ═══════════════════════════════════════════

        private static string ParseErrorCode(string code) => code.Trim() switch
        {
            "0" => "无错误",
            "1" => "命令错误",
            "2" => "参数错误",
            "3" => "超时",
            "4" => "设备忙",
            _ => $"未知错误 {code}"
        };

        public override string ToString() => $"Keyence SR-2000 TCP {Ip}:{Port}";
    }
}
