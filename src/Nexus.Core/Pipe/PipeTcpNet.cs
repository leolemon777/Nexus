// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Pipe
{
    /// <summary>
    /// TCP 客户端管道 — 最常见的传输介质。封装 <see cref="TcpClient"/> 生命周期 + 字节级收发。
    /// 支持持久连接(<see cref="IsPersistentConnection"/> = true 保持连接)和短连接模式
    /// (每次 SendAndReceive 后自动断开)。
    /// </summary>
    public class PipeTcpNet : CommunicationPipe
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private bool _persistent;

        /// <param name="host">主机名或 IP。</param>
        /// <param name="port">端口。</param>
        /// <param name="communicationLock">可选的自定义并发锁。</param>
        public PipeTcpNet(string host, int port, ICommunicationLock? communicationLock = null)
            : base(communicationLock)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));
            if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            _host = host;
            _port = port;
        }

        /// <summary>是否持久连接模式。短连接模式下每次 SendAndReceive 后自动断开。</summary>
        public bool IsPersistentConnection
        {
            get => _persistent;
            set => _persistent = value;
        }

        /// <summary>目标主机名/IP(供子类如 <see cref="PipeSslNet"/> 用作 SNI)。</summary>
        public string Host => _host;

        /// <summary>目标端口。</summary>
        public int Port => _port;

        /// <inheritdoc />
        public override bool IsConnect => _client?.Connected == true && _stream != null;

        /// <summary>底层 TcpClient(供子类/调试使用)。</summary>
        protected TcpClient? Client => _client;

        /// <summary>底层 NetworkStream(供子类/调试使用)。</summary>
        protected NetworkStream? Stream => _stream;

        /// <inheritdoc />
        public override OperateResult OpenCommunication()
        {
            try
            {
                CloseCommunication();
                _client = new TcpClient { SendTimeout = SendTimeout, ReceiveTimeout = ReceiveTimeout };
                var ar = _client.BeginConnect(_host, _port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ReceiveTimeout, true))
                {
                    CloseCommunication();
                    return OperateResult.Failed($"TCP 连接超时: {_host}:{_port} ({ReceiveTimeout}ms)");
                }
                _client.EndConnect(ar);
                _stream = _client.GetStream();
                _stream.ReadTimeout = ReceiveTimeout;
                _stream.WriteTimeout = SendTimeout;
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                CloseCommunication();
                return OperateResult.Failed($"TCP 连接失败: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public override async Task<OperateResult> OpenCommunicationAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                CloseCommunication();
                _client = new TcpClient { SendTimeout = SendTimeout, ReceiveTimeout = ReceiveTimeout };
                using (cancellationToken.Register(() => { try { _client?.Close(); } catch { } }))
                {
                    await _client.ConnectAsync(_host, _port).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                _stream = _client.GetStream();
                _stream.ReadTimeout = ReceiveTimeout;
                _stream.WriteTimeout = SendTimeout;
                return OperateResult.Success();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                CloseCommunication();
                return OperateResult.Failed($"TCP 连接失败: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public override void CloseCommunication()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        /// <inheritdoc />
        protected override OperateResult SendCore(byte[] data)
        {
            var s = _stream;
            if (s == null) return OperateResult.Failed("TCP 管道未打开");
            try
            {
                s.Write(data, 0, data.Length);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"TCP 发送异常: {ex.Message}");
            }
        }

        /// <inheritdoc />
        protected override async Task<OperateResult> SendCoreAsync(byte[] data, CancellationToken cancellationToken)
        {
            var s = _stream;
            if (s == null) return OperateResult.Failed("TCP 管道未打开");
            try
            {
                await s.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false);
                return OperateResult.Success();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return OperateResult.Failed($"TCP 发送异常: {ex.Message}");
            }
        }

        /// <inheritdoc />
        protected override OperateResult<byte[]> ReceiveCore(int expectedLength)
        {
            var s = _stream;
            if (s == null) return OperateResult<byte[]>.Failed("TCP 管道未打开");
            try
            {
                byte[] buf = new byte[expectedLength];
                int read = 0;
                while (read < expectedLength)
                {
                    int n = s.Read(buf, read, expectedLength - read);
                    if (n == 0) return OperateResult<byte[]>.Failed($"TCP 对端关闭,仅读到 {read}/{expectedLength} 字节");
                    read += n;
                }
                return OperateResult<byte[]>.Success(buf);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"TCP 接收异常: {ex.Message}");
            }
        }

        /// <inheritdoc />
        protected override async Task<OperateResult<byte[]>> ReceiveCoreAsync(int expectedLength, CancellationToken cancellationToken)
        {
            var s = _stream;
            if (s == null) return OperateResult<byte[]>.Failed("TCP 管道未打开");
            try
            {
                byte[] buf = new byte[expectedLength];
                int read = 0;
                while (read < expectedLength)
                {
                    int n = await s.ReadAsync(buf, read, expectedLength - read, cancellationToken).ConfigureAwait(false);
                    if (n == 0) return OperateResult<byte[]>.Failed($"TCP 对端关闭,仅读到 {read}/{expectedLength} 字节");
                    read += n;
                }
                return OperateResult<byte[]>.Success(buf);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"TCP 接收异常: {ex.Message}");
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseCommunication();
            }
            base.Dispose(disposing);
        }
    }
}
