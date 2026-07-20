// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Pipe
{
    /// <summary>
    /// SSL/TLS 加密 TCP 管道 — 在 <see cref="PipeTcpNet"/> 基础上把 <see cref="NetworkStream"/>
    /// 包成 <see cref="SslStream"/>。客户端模式(默认)用于连远程 TLS 服务器,
    /// 服务器模式用于 DeviceServer 接受 TLS 连接(后续 PR)。
    /// </summary>
    public class PipeSslNet : PipeTcpNet
    {
        private readonly bool _serverMode;
        private SslStream? _sslStream;
        private X509Certificate? _certificate;
        private bool _remoteCertificateValidation;

        /// <param name="host">远程主机名(也用作 TLS SNI / 证书 CN 校验)。</param>
        /// <param name="port">端口。</param>
        /// <param name="serverMode">true = 服务器模式(需提供证书);false = 客户端模式。</param>
        /// <param name="communicationLock">可选自定义并发锁。</param>
        public PipeSslNet(string host, int port, bool serverMode = false, ICommunicationLock? communicationLock = null)
            : base(host, port, communicationLock)
        {
            _serverMode = serverMode;
        }

        /// <summary>服务器模式需提供的证书。</summary>
        public X509Certificate? Certificate
        {
            get => _certificate;
            set => _certificate = value;
        }

        /// <summary>是否校验远程证书(客户端模式)。默认 false,跳过校验(常见于 PLC 自签证书场景)。</summary>
        public bool RemoteCertificateValidation
        {
            get => _remoteCertificateValidation;
            set => _remoteCertificateValidation = value;
        }

        /// <inheritdoc />
        public override OperateResult OpenCommunication()
        {
            var baseResult = base.OpenCommunication();
            if (!baseResult.IsSuccess) return baseResult;

            try
            {
                var ns = base.Stream;
                if (ns == null) return OperateResult.Failed("底层 NetworkStream 不可用");

                _sslStream = new SslStream(ns, leaveInnerStreamOpen: false,
                    (sender, cert, chain, errors) => _remoteCertificateValidation);

                if (_serverMode)
                {
                    if (_certificate == null)
                        return OperateResult.Failed("服务器模式需提供 Certificate");
                    var serverCert = _certificate as X509Certificate2 ?? new X509Certificate2(_certificate);
                    _sslStream.AuthenticateAsServer(serverCert);
                }
                else
                {
                    _sslStream.AuthenticateAsClient(base.Host ?? "nexus-device");
                }
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                CloseCommunication();
                return OperateResult.Failed($"SSL 握手失败: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public override async Task<OperateResult> OpenCommunicationAsync(CancellationToken cancellationToken = default)
        {
            var baseResult = await base.OpenCommunicationAsync(cancellationToken).ConfigureAwait(false);
            if (!baseResult.IsSuccess) return baseResult;

            try
            {
                var ns = base.Stream;
                if (ns == null) return OperateResult.Failed("底层 NetworkStream 不可用");

                _sslStream = new SslStream(ns, leaveInnerStreamOpen: false,
                    (sender, cert, chain, errors) => _remoteCertificateValidation);

                if (_serverMode)
                {
                    if (_certificate == null)
                        return OperateResult.Failed("服务器模式需提供 Certificate");
                    var serverCert = _certificate as X509Certificate2 ?? new X509Certificate2(_certificate);
                    await _sslStream.AuthenticateAsServerAsync(serverCert).ConfigureAwait(false);
                }
                else
                {
                    await _sslStream.AuthenticateAsClientAsync(base.Host ?? "nexus-device").ConfigureAwait(false);
                }
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                CloseCommunication();
                return OperateResult.Failed($"SSL 握手失败: {ex.Message}");
            }
        }

        /// <inheritdoc />
        protected override OperateResult SendCore(byte[] data)
        {
            var s = _sslStream;
            if (s == null) return OperateResult.Failed("SSL 管道未打开");
            try { s.Write(data, 0, data.Length); return OperateResult.Success(); }
            catch (Exception ex) { return OperateResult.Failed($"SSL 发送异常: {ex.Message}"); }
        }

        /// <inheritdoc />
        protected override async Task<OperateResult> SendCoreAsync(byte[] data, CancellationToken cancellationToken)
        {
            var s = _sslStream;
            if (s == null) return OperateResult.Failed("SSL 管道未打开");
            try { await s.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false); return OperateResult.Success(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return OperateResult.Failed($"SSL 发送异常: {ex.Message}"); }
        }

        /// <inheritdoc />
        protected override OperateResult<byte[]> ReceiveCore(int expectedLength)
        {
            var s = _sslStream;
            if (s == null) return OperateResult<byte[]>.Failed("SSL 管道未打开");
            try
            {
                byte[] buf = new byte[expectedLength];
                int read = 0;
                while (read < expectedLength)
                {
                    int n = s.Read(buf, read, expectedLength - read);
                    if (n == 0) return OperateResult<byte[]>.Failed($"SSL 对端关闭,仅读到 {read}/{expectedLength} 字节");
                    read += n;
                }
                return OperateResult<byte[]>.Success(buf);
            }
            catch (Exception ex) { return OperateResult<byte[]>.Failed($"SSL 接收异常: {ex.Message}"); }
        }

        /// <inheritdoc />
        protected override async Task<OperateResult<byte[]>> ReceiveCoreAsync(int expectedLength, CancellationToken cancellationToken)
        {
            var s = _sslStream;
            if (s == null) return OperateResult<byte[]>.Failed("SSL 管道未打开");
            try
            {
                byte[] buf = new byte[expectedLength];
                int read = 0;
                while (read < expectedLength)
                {
                    int n = await s.ReadAsync(buf, read, expectedLength - read, cancellationToken).ConfigureAwait(false);
                    if (n == 0) return OperateResult<byte[]>.Failed($"SSL 对端关闭,仅读到 {read}/{expectedLength} 字节");
                    read += n;
                }
                return OperateResult<byte[]>.Success(buf);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return OperateResult<byte[]>.Failed($"SSL 接收异常: {ex.Message}"); }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _sslStream?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
