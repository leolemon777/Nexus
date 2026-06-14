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
        }
    }
}
