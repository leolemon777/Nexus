using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.CoAP
{
    /// <summary>
    /// CoAP (Constrained Application Protocol) 客户端 — RFC 7252。
    /// <para>基于 UDP 的轻量级 IoT 协议，类似 HTTP 但适用于受限设备。</para>
    /// <para>支持 GET, PUT, POST, DELETE 方法。</para>
    /// <para>地址格式: /path 或 /path?key=value</para>
    /// </summary>
    public class CoapClient : IDisposable, IBatchReadWrite
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _timeout;
        private UdpClient? _udp;
        private int _messageId;
        private bool _disposed;

        public CoapClient(string host, int port = 5683, int timeout = 5000)
        {
            _host = host;
            _port = port;
            _timeout = timeout;
        }

        // ── 连接管理 ──────────────────
        public bool IsConnected => _udp != null;

        public OperateResult Connect()
        {
            try
            {
                _udp = new UdpClient();
                _udp.Connect(_host, _port);
                _udp.Client.ReceiveTimeout = _timeout;
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed($"CoAP 连接失败: {ex.Message}"); }
        }

        public Task<OperateResult> ConnectAsync(CancellationToken ct = default) => Task.Run(() => Connect(), ct);
        Task<OperateResult> IReadWriteDevice.ConnectAsync() => ConnectAsync(CancellationToken.None);
        public void Disconnect() { _udp?.Close(); _udp = null; }
        public void Dispose() { if (_disposed) return; _disposed = true; Disconnect(); }

        // ── CoAP 请求 ──────────────────
        public OperateResult<byte[]> Get(string path)
        {
            var addr = new CoapAddressParser().Parse(path);
            return SendRequest(1, 1, addr.UriPath, addr.UriQuery, null); // CON, GET
        }

        public OperateResult<byte[]> Put(string path, byte[] payload)
        {
            var addr = new CoapAddressParser().Parse(path);
            return SendRequest(1, 3, addr.UriPath, addr.UriQuery, payload); // CON, PUT
        }

        public OperateResult<byte[]> Post(string path, byte[] payload)
        {
            var addr = new CoapAddressParser().Parse(path);
            return SendRequest(1, 2, addr.UriPath, addr.UriQuery, payload); // CON, POST
        }

        public OperateResult Delete(string path)
        {
            var addr = new CoapAddressParser().Parse(path);
            var r = SendRequest(1, 4, addr.UriPath, addr.UriQuery, null); // CON, DELETE
            return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message, r.ErrorCode);
        }

        private OperateResult<byte[]> SendRequest(byte type, byte code, string path, string? query, byte[]? payload)
        {
            if (_udp == null) return OperateResult<byte[]>.Failed("未连接");

            ushort msgId = (ushort)(Interlocked.Increment(ref _messageId) & 0xFFFF);
            byte[] token = new byte[] { (byte)(msgId & 0xFF), (byte)(msgId >> 8) };

            // Build CoAP message
            var message = new System.Collections.Generic.List<byte>();

            // Header: Ver(2) + T(2) + TKL(4)
            byte verTypeTkl = (byte)(0x40 | ((type & 0x03) << 4) | (token.Length & 0x0F));
            message.Add(verTypeTkl);
            message.Add(code);
            message.Add((byte)(msgId >> 8));
            message.Add((byte)(msgId & 0xFF));
            message.AddRange(token);

            // Options
            if (!string.IsNullOrEmpty(path))
            {
                string[] segments = path.TrimStart('/').Split('/');
                byte prevOptionNumber = 0;
                foreach (var segment in segments)
                {
                    if (string.IsNullOrEmpty(segment)) continue;
                    byte[] segmentBytes = System.Text.Encoding.UTF8.GetBytes(segment);
                    byte optionNumber = 11; // Uri-Path
                    byte delta = (byte)(optionNumber - prevOptionNumber);
                    message.Add((byte)((delta << 4) | (byte)segmentBytes.Length));
                    message.AddRange(segmentBytes);
                    prevOptionNumber = optionNumber;
                }
            }

            if (!string.IsNullOrEmpty(query))
            {
                byte[] queryBytes = System.Text.Encoding.UTF8.GetBytes(query);
                byte optionNumber = 15; // Uri-Query
                byte prevOptionNumber = 11;
                byte delta = (byte)(optionNumber - prevOptionNumber);
                message.Add((byte)((delta << 4) | (byte)Math.Min(queryBytes.Length, 12)));
                message.AddRange(queryBytes);
            }

            // Payload marker
            if (payload != null && payload.Length > 0)
            {
                message.Add(0xFF);
                message.AddRange(payload);
            }

            byte[] request = message.ToArray();

            try
            {
                _udp.Send(request, request.Length);

                var deadline = DateTime.UtcNow.AddMilliseconds(_timeout);
                while (DateTime.UtcNow < deadline)
                {
                    if (_udp.Available > 0)
                    {
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        byte[] response = _udp.Receive(ref ep);
                        if (response.Length < 4) continue;

                        byte respCode = response[1];
                        if (respCode >= 0x40 && respCode < 0x80) // 2.xx Success
                        {
                            int payloadStart = FindPayloadMarker(response);
                            if (payloadStart >= 0 && payloadStart < response.Length - 1)
                            {
                                byte[] data = new byte[response.Length - payloadStart - 1];
                                Buffer.BlockCopy(response, payloadStart + 1, data, 0, data.Length);
                                return OperateResult<byte[]>.Success(data);
                            }
                            return OperateResult<byte[]>.Success(Array.Empty<byte>());
                        }
                        else
                        {
                            return OperateResult<byte[]>.Failed($"CoAP 错误: {respCode / 32}.{respCode % 32}");
                        }
                    }
                    Thread.Sleep(10);
                }
                return OperateResult<byte[]>.Failed("CoAP 响应超时");
            }
            catch (Exception ex) { return OperateResult<byte[]>.Failed($"CoAP 通讯异常: {ex.Message}"); }
        }

        private static int FindPayloadMarker(byte[] data)
        {
            for (int i = 4; i < data.Length; i++)
                if (data[i] == 0xFF) return i;
            return -1;
        }

        // ── IReadWriteDevice (通过 CoAP 资源) ──────────────────
        private CoapAddress ParseAddr(string address) => new CoapAddressParser().Parse(address);

        public OperateResult<bool> ReadBool(string address)
        {
            var r = Get(address);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
        }
        public OperateResult<short> ReadInt16(string address)
        {
            var r = Get(address);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }
        public OperateResult<ushort> ReadUInt16(string address) { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<int> ReadInt32(string address)
        {
            var r = Get(address);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据不足");
            return OperateResult<int>.Success((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }
        public OperateResult<uint> ReadUInt32(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<long> ReadInt64(string address) { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<ulong> ReadUInt64(string address) { var r = ReadUInt32(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<float> ReadFloat(string address)
        {
            var r = Get(address);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }
        public OperateResult<double> ReadDouble(string address) { var r = ReadFloat(address); return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode); }
        public OperateResult<string> ReadString(string address, ushort length)
        {
            var r = Get(address);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.UTF8.GetString(r.Content, 0, Math.Min(length, r.Content.Length)));
        }
        public OperateResult<byte[]> ReadBytes(string address, ushort length) => Get(address);

        public OperateResult Write(string address, bool value) { var r = Put(address, new byte[] { (byte)(value ? 1 : 0) }); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message); }
        public OperateResult Write(string address, short value) { var r = Put(address, new byte[] { (byte)(value >> 8), (byte)value }); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message); }
        public OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public OperateResult Write(string address, int value) { var r = Put(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message); }
        public OperateResult Write(string address, uint value) => Write(address, (int)value);
        public OperateResult Write(string address, long value) => Write(address, (int)value);
        public OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public OperateResult Write(string address, float value) { int bits; unsafe { bits = *(int*)&value; } return Write(address, bits); }
        public OperateResult Write(string address, double value) => Write(address, (float)value);
        public OperateResult Write(string address, string value) { var r = Put(address, System.Text.Encoding.UTF8.GetBytes(value)); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message); }
        public OperateResult Write(string address, byte[] data) { var r = Put(address, data); return r.IsSuccess ? OperateResult.Success() : OperateResult.Failed(r.Message); }

        // ── Async ──────────────────
        public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ── IBatchReadWrite ──────────────────
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList(); if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>(); foreach (var addr in addrList) { var r = ReadInt16(addr); if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; } return OperateResult<Dictionary<string, object?>>.Success(result);
        }
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList(); if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>(); foreach (var addr in addrList) { var r = ReadBytes(addr, 1); if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; } return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items) { OperateResult r = kv.Value switch { bool b => Write(kv.Key, b), short s => Write(kv.Key, s), ushort us => Write(kv.Key, us), int i => Write(kv.Key, i), uint ui => Write(kv.Key, ui), float f => Write(kv.Key, f), string s => Write(kv.Key, s), byte[] b => Write(kv.Key, b), _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}") }; if (!r.IsSuccess) return r; } return OperateResult.Success();
        }
        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));
    }
}
