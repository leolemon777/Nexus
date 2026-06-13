using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Freedom
{
    public class FreedomTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        private int _stx;

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public FreedomTcpClient(string ip, int port, int timeout = 5000)
            : base(ip, port, timeout)
        {
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

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                var ms = new MemoryStream();
                ReadUntilTimeoutSync(ns, ms);
                byte[] response = ms.ToArray();

                if (response.Length == 0)
                {
                    if (!_persistentMode)
                    {
                        _asyncLock.Wait();
                        try { DisconnectCore(); }
                        finally { _asyncLock.Release(); }
                    }
                    return OperateResult<byte[]>.Failed("接收超时");
                }

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                RaiseMessageReceived(DataConverter.ToHexString(response));

                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }

                return OperateResult<byte[]>.Success(response);
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

        protected new async Task<OperateResult<byte[]>> SendAndReceiveAsync(
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

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                await ns.WriteAsync(request, 0, request.Length, cancellationToken).ConfigureAwait(false);

                var ms = new MemoryStream();
                await ReadUntilTimeoutAsync(ns, ms, cancellationToken).ConfigureAwait(false);
                byte[] response = ms.ToArray();

                if (response.Length == 0)
                {
                    if (!_persistentMode)
                    {
                        await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try { DisconnectCore(); }
                        finally { _asyncLock.Release(); }
                    }
                    return OperateResult<byte[]>.Failed("接收超时");
                }

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                RaiseMessageReceived(DataConverter.ToHexString(response));

                if (!_persistentMode)
                {
                    await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }

                return OperateResult<byte[]>.Success(response);
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

        private void ReadUntilTimeoutSync(NetworkStream ns, MemoryStream ms)
        {
            byte[] buf = new byte[4096];
            while (true)
            {
                int read;
                try { read = ns.Read(buf, 0, buf.Length); }
                catch (System.IO.IOException) { break; }
                if (read <= 0) break;
                ms.Write(buf, 0, read);
                if (!ns.DataAvailable) break;
            }
        }

        private async Task ReadUntilTimeoutAsync(NetworkStream ns, MemoryStream ms, CancellationToken ct)
        {
            byte[] buf = new byte[4096];
            while (true)
            {
                int read;
                try { read = await ns.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false); }
                catch (System.IO.IOException) { break; }
                catch (OperationCanceledException) { break; }
                if (read <= 0) break;
                ms.Write(buf, 0, read);
                if (!ns.DataAvailable) break;
            }
        }

        private OperateResult<byte[]> ReadCore(string address)
        {
            _stx = ParseStx(ref address);
            byte[] request = ParseHex(address);
            if (request.Length == 0) return OperateResult<byte[]>.Failed("地址为空");

            var result = SendAndReceive(request);
            if (!result.IsSuccess) return result;

            return StripHeader(result.Content, _stx);
        }

        private async Task<OperateResult<byte[]>> ReadCoreAsync(string address, CancellationToken ct)
        {
            _stx = ParseStx(ref address);
            byte[] request = ParseHex(address);
            if (request.Length == 0) return OperateResult<byte[]>.Failed("地址为空");

            var result = await SendAndReceiveAsync(request, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return result;

            return StripHeader(result.Content, _stx);
        }

        private static OperateResult<byte[]> StripHeader(byte[] response, int stx)
        {
            if (stx <= 0) return OperateResult<byte[]>.Success(response);
            if (stx >= response.Length) return OperateResult<byte[]>.Success(Array.Empty<byte>());
            byte[] data = new byte[response.Length - stx];
            Buffer.BlockCopy(response, stx, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        // ── Address parsing ──

        private static int ParseStx(ref string address)
        {
            const string prefix = "stx=";
            if (address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                int semi = address.IndexOf(';');
                if (semi > 0)
                {
                    string num = address.Substring(prefix.Length, semi - prefix.Length);
                    if (int.TryParse(num, out int stx) && stx > 0)
                    {
                        address = address.Substring(semi + 1);
                        return stx;
                    }
                }
            }
            return 0;
        }

        private static byte[] ParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();

            int count = 0;
            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                if (IsHexChar(c))
                {
                    count++;
                    if (i + 1 < hex.Length && IsHexChar(hex[i + 1])) { i++; }
                }
            }

            byte[] result = new byte[count];
            int pos = 0;
            int idx = 0;
            while (idx < hex.Length && pos < count)
            {
                while (idx < hex.Length && !IsHexChar(hex[idx])) idx++;
                if (idx >= hex.Length) break;

                int hi = HexVal(hex[idx++]);
                int lo = 0;
                if (idx < hex.Length && IsHexChar(hex[idx]))
                    lo = HexVal(hex[idx++]);

                result[pos++] = (byte)((hi << 4) | lo);
            }

            return result;
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'A' && c <= 'F') ||
                   (c >= 'a' && c <= 'f');
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return c - 'a' + 10;
        }

        // ── IReadWriteDevice ──

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadCore(address);
            return r.IsSuccess
                ? OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[r.Content.Length - 1] != 0)
                : OperateResult<bool>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足 2 字节");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("响应数据不足 2 字节");
            return OperateResult<ushort>.Success((ushort)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足 4 字节");
            return OperateResult<int>.Success(
                (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<uint>.Failed("响应数据不足 4 字节");
            return OperateResult<uint>.Success(
                (uint)((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]));
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足 8 字节");
            return OperateResult<long>.Success(
                ((long)r.Content[0] << 56) | ((long)r.Content[1] << 48) |
                ((long)r.Content[2] << 40) | ((long)r.Content[3] << 32) |
                ((long)r.Content[4] << 24) | ((long)r.Content[5] << 16) |
                ((long)r.Content[6] << 8) | r.Content[7]);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<ulong>.Failed("响应数据不足 8 字节");
            return OperateResult<ulong>.Success(
                ((ulong)r.Content[0] << 56) | ((ulong)r.Content[1] << 48) |
                ((ulong)r.Content[2] << 40) | ((ulong)r.Content[3] << 32) |
                ((ulong)r.Content[4] << 24) | ((ulong)r.Content[5] << 16) |
                ((ulong)r.Content[6] << 8) | r.Content[7]);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足 4 字节");
            int bits = (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3];
            byte[] bytes = BitConverter.GetBytes(bits);
            return OperateResult<float>.Success(BitConverter.ToSingle(bytes, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<double>.Failed("响应数据不足 8 字节");
            long bits = ((long)r.Content[0] << 56) | ((long)r.Content[1] << 48) |
                        ((long)r.Content[2] << 40) | ((long)r.Content[3] << 32) |
                        ((long)r.Content[4] << 24) | ((long)r.Content[5] << 16) |
                        ((long)r.Content[6] << 8) | r.Content[7];
            byte[] bytes = BitConverter.GetBytes(bits);
            return OperateResult<double>.Success(BitConverter.ToDouble(bytes, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadCore(address);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.UTF8.GetString(r.Content));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            return ReadCore(address);
        }

        public override OperateResult Write(string address, bool value)
        {
            string hex = value ? "01" : "00";
            return ReadCore(address + hex);
        }

        public override OperateResult Write(string address, short value)
        {
            return ReadCore(address + ShortToHex(value));
        }

        public override OperateResult Write(string address, ushort value)
        {
            return ReadCore(address + UShortToHex(value));
        }

        public override OperateResult Write(string address, int value)
        {
            return ReadCore(address + IntToHex(value));
        }

        public override OperateResult Write(string address, uint value)
        {
            return ReadCore(address + UIntToHex(value));
        }

        public override OperateResult Write(string address, long value)
        {
            return ReadCore(address + LongToHex(value));
        }

        public override OperateResult Write(string address, ulong value)
        {
            return ReadCore(address + ULongToHex(value));
        }

        public override OperateResult Write(string address, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            int bits = BitConverter.ToInt32(bytes, 0);
            return ReadCore(address + IntToHex(bits));
        }

        public override OperateResult Write(string address, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            long bits = BitConverter.ToInt64(bytes, 0);
            return ReadCore(address + LongToHex(bits));
        }

        public override OperateResult Write(string address, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            return ReadCore(address + BytesToHex(bytes));
        }

        public override OperateResult Write(string address, byte[] data)
        {
            return ReadCore(address + BytesToHex(data));
        }

        // ── Async IReadWriteDevice ──

        public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            return r.IsSuccess
                ? OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[r.Content.Length - 1] != 0)
                : OperateResult<bool>.Failed(r.Message, r.ErrorCode);
        }

        public override async Task<OperateResult<short>> ReadInt16Async(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足 2 字节");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override async Task<OperateResult<ushort>> ReadUInt16Async(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("响应数据不足 2 字节");
            return OperateResult<ushort>.Success((ushort)((r.Content[0] << 8) | r.Content[1]));
        }

        public override async Task<OperateResult<int>> ReadInt32Async(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足 4 字节");
            return OperateResult<int>.Success(
                (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override async Task<OperateResult<uint>> ReadUInt32Async(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<uint>.Failed("响应数据不足 4 字节");
            return OperateResult<uint>.Success(
                (uint)((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]));
        }

        public override async Task<OperateResult<long>> ReadInt64Async(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足 8 字节");
            return OperateResult<long>.Success(
                ((long)r.Content[0] << 56) | ((long)r.Content[1] << 48) |
                ((long)r.Content[2] << 40) | ((long)r.Content[3] << 32) |
                ((long)r.Content[4] << 24) | ((long)r.Content[5] << 16) |
                ((long)r.Content[6] << 8) | r.Content[7]);
        }

        public override async Task<OperateResult<ulong>> ReadUInt64Async(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<ulong>.Failed("响应数据不足 8 字节");
            return OperateResult<ulong>.Success(
                ((ulong)r.Content[0] << 56) | ((ulong)r.Content[1] << 48) |
                ((ulong)r.Content[2] << 40) | ((ulong)r.Content[3] << 32) |
                ((ulong)r.Content[4] << 24) | ((ulong)r.Content[5] << 16) |
                ((ulong)r.Content[6] << 8) | r.Content[7]);
        }

        public override async Task<OperateResult<float>> ReadFloatAsync(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足 4 字节");
            int bits = (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3];
            byte[] bytes = BitConverter.GetBytes(bits);
            return OperateResult<float>.Success(BitConverter.ToSingle(bytes, 0));
        }

        public override async Task<OperateResult<double>> ReadDoubleAsync(string address)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<double>.Failed("响应数据不足 8 字节");
            long bits = ((long)r.Content[0] << 56) | ((long)r.Content[1] << 48) |
                        ((long)r.Content[2] << 40) | ((long)r.Content[3] << 32) |
                        ((long)r.Content[4] << 24) | ((long)r.Content[5] << 16) |
                        ((long)r.Content[6] << 8) | r.Content[7];
            byte[] bytes = BitConverter.GetBytes(bits);
            return OperateResult<double>.Success(BitConverter.ToDouble(bytes, 0));
        }

        public override async Task<OperateResult<string>> ReadStringAsync(string address, ushort length)
        {
            var r = await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.UTF8.GetString(r.Content));
        }

        public override async Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length)
        {
            return await ReadCoreAsync(address, CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, bool value)
        {
            string hex = value ? "01" : "00";
            return await ReadCoreAsync(address + hex, CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, short value)
        {
            return await ReadCoreAsync(address + ShortToHex(value), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, ushort value)
        {
            return await ReadCoreAsync(address + UShortToHex(value), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, int value)
        {
            return await ReadCoreAsync(address + IntToHex(value), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, uint value)
        {
            return await ReadCoreAsync(address + UIntToHex(value), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, long value)
        {
            return await ReadCoreAsync(address + LongToHex(value), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, ulong value)
        {
            return await ReadCoreAsync(address + ULongToHex(value), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            int bits = BitConverter.ToInt32(bytes, 0);
            return await ReadCoreAsync(address + IntToHex(bits), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            long bits = BitConverter.ToInt64(bytes, 0);
            return await ReadCoreAsync(address + LongToHex(bits), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            return await ReadCoreAsync(address + BytesToHex(bytes), CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperateResult> WriteAsync(string address, byte[] data)
        {
            return await ReadCoreAsync(address + BytesToHex(data), CancellationToken.None).ConfigureAwait(false);
        }

        // ── IBatchReadWrite ──

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            foreach (string addr in addresses)
            {
                var r = ReadCore(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public async Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken ct = default)
        {
            var result = new Dictionary<string, object?>();
            foreach (string addr in addresses)
            {
                var r = await ReadCoreAsync(addr, ct).ConfigureAwait(false);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addresses)
            {
                var r = ReadCore(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public async Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken ct = default)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addresses)
            {
                var r = await ReadCoreAsync(addr, ct).ConfigureAwait(false);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                var r = WriteCore(kv.Key, kv.Value);
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public async Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default)
        {
            foreach (var kv in items)
            {
                var r = await WriteCoreAsync(kv.Key, kv.Value, ct).ConfigureAwait(false);
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        private OperateResult WriteCore(string address, object value)
        {
            switch (value)
            {
                case bool v: return Write(address, v);
                case short v: return Write(address, v);
                case ushort v: return Write(address, v);
                case int v: return Write(address, v);
                case uint v: return Write(address, v);
                case long v: return Write(address, v);
                case ulong v: return Write(address, v);
                case float v: return Write(address, v);
                case double v: return Write(address, v);
                case string v: return Write(address, v);
                case byte[] v: return Write(address, v);
                default: return OperateResult.Failed($"不支持的类型: {value?.GetType().Name}");
            }
        }

        private Task<OperateResult> WriteCoreAsync(string address, object value, CancellationToken ct)
        {
            switch (value)
            {
                case bool v: return WriteAsync(address, v);
                case short v: return WriteAsync(address, v);
                case ushort v: return WriteAsync(address, v);
                case int v: return WriteAsync(address, v);
                case uint v: return WriteAsync(address, v);
                case long v: return WriteAsync(address, v);
                case ulong v: return WriteAsync(address, v);
                case float v: return WriteAsync(address, v);
                case double v: return WriteAsync(address, v);
                case string v: return WriteAsync(address, v);
                case byte[] v: return WriteAsync(address, v);
                default: return Task.FromResult(OperateResult.Failed($"不支持的类型: {value?.GetType().Name}"));
            }
        }

        // ── Hex encoding helpers ──

        private static string ShortToHex(short v) => UShortToHex((ushort)v);
        private static string UShortToHex(ushort v)
            => $"{(v >> 8) & 0xFF:X2} {v & 0xFF:X2}";

        private static string IntToHex(int v)
            => $"{(v >> 24) & 0xFF:X2} {(v >> 16) & 0xFF:X2} {(v >> 8) & 0xFF:X2} {v & 0xFF:X2}";

        private static string UIntToHex(uint v) => IntToHex((int)v);

        private static string LongToHex(long v)
            => $"{(v >> 56) & 0xFF:X2} {(v >> 48) & 0xFF:X2} {(v >> 40) & 0xFF:X2} {(v >> 32) & 0xFF:X2} " +
               $"{(v >> 24) & 0xFF:X2} {(v >> 16) & 0xFF:X2} {(v >> 8) & 0xFF:X2} {v & 0xFF:X2}";

        private static string ULongToHex(ulong v) => LongToHex((long)v);

        private static string BytesToHex(byte[] data)
        {
            if (data.Length == 0) return string.Empty;
            var sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
