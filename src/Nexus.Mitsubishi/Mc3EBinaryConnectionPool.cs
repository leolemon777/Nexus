using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// Mitsubishi MC-3E Binary 连接池 — 复用持久 TCP 连接，降低高频读写的建连成本。
    /// </summary>
    public sealed class Mc3EBinaryConnectionPool : IDisposable
    {
        private readonly ConnectionPool<Mc3EBinaryClient> _pool;
        private readonly string _key;
        private readonly MitsubishiModel _model;
        private readonly string _ip;
        private readonly int _port;
        private readonly int _timeout;
        private readonly byte _networkNo;
        private readonly byte _pcNo;
        private readonly ushort _destinationStationNo;
        private readonly byte _waitTimeUnit;
        private readonly Endianness _byteOrder;
        private readonly Encoding _stringEncoding;
        private readonly ushort _maxReadWordCount;
        private readonly ushort _maxWriteWordCount;
        private readonly ILogger _logger;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public Mc3EBinaryConnectionPool(
            MitsubishiModel model,
            string ip,
            int port = 5007,
            int timeout = 5000,
            byte networkNo = 0x00,
            byte pcNo = 0xFF,
            ushort destinationStationNo = 0x0000,
            byte waitTimeUnit = 0x00,
            Endianness byteOrder = Endianness.BigEndian,
            Encoding? stringEncoding = null,
            ushort maxReadWordCount = 960,
            ushort maxWriteWordCount = 960,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            ILogger? logger = null)
        {
            _model = model;
            _ip = ip ?? throw new ArgumentNullException(nameof(ip));
            _port = port;
            _timeout = timeout;
            _networkNo = networkNo;
            _pcNo = pcNo;
            _destinationStationNo = destinationStationNo;
            _waitTimeUnit = waitTimeUnit;
            _byteOrder = byteOrder;
            _stringEncoding = stringEncoding ?? Encoding.ASCII;
            _maxReadWordCount = maxReadWordCount;
            _maxWriteWordCount = maxWriteWordCount;
            _logger = logger ?? NullLogger.Instance;

            _key = $"{_model}:{_ip}:{_port}:{_timeout}:{_networkNo}:{_pcNo}:{_destinationStationNo}:{_waitTimeUnit}:{_byteOrder}:{_stringEncoding.WebName}:{_maxReadWordCount}:{_maxWriteWordCount}";
            _pool = new ConnectionPool<Mc3EBinaryClient>(
                CreateClient,
                maxPoolSize,
                idleTimeout,
                cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<Mc3EBinaryClient, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            Mc3EBinaryClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Mitsubishi MC-3E Binary 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public OperateResult Execute(Func<Mc3EBinaryClient, OperateResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            Mc3EBinaryClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Mitsubishi MC-3E Binary 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(
            Func<Mc3EBinaryClient, Task<OperateResult<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            Mc3EBinaryClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Mitsubishi MC-3E Binary 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public async Task<OperateResult> ExecuteAsync(
            Func<Mc3EBinaryClient, Task<OperateResult>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            Mc3EBinaryClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Mitsubishi MC-3E Binary 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public OperateResult<bool> ReadBool(string address) => Execute(c => c.ReadBool(address));
        public OperateResult<bool[]> ReadBools(string address, ushort count) => Execute(c => c.ReadBools(address, count));
        public OperateResult<short> ReadInt16(string address) => Execute(c => c.ReadInt16(address));
        public OperateResult<ushort> ReadUInt16(string address) => Execute(c => c.ReadUInt16(address));
        public OperateResult<int> ReadInt32(string address) => Execute(c => c.ReadInt32(address));
        public OperateResult<uint> ReadUInt32(string address) => Execute(c => c.ReadUInt32(address));
        public OperateResult<long> ReadInt64(string address) => Execute(c => c.ReadInt64(address));
        public OperateResult<ulong> ReadUInt64(string address) => Execute(c => c.ReadUInt64(address));
        public OperateResult<float> ReadFloat(string address) => Execute(c => c.ReadFloat(address));
        public OperateResult<double> ReadDouble(string address) => Execute(c => c.ReadDouble(address));
        public OperateResult<string> ReadString(string address, ushort length) => Execute(c => c.ReadString(address, length));
        public OperateResult<string> ReadStringEncoded(string address, ushort length) => Execute(c => c.ReadStringEncoded(address, length));
        public OperateResult<byte[]> ReadBytes(string address, ushort length) => Execute(c => c.ReadBytes(address, length));

        public OperateResult Write(string address, bool value) => Execute(c => c.Write(address, value));
        public OperateResult WriteBools(string address, bool[] values) => Execute(c => c.WriteBools(address, values));
        public OperateResult Write(string address, short value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, ushort value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, int value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, uint value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, long value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, ulong value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, float value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, double value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, string value) => Execute(c => c.Write(address, value));
        public OperateResult WriteStringEncoded(string address, string value) => Execute(c => c.WriteStringEncoded(address, value));
        public OperateResult Write(string address, byte[] data) => Execute(c => c.Write(address, data));

        public OperateResult<byte[]> ReadWordsBatch(byte subLabel, uint startAddress, ushort count)
            => Execute(c => c.ReadWordsBatch(subLabel, startAddress, count));
        public OperateResult WriteWordsBatch(byte subLabel, uint startAddress, ushort count, byte[] writeData)
            => Execute(c => c.WriteWordsBatch(subLabel, startAddress, count, writeData));
        public OperateResult<byte[]> ReadBitsBatch(byte subLabel, uint startAddress, ushort count)
            => Execute(c => c.ReadBitsBatch(subLabel, startAddress, count));
        public OperateResult WriteBitsBatch(byte subLabel, uint startAddress, ushort count, byte[] bitData)
            => Execute(c => c.WriteBitsBatch(subLabel, startAddress, count, bitData));
        public OperateResult<byte[]> ReadLarge(string address, ushort length) => Execute(c => c.ReadLarge(address, length));
        public OperateResult WriteLarge(string address, byte[] data) => Execute(c => c.WriteLarge(address, data));

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
            => Execute(c => c.BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
            => Execute(c => c.RandomRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> ReadRandomMultiLength(IEnumerable<(string address, ushort length)> items)
            => Execute(c => c.ReadRandomMultiLength(items));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
            => Execute(c => c.BatchWrite(items));
        public OperateResult RandomWrite(IEnumerable<KeyValuePair<string, object>> items)
            => Execute(c => c.RandomWrite(items));

        public OperateResult RemoteRun() => Execute(c => c.RemoteRun());
        public OperateResult RemoteStop() => Execute(c => c.RemoteStop());
        public OperateResult RemoteReset() => Execute(c => c.RemoteReset());
        public OperateResult<string> ReadPlcType() => Execute(c => c.ReadPlcType());
        public OperateResult ErrorStateReset() => Execute(c => c.ErrorStateReset());

        public void Clear() => _pool.Clear();
        public void Dispose() => _pool.Dispose();

        private Mc3EBinaryClient CreateClient()
        {
            var client = new Mc3EBinaryClient(_model, _ip, _port, _timeout)
            {
                NetworkNo = _networkNo,
                PcNo = _pcNo,
                DestinationStationNo = _destinationStationNo,
                WaitTimeUnit = _waitTimeUnit,
                ByteOrder = _byteOrder,
                StringEncoding = _stringEncoding,
                MaxReadWordCount = _maxReadWordCount,
                MaxWriteWordCount = _maxWriteWordCount
            };
            client.OnMessageSent += Client_OnMessageSent;
            client.OnMessageReceived += Client_OnMessageReceived;
            client.OnError += Client_OnError;
            client.SetPersistentConnection();
            client.SetLogger(_logger);
            return client;
        }

        private void Client_OnMessageSent(object? sender, string message) => OnMessageSent?.Invoke(this, message);
        private void Client_OnMessageReceived(object? sender, string message) => OnMessageReceived?.Invoke(this, message);
        private void Client_OnError(object? sender, string message) => OnError?.Invoke(this, message);
    }
}
