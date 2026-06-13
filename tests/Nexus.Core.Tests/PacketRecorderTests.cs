using System;
using System.IO;
using System.Threading;
using Nexus;
using Xunit;


namespace Nexus.Core.Tests
{
    public class PacketRecorderTests : IDisposable
    {
        private readonly PacketRecorder _recorder = new();

        public void Dispose() => _recorder.Dispose();

        [Fact]
        public void InitialState_NotRecording()
        {
            Assert.False(_recorder.IsRecording);
            Assert.Equal(0, _recorder.EntryCount);
        }

        [Fact]
        public void StartRecording_SetsIsRecording()
        {
            _recorder.StartRecording();
            Assert.True(_recorder.IsRecording);
            _recorder.StopRecording();
            Assert.False(_recorder.IsRecording);
        }

        [Fact]
        public void Record_TxMessage_Captured()
        {
            var device = new FakeTcpDevice();
            _recorder.Attach(device);
            _recorder.StartRecording();

            device.SimulateSend("01 03 00 00 00 0A C5 CD");

            Assert.Equal(1, _recorder.EntryCount);
            var entries = _recorder.GetEntries();
            Assert.Equal("TX", entries[0].Direction);
            Assert.Equal("01 03 00 00 00 0A C5 CD", entries[0].HexData);
        }

        [Fact]
        public void Record_RxMessage_Captured()
        {
            var device = new FakeTcpDevice();
            _recorder.Attach(device);
            _recorder.StartRecording();

            device.SimulateReceive("01 03 14 00 0A 00 14 00 1E");

            Assert.Equal(1, _recorder.EntryCount);
            var entries = _recorder.GetEntries();
            Assert.Equal("RX", entries[0].Direction);
        }

        [Fact]
        public void Record_WhenStopped_NotCaptured()
        {
            var device = new FakeTcpDevice();
            _recorder.Attach(device);
            _recorder.StartRecording();
            _recorder.StopRecording();

            device.SimulateSend("01 03");

            Assert.Equal(0, _recorder.EntryCount);
        }

        [Fact]
        public void Clear_RemovesAllEntries()
        {
            var device = new FakeTcpDevice();
            _recorder.Attach(device);
            _recorder.StartRecording();

            device.SimulateSend("01");
            device.SimulateSend("02");
            Assert.Equal(2, _recorder.EntryCount);

            _recorder.Clear();
            Assert.Equal(0, _recorder.EntryCount);
        }

        [Fact]
        public void Detach_StopsCapturing()
        {
            var device = new FakeTcpDevice();
            _recorder.Attach(device);
            _recorder.StartRecording();

            device.SimulateSend("01");
            Assert.Equal(1, _recorder.EntryCount);

            _recorder.Detach(device);
            device.SimulateSend("02");
            Assert.Equal(1, _recorder.EntryCount);
        }

        [Fact]
        public void ExportToJsonl_CreatesFile()
        {
            var device = new FakeTcpDevice();
            _recorder.Attach(device);
            _recorder.StartRecording();

            device.SimulateSend("01 03 00 00");
            device.SimulateReceive("01 03 02 00 0A");

            var path = Path.Combine(Path.GetTempPath(), $"pkt_test_{Guid.NewGuid():N}.jsonl");
            try
            {
                _recorder.ExportToJsonl(path);
                Assert.True(File.Exists(path));
                var lines = File.ReadAllLines(path);
                Assert.Equal(2, lines.Length);
                Assert.Contains("\"direction\":\"TX\"", lines[0]);
                Assert.Contains("\"direction\":\"RX\"", lines[1]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ExportToJsonl_CreatesDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"pkt_dir_{Guid.NewGuid():N}");
            var path = Path.Join(dir, "packets.jsonl");
            try
            {
                _recorder.StartRecording();
                _recorder.ExportToJsonl(path);
                Assert.True(Directory.Exists(dir));
                Assert.True(File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Analyze_Empty_ReturnsZeroCounts()
        {
            var analysis = _recorder.Analyze();
            Assert.Equal(0, analysis.TotalPackets);
            Assert.Equal(0, analysis.TxCount);
            Assert.Equal(0, analysis.RxCount);
            Assert.Equal(TimeSpan.Zero, analysis.Duration);
        }

        [Fact]
        public void Analyze_WithMessages_ReturnsCorrectCounts()
        {
            var device = new FakeTcpDevice();
            _recorder.Attach(device);
            _recorder.StartRecording();

            device.SimulateSend("01");
            device.SimulateReceive("02");
            device.SimulateSend("03");
            device.SimulateSend("04");
            device.SimulateReceive("05");

            var analysis = _recorder.Analyze();
            Assert.Equal(5, analysis.TotalPackets);
            Assert.Equal(3, analysis.TxCount);
            Assert.Equal(2, analysis.RxCount);
            Assert.True(analysis.AverageResponseTimeMs >= 0);
        }

        [Fact]
        public void Analyze_CalculatesResponseTime()
        {
            var device = new FakeTcpDevice();
            _recorder.Attach(device);
            _recorder.StartRecording();

            device.SimulateSend("01");
            Thread.Sleep(20);
            device.SimulateReceive("02");

            var analysis = _recorder.Analyze();
            Assert.True(analysis.AverageResponseTimeMs >= 10, $"Expected >= 10ms, got {analysis.AverageResponseTimeMs}");
        }

        [Fact]
        public void Attach_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => _recorder.Attach(null!));
        }

        [Fact]
        public void Detach_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => _recorder.Detach(null!));
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var recorder = new PacketRecorder();
            recorder.Dispose();
            recorder.Dispose();
        }

        [Fact]
        public void PacketEntry_Defaults()
        {
            var entry = new PacketEntry();
            Assert.Equal("", entry.Direction);
            Assert.Equal("", entry.HexData);
            Assert.Equal("", entry.Description);
        }

        [Fact]
        public void PacketAnalysis_Defaults()
        {
            var analysis = new PacketAnalysis();
            Assert.Equal(0, analysis.TotalPackets);
            Assert.Equal(0, analysis.TxCount);
            Assert.Equal(0, analysis.RxCount);
            Assert.Equal(0, analysis.AverageResponseTimeMs);
            Assert.NotNull(analysis.Errors);
        }

        /// <summary>
        /// 用于测试的虚拟 TCP 设备 — 继承 TcpDeviceBase 以触发 OnMessageSent/OnMessageReceived 事件。
        /// </summary>
        private class FakeTcpDevice : TcpDeviceBase
        {
            public FakeTcpDevice() : base("127.0.0.1", 502, 1000) { }

            public void SimulateSend(string hex) => RaiseMessageSent(hex);
            public void SimulateReceive(string hex) => RaiseMessageReceived(hex);

            protected override int ResponseHeaderLength => 0;
            protected override int GetResponsePayloadLength(byte[] header) => 0;
        }
    }
}
