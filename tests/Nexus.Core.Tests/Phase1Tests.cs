using System;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Nexus.Core.Tests
{
    // ── DataConverter Endianness overloads ─────────────────

    public class DataConverterEndiannessTests
    {
        // Bytes for value 0x1234 (4660 decimal)
        // BigEndian:       12 34
        // LittleEndian:    34 12
        // MidBigEndian:    34 12  (swap bytes in word)
        // MidLittleEndian: 12 34  (swap words — no-op for 2 bytes)

        [Fact]
        public void ToInt16_BigEndian()
        {
            byte[] data = { 0x12, 0x34 };
            Assert.Equal((short)0x1234, DataConverter.ToInt16(data, 0, Endianness.BigEndian));
        }

        [Fact]
        public void ToInt16_LittleEndian()
        {
            byte[] data = { 0x34, 0x12 };
            Assert.Equal((short)0x1234, DataConverter.ToInt16(data, 0, Endianness.LittleEndian));
        }

        [Fact]
        public void ToUInt16_MidBigEndian()
        {
            byte[] data = { 0x34, 0x12 };
            Assert.Equal((ushort)0x1234, DataConverter.ToUInt16(data, 0, Endianness.MidBigEndian));
        }

        // Bytes for value 0x12345678
        // BigEndian:       12 34 56 78
        // LittleEndian:    78 56 34 12
        // MidBigEndian:    34 12 78 56
        // MidLittleEndian: 56 78 12 34

        [Fact]
        public void ToInt32_BigEndian()
        {
            byte[] data = { 0x12, 0x34, 0x56, 0x78 };
            Assert.Equal(0x12345678, DataConverter.ToInt32(data, 0, Endianness.BigEndian));
        }

        [Fact]
        public void ToInt32_LittleEndian()
        {
            byte[] data = { 0x78, 0x56, 0x34, 0x12 };
            Assert.Equal(0x12345678, DataConverter.ToInt32(data, 0, Endianness.LittleEndian));
        }

        [Fact]
        public void ToInt32_MidBigEndian()
        {
            byte[] data = { 0x34, 0x12, 0x78, 0x56 };
            Assert.Equal(0x12345678, DataConverter.ToInt32(data, 0, Endianness.MidBigEndian));
        }

        [Fact]
        public void ToInt32_MidLittleEndian()
        {
            byte[] data = { 0x56, 0x78, 0x12, 0x34 };
            Assert.Equal(0x12345678, DataConverter.ToInt32(data, 0, Endianness.MidLittleEndian));
        }

        [Fact]
        public void ToFloat_LittleEndian()
        {
            float value = 3.14f;
            byte[] be = DataConverter.GetBytes(value);
            // Reverse to LittleEndian
            byte[] le = DataConverter.GetBytes(value, Endianness.LittleEndian);
            float recovered = DataConverter.ToFloat(le, 0, Endianness.LittleEndian);
            Assert.Equal(be, DataConverter.GetBytes(recovered));
        }

        [Fact]
        public void GetBytes_Int32_Roundtrip_AllEndianness()
        {
            int value = -12345678;
            foreach (Endianness bo in new[] { Endianness.BigEndian, Endianness.LittleEndian, Endianness.MidBigEndian, Endianness.MidLittleEndian })
            {
                byte[] bytes = DataConverter.GetBytes(value, bo);
                int recovered = DataConverter.ToInt32(bytes, 0, bo);
                Assert.Equal(value, recovered);
            }
        }

        [Fact]
        public void GetBytes_Int64_Roundtrip_AllEndianness()
        {
            long value = -9876543210123L;
            foreach (Endianness bo in new[] { Endianness.BigEndian, Endianness.LittleEndian, Endianness.MidBigEndian, Endianness.MidLittleEndian })
            {
                byte[] bytes = DataConverter.GetBytes(value, bo);
                long recovered = DataConverter.ToInt64(bytes, 0, bo);
                Assert.Equal(value, recovered);
            }
        }

        [Fact]
        public void GetBytes_Double_Roundtrip_AllEndianness()
        {
            double value = -3.141592653589793;
            foreach (Endianness bo in new[] { Endianness.BigEndian, Endianness.LittleEndian, Endianness.MidBigEndian, Endianness.MidLittleEndian })
            {
                byte[] bytes = DataConverter.GetBytes(value, bo);
                double recovered = DataConverter.ToDouble(bytes, 0, bo);
                Assert.Equal(value, recovered);
            }
        }

        [Fact]
        public void Reorder_LittleEndian_ReversesBytes()
        {
            byte[] buf = { 1, 2, 3, 4 };
            DataConverter.Reorder(buf, 0, 4, Endianness.LittleEndian);
            Assert.Equal(new byte[] { 4, 3, 2, 1 }, buf);
        }

        [Fact]
        public void Reorder_MidBigEndian_SwapsBytesInWords()
        {
            byte[] buf = { 1, 2, 3, 4 };
            DataConverter.Reorder(buf, 0, 4, Endianness.MidBigEndian);
            Assert.Equal(new byte[] { 2, 1, 4, 3 }, buf);
        }

        [Fact]
        public void Reorder_MidLittleEndian_SwapsWords()
        {
            byte[] buf = { 1, 2, 3, 4 };
            DataConverter.Reorder(buf, 0, 4, Endianness.MidLittleEndian);
            Assert.Equal(new byte[] { 3, 4, 1, 2 }, buf);
        }
    }

    // ── StringConverter ────────────────────────────────────

    public class StringConverterTests
    {
        [Fact]
        public void S7String_Roundtrip()
        {
            var encoded = StringConverter.EncodeS7String("Hello", maxLength: 10);
            // [maxLen=10][actualLen=5][H][e][l][l][o][0][0][0][0][0]
            Assert.Equal(12, encoded.Length);
            Assert.Equal(10, encoded[0]);
            Assert.Equal(5, encoded[1]);

            string decoded = StringConverter.DecodeS7String(encoded, 0);
            Assert.Equal("Hello", decoded);
        }

        [Fact]
        public void S7String_Empty()
        {
            var encoded = StringConverter.EncodeS7String("", maxLength: 10);
            Assert.Equal(0, encoded[1]);
            Assert.Equal("", StringConverter.DecodeS7String(encoded, 0));
        }

        [Fact]
        public void WString_Roundtrip()
        {
            var encoded = StringConverter.EncodeWString("AB", maxLength: 10);
            // [maxLen=10(BE)][actualLen=2(BE)][A_utf16be][B_utf16be]
            Assert.Equal(4 + 10 * 2, encoded.Length);

            string decoded = StringConverter.DecodeWString(encoded, 0);
            Assert.Equal("AB", decoded);
        }

        [Fact]
        public void BcdString_Roundtrip()
        {
            var encoded = StringConverter.EncodeBcdString("1234", 2);
            Assert.Equal(2, encoded.Length);
            Assert.Equal(0x12, encoded[0]);
            Assert.Equal(0x34, encoded[1]);

            string decoded = StringConverter.DecodeBcdString(encoded, 0, 2);
            Assert.Equal("1234", decoded);
        }

        [Fact]
        public void BcdString_PadsWithZero()
        {
            var encoded = StringConverter.EncodeBcdString("99", 2);
            Assert.Equal(new byte[] { 0x00, 0x99 }, encoded);
        }

        [Fact]
        public void DecodeModbusString_BigEndian()
        {
            byte[] data = Encoding.ASCII.GetBytes("AB");
            string result = StringConverter.DecodeModbusString(data, 0, 2, Endianness.BigEndian);
            Assert.Equal("AB", result);
        }

        [Fact]
        public void DecodeModbusString_LittleEndian_SwapsBytes()
        {
            byte[] data = { (byte)'B', (byte)'A' };
            string result = StringConverter.DecodeModbusString(data, 0, 2, Endianness.LittleEndian);
            Assert.Equal("AB", result);
        }

        [Fact]
        public void DecodeMitsubishiString_StopsAtNull()
        {
            byte[] data = { (byte)'H', (byte)'i', 0, (byte)'X' };
            string result = StringConverter.DecodeMitsubishiString(data, 0, 4, Encoding.ASCII);
            Assert.Equal("Hi", result);
        }
    }

    // ── StructConverter ────────────────────────────────────

    public class StructConverterTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct TestStruct
        {
            public short Id;
            public int Value;
        }

        [Fact]
        public void FromBytes_Native_Roundtrip()
        {
            var original = new TestStruct { Id = 42, Value = 123456 };
            byte[] bytes = StructConverter.ToBytes(ref original);
            var recovered = StructConverter.FromBytes<TestStruct>(bytes, 0);
            Assert.Equal(42, recovered.Id);
            Assert.Equal(123456, recovered.Value);
        }

        [Fact]
        public void FromBytes_Endianness_Roundtrip()
        {
            var original = new TestStruct { Id = 0x1234, Value = 0x56789ABC };
            foreach (Endianness bo in new[] { Endianness.BigEndian, Endianness.LittleEndian, Endianness.MidBigEndian, Endianness.MidLittleEndian })
            {
                byte[] bytes = StructConverter.ToBytes(ref original, bo);
                var recovered = StructConverter.FromBytes<TestStruct>(bytes, 0, bo);
                Assert.Equal(original.Id, recovered.Id);
                Assert.Equal(original.Value, recovered.Value);
            }
        }
    }

    // ── ILogger upgrades ──────────────────────────────────

    public class LoggerTests
    {
        [Fact]
        public void LogLevel_HasFourValues()
        {
            var values = Enum.GetValues(typeof(LogLevel));
            Assert.Equal(4, values.Length);
        }

        [Fact]
        public void DelegateLogger_InvokesAction()
        {
            LogLevel? seen = null;
            string? msg = null;
            var logger = new DelegateLogger((level, message) => { seen = level; msg = message; });
            logger.Warn("test");
            Assert.Equal(LogLevel.Warn, seen);
            Assert.Equal("test", msg);
        }

        [Fact]
        public void BufferedLogger_CapturesAndLimits()
        {
            var logger = new BufferedLogger(capacity: 3);
            logger.Info("a");
            logger.Info("b");
            logger.Info("c");
            logger.Info("d"); // should evict "a"

            var snapshot = logger.GetSnapshot();
            Assert.Equal(3, snapshot.Count);
            Assert.Equal("b", snapshot[0].Message);
            Assert.Equal("c", snapshot[1].Message);
            Assert.Equal("d", snapshot[2].Message);
        }

        [Fact]
        public void BufferedLogger_Clear()
        {
            var logger = new BufferedLogger();
            logger.Info("x");
            logger.Clear();
            Assert.Empty(logger.GetSnapshot());
        }

        [Fact]
        public void MultiplexLogger_DispatchesToAll()
        {
            int count = 0;
            var l1 = new DelegateLogger((_, __) => count++);
            var l2 = new DelegateLogger((_, __) => count++);
            var multi = new MultiplexLogger(l1, l2);
            multi.Error("err");
            Assert.Equal(2, count);
        }

        [Fact]
        public void NullLogger_DoesNotThrow()
        {
            var logger = NullLogger.Instance;
            logger.Debug("d");
            logger.Info("i");
            logger.Warn("w");
            logger.Error("e");
            logger.Log(LogLevel.Debug, "x");
        }

        [Fact]
        public void ConsoleLogger_DoesNotThrow()
        {
            var logger = new ConsoleLogger();
            logger.Debug("d");
            logger.Info("i");
            logger.Warn("w");
            logger.Error("e");
            logger.Log(LogLevel.Info, "x");
        }

        [Fact]
        public void LogRecord_ToString_ContainsLevel()
        {
            var entry = new LogRecord(new DateTime(2026, 1, 1, 12, 0, 0), LogLevel.Warn, "hello");
            string s = entry.ToString();
            Assert.Contains("Warn", s);
            Assert.Contains("hello", s);
        }
    }

    // ── AddressContext ────────────────────────────────────

    public class AddressContextTests
    {
        [Fact]
        public void Parse_ParamsAndCoreAddress()
        {
            var ctx = AddressContext.Parse("x=3;s=2;D100");
            Assert.Equal("D100", ctx.CoreAddress);
            Assert.Equal("3", ctx.GetParameter("x"));
            Assert.Equal("2", ctx.GetParameter("s"));
            Assert.Equal(3, ctx.GetIntParameter("x"));
            Assert.True(ctx.HasParameter("x"));
        }

        [Fact]
        public void Parse_NoParams()
        {
            var ctx = AddressContext.Parse("DB1.DBD0");
            Assert.Equal("DB1.DBD0", ctx.CoreAddress);
            Assert.Empty(ctx.Parameters);
            Assert.Null(ctx.GetParameter("x"));
            Assert.False(ctx.HasParameter("x"));
        }

        [Fact]
        public void Parse_OnlyParams()
        {
            var ctx = AddressContext.Parse("x=3;y=4");
            Assert.Equal("", ctx.CoreAddress);
            Assert.Equal("3", ctx.GetParameter("x"));
            Assert.Equal("4", ctx.GetParameter("y"));
        }

        [Fact]
        public void Parse_EmptyString()
        {
            var ctx = AddressContext.Parse("");
            Assert.Equal("", ctx.CoreAddress);
            Assert.Empty(ctx.Parameters);
        }

        [Fact]
        public void Parse_NullThrows()
        {
            Assert.Throws<AddressParseException>(() => AddressContext.Parse(null!));
        }

        [Fact]
        public void Parse_DuplicateKey_LastWins()
        {
            var ctx = AddressContext.Parse("x=1;x=2;D100");
            Assert.Equal("2", ctx.GetParameter("x"));
        }

        [Fact]
        public void TryParse_Valid()
        {
            Assert.True(AddressContext.TryParse("x=3;D100", out var ctx));
            Assert.Equal("D100", ctx.CoreAddress);
        }

        [Fact]
        public void GetIntParameter_NotANumber()
        {
            var ctx = AddressContext.Parse("x=abc;D100");
            Assert.Null(ctx.GetIntParameter("x"));
        }

        [Fact]
        public void ExtractCoreAddress_Shorthand()
        {
            Assert.Equal("D100", AddressContext.ExtractCoreAddress("x=3;s=2;D100"));
        }

        [Fact]
        public void ToString_Roundtrip()
        {
            var ctx = AddressContext.Parse("x=3;s=2;D100");
            string s = ctx.ToString();
            Assert.Contains("x=3", s);
            Assert.Contains("s=2", s);
            Assert.Contains("D100", s);
        }

        [Fact]
        public void Parse_ModbusFiveDigit()
        {
            var ctx = AddressContext.Parse("unit=1;40001");
            Assert.Equal("40001", ctx.CoreAddress);
            Assert.Equal(1, ctx.GetIntParameter("unit"));
        }
    }

    // ── AutoReconnectGuard ────────────────────────────────

    public class AutoReconnectGuardTests
    {
        private class StubTcpDevice : TcpDeviceBase
        {
            public int ConnectCallCount;
            public bool ForceFail;

            public StubTcpDevice() : base("127.0.0.1", 0) { }

            protected override int ResponseHeaderLength => 0;
            protected override int GetResponsePayloadLength(byte[] header) => 0;

            public override OperateResult Connect()
            {
                ConnectCallCount++;
                if (ForceFail) return OperateResult.Failed("forced fail");
                return OperateResult.Success();
            }

            // IsConnected is not virtual — use ForceFail flag to control Connect outcome instead
            public bool WasConnected => ConnectCallCount > 0 && !ForceFail;

            public void FireDisconnected() => RaiseDisconnected();
        }

        [Fact]
        public void Start_SubscribesToEvents()
        {
            var device = new StubTcpDevice();
            var guard = new AutoReconnectGuard(device) { MaxRetries = 1, BaseDelayMs = 10 };
            guard.Start();

            // Fire disconnect → should trigger reconnect
            device.ForceFail = false;
            device.ConnectCallCount = 0;
            device.FireDisconnected();

            // Give timer a chance
            Thread.Sleep(150);

            Assert.True(device.ConnectCallCount >= 1);
            guard.Dispose();
        }

        [Fact]
        public void Stop_Unsubscribes()
        {
            var device = new StubTcpDevice();
            var guard = new AutoReconnectGuard(device) { BaseDelayMs = 10 };
            guard.Start();
            guard.Stop();

            int before = device.ConnectCallCount;
            device.FireDisconnected();
            Thread.Sleep(100);

            Assert.Equal(before, device.ConnectCallCount);
            guard.Dispose();
        }

        [Fact]
        public void Dispose_PreventsFurtherReconnect()
        {
            var device = new StubTcpDevice();
            var guard = new AutoReconnectGuard(device);
            guard.Start();
            guard.Dispose();

            int before = device.ConnectCallCount;
            device.FireDisconnected();
            Thread.Sleep(100);

            Assert.Equal(before, device.ConnectCallCount);
        }

        [Fact]
        public void IsReconnecting_ReflectsState()
        {
            var guard = new AutoReconnectGuard(new StubTcpDevice());
            Assert.False(guard.IsReconnecting);
            guard.Dispose();
        }
    }

    // ── HeartbeatGuard ────────────────────────────────────

    public class HeartbeatGuardTests
    {
        [Fact]
        public void Start_Stop_ControlsTimer()
        {
            int callCount = 0;
            var guard = new HeartbeatGuard(
                new StubDevice(),
                () => { callCount++; return Task.FromResult(OperateResult.Success()); })
            { IntervalMs = 50, MaxConsecutiveFailures = 3 };

            Assert.False(guard.IsRunning);

            guard.Start();
            Assert.True(guard.IsRunning);

            Thread.Sleep(200);
            guard.Stop();
            Assert.False(guard.IsRunning);

            Assert.True(callCount >= 1);
            guard.Dispose();
        }

        [Fact]
        public void ConsecutiveFailures_StopsAfterMax()
        {
            int failCount = 0;
            int failedEventCount = 0;

            var guard = new HeartbeatGuard(
                new StubDevice(),
                () => { failCount++; return Task.FromResult(OperateResult.Failed("fail")); })
            { IntervalMs = 50, MaxConsecutiveFailures = 2 };

            guard.OnHeartbeatFailed += (count, err) => failedEventCount++;
            guard.Start();

            // Wait for at least MaxConsecutiveFailures + 1 ticks
            Thread.Sleep(500);

            Assert.True(failedEventCount >= 1);
            Assert.False(guard.IsRunning); // should have auto-stopped
            guard.Dispose();
        }

        [Fact]
        public void Success_ResetsFailureCounter()
        {
            int tick = 0;
            var guard = new HeartbeatGuard(
                new StubDevice(),
                () =>
                {
                    tick++;
                    // Fail first, succeed after
                    return Task.FromResult(
                        tick <= 1 ? OperateResult.Failed("fail") : OperateResult.Success());
                })
            { IntervalMs = 50, MaxConsecutiveFailures = 3 };

            guard.Start();
            Thread.Sleep(400);

            // Should still be running since failures were reset
            Assert.True(guard.IsRunning || guard.ConsecutiveFailures == 0);
            guard.Dispose();
        }

        private class StubDevice : IReadWriteDevice
        {
            public bool IsConnected => true;
            public OperateResult Connect() => OperateResult.Success();
            public Task<OperateResult> ConnectAsync() => Task.FromResult(OperateResult.Success());
            public void Disconnect() { }
            public OperateResult<bool> ReadBool(string address) => OperateResult<bool>.Success(true);
            public OperateResult<short> ReadInt16(string address) => OperateResult<short>.Success((short)0);
            public OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Success((ushort)0);
            public OperateResult<int> ReadInt32(string address) => OperateResult<int>.Success(0);
            public OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Success((uint)0);
            public OperateResult<long> ReadInt64(string address) => OperateResult<long>.Success(0L);
            public OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Success((ulong)0);
            public OperateResult<float> ReadFloat(string address) => OperateResult<float>.Success(0f);
            public OperateResult<double> ReadDouble(string address) => OperateResult<double>.Success(0d);
            public OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Success("");
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
            public void Dispose() { }
        }
    }
}
