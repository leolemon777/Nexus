using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// 线程安全的连接池实现，支持多设备连接管理、健康检查和空闲超时自动清理。
    /// </summary>
    public class ConnectionPool<T> : IConnectionPool<T> where T : IReadWriteDevice
    {
        private readonly Func<T> _deviceFactory;
        private readonly int _maxPoolSize;
        private readonly TimeSpan _idleTimeout;
        private readonly ConcurrentDictionary<string, DeviceBucket> _pools =
            new ConcurrentDictionary<string, DeviceBucket>();
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _semaphore;
        private volatile bool _disposed;

        private class DeviceBucket
        {
            public ConcurrentStack<PooledDevice> IdleDevices { get; } = new ConcurrentStack<PooledDevice>();
            public int ActiveCount;
        }

        private class PooledDevice
        {
            public T Device { get; }
            public DateTime LastUsed { get; }

            public PooledDevice(T device)
            {
                Device = device;
                LastUsed = DateTime.UtcNow;
            }
        }

        /// <param name="deviceFactory">创建新设备实例的工厂函数</param>
        /// <param name="maxPoolSize">每个 key 的最大连接数，默认 5</param>
        /// <param name="idleTimeout">空闲超时时间，默认 5 分钟</param>
        /// <param name="cleanupInterval">清理周期，默认等于 idleTimeout</param>
        public ConnectionPool(
            Func<T> deviceFactory,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null)
        {
            _deviceFactory = deviceFactory ?? throw new ArgumentNullException(nameof(deviceFactory));
            if (maxPoolSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxPoolSize));
            _maxPoolSize = maxPoolSize;
            _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
            _cleanupTimer = new Timer(CleanupCallback, null, _idleTimeout, cleanupInterval ?? _idleTimeout);
            _semaphore = new SemaphoreSlim(maxPoolSize, maxPoolSize);
        }

        /// <inheritdoc />
        public int ActiveCount
        {
            get
            {
                int count = 0;
                foreach (var kvp in _pools)
                    count += kvp.Value.ActiveCount;
                return count;
            }
        }

        /// <inheritdoc />
        public int IdleCount
        {
            get
            {
                int count = 0;
                foreach (var kvp in _pools)
                    count += kvp.Value.IdleDevices.Count;
                return count;
            }
        }

        /// <inheritdoc />
        public T Acquire(string key)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPool<T>));
            if (key == null) throw new ArgumentNullException(nameof(key));

            var bucket = _pools.GetOrAdd(key, _ => new DeviceBucket());

            while (true)
            {
                if (bucket.IdleDevices.TryPop(out var pooled))
                {
                    if (pooled.Device.IsConnected)
                    {
                        Interlocked.Increment(ref bucket.ActiveCount);
                        return pooled.Device;
                    }

                    try
                    {
                        pooled.Device.Connect();
                        if (pooled.Device.IsConnected)
                        {
                            Interlocked.Increment(ref bucket.ActiveCount);
                            return pooled.Device;
                        }
                    }
                    catch
                    {
                        // reconnect failed — fall through to dispose and retry
                    }

                    TryDisposeDevice(pooled.Device);
                    continue;
                }

                var device = _deviceFactory();
                if (device == null)
                    throw new InvalidOperationException("Device factory returned null.");

                try
                {
                    device.Connect();
                }
                catch
                {
                    TryDisposeDevice(device);
                    throw;
                }

                Interlocked.Increment(ref bucket.ActiveCount);
                return device;
            }
        }

        /// <summary>
        /// 异步获取连接，受最大连接数严格限制。
        /// </summary>
        public async Task<T> AcquireAsync(string key, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPool<T>));
            if (key == null) throw new ArgumentNullException(nameof(key));

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPool<T>));

                var bucket = _pools.GetOrAdd(key, _ => new DeviceBucket());

                if (bucket.IdleDevices.TryPop(out var pooled))
                {
                    if (pooled.Device.IsConnected || await TryReconnectAsync(pooled.Device).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref bucket.ActiveCount);
                        return pooled.Device;
                    }
                    TryDisposeDevice(pooled.Device);
                }

                var device = _deviceFactory();
                if (device == null)
                    throw new InvalidOperationException("Device factory returned null.");

                try
                {
                    await device.ConnectAsync().ConfigureAwait(false);
                }
                catch
                {
                    TryDisposeDevice(device);
                    throw;
                }

                Interlocked.Increment(ref bucket.ActiveCount);
                return device;
            }
            catch
            {
                _semaphore.Release();
                throw;
            }
        }

        private static async Task<bool> TryReconnectAsync(T device)
        {
            try
            {
                await device.ConnectAsync().ConfigureAwait(false);
                return device.IsConnected;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc />
        public void Release(string key, T device)
        {
            if (device == null) return;

            if (key != null && _pools.TryGetValue(key, out var bucket))
            {
                if (bucket.IdleDevices.Count < _maxPoolSize && device.IsConnected)
                {
                    bucket.IdleDevices.Push(new PooledDevice(device));
                    Interlocked.Decrement(ref bucket.ActiveCount);
                    _semaphore.Release();
                    return;
                }

                Interlocked.Decrement(ref bucket.ActiveCount);
            }

            _semaphore.Release();
            TryDisposeDevice(device);
        }

        /// <summary>
        /// 异步释放连接回池中。
        /// </summary>
        public async Task ReleaseAsync(string key, T device)
        {
            if (device == null) return;

            if (key != null && _pools.TryGetValue(key, out var bucket))
            {
                if (bucket.IdleDevices.Count < _maxPoolSize && device.IsConnected)
                {
                    bucket.IdleDevices.Push(new PooledDevice(device));
                    Interlocked.Decrement(ref bucket.ActiveCount);
                    _semaphore.Release();
                    return;
                }

                Interlocked.Decrement(ref bucket.ActiveCount);
            }

            _semaphore.Release();
            TryDisposeDevice(device);
        }

        /// <inheritdoc />
        public void Remove(string key)
        {
            if (key == null) return;

            if (_pools.TryRemove(key, out var bucket))
            {
                while (bucket.IdleDevices.TryPop(out var pooled))
                    TryDisposeDevice(pooled.Device);
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            foreach (var kvp in _pools)
            {
                if (_pools.TryRemove(kvp.Key, out var bucket))
                {
                    while (bucket.IdleDevices.TryPop(out var pooled))
                        TryDisposeDevice(pooled.Device);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _cleanupTimer?.Dispose();
            _semaphore?.Dispose();

            foreach (var kvp in _pools)
            {
                if (_pools.TryRemove(kvp.Key, out var bucket))
                {
                    while (bucket.IdleDevices.TryPop(out var pooled))
                        TryDisposeDevice(pooled.Device);
                }
            }
        }

        private void CleanupCallback(object state)
        {
            if (_disposed) return;

            var cutoff = DateTime.UtcNow - _idleTimeout;

            foreach (var kvp in _pools)
            {
                var bucket = kvp.Value;
                var retained = new List<PooledDevice>();

                while (bucket.IdleDevices.TryPop(out var pooled))
                {
                    if (pooled.LastUsed >= cutoff)
                        retained.Add(pooled);
                    else
                        TryDisposeDevice(pooled.Device);
                }

                if (retained.Count > 0)
                    bucket.IdleDevices.PushRange(retained.ToArray());
            }
        }

        private static void TryDisposeDevice(T device)
        {
            try { device?.Dispose(); }
            catch { /* swallow disposal exceptions */ }
        }
    }
}
