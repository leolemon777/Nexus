// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
// Rewritten for Nexus: transport-agnostic IO abstraction. Leaner than HSL's Pipe,
// uses Nexus OperateResult style, netstandard2.0-safe.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Pipe
{
    /// <summary>
    /// 通信管道抽象 — 把"用什么传输介质(TCP/UDP/串口/TLS/DTU)"与"什么协议(MC3E/S7/Modbus...)"
    /// 彻底解耦。每个具体 Pipe 持有 IO 资源(socket / serial port / SSL stream),
    /// 协议客户端只持有 <see cref="CommunicationPipe"/> 引用,不再关心传输细节。
    /// </summary>
    /// <remarks>
    /// <b>设计哲学(B2 重构核心)</b>:
    /// <para>
    /// 当前 Nexus 的 <c>TcpDeviceBase</c>/<c>SerialDeviceBase</c>/<c>UdpDeviceBase</c>
    /// 把传输介质和协议帧解析耦合在一个类里 — 一个协议要支持"TCP + 串口"两个版本,
    /// 必须写两份代码(<c>Nexus.Modbus</c> + <c>Nexus.Modbus.Rtu.Serial</c>)。
    /// HSL 通过 Pipe 抽象让一个协议实现能透明切换传输介质。
    /// </para>
    /// <para>
    /// 本类提供:连接生命周期、收发字节级 API、可插拔的 <see cref="ICommunicationLock"/>
    /// 互斥(管道本质上半双工或单 socket,需要序列化访问)。帧解析(<see cref="INetMessage"/>)由
    /// 调用方在 B3 引入;B2 先只提供字节级 Send/Receive。
    /// </para>
    /// </remarks>
    public abstract class CommunicationPipe : IDisposable
    {
        private int _receiveTimeout = 5000;
        private int _sendTimeout = 5000;
        private volatile bool _disposed;
        private readonly ICommunicationLock _lock;

        /// <summary>构造。lock 实例可由子类覆盖以提供自定义并发模型。</summary>
        protected CommunicationPipe(ICommunicationLock? communicationLock = null)
        {
            _lock = communicationLock ?? new CommunicationLockSemaphore();
        }

        // ── 公共配置 ─────────────────────────────

        /// <summary>接收超时(毫秒),默认 5000。负数表示不接收反馈。</summary>
        public int ReceiveTimeout
        {
            get => _receiveTimeout;
            set => _receiveTimeout = value;
        }

        /// <summary>发送超时(毫秒),默认 5000。</summary>
        public int SendTimeout
        {
            get => _sendTimeout;
            set => _sendTimeout = value;
        }

        /// <summary>接收完发送内容后、读取响应前的休息时间(毫秒)。串口半双工时常用。默认 0。</summary>
        public int SleepTime { get; set; }

        /// <summary>管道当前是否已打开(在线)。</summary>
        public abstract bool IsConnect { get; }

        /// <summary>管道是否处于错误计数状态(连续多次失败后置 true,触发上层重连)。</summary>
        public bool IsConnectError => _connectErrorCount > 0;

        /// <summary>当前连续连接错误次数。</summary>
        public int ConnectErrorCount => _connectErrorCount;

        private int _connectErrorCount;

        /// <summary>重置错误计数(一次成功操作后调用)。</summary>
        public void ResetConnectErrorCount() => Interlocked.Exchange(ref _connectErrorCount, 0);

        /// <summary>递增错误计数并返回新值。</summary>
        public int RaisePipeError() => Interlocked.Increment(ref _connectErrorCount);

        // ── 连接生命周期(子类实现)──────────────

        /// <summary>打开管道(TCP connect / 串口 open / DTU 注册等)。同步。</summary>
        public abstract OperateResult OpenCommunication();

        /// <summary>异步打开管道。默认走同步。</summary>
        public virtual Task<OperateResult> OpenCommunicationAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) return Task.FromResult(OperateResult.Failed("管道已释放"));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OpenCommunication());
        }

        /// <summary>关闭管道。同步。</summary>
        public abstract void CloseCommunication();

        // ── 字节级收发(子类实现核心)──────────────

        /// <summary>发送原始字节(同步)。</summary>
        protected abstract OperateResult SendCore(byte[] data);

        /// <summary>接收指定长度字节(同步)。子类需自己处理超时。</summary>
        protected abstract OperateResult<byte[]> ReceiveCore(int expectedLength);

        /// <summary>异步发送。默认走同步。</summary>
        protected virtual Task<OperateResult> SendCoreAsync(byte[] data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SendCore(data));
        }

        /// <summary>异步接收。默认走同步。</summary>
        protected virtual Task<OperateResult<byte[]>> ReceiveCoreAsync(int expectedLength, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReceiveCore(expectedLength));
        }

        // ── 公共高层收发(带锁、错误计数)──────────

        /// <summary>
        /// 完整的"发送 → 等待 → 接收"事务。在 <see cref="ICommunicationLock"/> 保护下原子执行。
        /// 成功后 <see cref="ResetConnectErrorCount"/>;失败后 <see cref="RaisePipeError"/>。
        /// </summary>
        public OperateResult<byte[]> SendAndReceive(byte[] sendData, int responseLength)
        {
            if (_disposed) return OperateResult<byte[]>.Failed("管道已释放");
            if (sendData == null) throw new ArgumentNullException(nameof(sendData));

            _lock.Acquire();
            try
            {
                var send = SendCore(sendData);
                if (!send.IsSuccess)
                {
                    RaisePipeError();
                    return OperateResult<byte[]>.Failed($"发送失败: {send.Message}");
                }

                if (SleepTime > 0) Thread.Sleep(SleepTime);

                var recv = ReceiveCore(responseLength);
                if (!recv.IsSuccess)
                {
                    RaisePipeError();
                    return OperateResult<byte[]>.Failed($"接收失败: {recv.Message}");
                }

                ResetConnectErrorCount();
                return recv;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 完整的"发送 → 等待 → 接收"事务,异步版。持锁期间 await 是允许的(SemaphoreSlim 支持)。
        /// </summary>
        public async Task<OperateResult<byte[]>> SendAndReceiveAsync(
            byte[] sendData, int responseLength, CancellationToken cancellationToken = default)
        {
            if (_disposed) return OperateResult<byte[]>.Failed("管道已释放");
            if (sendData == null) throw new ArgumentNullException(nameof(sendData));

            await _lock.AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var send = await SendCoreAsync(sendData, cancellationToken).ConfigureAwait(false);
                if (!send.IsSuccess)
                {
                    RaisePipeError();
                    return OperateResult<byte[]>.Failed($"发送失败: {send.Message}");
                }

                if (SleepTime > 0) await Task.Delay(SleepTime, cancellationToken).ConfigureAwait(false);

                var recv = await ReceiveCoreAsync(responseLength, cancellationToken).ConfigureAwait(false);
                if (!recv.IsSuccess)
                {
                    RaisePipeError();
                    return OperateResult<byte[]>.Failed($"接收失败: {recv.Message}");
                }

                ResetConnectErrorCount();
                return recv;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>仅发送(无响应,如广播/写指令)。受锁保护。</summary>
        public OperateResult SendOnly(byte[] sendData)
        {
            if (_disposed) return OperateResult.Failed("管道已释放");
            if (sendData == null) throw new ArgumentNullException(nameof(sendData));

            _lock.Acquire();
            try
            {
                var send = SendCore(sendData);
                if (!send.IsSuccess) RaisePipeError();
                else ResetConnectErrorCount();
                return send;
            }
            finally { _lock.Release(); }
        }

        // ── IDisposable ────────────────────────────

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 释放资源。子类重写时应释放 IO 资源(socket / port),并调用 base.Dispose(disposing)。
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                try { CloseCommunication(); } catch { /* swallow */ }
                (_lock as IDisposable)?.Dispose();
            }
        }
    }
}
