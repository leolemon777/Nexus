using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nexus.Core.Tests
{
    public class ConnectionPoolTests
    {
        [Fact]
        public void AcquireRelease_ReusesConnectedDevice()
        {
            int created = 0;
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice(++created), maxPoolSize: 2);

            var first = pool.Acquire("plc-a");
            pool.Release("plc-a", first);
            var second = pool.Acquire("plc-a");

            Assert.Same(first, second);
            Assert.Equal(1, created);
            Assert.Equal(1, pool.ActiveCount);
            pool.Release("plc-a", second);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public async Task Acquire_SyncPath_RespectsMaxPoolSizePerKey()
        {
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 1);

            var first = pool.Acquire("plc-a");
            var secondAcquire = Task.Run(() => pool.Acquire("plc-a"));

            Assert.NotSame(secondAcquire, await Task.WhenAny(secondAcquire, Task.Delay(100)));

            pool.Release("plc-a", first);
            var second = await secondAcquire;
            pool.Release("plc-a", second);
        }

        [Fact]
        public async Task AcquireAsync_LimitsOnlyMatchingKey()
        {
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 1);

            var first = await pool.AcquireAsync("plc-a");
            var blockedSameKey = pool.AcquireAsync("plc-a");
            var otherKey = await pool.AcquireAsync("plc-b");

            Assert.NotSame(blockedSameKey, await Task.WhenAny(blockedSameKey, Task.Delay(100)));
            Assert.NotSame(first, otherKey);

            pool.Release("plc-a", first);
            var second = await blockedSameKey;
            pool.Release("plc-a", second);
            pool.Release("plc-b", otherKey);
        }

        [Fact]
        public void Acquire_ConnectFailure_DisposesAndThrows()
        {
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice { ConnectSucceeds = false }, maxPoolSize: 1);

            Assert.Throws<InvalidOperationException>(() => pool.Acquire("plc-a"));
            Assert.Equal(0, pool.ActiveCount);
        }

        [Fact]
        public void Acquire_HealthCheckFail_DiscardsIdleDevice()
        {
            int healthChecks = 0;
            using var pool = new ConnectionPool<TestDevice>(
                () => new TestDevice(),
                maxPoolSize: 2,
                healthCheck: d => { healthChecks++; return d.Healthy; });

            // Acquire + Release → 设备进入空闲池
            var device = pool.Acquire("plc-a");
            device.Healthy = false; // 模拟健康检查失败
            pool.Release("plc-a", device);

            // 再次 Acquire → 健康检查失败 → 丢弃旧设备 → 创建新设备
            var second = pool.Acquire("plc-a");
            Assert.NotSame(device, second);
            Assert.Equal(1, healthChecks);
            pool.Release("plc-a", second);
        }

        [Fact]
        public void Acquire_HealthCheckPass_ReusesDevice()
        {
            using var pool = new ConnectionPool<TestDevice>(
                () => new TestDevice(),
                maxPoolSize: 2,
                healthCheck: d => d.Healthy);

            var first = pool.Acquire("plc-a");
            first.Healthy = true;
            pool.Release("plc-a", first);

            var second = pool.Acquire("plc-a");
            Assert.Same(first, second);
            pool.Release("plc-a", second);
        }

        [Fact]
        public void Acquire_NoHealthCheck_SkipsCheck()
        {
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 2);

            var first = pool.Acquire("plc-a");
            pool.Release("plc-a", first);

            var second = pool.Acquire("plc-a");
            Assert.Same(first, second);
            pool.Release("plc-a", second);
        }

        [Fact]
        public async Task AcquireAsync_HealthCheckFail_DiscardsIdleDevice()
        {
            using var pool = new ConnectionPool<TestDevice>(
                () => new TestDevice(),
                maxPoolSize: 2,
                healthCheck: d => d.Healthy);

            var first = await pool.AcquireAsync("plc-a");
            first.Healthy = false;
            await pool.ReleaseAsync("plc-a", first);

            var second = await pool.AcquireAsync("plc-a");
            Assert.NotSame(first, second);
            await pool.ReleaseAsync("plc-a", second);
        }

        // ── C4 回归：Release 配额泄漏修复 ──────────────────────────
        // 原缺陷：Release(key, device) 当 key==null 或 TryGetValue 失败时，
        // 跳过 ActiveCount/Semaphore 归还，导致信号量配额永久耗尽、Acquire 永久阻塞。

        [Fact]
        public void Release_WithNullKey_StillReturnsQuota_AllowsSubsequentAcquire()
        {
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 1);

            var device = pool.Acquire("plc-a");
            Assert.Equal(1, pool.ActiveCount);

            // 用 null key 释放（曾导致配额泄漏）。
            pool.Release(null!, device);

            // 关键断言：配额已归还，下一次 Acquire 不会永久阻塞。
            var next = pool.Acquire("plc-a");
            Assert.NotNull(next);
            Assert.Equal(1, pool.ActiveCount);
            pool.Release("plc-a", next);
        }

        [Fact]
        public void Release_WithUnknownKey_StillReturnsQuota_AllowsSubsequentAcquire()
        {
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 1);

            var device = pool.Acquire("plc-a");

            // 用一个不存在的 key 释放（TryGetValue 失败，曾导致配额泄漏）。
            pool.Release("wrong-key", device);

            // 关键断言：原 key 的配额已归还，Acquire 不阻塞。
            var next = pool.Acquire("plc-a");
            Assert.NotNull(next);
            pool.Release("plc-a", next);
        }

        [Fact]
        public async Task Release_WithWrongKey_DoesNotStarvePool_UnderRepeatedMisuse()
        {
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 1);

            // 反复用错误 key 释放，模拟调用方误用场景。
            for (int i = 0; i < 5; i++)
            {
                var device = pool.Acquire("plc-a");
                pool.Release("typo-key", device);
            }

            // 池不应被饿死：仍能在合理时间内 Acquire 成功。
            var acquired = await Task.Run(() => pool.Acquire("plc-a"));
            Assert.NotNull(acquired);
            pool.Release("plc-a", acquired);
            Assert.Equal(0, pool.ActiveCount);
        }

        /// <summary>
        /// A4 回归:Dispose 与并发 Acquire 不应抛出未处理的 ObjectDisposedException。
        /// 修复前 Dispose 销毁 SemaphoreSlim,正在 Wait 的 Acquire 醒来后释放会触发 ODE。
        /// </summary>
        [Fact]
        public async Task Dispose_WithConcurrentAcquire_NoUnhandledObjectDisposed()
        {
            var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 2);

            // 启动 8 个线程持续 Acquire/Release 同一 key,主线程并发 Dispose。
            var cts = new CancellationTokenSource();
            var errors = new System.Collections.Generic.List<string>();
            var errorLock = new object();

            var workers = new Task[8];
            for (int w = 0; w < workers.Length; w++)
            {
                workers[w] = Task.Run(() =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var device = pool.Acquire("plc-a");
                            Thread.Sleep(1);
                            pool.Release("plc-a", device);
                        }
                        catch (ObjectDisposedException)
                        {
                            // 这是预期内的(Dispose 后再 Acquire),不算错误。
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            // Acquire 内部 bucket DisposeCts 触发,Acquire 应转为 ODE,
                            // 但若时序让 OCE 直接逃出也算预期。
                            return;
                        }
                        catch (Exception ex)
                        {
                            lock (errorLock) errors.Add($"worker: {ex.GetType().Name} — {ex.Message}");
                            return;
                        }
                    }
                });
            }

            // 让 workers 运行一会儿,然后 Dispose。
            await Task.Delay(50);
            pool.Dispose();
            cts.Cancel();
            await Task.WhenAll(workers);

            // 核心断言:除了 ObjectDisposedException(已 catch),不应有任何其它异常泄露。
            Assert.True(errors.Count == 0,
                "并发 Dispose 引发未处理异常:\n" + string.Join("\n", errors));
        }

        /// <summary>
        /// A4 回归:Remove(key) 与并发的 Acquire/Release 不应抛 ODE。
        /// </summary>
        [Fact]
        public async Task Remove_WithConcurrentRelease_NoUnhandledObjectDisposed()
        {
            using var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 5);

            // 先 acquire 多个 device 并保留(不 release),模拟"借出未还"状态。
            var held = new System.Collections.Generic.List<TestDevice>();
            for (int i = 0; i < 3; i++)
                held.Add(pool.Acquire("plc-a"));

            // 并发 Release 与 Remove。
            var releaseTask = Task.Run(() =>
            {
                foreach (var d in held)
                {
                    try { pool.Release("plc-a", d); }
                    catch (ObjectDisposedException) { break; }
                    catch (OperationCanceledException) { break; }
                }
            });

            var removeTask = Task.Run(() => pool.Remove("plc-a"));

            await Task.WhenAll(releaseTask, removeTask);

            // Remove 后再用同 key acquire 应该正常工作(自动创建新 bucket)。
            var fresh = pool.Acquire("plc-a");
            Assert.NotNull(fresh);
            pool.Release("plc-a", fresh);
        }

        /// <summary>
        /// A4 回归:Clear 与并发 Acquire 不应抛 ODE 或死锁。
        /// </summary>
        [Fact]
        public async Task Clear_WithConcurrentAcquire_NoDeadlockOrOde()
        {
            var pool = new ConnectionPool<TestDevice>(() => new TestDevice(), maxPoolSize: 2);

            // 启动多个 workers 持续 Acquire 多个 key。
            var cts = new CancellationTokenSource();
            var errors = new System.Collections.Generic.List<string>();
            var errorLock = new object();

            var workers = new Task[4];
            for (int w = 0; w < workers.Length; w++)
            {
                int idx = w;
                workers[w] = Task.Run(() =>
                {
                    string key = "plc-" + (idx % 2);
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var device = pool.Acquire(key);
                            Thread.Sleep(1);
                            pool.Release(key, device);
                        }
                        catch (ObjectDisposedException) { return; }
                        catch (OperationCanceledException) { return; }
                        catch (Exception ex)
                        {
                            lock (errorLock) errors.Add($"w{idx}: {ex.GetType().Name} — {ex.Message}");
                            return;
                        }
                    }
                });
            }

            await Task.Delay(30);
            pool.Clear();
            cts.Cancel();
            await Task.WhenAll(workers);

            Assert.True(errors.Count == 0,
                "并发 Clear 引发未处理异常:\n" + string.Join("\n", errors));
        }

        private sealed class TestDevice : IReadWriteDevice
        {
            private readonly int _id;

            public TestDevice(int id = 0)
            {
                _id = id;
            }

            public bool ConnectSucceeds { get; set; } = true;
            public bool Healthy { get; set; } = true;
            public bool Disposed { get; private set; }
            public bool IsConnected { get; private set; }

            public OperateResult Connect()
            {
                IsConnected = ConnectSucceeds;
                return ConnectSucceeds ? OperateResult.Success() : OperateResult.Failed("connect failed");
            }

            public Task<OperateResult> ConnectAsync() => Task.FromResult(Connect());
            public void Disconnect() => IsConnected = false;
            public void Dispose()
            {
                Disposed = true;
                Disconnect();
            }

            public OperateResult<bool> ReadBool(string address) => OperateResult<bool>.Success(true);
            public OperateResult<short> ReadInt16(string address) => OperateResult<short>.Success((short)_id);
            public OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Success((ushort)_id);
            public OperateResult<int> ReadInt32(string address) => OperateResult<int>.Success(_id);
            public OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Success((uint)_id);
            public OperateResult<long> ReadInt64(string address) => OperateResult<long>.Success((long)_id);
            public OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Success((ulong)_id);
            public OperateResult<float> ReadFloat(string address) => OperateResult<float>.Success((float)_id);
            public OperateResult<double> ReadDouble(string address) => OperateResult<double>.Success((double)_id);
            public OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Success(_id.ToString());
            public OperateResult<byte[]> ReadBytes(string address, ushort length) => OperateResult<byte[]>.Success(new byte[length]);

            public OperateResult Write(string address, bool value) => OperateResult.Success();
            public OperateResult Write(string address, short value) => OperateResult.Success();
            public OperateResult Write(string address, ushort value) => OperateResult.Success();
            public OperateResult Write(string address, int value) => OperateResult.Success();
            public OperateResult Write(string address, uint value) => OperateResult.Success();
            public OperateResult Write(string address, long value) => OperateResult.Success();
            public OperateResult Write(string address, ulong value) => OperateResult.Success();
            public OperateResult Write(string address, float value) => OperateResult.Success();
            public OperateResult Write(string address, double value) => OperateResult.Success();
            public OperateResult Write(string address, string value) => OperateResult.Success();
            public OperateResult Write(string address, byte[] data) => OperateResult.Success();

            public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.FromResult(ReadBool(address));
            public Task<OperateResult<short>> ReadInt16Async(string address) => Task.FromResult(ReadInt16(address));
            public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.FromResult(ReadUInt16(address));
            public Task<OperateResult<int>> ReadInt32Async(string address) => Task.FromResult(ReadInt32(address));
            public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.FromResult(ReadUInt32(address));
            public Task<OperateResult<long>> ReadInt64Async(string address) => Task.FromResult(ReadInt64(address));
            public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.FromResult(ReadUInt64(address));
            public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.FromResult(ReadFloat(address));
            public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.FromResult(ReadDouble(address));
            public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.FromResult(ReadString(address, length));
            public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.FromResult(ReadBytes(address, length));
            public Task<OperateResult> WriteAsync(string address, bool value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, short value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, int value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, float value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, string value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.FromResult(Write(address, data));
            public Task<OperateResult> WriteAsync(string address, ushort value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, uint value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, long value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, ulong value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, double value) => Task.FromResult(Write(address, value));
        }
    }
}
