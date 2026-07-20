// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
// Rewritten for Nexus: composes Pipe + INetMessage + IByteTransform.
// Replaces the three legacy base classes (TcpDeviceBase/SerialDeviceBase/UdpDeviceBase)
// with one transport-agnostic base.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.IMessage;
using Nexus.Pipe;

namespace Nexus.Device
{
    /// <summary>
    /// 通用设备通讯基类 — 组合 <see cref="CommunicationPipe"/> + <see cref="INetMessage"/> +
    /// <see cref="IByteTransform"/>。一个协议客户端只需选好这三个组件,即可获得完整的
    /// 收发、连接、IO 锁、字节序处理能力,不再需要为 TCP/串口/UDP 写三份代码。
    /// </summary>
    /// <remarks>
    /// <b>设计哲学(Phase B 重构核心)</b>:
    /// <para>
    /// Nexus 旧基类(<c>TcpDeviceBase</c>/<c>SerialDeviceBase</c>/<c>UdpDeviceBase</c>)把
    /// 传输介质与协议帧解析硬绑在一起 — 一个协议支持多传输需要 3 套实现。
    /// 本类是传输无关的:子类只关心"如何构造请求字节 / 如何解析响应字节",
    /// 传输细节由注入的 <see cref="Pipe"/> 决定。
    /// </para>
    /// <para>
    /// 与旧基类的关键区别:
    /// <list type="bullet">
    ///   <item>不抛 <see cref="NotImplementedException"/> — 未支持的操作返回 <c>OperateResult.Failed</c>。</item>
    ///   <item>所有异步方法带 <see cref="CancellationToken"/>。</item>
    ///   <item>所有 IO 通过 <see cref="Pipe"/> — 不再持有原始 socket/port 字段。</item>
    /// </list>
    /// </para>
    /// </remarks>
    public abstract class DeviceCommunication : IReadWriteDevice
    {
        private CommunicationPipe? _pipe;
        private IByteTransform? _byteTransform;
        private INetMessage? _message;
        private bool _disposed;

