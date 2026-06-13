using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nexus;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// DataAcquisitionEngine + IDataSink 基础测试。
    /// 使用 FakeDevice（无需网络）验证引擎的轮询、数据变更、Sink 推送逻辑。
    /// </summary>
    public class DataAcquisitionTests : IDisposable
    {
        private readonly DataAcquisitionEngine _engine = new();

        public void Dispose() => _engine.Dispose();

        [Fact]
        public void RegisterDevice_Adds_DeviceCount()
        {
            var device = new FakeDevice();
            _engine.RegisterDevice("plc1", device, new PollConfig());

            Assert.Equal(1, _engine.DeviceCount);
        }

        [Fact]
        public void UnregisterDevice_Removes_DeviceCount()
        {
            var device = new FakeDevice();
            _engine.RegisterDevice("plc1", device, new PollConfig());
            _engine.UnregisterDevice("plc1");

            Assert.Equal(0, _engine.DeviceCount);
        }

        [Fact]
        public void AddSink_Increments_SinkCount()
        {
            _engine.AddSink(new ConsoleDataSink());
            Assert.Equal(1, _engine.SinkCount);
        }

        [Fact]
        public void RemoveSink_Decrements_SinkCount()
        {
            var sink = new ConsoleDataSink();
            _engine.AddSink(sink);
            _engine.RemoveSink(sink);
            Assert.Equal(0, _engine.SinkCount);
        }

        [Fact]
        public void Start_Sets_IsRunning()
        {
            var device = new FakeDevice();
            _engine.RegisterDevice("plc1", device, new PollConfig { IntervalMs = 100 });
            _engine.AddPoint("plc1", "D100", "Int16");

            _engine.Start();
            Assert.True(_engine.IsRunning);

            _engine.Stop();
            Assert.False(_engine.IsRunning);
        }

        [Fact]
        public void MemoryDataSink_Stores_Samples()
        {
            var sink = new MemoryDataSink(capacity: 100);

            for (int i = 0; i < 5; i++)
            {
                sink.Write(new DataSample { DeviceName = "plc1", Address = "D100", Value = i.ToString() });
            }

            Assert.Equal(5, sink.Count);
            var all = sink.GetAll();
            Assert.Equal(5, all.Length);
        }

        [Fact]
        public void MemoryDataSink_RingBuffer_Wraps()
        {
            var sink = new MemoryDataSink(capacity: 3);

            for (int i = 0; i < 5; i++)
                sink.Write(new DataSample { DeviceName = "plc1", Address = "D100", Value = i.ToString() });

            Assert.Equal(3, sink.Count);
            var all = sink.GetAll();
            // Should have the last 3: values 2, 3, 4
            Assert.Equal("2", all[0].Value);
            Assert.Equal("4", all[2].Value);
        }

        [Fact]
        public void OnSample_Fires_When_Data_Changes()
        {
            var device = new FakeDevice();
            device.NextValue = 42;
            _engine.RegisterDevice("plc1", device, new PollConfig
            {
                IntervalMs = 50,
                OnlyOnChange = true
            });
            _engine.AddPoint("plc1", "D100", "Int16");

            var samples = new List<DataSample>();
            _engine.OnSample += (_, e) => samples.Add(e.Sample);

            _engine.Start();
            System.Threading.Thread.Sleep(300);
            _engine.Stop();

            Assert.True(samples.Count > 0, "Should have received at least one sample");
            Assert.Equal("42", samples[0].Value);
            Assert.Equal("plc1", samples[0].DeviceName);
            Assert.Equal("D100", samples[0].Address);
            Assert.Equal("Good", samples[0].Quality);
        }

        [Fact]
        public void OnlyOnChange_Skips_Unchanged_Values()
        {
            var device = new FakeDevice();
            device.NextValue = 100; // Same value every time
            _engine.RegisterDevice("plc1", device, new PollConfig
            {
                IntervalMs = 50,
                OnlyOnChange = true
            });
            _engine.AddPoint("plc1", "D100", "Int16");

            var samples = new List<DataSample>();
            _engine.OnSample += (_, e) => samples.Add(e.Sample);

            _engine.Start();
            System.Threading.Thread.Sleep(400);
            _engine.Stop();

            // Should only get the first sample, not subsequent unchanged ones
            Assert.True(samples.Count <= 2, $"Expected ≤ 2 samples with OnlyOnChange, got {samples.Count}");
        }

        [Fact]
        public void PushToSinks_Writes_To_MemorySink()
        {
            var device = new FakeDevice();
            device.NextValue = 99;
            var sink = new MemoryDataSink(100);
            _engine.RegisterDevice("plc1", device, new PollConfig
            {
                IntervalMs = 50,
                OnlyOnChange = false // Always push
            });
            _engine.AddPoint("plc1", "D100", "Int16");
            _engine.AddSink(sink);

            _engine.Start();
            System.Threading.Thread.Sleep(300);
            _engine.Stop();

            Assert.True(sink.Count > 0, "Sink should have received samples");
        }

        // ── CsvDataSink Tests ─────────────────────

        [Fact]
        public void CsvDataSink_CreatesFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"csv_test_{Guid.NewGuid():N}.csv");
            try
            {
                using var sink = new CsvDataSink(path);
                sink.Write(new DataSample
                {
                    DeviceName = "plc1",
                    Address = "D100",
                    DataType = "Int16",
                    Value = "42",
                    Quality = "Good",
                    Timestamp = new DateTime(2026, 1, 1, 12, 0, 0)
                });

                Assert.True(File.Exists(path));
                var lines = File.ReadAllLines(path);
                Assert.Equal(2, lines.Length); // header + 1 data row
                Assert.Equal("Timestamp,DeviceName,Address,DataType,Tag,Quality,Value", lines[0]);
                Assert.Contains("plc1", lines[1]);
                Assert.Contains("D100", lines[1]);
                Assert.Contains("42", lines[1]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void CsvDataSink_AppendsMultipleRows()
        {
            var path = Path.Combine(Path.GetTempPath(), $"csv_test_{Guid.NewGuid():N}.csv");
            try
            {
                using var sink = new CsvDataSink(path);
                for (int i = 0; i < 5; i++)
                {
                    sink.Write(new DataSample
                    {
                        DeviceName = "plc1",
                        Address = "D100",
                        Value = i.ToString(),
                        Timestamp = DateTime.Now
                    });
                }

                var lines = File.ReadAllLines(path);
                Assert.Equal(6, lines.Length); // 1 header + 5 data rows
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void CsvDataSink_EscapesCommasAndQuotes()
        {
            var path = Path.Combine(Path.GetTempPath(), $"csv_test_{Guid.NewGuid():N}.csv");
            try
            {
                using var sink = new CsvDataSink(path);
                sink.Write(new DataSample
                {
                    DeviceName = "plc,1",   // comma
                    Address = "D\"100",       // quote
                    Value = "hello world",    // safe value, no newline
                    Timestamp = DateTime.Now
                });

                var lines = File.ReadAllLines(path);
                Assert.Equal(2, lines.Length); // header + 1 data row
                // Escaped fields should be quoted
                Assert.Contains("\"plc,1\"", lines[1]);
                Assert.Contains("\"D\"\"100\"", lines[1]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void CsvDataSink_EscapesNewlineInValue()
        {
            var path = Path.Combine(Path.GetTempPath(), $"csv_test_{Guid.NewGuid():N}.csv");
            try
            {
                using var sink = new CsvDataSink(path);
                sink.Write(new DataSample
                {
                    DeviceName = "plc1",
                    Address = "D100",
                    Value = "hello\nworld",  // newline in value
                    Timestamp = DateTime.Now
                });

                // The value with newline is CSV-escaped (quoted), so it spans multiple lines in the file
                var content = File.ReadAllText(path);
                Assert.Contains("hello\nworld", content); // raw content has the newline inside quotes
                Assert.Contains("\"hello\nworld\"", content);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void CsvDataSink_CreatesDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"csv_dir_{Guid.NewGuid():N}");
            var path = Path.Combine(dir, "data.csv");
            try
            {
                Assert.False(Directory.Exists(dir));
                using var sink = new CsvDataSink(path);
                sink.Write(new DataSample
                {
                    DeviceName = "plc1",
                    Address = "D100",
                    Value = "1",
                    Timestamp = DateTime.Now
                });

                Assert.True(Directory.Exists(dir));
                Assert.True(File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void CsvDataSink_NullPath_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new CsvDataSink(null!));
        }

        [Fact]
        public void CsvDataSink_WritesTag()
        {
            var path = Path.Combine(Path.GetTempPath(), $"csv_test_{Guid.NewGuid():N}.csv");
            try
            {
                using var sink = new CsvDataSink(path);
                sink.Write(new DataSample
                {
                    DeviceName = "plc1",
                    Address = "D100",
                    Tag = "temperature",
                    Value = "25.3",
                    Timestamp = DateTime.Now
                });

                var lines = File.ReadAllLines(path);
                Assert.Equal(2, lines.Length);
                Assert.Contains("temperature", lines[1]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void GetCurrentValues_ReturnsLastValues()
        {
            var device = new FakeDevice();
            device.NextValue = 42;
            _engine.RegisterDevice("plc1", device, new PollConfig
            {
                IntervalMs = 50,
                OnlyOnChange = false
            });
            _engine.AddPoint("plc1", "D100", "Int16");
            _engine.AddPoint("plc1", "D200", "Int16");

            _engine.Start();
            Thread.Sleep(300);
            _engine.Stop();

            var values = _engine.GetCurrentValues();
            Assert.True(values.ContainsKey("plc1/D100"));
            Assert.True(values.ContainsKey("plc1/D200"));
        }

        [Fact]
        public void GetCurrentValues_Empty_WhenNoPoints()
        {
            var device = new FakeDevice();
            _engine.RegisterDevice("plc1", device, new PollConfig());
            var values = _engine.GetCurrentValues();
            Assert.Empty(values);
        }

        [Fact]
        public void ExportToCsv_CreatesFile()
        {
            var device = new FakeDevice();
            device.NextValue = 99;
            var sink = new MemoryDataSink(100);
            _engine.RegisterDevice("plc1", device, new PollConfig
            {
                IntervalMs = 50,
                OnlyOnChange = false
            });
            _engine.AddPoint("plc1", "D100", "Int16");
            _engine.AddSink(sink);

            _engine.Start();
            Thread.Sleep(300);
            _engine.Stop();

            var path = Path.Combine(Path.GetTempPath(), $"export_test_{Guid.NewGuid():N}.csv");
            try
            {
                _engine.ExportToCsv(path);
                Assert.True(File.Exists(path));
                var lines = File.ReadAllLines(path);
                Assert.True(lines.Length >= 2, $"Expected header + data, got {lines.Length} lines");
                Assert.Equal("Timestamp,DeviceName,Address,DataType,Tag,Quality,Value", lines[0]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ExportToCsv_CreatesDirectory()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"export_dir_{Guid.NewGuid():N}");
            var path = Path.Combine(dir, "export.csv");
            try
            {
                _engine.ExportToCsv(path);
                Assert.True(Directory.Exists(dir));
                Assert.True(File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ExportToCsv_NullPath_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => _engine.ExportToCsv(null!));
        }

        // ── Fake Device for Testing ────────────────

        /// <summary>
        /// 虚拟设备 — 返回预设值，无需网络连接。
        /// </summary>
        private class FakeDevice : IReadWriteDevice
        {
            public int NextValue { get; set; } = 0;
            public bool IsConnected { get; private set; }

            public OperateResult Connect() { IsConnected = true; return OperateResult.Success(); }
            public Task<OperateResult> ConnectAsync() { IsConnected = true; return Task.FromResult(OperateResult.Success()); }
            public void Disconnect() { IsConnected = false; }

            public OperateResult<bool> ReadBool(string address) => OperateResult<bool>.Success(NextValue != 0);
            public OperateResult<short> ReadInt16(string address) => OperateResult<short>.Success((short)NextValue);
            public OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Success((ushort)NextValue);
            public OperateResult<int> ReadInt32(string address) => OperateResult<int>.Success(NextValue);
            public OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Success((uint)NextValue);
            public OperateResult<long> ReadInt64(string address) => OperateResult<long>.Success((long)NextValue);
            public OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Success((ulong)NextValue);
            public OperateResult<float> ReadFloat(string address) => OperateResult<float>.Success((float)NextValue);
            public OperateResult<double> ReadDouble(string address) => OperateResult<double>.Success((double)NextValue);
            public OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Success(NextValue.ToString());
            public OperateResult<byte[]> ReadBytes(string address, ushort length) => OperateResult<byte[]>.Success(new byte[0]);

            public OperateResult Write(string address, bool value) => OperateResult.Success();
            public OperateResult Write(string address, short value) => OperateResult.Success();
            public OperateResult Write(string address, ushort value) => OperateResult.Success();
            public OperateResult Write(string address, int value) => OperateResult.Success();
            public OperateResult Write(string address, uint value) => OperateResult.Success();
            public OperateResult Write(string address, long value) => OperateResult.Success();
            public OperateResult Write(string address, ulong value) => OperateResult.Success();
            public OperateResult Write(string address, float value) => OperateResult.Success();
            public OperateResult Write(string address, double value) => OperateResult.Success();
            public OperateResult Write(string address, string value) => OperateResult.Success();
            public OperateResult Write(string address, byte[] data) => OperateResult.Success();

            public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.FromResult(ReadBool(address));
            public Task<OperateResult<short>> ReadInt16Async(string address) => Task.FromResult(ReadInt16(address));
            public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.FromResult(ReadUInt16(address));
            public Task<OperateResult<int>> ReadInt32Async(string address) => Task.FromResult(ReadInt32(address));
            public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.FromResult(ReadUInt32(address));
            public Task<OperateResult<long>> ReadInt64Async(string address) => Task.FromResult(ReadInt64(address));
            public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.FromResult(ReadUInt64(address));
            public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.FromResult(ReadFloat(address));
            public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.FromResult(ReadDouble(address));
            public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.FromResult(ReadString(address, length));
            public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.FromResult(ReadBytes(address, length));

            public Task<OperateResult> WriteAsync(string address, bool value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, short value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, int value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, float value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, string value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.FromResult(Write(address, data));

            public void Dispose() { IsConnected = false; }
        }
    }
}
