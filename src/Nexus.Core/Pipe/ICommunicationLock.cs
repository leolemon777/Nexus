// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Pipe
{
    /// <summary>
    /// 管道并发锁抽象 — 让单线程/多线程场景可换不同实现。
    /// 单线程场景用 <see cref="CommunicationLockNone"/> 避免锁开销;多线程场景用
    /// <see cref="CommunicationLockSemaphore"/>。
    /// </summary>
    public interface ICommunicationLock : IDisposable
    {
        /// <summary>同步获取锁(阻塞至拿到锁)。</summary>
        void Acquire();

        /// <summary>异步获取锁(可取消)。</summary>
        Task AcquireAsync(CancellationToken cancellationToken);

        /// <summary>释放锁。</summary>
        void Release();
    }

    /// <summary>
    /// 基于 <see cref="SemaphoreSlim"/> 的并发锁 — 多线程串行化访问管道。
    /// </summary>
    public sealed class CommunicationLockSemaphore : ICommunicationLock
    {
        private readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);

        /// <inheritdoc />
        public void Acquire() => _sem.Wait();

        /// <inheritdoc />
        public Task AcquireAsync(CancellationToken cancellationToken)
            => _sem.WaitAsync(cancellationToken);

        /// <inheritdoc />
        public void Release() => _sem.Release();

        /// <inheritdoc />
        public void Dispose() => _sem.Dispose();
    }

    /// <summary>
    /// 空锁(无操作)— 单线程场景使用,避免信号量开销。
    /// </summary>
    public sealed class CommunicationLockNone : ICommunicationLock
    {
        /// <inheritdoc />
        public void Acquire() { }

        /// <inheritdoc />
        public Task AcquireAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <inheritdoc />
        public void Release() { }

        /// <inheritdoc />
        public void Dispose() { }
    }
}