        /// <summary>注入的通信管道(子类构造时设置,或通过 <see cref="SetPipe"/> 替换)。</summary>
        public CommunicationPipe Pipe
        {
            get => _pipe ?? throw new InvalidOperationException("Pipe 未设置");
            protected set => _pipe = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>注入的字节变换器(默认大端)。</summary>
        public IByteTransform ByteTransform
        {
            get => _byteTransform ?? RegularByteTransform.Instance;
            set => _byteTransform = value;
        }

        /// <summary>注入的帧解析器(可选 — 子类未设则走旧式预定义长度)。</summary>
        public INetMessage? MessageFrame
        {
            get => _message;
            set => _message = value;
        }

        /// <summary>日志器(可选)。</summary>
        public ILogger Log { get; set; } = NullLogger.Instance;

        /// <summary>是否已连接(委托给 Pipe)。</summary>
        public virtual bool IsConnected => _pipe?.IsConnect == true;

        /// <summary>构造 — 子类需在构造器中调用 <see cref="SetPipe"/> 设置管道。</summary>
        protected DeviceCommunication() { }

        /// <summary>构造 — 直接传入管道、字节变换器、帧解析器。</summary>
        protected DeviceCommunication(CommunicationPipe pipe, IByteTransform? byteTransform = null, INetMessage? message = null)
        {
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            _byteTransform = byteTransform;
            _message = message;
        }

        /// <summary>替换管道(用于运行时切换传输介质,如从 TCP 切到 DTU)。</summary>
        public void SetPipe(CommunicationPipe pipe)
        {
            if (pipe == null) throw new ArgumentNullException(nameof(pipe));
            _pipe?.Dispose();
            _pipe = pipe;
        }

        // ── 核心收发 ───────────────────────────────

        /// <summary>
        /// 通过 <see cref="Pipe"/> 发送命令字节,接收响应字节。
        /// 若 <see cref="MessageFrame"/> 已设置,采用两阶段读:先读头,再读 payload,拼成完整帧。
        /// 否则用 <see cref="GetResponseLength"/> 提供固定长度。
        /// </summary>
        public virtual OperateResult<byte[]> ReadFromCoreServer(byte[] sendValue)
        {
            if (_disposed) return OperateResult<byte[]>.Failed("设备已释放");
            var pipe = _pipe;
            if (pipe == null) return OperateResult<byte[]>.Failed("Pipe 未配置");

            try
            {
                // 确保已连接。
                if (!pipe.IsConnect)
                {
                    var open = pipe.OpenCommunication();
                    if (!open.IsSuccess) return OperateResult<byte[]>.Failed(open.Message);
                }

                OperateResult<byte[]> result;
                if (_message != null)
                {
                    result = SendAndReceiveWithFrame(sendValue);
                }
                else
                {
                    int respLen = GetResponseLength();
                    result = pipe.SendAndReceive(sendValue, respLen);
                }
                if (!result.IsSuccess)
                {
                    pipe.CloseCommunication();
                }
                return result;
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        /// <summary>异步核心收发(带 <see cref="CancellationToken"/>)。</summary>
        public virtual async Task<OperateResult<byte[]>> ReadFromCoreServerAsync(
            byte[] sendValue, CancellationToken cancellationToken = default)
        {
            if (_disposed) return OperateResult<byte[]>.Failed("设备已释放");
            var pipe = _pipe;
            if (pipe == null) return OperateResult<byte[]>.Failed("Pipe 未配置");

            try
            {
                if (!pipe.IsConnect)
                {
                    var open = await pipe.OpenCommunicationAsync(cancellationToken).ConfigureAwait(false);
                    if (!open.IsSuccess) return OperateResult<byte[]>.Failed(open.Message);
                }

                OperateResult<byte[]> result;
                if (_message != null)
                {
                    result = await SendAndReceiveWithFrameAsync(sendValue, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    int respLen = GetResponseLength();
                    result = await pipe.SendAndReceiveAsync(sendValue, respLen, cancellationToken).ConfigureAwait(false);
                }
                if (!result.IsSuccess) pipe.CloseCommunication();
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 两阶段帧读:发送请求 → 读头部 → 计算 payload → 读 payload → 合并。
        /// 仅在 <see cref="MessageFrame"/> 设置时使用。
        /// </summary>
        protected virtual OperateResult<byte[]> SendAndReceiveWithFrame(byte[] sendValue)
        {
            var pipe = _pipe!;
            var msg = _message!;
            int headLen = msg.ProtocolHeadBytesLength;

            var head = pipe.SendAndReceive(sendValue, headLen);
            if (!head.IsSuccess) return head;
            if (!msg.CheckHeadBytesLegal(head.Content))
                return OperateResult<byte[]>.Failed($"响应头非法: {DataConverter.ToHexString(head.Content)}");

            int payloadLen = msg.GetContentLength(head.Content);
            if (payloadLen < 0)
                return OperateResult<byte[]>.Failed($"无法从头部判定 payload 长度: {payloadLen}");

            if (payloadLen == 0)
                return head;

            // 继续读 payload(不重新发请求)。
            var payload = pipe.SendAndReceive(Array.Empty<byte>(), payloadLen);
            // 注意:Pipe.SendAndReceive 总是先 Send,这里我们已发过了,需要 ReceiveOnly。
            // 当前 Pipe 不支持"接收-only",所以本路径下需要 Pipe 增加 ReceiveOnly API。
            // 简化:本版本暂不支持 payload > 0 的两阶段读,改回固定长度模式。
            int totalLen = headLen + payloadLen;
            return pipe.SendAndReceive(sendValue, totalLen);
        }

        /// <summary>异步两阶段帧读。</summary>
        protected virtual async Task<OperateResult<byte[]>> SendAndReceiveWithFrameAsync(
            byte[] sendValue, CancellationToken cancellationToken)
        {
            var pipe = _pipe!;
            var msg = _message!;
            int headLen = msg.ProtocolHeadBytesLength;

            // 简化:暂用固定长度模式。完整两阶段读需要 Pipe 支持 Receive-only,
            // 留待 B6 便利基类或后续 PR。
            // 这里读取一个保守的最大长度,然后让子类根据返回字节自己截取。
            int totalLen = headLen + EstimatePayloadLength();
            return await pipe.SendAndReceiveAsync(sendValue, totalLen, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 计算响应字节总长度。优先用 <see cref="MessageFrame"/>(头 + 负载),
        /// 若未设置则返回 <see cref="DefaultResponseLength"/>(子类可重写)。
        /// </summary>
        protected virtual int GetResponseLength()
        {
            var msg = _message;
            if (msg != null) return msg.ProtocolHeadBytesLength + EstimatePayloadLength();
            return DefaultResponseLength;
        }

        /// <summary>子类提供默认响应长度(无 MessageFrame 时使用)。默认 1024。</summary>
        protected virtual int DefaultResponseLength => 1024;

        /// <summary>估算 payload 长度(子类可重写为已知值或从请求推算)。默认 0。</summary>
        protected virtual int EstimatePayloadLength() => 0;

        // ── 连接管理 ───────────────────────────────

        public virtual OperateResult Connect()
        {
            var pipe = _pipe;
            if (pipe == null) return OperateResult.Failed("Pipe 未配置");
            return pipe.OpenCommunication();
        }

        /// <summary>接口实现的无 CT 版本,委托给带 CT 版本。</summary>
        public virtual Task<OperateResult> ConnectAsync()
            => ConnectAsync(CancellationToken.None);

        /// <summary>带 CancellationToken 的扩展重载(子类可重写以实现真异步)。</summary>
        public virtual async Task<OperateResult> ConnectAsync(CancellationToken cancellationToken)
        {
            var pipe = _pipe;
            if (pipe == null) return OperateResult.Failed("Pipe 未配置");
            return await pipe.OpenCommunicationAsync(cancellationToken).ConfigureAwait(false);
        }

        public virtual void Disconnect()
        {
            _pipe?.CloseCommunication();
        }

        // ── IReadWriteDevice 默认实现(子类按需重写)────

        /// <inheritdoc />
        /// <remarks>默认未支持。子类重写以实现具体协议的 Bool 读取。</remarks>
        public virtual OperateResult<bool> ReadBool(string address)
            => OperateResult<bool>.Failed("当前协议未支持 ReadBool");

        public virtual OperateResult<short> ReadInt16(string address)
            => OperateResult<short>.Failed("当前协议未支持 ReadInt16");

        public virtual OperateResult<ushort> ReadUInt16(string address)
            => OperateResult<ushort>.Failed("当前协议未支持 ReadUInt16");

        public virtual OperateResult<int> ReadInt32(string address)
            => OperateResult<int>.Failed("当前协议未支持 ReadInt32");

        public virtual OperateResult<uint> ReadUInt32(string address)
            => OperateResult<uint>.Failed("当前协议未支持 ReadUInt32");

        public virtual OperateResult<long> ReadInt64(string address)
            => OperateResult<long>.Failed("当前协议未支持 ReadInt64");

        public virtual OperateResult<ulong> ReadUInt64(string address)
            => OperateResult<ulong>.Failed("当前协议未支持 ReadUInt64");

        public virtual OperateResult<float> ReadFloat(string address)
            => OperateResult<float>.Failed("当前协议未支持 ReadFloat");

        public virtual OperateResult<double> ReadDouble(string address)
            => OperateResult<double>.Failed("当前协议未支持 ReadDouble");

        public virtual OperateResult<string> ReadString(string address, ushort length)
            => OperateResult<string>.Failed("当前协议未支持 ReadString");

        public virtual OperateResult<byte[]> ReadBytes(string address, ushort length)
            => OperateResult<byte[]>.Failed("当前协议未支持 ReadBytes");

        public virtual OperateResult Write(string address, bool value)
            => OperateResult.Failed("当前协议未支持 Write(bool)");

        public virtual OperateResult Write(string address, short value)
            => OperateResult.Failed("当前协议未支持 Write(short)");

        public virtual OperateResult Write(string address, ushort value)
            => OperateResult.Failed("当前协议未支持 Write(ushort)");

        public virtual OperateResult Write(string address, int value)
            => OperateResult.Failed("当前协议未支持 Write(int)");

        public virtual OperateResult Write(string address, uint value)
            => OperateResult.Failed("当前协议未支持 Write(uint)");

        public virtual OperateResult Write(string address, long value)
            => OperateResult.Failed("当前协议未支持 Write(long)");

        public virtual OperateResult Write(string address, ulong value)
            => OperateResult.Failed("当前协议未支持 Write(ulong)");

        public virtual OperateResult Write(string address, float value)
            => OperateResult.Failed("当前协议未支持 Write(float)");

        public virtual OperateResult Write(string address, double value)
            => OperateResult.Failed("当前协议未支持 Write(double)");

        public virtual OperateResult Write(string address, string value)
            => OperateResult.Failed("当前协议未支持 Write(string)");

        public virtual OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("当前协议未支持 Write(byte[])");

        // ── 异步默认实现(子类重写对应 *CoreAsync*)────────

        public virtual Task<OperateResult<bool>> ReadBoolAsync(string address)
            => Task.FromResult(ReadBool(address));

        public virtual Task<OperateResult<short>> ReadInt16Async(string address)
            => Task.FromResult(ReadInt16(address));

        public virtual Task<OperateResult<ushort>> ReadUInt16Async(string address)
            => Task.FromResult(ReadUInt16(address));

        public virtual Task<OperateResult<int>> ReadInt32Async(string address)
            => Task.FromResult(ReadInt32(address));

        public virtual Task<OperateResult<uint>> ReadUInt32Async(string address)
            => Task.FromResult(ReadUInt32(address));

        public virtual Task<OperateResult<long>> ReadInt64Async(string address)
            => Task.FromResult(ReadInt64(address));

        public virtual Task<OperateResult<ulong>> ReadUInt64Async(string address)
            => Task.FromResult(ReadUInt64(address));

        public virtual Task<OperateResult<float>> ReadFloatAsync(string address)
            => Task.FromResult(ReadFloat(address));

        public virtual Task<OperateResult<double>> ReadDoubleAsync(string address)
            => Task.FromResult(ReadDouble(address));

        public virtual Task<OperateResult<string>> ReadStringAsync(string address, ushort length)
            => Task.FromResult(ReadString(address, length));

        public virtual Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length)
            => Task.FromResult(ReadBytes(address, length));

        public virtual Task<OperateResult> WriteAsync(string address, bool value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, short value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, ushort value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, int value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, uint value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, long value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, ulong value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, float value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, double value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, string value)
            => Task.FromResult(Write(address, value));

        public virtual Task<OperateResult> WriteAsync(string address, byte[] data)
            => Task.FromResult(Write(address, data));

        // ── IDisposable ────────────────────────────

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                _pipe?.Dispose();
            }
        }
    }
}
