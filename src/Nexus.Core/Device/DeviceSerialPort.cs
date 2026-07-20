// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;
using Nexus.Pipe;

namespace Nexus.Device
{
    /// <summary>
    /// 串口便利基类 — 继承 <see cref="DeviceCommunication"/>,内置 <see cref="PipeSerialPort"/>。
    /// 协议子类提供 <see cref="ISerialPort"/> 实例,自动配置帧间延时。
    /// </summary>
    public abstract class DeviceSerialPort : DeviceCommunication
    {
        protected DeviceSerialPort(ISerialPort serialPort, int timeout = 5000, int interFrameDelay = 0)
            : base(CreatePipe(serialPort, timeout, interFrameDelay))
        {
            Timeout = timeout;
            InterFrameDelay = interFrameDelay;
        }

        private static PipeSerialPort CreatePipe(ISerialPort serialPort, int timeout, int interFrameDelay)
        {
            var pipe = new PipeSerialPort(serialPort, interFrameDelay);
            pipe.ReceiveTimeout = timeout;
            pipe.SendTimeout = timeout;
            return pipe;
        }

        /// <summary>底层串口(直接访问,如配置 DTR/RTS)。</summary>
        public ISerialPort SerialPort => ((PipeSerialPort)Pipe).Port;

        /// <summary>收发超时(毫秒)。</summary>
        public int Timeout { get; set; }

        /// <summary>RS485 帧间延时(毫秒)。</summary>
        public int InterFrameDelay { get; set; }
    }
}
