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
    /// 基恩士 Nano 系列 TCP 客户端 — 支持 KV-10/24 等（以太网连接）。
    /// <para>文本协议 over TCP，帧格式: [站号] + 命令 + \r</para>
    /// <para>响应: OK + 数据（读取）/ OK（写入）/ E0/E1/E2 + 错误码</para>
    /// <para>对标 HSL: KeyenceNanoNet — 通过 TCP 访问 Nano 系列 PLC</para>
    /// </summary>
    public class KeyenceNanoOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        public byte Station { get; set; }

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public KeyenceNanoOverTcpClient(string ip, int port = 8501, byte station = 0, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
        }

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
                Log.Error($"通讯异常 — {ex.Message}");
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
                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }
                return OperateResult<byte[]>.Failed("操作已取消");
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
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

            if (response.StartsWith("E0"))
                return OperateResult<string>.Failed($"Nano 错误: 未定义命令 ({response})");
            if (response.StartsWith("E1"))
                return OperateResult<string>.Failed($"Nano 错误: 非法数据 ({response})");
            if (response.StartsWith("E2"))
                return OperateResult<string>.Failed($"Nano 错误: 地址越界 ({response})");
            if (response.StartsWith("E"))
                return OperateResult<string>.Failed($"Nano 错误: {response}");

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

            if (response.StartsWith("E0"))
                return OperateResult<string>.Failed($"Nano 错误: 未定义命令 ({response})");
            if (response.StartsWith("E1"))
                return OperateResult<string>.Failed($"Nano 错误: 非法数据 ({response})");
            if (response.StartsWith("E2"))
                return OperateResult<string>.Failed($"Nano 错误: 地址越界 ({response})");
            if (response.StartsWith("E"))
                return OperateResult<string>.Failed($"Nano 错误: {response}");

            if (!response.StartsWith("OK"))
                return OperateResult<string>.Failed($"未知响应: {response}");

            string data = response.Length > 2 ? response.Substring(2) : "";
            return OperateResult<string>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  内部读写
        // ═══════════════════════════════════════════

        private OperateResult<string> ReadWord(KeyenceNanoAddress addr)
            => SendCommand($"RD {addr.AreaCode}{addr.Address}.{addr.SubAddress}");

        private OperateResult<string> ReadBit(KeyenceNanoAddress addr)
            => SendCommand($"RDS {addr.AreaCode}{addr.Address}.{addr.SubAddress}");

        private OperateResult WriteWord(KeyenceNanoAddress addr, string data)
            => SendCommand($"WD {addr.AreaCode}{addr.Address}.{addr.SubAddress} {data}");

        private OperateResult WriteBit(KeyenceNanoAddress addr, string data)
            => SendCommand($"WRS {addr.AreaCode}{addr.Address}.{addr.SubAddress} {data}");

        private async Task<OperateResult<string>> ReadWordAsync(KeyenceNanoAddress addr, CancellationToken ct)
            => await SendCommandAsync($"RD {addr.AreaCode}{addr.Address}.{addr.SubAddress}", ct).ConfigureAwait(false);

        private async Task<OperateResult<string>> ReadBitAsync(KeyenceNanoAddress addr, CancellationToken ct)
            => await SendCommandAsync($"RDS {addr.AreaCode}{addr.Address}.{addr.SubAddress}", ct).ConfigureAwait(false);

        private async Task<OperateResult> WriteWordAsync(KeyenceNanoAddress addr, string data, CancellationToken ct)
            => await SendCommandAsync($"WD {addr.AreaCode}{addr.Address}.{addr.SubAddress} {data}", ct).ConfigureAwait(false);

        private async Task<OperateResult> WriteBitAsync(KeyenceNanoAddress addr, string data, CancellationToken ct)
            => await SendCommandAsync($"WRS {addr.AreaCode}{addr.Address}.{addr.SubAddress} {data}", ct).ConfigureAwait(false);

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 数据类型读写
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            if (addr.IsBitArea || addr.SubAddress > 0)
            {
                var r = ReadBit(addr);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
                return OperateResult<bool>.Success(r.Content.Trim() != "0");
            }
            else
            {
                var r = ReadWord(addr);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
                return OperateResult<bool>.Success(Convert.ToInt16(r.Content.Trim(), 16) != 0);
            }
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadWord(KeyenceNanoAddress.Parse(address));
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(Convert.ToInt16(r.Content.Trim(), 16));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess
                ? OperateResult<ushort>.Success((ushort)r.Content)
                : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            var r1 = ReadWord(addr);
            if (!r1.IsSuccess) return OperateResult<int>.Failed(r1.Message, r1.ErrorCode);

            var nextAddr = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + 1}.{addr.SubAddress}");
            var r2 = ReadWord(nextAddr);
            if (!r2.IsSuccess) return OperateResult<int>.Failed(r2.Message, r2.ErrorCode);

            ushort hi = Convert.ToUInt16(r1.Content.Trim(), 16);
            ushort lo = Convert.ToUInt16(r2.Content.Trim(), 16);
            return OperateResult<int>.Success((hi << 16) | lo);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess
                ? OperateResult<uint>.Success((uint)r.Content)
                : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            long value = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = ReadWord(a);
                if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
                value = (value << 16) | Convert.ToUInt16(r.Content.Trim(), 16);
            }
            return OperateResult<long>.Success(value);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess
                ? OperateResult<ulong>.Success((ulong)r.Content)
                : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
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
            var addr = KeyenceNanoAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var bytes = new List<byte>();

            for (int i = 0; i < regCount; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = ReadWord(a);
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
                ushort val = Convert.ToUInt16(r.Content.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }

            string text = Encoding.ASCII.GetString(bytes.ToArray(), 0, Math.Min(length, bytes.Count));
            return OperateResult<string>.Success(text.TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var bytes = new List<byte>();

            for (int i = 0; i < regCount; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = ReadWord(a);
                if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
                ushort val = Convert.ToUInt16(r.Content.Trim(), 16);
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
            var addr = KeyenceNanoAddress.Parse(address);
            if (addr.IsBitArea || addr.SubAddress > 0)
                return WriteBit(addr, value ? "1" : "0");
            return WriteWord(addr, value ? "0001" : "0000");
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            return WriteWord(addr, ((ushort)value).ToString("X4"));
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            var r1 = WriteWord(addr, ((ushort)((uint)value >> 16)).ToString("X4"));
            if (!r1.IsSuccess) return r1;
            var nextAddr = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + 1}.{addr.SubAddress}");
            return WriteWord(nextAddr, ((ushort)(value & 0xFFFF)).ToString("X4"));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value) => Write(address, unchecked((ulong)value));

        public override OperateResult Write(string address, ulong value)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            for (int i = 0; i < 4; i++)
            {
                ushort word = (ushort)(value >> (48 - i * 16));
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = WriteWord(a, word.ToString("X4"));
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
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
            var addr = KeyenceNanoAddress.Parse(address);
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            if (strBytes.Length % 2 != 0) Array.Resize(ref strBytes, strBytes.Length + 1);

            for (int i = 0; i < strBytes.Length; i += 2)
            {
                ushort word = (ushort)((strBytes[i] << 8) | strBytes[i + 1]);
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i / 2}.{addr.SubAddress}");
                var r = WriteWord(a, word.ToString("X4"));
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var addr = KeyenceNanoAddress.Parse(address);
            byte[] padded = data;
            if (padded.Length % 2 != 0) { padded = new byte[data.Length + 1]; Array.Copy(data, padded, data.Length); }

            for (int i = 0; i < padded.Length; i += 2)
            {
                ushort word = (ushort)((padded[i] << 8) | padded[i + 1]);
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i / 2}.{addr.SubAddress}");
                var r = WriteWord(a, word.ToString("X4"));
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        // ── Async (true async via CoreAsync) ──

        protected override async Task<OperateResult<bool>> ReadBoolCoreAsync(string address, CancellationToken ct)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            if (addr.IsBitArea || addr.SubAddress > 0)
            {
                var r = await ReadBitAsync(addr, ct).ConfigureAwait(false);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
                return OperateResult<bool>.Success(r.Content.Trim() != "0");
            }
            else
            {
                var r = await ReadWordAsync(addr, ct).ConfigureAwait(false);
                if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
                return OperateResult<bool>.Success(Convert.ToInt16(r.Content.Trim(), 16) != 0);
            }
        }

        protected override async Task<OperateResult<short>> ReadInt16CoreAsync(string address, CancellationToken ct)
        {
            var r = await ReadWordAsync(KeyenceNanoAddress.Parse(address), ct).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(Convert.ToInt16(r.Content.Trim(), 16));
        }

        protected override async Task<OperateResult<int>> ReadInt32CoreAsync(string address, CancellationToken ct)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            var r1 = await ReadWordAsync(addr, ct).ConfigureAwait(false);
            if (!r1.IsSuccess) return OperateResult<int>.Failed(r1.Message, r1.ErrorCode);

            var nextAddr = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + 1}.{addr.SubAddress}");
            var r2 = await ReadWordAsync(nextAddr, ct).ConfigureAwait(false);
            if (!r2.IsSuccess) return OperateResult<int>.Failed(r2.Message, r2.ErrorCode);

            ushort hi = Convert.ToUInt16(r1.Content.Trim(), 16);
            ushort lo = Convert.ToUInt16(r2.Content.Trim(), 16);
            return OperateResult<int>.Success((hi << 16) | lo);
        }

        protected override async Task<OperateResult<long>> ReadInt64CoreAsync(string address, CancellationToken ct)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            long value = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = await ReadWordAsync(a, ct).ConfigureAwait(false);
                if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
                value = (value << 16) | Convert.ToUInt16(r.Content.Trim(), 16);
            }
            return OperateResult<long>.Success(value);
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
            var addr = KeyenceNanoAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var bytes = new List<byte>();

            for (int i = 0; i < regCount; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = await ReadWordAsync(a, ct).ConfigureAwait(false);
                if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
                ushort val = Convert.ToUInt16(r.Content.Trim(), 16);
                bytes.Add((byte)(val >> 8));
                bytes.Add((byte)(val & 0xFF));
            }

            string text = Encoding.ASCII.GetString(bytes.ToArray(), 0, Math.Min(length, bytes.Count));
            return OperateResult<string>.Success(text.TrimEnd('\0'));
        }

        protected override async Task<OperateResult<byte[]>> ReadBytesCoreAsync(string address, ushort length, CancellationToken ct)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var bytes = new List<byte>();

            for (int i = 0; i < regCount; i++)
            {
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = await ReadWordAsync(a, ct).ConfigureAwait(false);
                if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
                ushort val = Convert.ToUInt16(r.Content.Trim(), 16);
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
            var addr = KeyenceNanoAddress.Parse(address);
            if (addr.IsBitArea || addr.SubAddress > 0)
                return await WriteBitAsync(addr, value ? "1" : "0", ct).ConfigureAwait(false);
            return await WriteWordAsync(addr, value ? "0001" : "0000", ct).ConfigureAwait(false);
        }

        protected override async Task<OperateResult> WriteInt16CoreAsync(string address, short value, CancellationToken ct)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            return await WriteWordAsync(addr, ((ushort)value).ToString("X4"), ct).ConfigureAwait(false);
        }

        protected override async Task<OperateResult> WriteInt32CoreAsync(string address, int value, CancellationToken ct)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            var r1 = await WriteWordAsync(addr, ((ushort)((uint)value >> 16)).ToString("X4"), ct).ConfigureAwait(false);
            if (!r1.IsSuccess) return r1;
            var nextAddr = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + 1}.{addr.SubAddress}");
            return await WriteWordAsync(nextAddr, ((ushort)(value & 0xFFFF)).ToString("X4"), ct).ConfigureAwait(false);
        }

        protected override async Task<OperateResult> WriteInt64CoreAsync(string address, long value, CancellationToken ct)
            => await WriteUInt64CoreAsync(address, unchecked((ulong)value), ct).ConfigureAwait(false);

        protected override async Task<OperateResult> WriteUInt64CoreAsync(string address, ulong value, CancellationToken ct)
        {
            var addr = KeyenceNanoAddress.Parse(address);
            for (int i = 0; i < 4; i++)
            {
                ushort word = (ushort)(value >> (48 - i * 16));
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i}.{addr.SubAddress}");
                var r = await WriteWordAsync(a, word.ToString("X4"), ct).ConfigureAwait(false);
                if (!r.IsSuccess) return r;
            }
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
            var addr = KeyenceNanoAddress.Parse(address);
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? string.Empty);
            if (strBytes.Length % 2 != 0) Array.Resize(ref strBytes, strBytes.Length + 1);

            for (int i = 0; i < strBytes.Length; i += 2)
            {
                ushort word = (ushort)((strBytes[i] << 8) | strBytes[i + 1]);
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i / 2}.{addr.SubAddress}");
                var r = await WriteWordAsync(a, word.ToString("X4"), ct).ConfigureAwait(false);
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        protected override async Task<OperateResult> WriteBytesCoreAsync(string address, byte[] data, CancellationToken ct)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var addr = KeyenceNanoAddress.Parse(address);
            byte[] padded = data;
            if (padded.Length % 2 != 0) { padded = new byte[data.Length + 1]; Array.Copy(data, padded, data.Length); }

            for (int i = 0; i < padded.Length; i += 2)
            {
                ushort word = (ushort)((padded[i] << 8) | padded[i + 1]);
                var a = KeyenceNanoAddress.Parse($"{addr.AreaCode}{addr.Address + i / 2}.{addr.SubAddress}");
                var r = await WriteWordAsync(a, word.ToString("X4"), ct).ConfigureAwait(false);
                if (!r.IsSuccess) return r;
            }
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

        public override string ToString() => $"KeyenceNano TCP {Ip}:{Port}";
    }
}
