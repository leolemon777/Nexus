using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// A1 并发回归测试 — 验证 <see cref="SerialDeviceBase"/> 的双锁修复。
    ///
    /// 风险背景：修复前 <see cref="SerialDeviceBase.SendAndReceive(byte[])"/> 用
    /// <c>lock(_lock)</c>，而 <see cref="SerialDeviceBase.SendAndReceiveAsync(byte[], CancellationToken)"/>
    /// 用 <c>_asyncLock</c>，是<b>两把互不相干的锁</b>。当同步调用方和异步调用方并发使用同一设备实例时，
    /// 两把锁互不感知，半双工串口的 Write/Read 会交错 → 响应被偷吃或拼接到错误的请求上。
    ///
    /// 本测试用线程安全的 fake serial port：响应由请求驱动（响应 = 请求前缀 + tag），
    /// 多线程通过<b>混合</b> sync 和 async API 并发收发，验证每个响应严格匹配对应请求。
    /// </summary>
    public class SerialDeviceBaseConcurrencyTests
    {
        /// <summary>
        /// 线程安全的 fake serial port：响应由请求驱动。
        /// 请求格式: [4-byte big-endian payload-len=4][4-byte big-endian tag]；
        /// 响应格式: [4-byte big-endian payload-len=4][4-byte 回显 tag]。
        /// 协议头长度 = 4 字节，由测试用 LengthPrefixSerialDevice 解析。
        /// <para>
        /// <b>实现要点</b>:Write 调用把整个响应作为单个 byte[] 原子入队(用 lock),
        /// 避免 ConcurrentQueue 字节级入队在多 Write 交错时产生跨响应的字节污染。
        /// </para>
        /// </summary>
        private sealed class FixedEchoFakeSerialPort : ISerialPort
        {
            // 每个元素是一次完整的响应字节序列,作为一个不可分割的单元。
            private readonly ConcurrentQueue<byte[]> _pendingResponses = new ConcurrentQueue<byte[]>();
            // 当前正在被 Read 消费的响应 + 已读偏移。
            private byte[]? _currentResponse;
            private int _currentOffset;
            private readonly object _readAdvanceLock = new object();

            public string PortName { get; set; } = "COM_ECHO";
            public int BaudRate { get; set; } = 9600;
            public int DataBits { get; set; } = 8;
            public StopBits StopBits { get; set; } = StopBits.One;
            public Parity Parity { get; set; } = Parity.None;
            public int ReadTimeout { get; set; } = 2000;
            public int WriteTimeout { get; set; } = 2000;
            public bool IsOpen { get; private set; }
            public bool DtrEnable { get; set; }
            public bool RtsEnable { get; set; }

            public void Open() { IsOpen = true; }
            public void Close() { IsOpen = false; }

            public void Write(byte[] buffer, int offset, int count)
            {
                byte[] request = new byte[count];
                Buffer.BlockCopy(buffer, offset, request, 0, count);

                // 响应: 4-byte header(长度=4, 大端) + 4-byte payload(回显请求 [4..8] 的 tag)。
                // 请求格式是 [len=4][tag],所以 tag 在 request[4..8]。
                byte[] response = new byte[8];
                response[0] = 0x00;
                response[1] = 0x00;
                response[2] = 0x00;
                response[3] = 0x04; // payload length = 4
                if (count >= 8)
                    Buffer.BlockCopy(request, 4, response, 4, 4); // 回显 tag 部分
                else
                    Buffer.BlockCopy(request, 0, response, 4, Math.Min(4, count));

                // 原子入队 — 整个响应作为单个不可分割单元。
                _pendingResponses.Enqueue(response);
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                int deadline = Environment.TickCount + ReadTimeout;
                int read = 0;
                // 整个 Read 调用在锁内串行执行 — 避免两个并发 Read 在中间状态互相覆盖。
                // (SerialDeviceBase 的 _asyncLock 本来就保证 Read 不会真并发,这是双保险。)
                lock (_readAdvanceLock)
                {
                    while (read < count)
                    {
                        // 当前响应未读完 → 继续消费。
                        if (_currentResponse != null && _currentOffset < _currentResponse.Length)
                        {
                            int toCopy = Math.Min(count - read, _currentResponse.Length - _currentOffset);
                            Buffer.BlockCopy(_currentResponse, _currentOffset, buffer, offset + read, toCopy);
                            _currentOffset += toCopy;
                            read += toCopy;
                            if (_currentOffset >= _currentResponse.Length) _currentResponse = null;
                            continue;
                        }
                        // 当前响应耗尽 → 取下一个。
                        if (_pendingResponses.TryDequeue(out byte[]? next))
                        {
                            _currentResponse = next;
                            _currentOffset = 0;
                            continue;
                        }
                        // 暂无数据 → 等待,但仍在锁内。
                        if (Environment.TickCount > deadline) return read;
                        Thread.Sleep(1);
                    }
                }
                return read;
            }

            public void Dispose() { }
        }

        /// <summary>4 字节长度前缀 stub 客户端：header=4 字节(大端 payload 长度)。</summary>
        private sealed class LengthPrefixSerialDevice : SerialDeviceBase
        {
            public LengthPrefixSerialDevice(ISerialPort port, int timeout = 2000) : base(port, timeout)
            {
                // 持续连接模式 — 半双工串口的并发场景。
                SetPersistentConnection();
                InterFrameDelay = 0; // 测试不需要帧间延时，加速。
            }

            protected override int ResponseHeaderLength => 4;

            protected override int GetResponsePayloadLength(byte[] header)
                => (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];

            // 暴露 protected 方法供测试调用。
            public OperateResult<byte[]> DoSync(byte[] req) => SendAndReceive(req);
            public Task<OperateResult<byte[]>> DoAsync(byte[] req, CancellationToken ct = default)
                => SendAndReceiveAsync(req, ct);
        }

        /// <summary>
        /// 单线程基线测试 — 验证 fake port 自身工作正常,排除测试基础设施问题。
        /// </summary>
        [Fact]
        public async Task SingleThread_Baseline_FakePortWorks()
        {
            var port = new FixedEchoFakeSerialPort();
            port.Open();
            using (var device = new LengthPrefixSerialDevice(port))
            {
                Assert.True(device.Connect().IsSuccess);

                for (int i = 0; i < 20; i++)
                {
                    int tag = i;
                    byte[] request = new byte[8];
                    request[3] = 0x04;
                    request[4] = (byte)(tag >> 24);
                    request[5] = (byte)(tag >> 16);
                    request[6] = (byte)(tag >> 8);
                    request[7] = (byte)tag;

                    var resp = await device.DoAsync(request).ConfigureAwait(false);
                    Assert.True(resp.IsSuccess, $"i{i}: {resp.Message}");
                    int respTag = (resp.Content[4] << 24) | (resp.Content[5] << 16)
                                | (resp.Content[6] << 8) | resp.Content[7];
                    Assert.Equal(tag, respTag);
                }
            }
        }

        /// <summary>
        /// 核心场景：sync + async 并发调用同一设备实例，验证双锁修复后无响应偷吃。
        /// </summary>
        [Fact]
        public async Task ConcurrentSyncAndAsync_NoResponseInterleaving()
        {
            // Arrange
            var port = new FixedEchoFakeSerialPort();
            port.Open();
            using (var device = new LengthPrefixSerialDevice(port))
            {
                Assert.True(device.Connect().IsSuccess);

                const int threadCount = 6;
                const int opsPerThread = 15;
                var errors = new List<string>();
                var errorLock = new object();

                var tasks = new Task[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int tid = t;
                    // 一半线程走 sync API, 一半走 async API — 这是双锁 bug 的触发条件。
                    bool useAsync = (tid % 2 == 0);
                    tasks[tid] = Task.Run(async () =>
                    {
                        for (int i = 0; i < opsPerThread; i++)
                        {
                            int tag = (tid << 16) | i;
                            byte[] request = new byte[8];
                            request[0] = 0x00; request[1] = 0x00; request[2] = 0x00; request[3] = 0x04;
                            request[4] = (byte)(tag >> 24);
                            request[5] = (byte)(tag >> 16);
                            request[6] = (byte)(tag >> 8);
                            request[7] = (byte)tag;

                            OperateResult<byte[]> resp = useAsync
                                ? await device.DoAsync(request).ConfigureAwait(false)
                                : device.DoSync(request);

                            if (!resp.IsSuccess)
                            {
                                lock (errorLock) errors.Add($"t{tid} i{i} ({(useAsync ? "A" : "S")}): {resp.Message}");
                                continue;
                            }

                            // 解析 payload (跳过 4 字节 header),必须等于原 tag。
                            int respTag = (resp.Content[4] << 24) | (resp.Content[5] << 16)
                                        | (resp.Content[6] << 8) | resp.Content[7];
                            if (respTag != tag)
                            {
                                lock (errorLock)
                                    errors.Add($"t{tid} i{i}: tag mismatch — sent {tag:X8}, got {respTag:X8} — 响应串台");
                            }
                        }
                    });
                }

                await Task.WhenAll(tasks);

                Assert.True(errors.Count == 0,
                    "并发 sync+async 收发串台:\n" + string.Join("\n", errors));
            }
        }

        /// <summary>
        /// 验证纯 async 并发场景(原本是唯一被 _asyncLock 保护的路径,作为基线对照)。
        /// </summary>
        [Fact]
        public async Task ConcurrentAsync_NoResponseInterleaving()
        {
            var port = new FixedEchoFakeSerialPort();
            port.Open();
            using (var device = new LengthPrefixSerialDevice(port))
            {
                Assert.True(device.Connect().IsSuccess);

                const int threadCount = 8;
                const int opsPerThread = 20;
                var errors = new List<string>();
                var errorLock = new object();

                var tasks = new Task[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int tid = t;
                    tasks[tid] = Task.Run(async () =>
                    {
                        for (int i = 0; i < opsPerThread; i++)
                        {
                            int tag = (tid << 16) | i;
                            byte[] request = new byte[8];
                            request[0] = 0x00; request[1] = 0x00; request[2] = 0x00; request[3] = 0x04;
                            request[4] = (byte)(tag >> 24);
                            request[5] = (byte)(tag >> 16);
                            request[6] = (byte)(tag >> 8);
                            request[7] = (byte)tag;

                            var resp = await device.DoAsync(request).ConfigureAwait(false);
                            if (!resp.IsSuccess)
                            {
                                lock (errorLock) errors.Add($"t{tid} i{i}: {resp.Message}");
                                continue;
                            }
                            int respTag = (resp.Content[4] << 24) | (resp.Content[5] << 16)
                                        | (resp.Content[6] << 8) | resp.Content[7];
                            if (respTag != tag)
                                lock (errorLock) errors.Add($"t{tid} i{i}: tag mismatch {tag:X8}→{respTag:X8}");
                        }
                    });
                }

                await Task.WhenAll(tasks);
                Assert.Empty(errors);
            }
        }

        /// <summary>
        /// 验证 Dispose 释放 _asyncLock 后，重复 Dispose 或并发操作不会抛 ObjectDisposedException 之外的异常。
        /// </summary>
        [Fact]
        public void Dispose_ReleasesAsyncLock_AndIsIdempotent()
        {
            var port = new FixedEchoFakeSerialPort();
            port.Open();
            var device = new LengthPrefixSerialDevice(port);
            Assert.True(device.Connect().IsSuccess);

            // 第一次 Dispose 应该不抛异常。
            device.Dispose();
            // 第二次 Dispose 必须幂等。
            device.Dispose();
            // port 也应已关闭。
            Assert.False(port.IsOpen);
        }

        /// <summary>
        /// 验证 Connect 在 Dispose 之后返回失败而不是抛异常。
        /// </summary>
        [Fact]
        public void Connect_AfterDispose_ReturnsFailure()
        {
            var port = new FixedEchoFakeSerialPort();
            var device = new LengthPrefixSerialDevice(port);
            device.Dispose();

            var r = device.Connect();
            Assert.False(r.IsSuccess);
            Assert.Contains("已释放", r.Message);
        }
    }
}
