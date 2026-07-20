// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Pipe
{
    /// <summary>
    /// UDP 客户端管道 — 无连接但需 <see cref="UdpClient.Connect(string, int)"/> 绑定默认远端。
    /// <see cref="ReceiveCore(int)"/> 用 <see cref="UdpClient.Receive(ref IPEndPoint)"/> 阻塞读取一个完整数据报,
    /// expectedLength 仅作最小长度校验(UDP 数据报原子到达)。
    /// </summary>
    public class PipeUdpNet : CommunicationPipe
    {
        private readonly string _host;
        private readonly int _port;
        private UdpClient? _client;

        public PipeUdpNet(string host, int port, ICommunicationLock? communicationLock = null)
            : base(communicationLock)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));
            if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            _host = host;
            _port = port;
        }

        /// <inheritdoc />
        public override bool IsConnect => _client != null;

        /// <inheritdoc />
        public override OperateResult OpenCommunication()
        {
            try
            {
                CloseCommunication();
                _client = new UdpClient();
                _client.Client.SendTimeout = SendTimeout;
                _client.Client.ReceiveTimeout = ReceiveTimeout;
                _client.Client.EnableBroadcast = true;
                _client.Connect(_host, _port);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                CloseCommunication();
                return OperateResult.Failed($"UDP 创建失败: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public override void CloseCommunication()
        {
            try { _client?.Close(); } catch { }
            _client = null;
        }

        /// <inheritdoc />
        protected override OperateResult SendCore(byte[] data)
        {
            var c = _client;
            if (c == null) return OperateResult.Failed("UDP 管道未打开");
            try
            {
                c.Send(data, data.Length);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed($"UDP 发送异常: {ex.Message}"); }
        }

        /// <inheritdoc />
        protected override OperateResult<byte[]> ReceiveCore(int expectedLength)
        {
            var c = _client;
            if (c == null) return OperateResult<byte[]>.Failed("UDP 管道未打开");
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = c.Receive(ref ep);
                if (data.Length < expectedLength)
                    return OperateResult<byte[]>.Failed($"UDP 数据报过短: 收到 {data.Length}, 期望 {expectedLength}");
                return OperateResult<byte[]>.Success(data);
            }
            catch (Exception ex) { return OperateResult<byte[]>.Failed($"UDP 接收异常: {ex.Message}"); }
        }
    }
}
