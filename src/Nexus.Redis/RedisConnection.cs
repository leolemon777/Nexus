using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Redis
{
    public class RedisConnection : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _timeoutMs;
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly object _lock = new object();
        private volatile bool _disposed;

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return _client != null && _client.Connected &&
                           (_client.Client.Poll(0, SelectMode.SelectRead) == false || _client.Available > 0);
                }
            }
        }

        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event EventHandler<string> OnError;

        public RedisConnection(string host, int port = 6379, int timeoutMs = 5000)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _timeoutMs = timeoutMs;
        }

        public void Connect()
        {
            lock (_lock)
            {
                DisconnectInternal();
                _client = new TcpClient { SendTimeout = _timeoutMs, ReceiveTimeout = _timeoutMs };
                var result = _client.BeginConnect(_host, _port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(_timeoutMs, true))
                {
                    DisconnectInternal();
                    throw new TimeoutException($"连接超时: {_host}:{_port} ({_timeoutMs}ms)");
                }
                _client.EndConnect(result);
                _stream = _client.GetStream();
                _stream.ReadTimeout = _timeoutMs;
                _stream.WriteTimeout = _timeoutMs;
            }
            OnConnected?.Invoke(this, EventArgs.Empty);
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            lock (_lock) DisconnectInternal();
            _client = new TcpClient { SendTimeout = _timeoutMs, ReceiveTimeout = _timeoutMs };
            using (ct.Register(() => { try { _client?.Close(); } catch { } }))
            {
                await _client.ConnectAsync(_host, _port).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _stream = _client.GetStream();
                _stream.ReadTimeout = _timeoutMs;
                _stream.WriteTimeout = _timeoutMs;
            }
            OnConnected?.Invoke(this, EventArgs.Empty);
        }

        public RespValue SendCommand(params string[] args)
        {
            EnsureConnected();
            byte[] data = RespParser.EncodeCommand(args);
            return SendAndReceive(data);
        }

        public async Task<RespValue> SendCommandAsync(string[] args, CancellationToken ct = default)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            byte[] data = RespParser.EncodeCommand(args);
            return await SendAndReceiveAsync(data, ct).ConfigureAwait(false);
        }

        public RespValue SendRaw(byte[] data)
        {
            EnsureConnected();
            return SendAndReceive(data);
        }

        public async Task<RespValue> SendRawAsync(byte[] data, CancellationToken ct = default)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await SendAndReceiveAsync(data, ct).ConfigureAwait(false);
        }

        public List<RespValue> SendPipeline(byte[][] commands)
        {
            EnsureConnected();
            return SendAndReceiveMultiple(commands);
        }

        public async Task<List<RespValue>> SendPipelineAsync(byte[][] commands, CancellationToken ct = default)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await SendAndReceiveMultipleAsync(commands, ct).ConfigureAwait(false);
        }

        private RespValue SendAndReceive(byte[] data)
        {
            try
            {
                NetworkStream ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) throw new IOException("连接已断开");

                ns.Write(data, 0, data.Length);
                return ReadRespValue(ns);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
                lock (_lock) DisconnectInternal();
                throw;
            }
        }

        private async Task<RespValue> SendAndReceiveAsync(byte[] data, CancellationToken ct)
        {
            try
            {
                NetworkStream ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) throw new IOException("连接已断开");

                await ns.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
                return await ReadRespValueAsync(ns, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
                lock (_lock) DisconnectInternal();
                throw;
            }
        }

        private List<RespValue> SendAndReceiveMultiple(byte[][] commands)
        {
            try
            {
                NetworkStream ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) throw new IOException("连接已断开");

                foreach (var cmd in commands)
                    ns.Write(cmd, 0, cmd.Length);

                var results = new List<RespValue>(commands.Length);
                for (int i = 0; i < commands.Length; i++)
                    results.Add(ReadRespValue(ns));
                return results;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
                lock (_lock) DisconnectInternal();
                throw;
            }
        }

        private async Task<List<RespValue>> SendAndReceiveMultipleAsync(byte[][] commands, CancellationToken ct)
        {
            try
            {
                NetworkStream ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) throw new IOException("连接已断开");

                foreach (var cmd in commands)
                    await ns.WriteAsync(cmd, 0, cmd.Length, ct).ConfigureAwait(false);

                var results = new List<RespValue>(commands.Length);
                for (int i = 0; i < commands.Length; i++)
                    results.Add(await ReadRespValueAsync(ns, ct).ConfigureAwait(false));
                return results;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
                lock (_lock) DisconnectInternal();
                throw;
            }
        }

        private RespValue ReadRespValue(NetworkStream ns)
        {
            byte[] buffer = new byte[8192];
            int offset = 0;
            return ReadRespValueFromStream(ns, buffer, ref offset);
        }

        private async Task<RespValue> ReadRespValueAsync(NetworkStream ns, CancellationToken ct)
        {
            byte[] buffer = new byte[8192];
            int[] offset = new int[1]; // 用数组包装以支持 async 中修改值
            return await ReadRespValueFromStreamAsync(ns, buffer, offset, ct).ConfigureAwait(false);
        }

        private RespValue ReadRespValueFromStream(NetworkStream ns, byte[] buffer, ref int offset)
        {
            EnsureBufferHasLine(ns, buffer, ref offset);
            byte typeByte = buffer[offset++];
            EnsureBufferHasLine(ns, buffer, ref offset);

            switch ((char)typeByte)
            {
                case '+':
                    return RespValue.SimpleString(ReadLineFromBuffer(buffer, ref offset));

                case '-':
                    return RespValue.Error(ReadLineFromBuffer(buffer, ref offset));

                case ':':
                    string intStr = ReadLineFromBuffer(buffer, ref offset);
                    long intVal = long.TryParse(intStr, out var v) ? v : 0;
                    return RespValue.Integer(intVal);

                case '$':
                    string lenStr = ReadLineFromBuffer(buffer, ref offset);
                    int len = int.TryParse(lenStr, out var l) ? l : 0;
                    if (len == -1) return RespValue.BulkNull();
                    EnsureBufferHasBytes(ns, buffer, ref offset, len + 2);
                    byte[] bulk = new byte[len];
                    Buffer.BlockCopy(buffer, offset, bulk, 0, len);
                    offset += len + 2;
                    return RespValue.BulkString(bulk);

                case '*':
                    string cntStr = ReadLineFromBuffer(buffer, ref offset);
                    int cnt = int.TryParse(cntStr, out var c) ? c : 0;
                    if (cnt == -1) return RespValue.ArrayNull();
                    var items = new RespValue[cnt];
                    for (int i = 0; i < cnt; i++)
                        items[i] = ReadRespValueFromStream(ns, buffer, ref offset);
                    return RespValue.Array(items);

                default:
                    throw new RespException($"Unexpected RESP type byte: 0x{typeByte:X2}");
            }
        }

        private async Task<RespValue> ReadRespValueFromStreamAsync(NetworkStream ns, byte[] buffer, int[] offset, CancellationToken ct)
        {
            await EnsureBufferHasLineAsync(ns, buffer, offset, ct).ConfigureAwait(false);
            byte typeByte = buffer[offset[0]++];
            await EnsureBufferHasLineAsync(ns, buffer, offset, ct).ConfigureAwait(false);

            switch ((char)typeByte)
            {
                case '+':
                    return RespValue.SimpleString(ReadLineFromArrayBuffer(buffer, offset));
                case '-':
                    return RespValue.Error(ReadLineFromArrayBuffer(buffer, offset));
                case ':':
                    string intStr = ReadLineFromArrayBuffer(buffer, offset);
                    long intVal = long.TryParse(intStr, out var v) ? v : 0;
                    return RespValue.Integer(intVal);
                case '$':
                    string lenStr = ReadLineFromArrayBuffer(buffer, offset);
                    int len = int.TryParse(lenStr, out var l) ? l : 0;
                    if (len == -1) return RespValue.BulkNull();
                    await EnsureBufferHasBytesAsync(ns, buffer, offset, len + 2, ct).ConfigureAwait(false);
                    byte[] bulk = new byte[len];
                    Buffer.BlockCopy(buffer, offset[0], bulk, 0, len);
                    offset[0] += len + 2;
                    return RespValue.BulkString(bulk);
                case '*':
                    string cntStr = ReadLineFromArrayBuffer(buffer, offset);
                    int cnt = int.TryParse(cntStr, out var c) ? c : 0;
                    if (cnt == -1) return RespValue.ArrayNull();
                    var items = new RespValue[cnt];
                    for (int i = 0; i < cnt; i++)
                        items[i] = await ReadRespValueFromStreamAsync(ns, buffer, offset, ct).ConfigureAwait(false);
                    return RespValue.Array(items);
                default:
                    throw new RespException($"Unexpected RESP type byte: 0x{typeByte:X2}");
            }
        }

        private void EnsureBufferHasLine(NetworkStream ns, byte[] buffer, ref int offset)
        {
            while (true)
            {
                for (int i = offset; i < buffer.Length - 1; i++)
                {
                    if (buffer[i] == '\r' && buffer[i + 1] == '\n')
                        return;
                }
                int available = ReadMoreData(ns, buffer, ref offset);
                if (available == 0)
                    throw new IOException("Connection closed");
            }
        }

        private async Task EnsureBufferHasLineAsync(NetworkStream ns, byte[] buffer, int[] offset, CancellationToken ct)
        {
            while (true)
            {
                for (int i = offset[0]; i < buffer.Length - 1; i++)
                {
                    if (buffer[i] == '\r' && buffer[i + 1] == '\n')
                        return;
                }
                int available = await ReadMoreDataAsync(ns, buffer, offset, ct).ConfigureAwait(false);
                if (available == 0)
                    throw new IOException("Connection closed");
            }
        }

        private void EnsureBufferHasBytes(NetworkStream ns, byte[] buffer, ref int offset, int count)
        {
            while (buffer.Length - offset < count)
            {
                int read = ReadMoreData(ns, buffer, ref offset);
                if (read == 0) throw new IOException("Connection closed");
            }
        }

        private async Task EnsureBufferHasBytesAsync(NetworkStream ns, byte[] buffer, int[] offset, int count, CancellationToken ct)
        {
            while (buffer.Length - offset[0] < count)
            {
                int read = await ReadMoreDataAsync(ns, buffer, offset, ct).ConfigureAwait(false);
                if (read == 0) throw new IOException("Connection closed");
            }
        }

        private int ReadMoreData(NetworkStream ns, byte[] buffer, ref int offset)
        {
            CompactBuffer(buffer, ref offset);
            int read = ns.Read(buffer, offset, buffer.Length - offset);
            return read;
        }

        private async Task<int> ReadMoreDataAsync(NetworkStream ns, byte[] buffer, int[] offset, CancellationToken ct)
        {
            CompactBufferArray(buffer, offset);
            int read = await ns.ReadAsync(buffer, offset[0], buffer.Length - offset[0], ct).ConfigureAwait(false);
            return read;
        }

        private static void CompactBuffer(byte[] buffer, ref int offset)
        {
            if (offset > 0 && offset < buffer.Length)
            {
                int remaining = buffer.Length - offset;
                Buffer.BlockCopy(buffer, offset, buffer, 0, remaining);
                offset = remaining;
            }
            else if (offset >= buffer.Length)
            {
                offset = 0;
            }
        }

        private static string ReadLineFromBuffer(byte[] buffer, ref int offset)
        {
            int start = offset;
            while (offset < buffer.Length - 1)
            {
                if (buffer[offset] == '\r' && buffer[offset + 1] == '\n')
                {
                    string line = Encoding.UTF8.GetString(buffer, start, offset - start);
                    offset += 2;
                    return line;
                }
                offset++;
            }
            throw new RespException("CRLF not found in buffer");
        }

        private static void CompactBufferArray(byte[] buffer, int[] offset)
        {
            if (offset[0] > 0 && offset[0] < buffer.Length)
            {
                int remaining = buffer.Length - offset[0];
                Buffer.BlockCopy(buffer, offset[0], buffer, 0, remaining);
                offset[0] = remaining;
            }
            else if (offset[0] >= buffer.Length)
            {
                offset[0] = 0;
            }
        }

        private static string ReadLineFromArrayBuffer(byte[] buffer, int[] offset)
        {
            int start = offset[0];
            while (offset[0] < buffer.Length - 1)
            {
                if (buffer[offset[0]] == '\r' && buffer[offset[0] + 1] == '\n')
                {
                    string line = Encoding.UTF8.GetString(buffer, start, offset[0] - start);
                    offset[0] += 2;
                    return line;
                }
                offset[0]++;
            }
            throw new RespException("CRLF not found in buffer");
        }

        private void EnsureConnected()
        {
            if (IsConnected) return;
            Connect();
        }

        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (IsConnected) return;
            await ConnectAsync(ct).ConfigureAwait(false);
        }

        public void Disconnect()
        {
            lock (_lock) DisconnectInternal();
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        private void DisconnectInternal()
        {
            try { _stream?.Close(); } catch { }
            _stream = null;
            try { _client?.Close(); } catch { }
            _client = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock) DisconnectInternal();
        }
    }
}
