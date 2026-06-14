using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 KV-1000/3000 旧协议上位通讯客户端。
    /// <para>文本协议 over TCP (端口 8501)，帧格式: [站号2位] + 命令 + CR(\r)</para>
    /// <para>响应: OK + 数据（读取）/ OK（写入）/ ? + 错误码 或 E + 错误码</para>
    /// <para>与 KV-5000/7000 的区别: 使用 ETX 终止的命令格式，区域码: R, B, T, C, D, W, M 等。</para>
    /// </summary>
    public class KeyenceKvOldClient : TcpDeviceBase, IBatchReadWrite
    {
        public byte Station { get; set; }

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public KeyenceKvOldClient(string ip, int port = 8501, byte station = 0, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
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

                Log.Debug($"TX → {Encoding.ASCII.GetString(request)}");
                RaiseMessageSent(Encoding.ASCII.GetString(request));

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
                Log.Error($"KV Old 通讯异常 — {ex.Message}");
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

        private async Task<OperateResult<byte[]>> SendAndReceiveTextAsync(
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

                Log.Debug($"TX → {Encoding.ASCII.GetString(request)}");
                RaiseMessageSent(Encoding.ASCII.GetString(request));

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
                Log.Error($"KV Old 通讯异常 — {ex.Message}");
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

        // ═══════════════════════════════════════════
        //  文本行读取
        // ═══════════════════════════════════════════

        private string? ReadLine(NetworkStream ns)
        {
            var sb = new StringBuilder(64);
            int start = Environment.TickCount;

            while (unchecked(Environment.TickCount - start) <= Timeout)
            {
                int remaining = Timeout - unchecked(Environment.TickCount - start);
                if (remaining < 0) return null;
                int b = ReadByteWithTimeout(ns, remaining);
                if (b < 0) return null;
                if (b == '\r' || b == '\n')
                {
                    if (b == '\r')
                    {
                        int rem2 = Timeout - unchecked(Environment.TickCount - start);
                        int next = ReadByteWithTimeout(ns, Math.Min(rem2 < 0 ? 0 : rem2, 200));
                        if (next >= 0 && next != '\n')
                            sb.Append((char)next);
                    }
                    return sb.ToString();
                }
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
                if (b == '\r' || b == '\n')
                {
                    if (b == '\r')
                    {
                        int next = await ReadByteWithTimeoutAsync(ns, 200, ct).ConfigureAwait(false);
                        if (next >= 0 && next != '\n')
                            sb.Append((char)next);
                    }
                    return sb.ToString();
                }
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

        private OperateResult<string> SendCommand(string command)
        {
            string frame = Station.ToString("D2") + command + "\r";
            byte[] txBytes = Encoding.ASCII.GetBytes(frame);
            var r = SendAndReceive(txBytes);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            string response = Encoding.ASCII.GetString(r.Content).TrimEnd('\r', '\n');

            if (response.StartsWith("?"))
            {
                string errCode = response.Length > 1 ? response.Substring(1).Trim() : "??";
                return OperateResult<string>.Failed($"KV Old 错误: {ParseErrorCode(errCode)}");
            }
            if (response.StartsWith("E"))
            {
                string errCode = response.Length > 1 ? response.Substring(1).Trim() : "??";
                return OperateResult<string>.Failed($"KV Old 错误: {ParseErrorCode(errCode)}");
            }

            if (!response.StartsWith("OK"))
                return OperateResult<string>.Failed($"未知响应: {response}");

            string data = response.Length > 2 ? response.Substring(2) : "";
            return OperateResult<string>.Success(data);
        }

        private async Task<OperateResult<string>> SendCommandAsync(string command, CancellationToken ct)
        {
            string frame = Station.ToString("D2") + command + "\r";
            byte[] txBytes = Encoding.ASCII.GetBytes(frame);
            var r = await SendAndReceiveTextAsync(txBytes, ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            string response = Encoding.ASCII.GetString(r.Content).TrimEnd('\r', '\n');

            if (response.StartsWith("?"))
            {
                string errCode = response.Length > 1 ? response.Substring(1).Trim() : "??";
                return OperateResult<string>.Failed($"KV Old 错误: {ParseErrorCode(errCode)}");
            }
            if (response.StartsWith("E"))
            {
                string errCode = response.Length > 1 ? response.Substring(1).Trim() : "??";
                return OperateResult<string>.Failed($"KV Old 错误: {ParseErrorCode(errCode)}");
            }

            if (!response.StartsWith("OK"))
                return OperateResult<string>.Failed($"未知响应: {response}");

            string data = response.Length > 2 ? response.Substring(2) : "";
            return OperateResult<string>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        /// <summary>
        /// KV Old 地址格式: "DM100", "R100", "B100", "T100", "C100", "D100", "W100", "M100"
        /// </summary>
        private static (string type, int address) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.Trim().ToUpperInvariant();

            if (address.StartsWith("DM")) return ("DM", int.Parse(address.Substring(2)));
            if (address.StartsWith("EM")) return ("EM", int.Parse(address.Substring(2)));
            if (address.StartsWith("WR")) return ("WR", int.Parse(address.Substring(2)));
            if (address.StartsWith("WL")) return ("WL", int.Parse(address.Substring(2)));
            if (address.StartsWith("MR")) return ("MR", int.Parse(address.Substring(2)));
            if (address.StartsWith("CR")) return ("CR", int.Parse(address.Substring(2)));
            if (address.StartsWith("VR")) return ("VR", int.Parse(address.Substring(2)));
            if (address.StartsWith("ZR")) return ("ZR", int.Parse(address.Substring(2)));
            if (address.StartsWith("R")) return ("R", int.Parse(address.Substring(1)));
            if (address.StartsWith("B")) return ("B", int.Parse(address.Substring(1)));
            if (address.StartsWith("T")) return ("T", int.Parse(address.Substring(1)));
            if (address.StartsWith("C")) return ("C", int.Parse(address.Substring(1)));
            if (address.StartsWith("D")) return ("DM", int.Parse(address.Substring(1)));
            if (address.StartsWith("W")) return ("WR", int.Parse(address.Substring(1)));
            if (address.StartsWith("M")) return ("MR", int.Parse(address.Substring(1)));

            return ("DM", int.Parse(address));
        }

        // ═══════════════════════════════════════════
        //  读写命令
        // ═══════════════════════════════════════════

        private OperateResult<string> ReadSingle(string type, int address)
            => SendCommand($"RD {type}{address}");

        private OperateResult<string[]> ReadMultiple(string type, int startAddress, int count)
        {
            var r = SendCommand($"RDS {type}{startAddress} {count}");
            if (!r.IsSuccess) return OperateResult<string[]>.Failed(r.Message, r.ErrorCode);
            string[] values = r.Content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return OperateResult<string[]>.Success(values);
        }

        private OperateResult WriteSingle(string type, int address, string value)
        {
            var r = SendCommand($"WR {type}{address} {value}");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        private OperateResult WriteMultiple(string type, int startAddress, string[] values)
        {
            string valStr = string.Join(" ", values);
            var r = SendCommand($"WRS {type}{startAddress} {values.Length} {valStr}");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取 PLC 运行状态 (STS 命令)。
        /// <para>返回: 0=停止, 1=运行, 2=调试, 3=错误。</para>
        /// </summary>
        public OperateResult<byte> ReadStatus()
        {
            var r = SendCommand("STS");
            if (!r.IsSuccess) return OperateResult<byte>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 1) return OperateResult<byte>.Failed("状态响应不足");
            if (!byte.TryParse(r.Content.Trim(), out byte status))
                return OperateResult<byte>.Failed($"无法解析状态: {r.Content}");
            return OperateResult<byte>.Success(status);
        }

        /// <summary>运行 PLC (MODE 命令)。</summary>
        public OperateResult Run()
        {
            var r = SendCommand("MODE 0");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>停止 PLC (MODE 命令)。</summary>
        public OperateResult Stop()
        {
            var r = SendCommand("MODE 1");
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>读取 PLC 型号 (UNIT 命令)。</summary>
        public OperateResult<string> ReadPlcModel()
        {
            var r = SendCommand("UNIT");
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(r.Content.Trim());
        }

        /// <summary>异步运行 PLC。</summary>
        public Task<OperateResult> RunAsync() => Task.FromResult(Run());

        /// <summary>异步停止 PLC。</summary>
        public Task<OperateResult> StopAsync() => Task.FromResult(Stop());

        /// <summary>异步读取 PLC 状态。</summary>
        public Task<OperateResult<byte>> ReadStatusAsync() => Task.FromResult(ReadStatus());

        /// <summary>异步读取 PLC 型号。</summary>
        public Task<OperateResult<string>> ReadPlcModelAsync() => Task.FromResult(ReadPlcModel());

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 数据类型读写
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var (type, addr) = ParseAddress(address);
            var r = ReadSingle(type, addr);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() != "0");
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (type, addr) = ParseAddress(address);
            var r = ReadSingle(type, addr);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(Convert.ToInt16(r.Content.Trim(), 16));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (type, addr) = ParseAddress(address);
            var r = ReadMultiple(type, addr, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<int>.Failed("响应数据不足");
            ushort hi = Convert.ToUInt16(r.Content[0].Trim(), 16);
            ushort lo = Convert.ToUInt16(r.Content[1].Trim(), 16);
            return OperateResult<int>.Success((int)((hi << 16) | lo));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var (type, addr) = ParseAddress(address);
            var r = ReadMultiple(type, addr, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<long>.Failed("响应数据不足");
            long v = 0;
            for (int i = 0; i < 4; i++)
                v = (v << 16) | Convert.ToUInt16(r.Content[i].Trim(), 16);
            return OperateResult<long>.Success(v);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override unsafe OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            int v = r.Content;
            return OperateResult<float>.Success(*(float*)&v);
        }

        public override unsafe OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            long v = r.Content;
            return OperateResult<double>.Success(*(double*)&v);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var (type, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadMultiple(type, addr, regCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            var bytes = new List<byte>();
            foreach (string hex in r.Content)
            {
                ushort val = Convert.ToUInt16(hex.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }
            string text = Encoding.ASCII.GetString(bytes.ToArray(), 0, Math.Min(length, bytes.Count));
            return OperateResult<string>.Success(text.TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (type, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = ReadMultiple(type, addr, regCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            var bytes = new List<byte>();
            foreach (string hex in r.Content)
            {
                ushort val = Convert.ToUInt16(hex.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }
            if (bytes.Count < length)
                return OperateResult<byte[]>.Failed($"响应数据不足: 期望 {length} 字节，实际 {bytes.Count} 字节");

            byte[] result = new byte[length];
            Array.Copy(bytes.ToArray(), result, length);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 写入 ──

        public override OperateResult Write(string address, bool value)
        {
            var (type, addr) = ParseAddress(address);
            return WriteSingle(type, addr, value ? "1" : "0");
        }

        public override OperateResult Write(string address, short value)
        {
            var (type, addr) = ParseAddress(address);
            return WriteSingle(type, addr, ((ushort)value).ToString("X4"));
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var (type, addr) = ParseAddress(address);
            string[] vals = {
                ((ushort)((uint)value >> 16)).ToString("X4"),
                ((ushort)(value & 0xFFFF)).ToString("X4")
            };
            return WriteMultiple(type, addr, vals);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, unchecked((ulong)value));
        public override OperateResult Write(string address, ulong value)
        {
            var (type, addr) = ParseAddress(address);
            string[] vals = {
                ((ushort)(value >> 48)).ToString("X4"),
                ((ushort)(value >> 32)).ToString("X4"),
                ((ushort)(value >> 16)).ToString("X4"),
                ((ushort)value).ToString("X4")
            };
            return WriteMultiple(type, addr, vals);
        }

        public override unsafe OperateResult Write(string address, float value)
        {
            int v = *(int*)&value;
            return Write(address, v);
        }

        public override unsafe OperateResult Write(string address, double value)
        {
            ulong v = *(ulong*)&value;
            return Write(address, v);
        }

        public override OperateResult Write(string address, string value)
        {
            var (type, addr) = ParseAddress(address);
            byte[] bytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            if (bytes.Length % 2 != 0) Array.Resize(ref bytes, bytes.Length + 1);
            var vals = new List<string>();
            for (int i = 0; i < bytes.Length; i += 2)
            {
                ushort v = (ushort)((bytes[i] << 8) | bytes[i + 1]);
                vals.Add(v.ToString("X4"));
            }
            return WriteMultiple(type, addr, vals.ToArray());
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var (type, addr) = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var vals = new List<string>();
            for (int i = 0; i < data.Length; i += 2)
            {
                ushort v = (ushort)((data[i] << 8) | data[i + 1]);
                vals.Add(v.ToString("X4"));
            }
            return WriteMultiple(type, addr, vals.ToArray());
        }

        // ── Async Core (true async) ──

        protected override async Task<OperateResult<bool>> ReadBoolCoreAsync(string address, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            var r = await SendCommandAsync($"RD {type}{addr}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Trim() != "0");
        }

        protected override async Task<OperateResult<short>> ReadInt16CoreAsync(string address, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            var r = await SendCommandAsync($"RD {type}{addr}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(Convert.ToInt16(r.Content.Trim(), 16));
        }

        protected override async Task<OperateResult<int>> ReadInt32CoreAsync(string address, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            var r = await SendCommandAsync($"RDS {type}{addr} 2", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            string[] values = r.Content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 2) return OperateResult<int>.Failed("响应数据不足");
            ushort hi = Convert.ToUInt16(values[0].Trim(), 16);
            ushort lo = Convert.ToUInt16(values[1].Trim(), 16);
            return OperateResult<int>.Success((int)((hi << 16) | lo));
        }

        protected override async Task<OperateResult<long>> ReadInt64CoreAsync(string address, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            var r = await SendCommandAsync($"RDS {type}{addr} 4", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            string[] values = r.Content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 4) return OperateResult<long>.Failed("响应数据不足");
            long v = 0;
            for (int i = 0; i < 4; i++)
                v = (v << 16) | Convert.ToUInt16(values[i].Trim(), 16);
            return OperateResult<long>.Success(v);
        }

        protected override async Task<OperateResult<float>> ReadFloatCoreAsync(string address, CancellationToken ct)
        {
            var r = await ReadInt32CoreAsync(address, ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            int v = r.Content;
            unsafe { return OperateResult<float>.Success(*(float*)&v); }
        }

        protected override async Task<OperateResult<double>> ReadDoubleCoreAsync(string address, CancellationToken ct)
        {
            var r = await ReadInt64CoreAsync(address, ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            long v = r.Content;
            unsafe { return OperateResult<double>.Success(*(double*)&v); }
        }

        protected override async Task<OperateResult<string>> ReadStringCoreAsync(string address, ushort length, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = await SendCommandAsync($"RDS {type}{addr} {regCount}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            string[] values = r.Content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new List<byte>();
            foreach (string hex in values)
            {
                ushort val = Convert.ToUInt16(hex.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }
            string text = Encoding.ASCII.GetString(bytes.ToArray(), 0, Math.Min(length, bytes.Count));
            return OperateResult<string>.Success(text.TrimEnd('\0'));
        }

        protected override async Task<OperateResult<byte[]>> ReadBytesCoreAsync(string address, ushort length, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            int regCount = (length + 1) / 2;
            var r = await SendCommandAsync($"RDS {type}{addr} {regCount}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            string[] values = r.Content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new List<byte>();
            foreach (string hex in values)
            {
                ushort val = Convert.ToUInt16(hex.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }
            if (bytes.Count < length)
                return OperateResult<byte[]>.Failed($"响应数据不足: 期望 {length} 字节，实际 {bytes.Count} 字节");

            byte[] result = new byte[length];
            Array.Copy(bytes.ToArray(), result, length);
            return OperateResult<byte[]>.Success(result);
        }

        protected override async Task<OperateResult> WriteBoolCoreAsync(string address, bool value, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            var r = await SendCommandAsync($"WR {type}{addr} {(value ? "1" : "0")}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        protected override async Task<OperateResult> WriteInt16CoreAsync(string address, short value, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            var r = await SendCommandAsync($"WR {type}{addr} {((ushort)value).ToString("X4")}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        protected override async Task<OperateResult> WriteInt32CoreAsync(string address, int value, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            string hi = ((ushort)((uint)value >> 16)).ToString("X4");
            string lo = ((ushort)(value & 0xFFFF)).ToString("X4");
            var r = await SendCommandAsync($"WRS {type}{addr} 2 {hi} {lo}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        protected override async Task<OperateResult> WriteInt64CoreAsync(string address, long value, CancellationToken ct)
            => await WriteUInt64CoreAsync(address, unchecked((ulong)value), ct).ConfigureAwait(false);

        protected override async Task<OperateResult> WriteUInt64CoreAsync(string address, ulong value, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            string[] vals = {
                ((ushort)(value >> 48)).ToString("X4"),
                ((ushort)(value >> 32)).ToString("X4"),
                ((ushort)(value >> 16)).ToString("X4"),
                ((ushort)value).ToString("X4")
            };
            string valStr = string.Join(" ", vals);
            var r = await SendCommandAsync($"WRS {type}{addr} 4 {valStr}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        protected override Task<OperateResult> WriteFloatCoreAsync(string address, float value, CancellationToken ct)
        {
            unsafe
            {
                int v = *(int*)&value;
                return WriteInt32CoreAsync(address, v, ct);
            }
        }

        protected override Task<OperateResult> WriteDoubleCoreAsync(string address, double value, CancellationToken ct)
        {
            unsafe
            {
                ulong v = *(ulong*)&value;
                return WriteUInt64CoreAsync(address, v, ct);
            }
        }

        protected override async Task<OperateResult> WriteStringCoreAsync(string address, string value, CancellationToken ct)
        {
            var (type, addr) = ParseAddress(address);
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            if (strBytes.Length % 2 != 0) Array.Resize(ref strBytes, strBytes.Length + 1);
            var vals = new List<string>();
            for (int i = 0; i < strBytes.Length; i += 2)
            {
                ushort v = (ushort)((strBytes[i] << 8) | strBytes[i + 1]);
                vals.Add(v.ToString("X4"));
            }
            string valStr = string.Join(" ", vals);
            var r = await SendCommandAsync($"WRS {type}{addr} {vals.Count} {valStr}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        protected override async Task<OperateResult> WriteBytesCoreAsync(string address, byte[] data, CancellationToken ct)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var (type, addr) = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var vals = new List<string>();
            for (int i = 0; i < data.Length; i += 2)
            {
                ushort v = (ushort)((data[i] << 8) | data[i + 1]);
                vals.Add(v.ToString("X4"));
            }
            string valStr = string.Join(" ", vals);
            var r = await SendCommandAsync($"WRS {type}{addr} {vals.Count} {valStr}", ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addressList = addresses.ToList();
            if (addressList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            var result = new Dictionary<string, object?>();
            foreach (string addr in addressList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = (object?)r.Content;
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

            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addressList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
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
                    bool v => Write(kv.Key, v),
                    short v => Write(kv.Key, v),
                    ushort v => Write(kv.Key, v),
                    int v => Write(kv.Key, v),
                    uint v => Write(kv.Key, v),
                    long v => Write(kv.Key, v),
                    ulong v => Write(kv.Key, v),
                    float v => Write(kv.Key, v),
                    double v => Write(kv.Key, v),
                    string v => Write(kv.Key, v),
                    byte[] v => Write(kv.Key, v),
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
        //  错误码解析
        // ═══════════════════════════════════════════

        private static string ParseErrorCode(string code) => code.Trim() switch
        {
            "0" => "无错误",
            "1" => "未定义命令",
            "2" => "非法数据",
            "3" => "地址越界",
            "4" => "写保护",
            "5" => "通讯错误",
            "6" => "忙碌",
            "7" => "超时",
            _ => $"未知错误 {code}"
        };

        public override string ToString() => $"Keyence KV Old TCP {Ip}:{Port}";
    }
}
