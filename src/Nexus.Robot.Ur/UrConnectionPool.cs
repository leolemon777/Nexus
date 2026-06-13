using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Robot.Ur
{
    public sealed class UrConnectionPool : IDisposable
    {
        private readonly ConnectionPool<UrClient> _pool;
        private readonly string _key;
        private readonly string _ip;
        private readonly int _port;
        private readonly int _timeout;
        private readonly ILogger _logger;

        public event EventHandler<string>? OnMessageSent;
        public event EventHandler<string>? OnMessageReceived;
        public event EventHandler<string>? OnError;

        public UrConnectionPool(string ip, int port = 30003, int timeout = 5000, int maxPoolSize = 5, TimeSpan? idleTimeout = null, TimeSpan? cleanupInterval = null, ILogger? logger = null)
        {
            _ip = ip ?? throw new ArgumentNullException(nameof(ip));
            _port = port;
            _timeout = timeout;
            _logger = logger ?? NullLogger.Instance;
            _key = $"{_ip}:{_port}:{_timeout}";
            _pool = new ConnectionPool<UrClient>(CreateClient, maxPoolSize, idleTimeout, cleanupInterval);
        }

        public int ActiveCount => _pool.ActiveCount;
        public int IdleCount => _pool.IdleCount;

        public OperateResult<T> Execute<T>(Func<UrClient, OperateResult<T>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            UrClient? client = null;
            try { client = _pool.Acquire(_key); return operation(client); }
            finally { if (client != null) _pool.Release(_key, client); }
        }

        public async Task<OperateResult<T>> ExecuteAsync<T>(Func<UrClient, Task<OperateResult<T>>> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            var client = await _pool.AcquireAsync(_key).ConfigureAwait(false);
            try { return await operation(client).ConfigureAwait(false); }
            finally { _pool.Release(_key, client); }
        }

        private UrClient CreateClient()
        {
            var client = new UrClient(_ip, rtPort: _port);
            client.SetLogger(_logger);
            return client;
        }

        public void Dispose() { _pool.Dispose(); }
    }
}
