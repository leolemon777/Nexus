using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Siemens
{
    /// <summary>
    /// Siemens PPI 连接池 — 复用串口 PPI 连接，适用于多任务并发访问同一串口 PLC。
    /// </summary>
    public sealed class SiemensPpiConnectionPool : IDisposable
    {
        private readonly ConnectionPool<SiemensPpiClient> _pool;
        private readonly string _key;
        private readonly Func<ISerialPort> _portFactory;
        private readonly int _timeout;
        private readonly byte _masterAddress;
        private readonly byte _slaveAddress;
        private readonly ILogger _logger;

        public SiemensPpiConnectionPool(
            Func<ISerialPort> portFactory,
            int timeout = 5000,
            byte masterAddress = 1,
            byte slaveAddress = 2,
            int maxPoolSize = 3,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            ILogger? logger = null)
        {
            _portFactory = portFactory ?? throw new ArgumentNullException(nameof(portFactory));
            _timeout = timeout;
            _masterAddress = masterAddress;
            _slaveAddress = slaveAddress;
            _logger = logger ?? NullLogger.Instance;
            _key = $"PPI:{masterAddress}:{slaveAddress}";
            _pool = new ConnectionPool<SiemensPpiClient>(
                CreateClient,
                maxPoolSize,
                idleTimeout,
                cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<SiemensPpiClient, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SiemensPpiClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Siemens PPI 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public OperateResult Execute(Func<SiemensPpiClient, OperateResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SiemensPpiClient? client = null;
            try
            {
                client = _pool.Acquire(_key);
                return operation(client);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Siemens PPI 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    _pool.Release(_key, client);
            }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(
            Func<SiemensPpiClient, Task<OperateResult<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SiemensPpiClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult<T>.Failed($"Siemens PPI 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public async Task<OperateResult> ExecuteAsync(
            Func<SiemensPpiClient, Task<OperateResult>> operation,
            CancellationToken cancellationToken = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            SiemensPpiClient? client = null;
            try
            {
                client = await _pool.AcquireAsync(_key, cancellationToken).ConfigureAwait(false);
                return await operation(client).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"Siemens PPI 连接池操作失败: {ex.Message}");
            }
            finally
            {
                if (client != null)
                    await _pool.ReleaseAsync(_key, client).ConfigureAwait(false);
            }
        }

        public OperateResult<bool> ReadBool(string address) => Execute(c => c.ReadBool(address));
        public OperateResult<short> ReadInt16(string address) => Execute(c => c.ReadInt16(address));
        public OperateResult<ushort> ReadUInt16(string address) => Execute(c => c.ReadUInt16(address));
        public OperateResult<int> ReadInt32(string address) => Execute(c => c.ReadInt32(address));
        public OperateResult<uint> ReadUInt32(string address) => Execute(c => c.ReadUInt32(address));
        public OperateResult<long> ReadInt64(string address) => Execute(c => c.ReadInt64(address));
        public OperateResult<ulong> ReadUInt64(string address) => Execute(c => c.ReadUInt64(address));
        public OperateResult<float> ReadFloat(string address) => Execute(c => c.ReadFloat(address));
        public OperateResult<double> ReadDouble(string address) => Execute(c => c.ReadDouble(address));
        public OperateResult<string> ReadString(string address, ushort length) => Execute(c => c.ReadString(address, length));
        public OperateResult<byte[]> ReadBytes(string address, ushort length) => Execute(c => c.ReadBytes(address, length));

        public OperateResult Write(string address, bool value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, short value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, ushort value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, int value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, uint value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, long value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, ulong value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, float value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, double value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, string value) => Execute(c => c.Write(address, value));
        public OperateResult Write(string address, byte[] data) => Execute(c => c.Write(address, data));

        public void Clear() => _pool.Clear();
        public void Dispose() => _pool.Dispose();

        private SiemensPpiClient CreateClient()
        {
            var port = _portFactory();
            var client = new SiemensPpiClient(port, _timeout)
            {
                MasterAddress = _masterAddress,
                SlaveAddress = _slaveAddress
            };
            client.SetLogger(_logger);
            return client;
        }
    }
}
