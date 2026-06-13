using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 DL-EN1 激光测距传感器 TCP 客户端。
    /// <para>简单文本协议 over TCP，命令以 CR(\r) 终止。</para>
    /// <para>支持: M(测量)、RESET(复位)、OD(输出数据切换)等配置命令。</para>
    /// <para>响应: 测量值作为文本返回，或错误信息。</para>
    /// </summary>
    public class KeyenceDlEn1Client : TcpDeviceBase
    {
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public KeyenceDlEn1Client(string ip, int port = 8500, int timeout = 5000)
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
                Log.Error($"DL-EN1 通讯异常 — {ex.Message}");
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
                Log.Error($"DL-EN1 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  文本行读取
        // ═══════════════════════════════════════════

        private string? ReadLine(NetworkStream ns)
        {
            var sb = new StringBuilder(64);
            int deadline = Environment.TickCount + Timeout;

            while (Environment.TickCount <= deadline)
            {
                int b = ReadByteWithTimeout(ns, deadline);
                if (b < 0) return null;
                if (b == '\r')
                {
                    int next = ReadByteWithTimeout(ns, Math.Min(deadline, Environment.TickCount + 200));
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
            var sb = new StringBuilder(64);
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

        private int ReadByteWithTimeout(NetworkStream ns, int deadline)
        {
            while (Environment.TickCount <= deadline)
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
            return OperateResult<string>.Success(response);
        }

        private async Task<OperateResult<string>> SendTextCommandAsync(string command, CancellationToken ct)
        {
            byte[] txBytes = Encoding.ASCII.GetBytes(command + "\r");
            var r = await SendTextAsync(txBytes, ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            string response = Encoding.ASCII.GetString(r.Content).TrimEnd('\r', '\n');
            return OperateResult<string>.Success(response);
        }

        // ═══════════════════════════════════════════
        //  DL-EN1 测量命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 执行单次测量 (M)。返回测量值文本。
        /// </summary>
        public OperateResult<string> Measure() => SendTextCommand("M");

        /// <summary>异步执行单次测量。</summary>
        public Task<OperateResult<string>> MeasureAsync(CancellationToken ct = default)
            => SendTextCommandAsync("M", ct);

        /// <summary>
        /// 执行测量并返回解析后的距离值（毫米）。
        /// </summary>
        public OperateResult<double> MeasureDistance()
        {
            var r = Measure();
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);

            if (double.TryParse(r.Content.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
                return OperateResult<double>.Success(value);

            return OperateResult<double>.Failed($"无法解析测量值: {r.Content}");
        }

        /// <summary>异步执行测量并返回距离值。</summary>
        public async Task<OperateResult<double>> MeasureDistanceAsync(CancellationToken ct = default)
        {
            var r = await MeasureAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);

            if (double.TryParse(r.Content.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
                return OperateResult<double>.Success(value);

            return OperateResult<double>.Failed($"无法解析测量值: {r.Content}");
        }

        /// <summary>
        /// 复位设备 (RESET)。
        /// </summary>
        public OperateResult<string> Reset() => SendTextCommand("RESET");

        /// <summary>异步复位设备。</summary>
        public Task<OperateResult<string>> ResetAsync(CancellationToken ct = default)
            => SendTextCommandAsync("RESET", ct);

        /// <summary>
        /// 发送自定义命令并返回响应。
        /// </summary>
        public OperateResult<string> SendCustomCommand(string command) => SendTextCommand(command);

        /// <summary>异步发送自定义命令。</summary>
        public Task<OperateResult<string>> SendCustomCommandAsync(string command, CancellationToken ct = default)
            => SendTextCommandAsync(command, ct);

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 测量结果映射
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content != 0.0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success((short)r.Content);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success((int)r.Content);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success((long)r.Content);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ulong>.Success((ulong)r.Content);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success((float)r.Content);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = MeasureDistance();
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(r.Content);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = Measure();
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content);
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = Measure();
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(r.Content));
        }

        public override OperateResult Write(string address, bool value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, short value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, ushort value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, int value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, uint value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, long value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, ulong value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, float value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, double value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("DL-EN1 不支持写入操作");

        // ── Async Core (true async) ──

        protected override async Task<OperateResult<short>> ReadInt16CoreAsync(string address, CancellationToken ct)
        {
            var r = await MeasureDistanceAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success((short)r.Content);
        }

        protected override async Task<OperateResult<int>> ReadInt32CoreAsync(string address, CancellationToken ct)
        {
            var r = await MeasureDistanceAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success((int)r.Content);
        }

        protected override async Task<OperateResult<float>> ReadFloatCoreAsync(string address, CancellationToken ct)
        {
            var r = await MeasureDistanceAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success((float)r.Content);
        }

        protected override async Task<OperateResult<double>> ReadDoubleCoreAsync(string address, CancellationToken ct)
        {
            var r = await MeasureDistanceAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(r.Content);
        }

        protected override async Task<OperateResult<string>> ReadStringCoreAsync(string address, ushort length, CancellationToken ct)
        {
            var r = await MeasureAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content);
        }

        protected override async Task<OperateResult<byte[]>> ReadBytesCoreAsync(string address, ushort length, CancellationToken ct)
        {
            var r = await MeasureAsync(ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(r.Content));
        }

        public override string ToString() => $"Keyence DL-EN1 TCP {Ip}:{Port}";
    }
}
