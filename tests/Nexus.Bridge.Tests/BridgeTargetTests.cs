using System;
using System.IO;
using Nexus.Bridge;
using Xunit;

namespace Nexus.Bridge.Tests
{
    public class CsvBridgeTargetTests : IDisposable
    {
        private readonly string _tempFile;

        public CsvBridgeTargetTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"nexus_test_{Guid.NewGuid():N}.csv");
        }

        public void Dispose()
        {
            try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        }

        [Fact]
        public void Connect_CreatesFileAndWritesHeader()
        {
            using var target = new CsvBridgeTarget(_tempFile, false);
            var result = target.Connect();
            Assert.True(result.IsSuccess, result.Message);
            target.Disconnect();

            Assert.True(File.Exists(_tempFile));
            var lines = File.ReadAllLines(_tempFile);
            Assert.Single(lines);
            Assert.Contains("timestamp", lines[0]);
            Assert.Contains("tag", lines[0]);
        }

        [Fact]
        public void Publish_WritesCsvLine()
        {
            using var target = new CsvBridgeTarget(_tempFile, false);
            target.Connect();

            target.Publish(new BridgeData
            {
                Address = "D100",
                Tag = "temperature",
                DataType = "Float",
                RawValue = 25.5,
                ScaledValue = 25.5,
                Timestamp = new DateTime(2026, 6, 13, 10, 30, 0)
            });
            target.Disconnect();

            var lines = File.ReadAllLines(_tempFile);
            Assert.Equal(2, lines.Length); // header + 1 data line
            Assert.Contains("temperature", lines[1]);
            Assert.Contains("D100", lines[1]);
            Assert.Contains("25.5", lines[1]);
            Assert.Contains("Float", lines[1]);
        }

        [Fact]
        public void AppendMode_AppendsToExistingFile()
        {
            using (var target = new CsvBridgeTarget(_tempFile, false))
            {
                target.Connect();
                target.Publish(new BridgeData { Address = "D100", Tag = "t1", DataType = "Int16", ScaledValue = 1, Timestamp = DateTime.Now });
                target.Disconnect();
            }

            using (var target = new CsvBridgeTarget(_tempFile, true))
            {
                target.Connect();
                target.Publish(new BridgeData { Address = "D200", Tag = "t2", DataType = "Int16", ScaledValue = 2, Timestamp = DateTime.Now });
                target.Disconnect();
            }

            var lines = File.ReadAllLines(_tempFile);
            Assert.Equal(3, lines.Length); // header + 2 data lines
        }

        [Fact]
        public void Connect_InvalidPath_ReturnsError()
        {
            var badPath = Path.Combine("Z:\\nonexistent_dir_xyz_999", "test.csv");
            using var target = new CsvBridgeTarget(badPath);
            var result = target.Connect();
            Assert.False(result.IsSuccess);
        }
    }

    public class ConsoleBridgeTargetTests
    {
        [Fact]
        public void Connect_ReturnsSuccess()
        {
            using var target = new ConsoleBridgeTarget();
            Assert.True(target.Connect().IsSuccess);
        }

        [Fact]
        public void Publish_DoesNotThrow()
        {
            using var target = new ConsoleBridgeTarget();
            target.Connect();
            target.Publish(new BridgeData
            {
                Address = "D100",
                Tag = "test",
                DataType = "Float",
                RawValue = 3.14,
                ScaledValue = 3.14,
                Timestamp = DateTime.Now
            });
        }

        [Fact]
        public void Disconnect_DoesNotThrow()
        {
            var target = new ConsoleBridgeTarget();
            target.Connect();
            target.Disconnect();
            target.Dispose();
        }
    }

    public class InfluxDbBridgeTargetTests
    {
        [Fact]
        public void Connect_ReturnsSuccess()
        {
            using var target = new InfluxDbBridgeTarget("http://localhost:8086", "testdb");
            var result = target.Connect();
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public void Connect_WithCustomUrl_Succeeds()
        {
            using var target = new InfluxDbBridgeTarget("http://10.0.0.1:8086/", "mydb");
            var result = target.Connect();
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void Disconnect_BeforeConnect_DoesNotThrow()
        {
            var target = new InfluxDbBridgeTarget("http://localhost:8086");
            target.Disconnect();
            target.Dispose();
        }

        [Fact]
        public void Publish_BeforeConnect_DoesNotThrow()
        {
            using var target = new InfluxDbBridgeTarget("http://localhost:8086");
            target.Publish(new BridgeData
            {
                Address = "D100",
                Tag = "sensor1",
                DataType = "Float",
                ScaledValue = 42.5,
                Timestamp = DateTime.Now
            });
        }
    }

    public class RedisBridgeTargetTests
    {
        [Fact]
        public void Constructor_DoesNotThrow()
        {
            var target = new RedisBridgeTarget("127.0.0.1:6379", "test:");
            Assert.NotNull(target);
            target.Dispose();
        }

        [Fact]
        public void Disconnect_BeforeConnect_DoesNotThrow()
        {
            var target = new RedisBridgeTarget("127.0.0.1:6379");
            target.Disconnect();
            target.Dispose();
        }
    }

    public class BridgeConfigNewDefaultsTests
    {
        [Fact]
        public void SourcePort_DefaultIs502()
        {
            var config = new BridgeConfig();
            Assert.Equal(502, config.SourcePort);
        }

        [Fact]
        public void SourceStation_DefaultIs1()
        {
            var config = new BridgeConfig();
            Assert.Equal(1, config.SourceStation);
        }

        [Fact]
        public void CsvFilePath_Default()
        {
            var config = new BridgeConfig();
            Assert.Equal("nexus_bridge.csv", config.CsvFilePath);
            Assert.True(config.CsvAppend);
        }

        [Fact]
        public void RedisDefaults()
        {
            var config = new BridgeConfig();
            Assert.Equal("127.0.0.1:6379", config.RedisConnectionString);
            Assert.Equal("nexus:", config.RedisKeyPrefix);
        }

        [Fact]
        public void InfluxDbDefaults()
        {
            var config = new BridgeConfig();
            Assert.Equal("http://127.0.0.1:8086", config.InfluxDbUrl);
            Assert.Equal("nexus", config.InfluxDbDatabase);
        }
    }
}
