// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

namespace Nexus.Device
{
    /// <summary>
    /// UDP 便利基类 — 继承 <see cref="DeviceCommunication"/>,内置 <see cref="Nexus.Pipe.PipeUdpNet"/>。
    /// </summary>
    public abstract class DeviceUdpNet : DeviceCommunication
    {
        protected DeviceUdpNet(string ip, int port, int timeout = 5000)
            : base(CreatePipe(ip, port, timeout))
        {
            IpAddress = ip;
            Port = port;
            Timeout = timeout;
        }

        private static Nexus.Pipe.PipeUdpNet CreatePipe(string ip, int port, int timeout)
        {
            var pipe = new Nexus.Pipe.PipeUdpNet(ip, port);
            pipe.ReceiveTimeout = timeout;
            pipe.SendTimeout = timeout;
            return pipe;
        }

        public string IpAddress { get; }
        public new int Port { get; }
        public int Timeout { get; set; }
    }
}
