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
        private readonly Func<T, bool>? _healthCheck;
        private readonly ConcurrentDictionary<string, DeviceBucket> _pools =
            new ConcurrentDictionary<string, DeviceBucket>();
        private readonly Timer _cleanupTimer;
        private volatile bool _disposed;

        private class DeviceBucket
        {
            public ConcurrentStack<PooledDevice> IdleDevices { get; } = new ConcurrentStack<PooledDevice>();
            public SemaphoreSlim Semaphore { get; }
            public int ActiveCount;

            public DeviceBucket(int maxPoolSize)
            {
                Semaphore = new SemaphoreSlim(maxPoolSize, maxPoolSize);
            }
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
        /// <param name="healthCheck">可选健康检查委托；Acquire 时对空闲连接执行，返回 false 的连接被淘汰。可用于 ping/读寄存器等。</param>
        public ConnectionPool(
            Func<T> deviceFactory,
            int maxPoolSize = 5,
            TimeSpan? idleTimeout = null,
            TimeSpan? cleanupInterval = null,
            Func<T, bool>? healthCheck = null)
        {
            _deviceFactory = deviceFactory ?? throw new ArgumentNullException(nameof(deviceFactory));
            if (maxPoolSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxPoolSize));
            _maxPoolSize = maxPoolSize;
            _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
            _healthCheck = healthCheck;
            _cleanupTimer = new Timer(CleanupCallback, null, _idleTimeout, cleanupInterval ?? _idleTimeout);
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

            var bucket = _pools.GetOrAdd(key, _ => new DeviceBucket(_maxPoolSize));
            bucket.Semaphore.Wait();

            try
            {
                while (true)
                {
                    if (bucket.IdleDevices.TryPop(out var pooled))
                    {
                        if (pooled.Device.IsConnected)
                        {
                            // 已连接 → 先做健康检查
                            if (IsHealthy(pooled.Device))
                            {
                                Interlocked.Increment(ref bucket.ActiveCount);
                                return pooled.Device;
                            }

                            // 健康检查失败 → 淘汰（不断尝试重连不健康的设备）
                            TryDisposeDevice(pooled.Device);
                            continue;
                        }

                        try
                        {
                            var reconnect = pooled.Device.Connect();
                            if (reconnect.IsSuccess && pooled.Device.IsConnected)
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
                        var connect = device.Connect();
                        if (!connect.IsSuccess || !device.IsConnected)
                            throw new InvalidOperationException(connect.Message);
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
            catch
            {
                bucket.Semaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// 异步获取连接，受最大连接数严格限制。
        /// </summary>
        public async Task<T> AcquireAsync(string key, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPool<T>));
            if (key == null) throw new ArgumentNullException(nameof(key));

            var bucket = _pools.GetOrAdd(key, _ => new DeviceBucket(_maxPoolSize));
            await bucket.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPool<T>));

                if (bucket.IdleDevices.TryPop(out var pooled))
                {
                    bool healthy = pooled.Device.IsConnected && IsHealthy(pooled.Device);
                    if (!healthy && pooled.Device.IsConnected)
                    {
                        // 连接存在但健康检查失败，淘汰
                        TryDisposeDevice(pooled.Device);
                    }
                    else if (healthy)
                    {
                        Interlocked.Increment(ref bucket.ActiveCount);
                        return pooled.Device;
                    }
                    else if (await TryReconnectAsync(pooled.Device).ConfigureAwait(false))
                    {
                        if (IsHealthy(pooled.Device))
                        {
                            Interlocked.Increment(ref bucket.ActiveCount);
                            return pooled.Device;
                        }
                        TryDisposeDevice(pooled.Device);
                    }
                    else
                    {
                        TryDisposeDevice(pooled.Device);
                    }
                }

                var device = _deviceFactory();
                if (device == null)
                    throw new InvalidOperationException("Device factory returned null.");

                try
                {
                    var connect = await device.ConnectAsync().ConfigureAwait(false);
                    if (!connect.IsSuccess || !device.IsConnected)
                        throw new InvalidOperationException(connect.Message);
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
                bucket.Semaphore.Release();
                throw;
            }
        }

        /// <summary>
        /// 检查设备是否健康。若配置了 <see cref="_healthCheck"/> 委托则执行它，否则直接返回 true。
        /// 健康检查异常视为不健康（返回 false），不向上抛出。
        /// </summary>
        private bool IsHealthy(T device)
        {
            if (_healthCheck == null) return true;
            try { return _healthCheck(device); }
            catch { return false; }
        }

        private static async Task<bool> TryReconnectAsync(T device)
        {
            try
            {
                var result = await device.ConnectAsync().ConfigureAwait(false);
                return result.IsSuccess && device.IsConnected;
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
                    bucket.Semaphore.Release();
                    return;
                }

                Interlocked.Decrement(ref bucket.ActiveCount);
                bucket.Semaphore.Release();
            }

            TryDisposeDevice(device);
        }

        /// <summary>
        /// 异步释放连接回池中。
        /// </summary>
        public Task ReleaseAsync(string key, T device)
        {
            Release(key, device);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Remove(string key)
        {
            if (key == null) return;

            if (_pools.TryRemove(key, out var bucket))
            {
                while (bucket.IdleDevices.TryPop(out var pooled))
                    TryDisposeDevice(pooled.Device);
                bucket.Semaphore.Dispose();
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
                    bucket.Semaphore.Dispose();
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

            foreach (var kvp in _pools)
            {
                if (_pools.TryRemove(kvp.Key, out var bucket))
                {
                    while (bucket.IdleDevices.TryPop(out var pooled))
                        TryDisposeDevice(pooled.Device);
                    bucket.Semaphore.Dispose();
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
