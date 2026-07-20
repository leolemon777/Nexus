// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
// Convenience base for new TCP-based protocol clients.

using System;
using Nexus.IMessage;
using Nexus.Pipe;

namespace Nexus.Device
{
    /// <summary>
    /// TCP 便利基类 — 继承 <see cref="DeviceCommunication"/>,内置 <see cref="PipeTcpNet"/>。
    /// 协议子类只需继承此类,提供 <see cref="MessageFrame"/> 和读写实现。
    /// </summary>
    /// <remarks>
    /// 用法:
    /// <code>
    /// public class MyTcpClient : DeviceTcpNet
    /// {
    ///     public MyTcpClient(string ip, int port) : base(ip, port)
    ///     {
    ///         MessageFrame = new MyProtocolMessage();
    ///     }
    ///     // override ReadInt16 etc...
    /// }
    /// </code>
    /// </remarks>
    public abstract class DeviceTcpNet : DeviceCommunication
    {
        /// <param name="ip">远程主机名/IP。</param>
        /// <param name="port">端口。</param>
        /// <param name="timeout">收发超时(毫秒)。</param>
        protected DeviceTcpNet(string ip, int port, int timeout = 5000)
            : base(CreatePipe(ip, port, timeout))
        {
            IpAddress = ip;
            Port = port;
            Timeout = timeout;
        }

        private static PipeTcpNet CreatePipe(string ip, int port, int timeout)
        {
            var pipe = new PipeTcpNet(ip, port);
            pipe.ReceiveTimeout = timeout;
            pipe.SendTimeout = timeout;
            return pipe;
        }

        /// <summary>目标 IP/主机名。</summary>
        public string IpAddress { get; }

        /// <summary>目标端口。</summary>
        public new int Port { get; }

        /// <summary>收发超时(毫秒)。</summary>
        public int Timeout { get; set; }

        /// <summary>是否持久连接。短连接模式下每次操作后自动断开。</summary>
        public bool IsPersistent
        {
            get => ((PipeTcpNet)Pipe).IsPersistentConnection;
            set => ((PipeTcpNet)Pipe).IsPersistentConnection = value;
        }

        /// <summary>设置持久连接模式(等同于 <see cref="IsPersistent"/> = true)。</summary>
        public void SetPersistentConnection() => IsPersistent = true;
    }
}
