// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Pipe
{
    /// <summary>
    /// 串口管道 — 复用 Nexus 现有的 <see cref="ISerialPort"/> 抽象(已与 System.IO.Ports 解耦)。
    /// 半双工语义:SendAndReceive 整段在 <see cref="ICommunicationLock"/> 保护下串行执行。
    /// </summary>
    public class PipeSerialPort : CommunicationPipe
    {
        private readonly ISerialPort _port;
        private readonly int _interFrameDelay;

        /// <param name="serialPort">已配置好参数的 ISerialPort 实例。</param>
        /// <param name="interFrameDelay">RS485 帧间延时(毫秒),默认 0(由调用方设置)。</param>
        /// <param name="communicationLock">可选自定义并发锁。</param>
        public PipeSerialPort(ISerialPort serialPort, int interFrameDelay = 0, ICommunicationLock? communicationLock = null)
            : base(communicationLock)
        {
            _port = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
            _interFrameDelay = interFrameDelay;
            if (interFrameDelay > 0) SleepTime = interFrameDelay;
        }

        /// <summary>底层串口(供子类/调试使用)。</summary>
        public ISerialPort Port => _port;

        /// <inheritdoc />
        public override bool IsConnect => _port.IsOpen;

        /// <inheritdoc />
        public override OperateResult OpenCommunication()
        {
            try
            {
                if (!_port.IsOpen)
                {
                    _port.ReadTimeout = ReceiveTimeout;
                    _port.WriteTimeout = SendTimeout;
                    _port.Open();
                }
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed($"串口打开失败: {ex.Message}"); }
        }

        /// <inheritdoc />
        public override void CloseCommunication()
        {
            try { _port.Close(); } catch { }
        }

        /// <inheritdoc />
        protected override OperateResult SendCore(byte[] data)
        {
            if (!_port.IsOpen) return OperateResult.Failed("串口未打开");
            try
            {
                _port.Write(data, 0, data.Length);
                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed($"串口发送异常: {ex.Message}"); }
        }

        /// <inheritdoc />
        protected override OperateResult<byte[]> ReceiveCore(int expectedLength)
        {
            if (!_port.IsOpen) return OperateResult<byte[]>.Failed("串口未打开");
            try
            {
                byte[] buf = new byte[expectedLength];
                int read = 0;
                int deadline = Environment.TickCount + ReceiveTimeout;
                while (read < expectedLength)
                {
                    if (unchecked(Environment.TickCount - deadline) > 0)
                        return OperateResult<byte[]>.Failed($"串口接收超时,仅读到 {read}/{expectedLength} 字节");
                    try
                    {
                        int n = _port.Read(buf, read, expectedLength - read);
                        if (n == 0) return OperateResult<byte[]>.Failed($"串口对端关闭,仅读到 {read}/{expectedLength} 字节");
                        read += n;
                    }
                    catch (TimeoutException)
                    {
                        return OperateResult<byte[]>.Failed($"串口接收超时,仅读到 {read}/{expectedLength} 字节");
                    }
                }
                return OperateResult<byte[]>.Success(buf);
            }
            catch (Exception ex) { return OperateResult<byte[]>.Failed($"串口接收异常: {ex.Message}"); }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _port.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
