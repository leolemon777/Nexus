using System;
using Xunit;
using Nexus.Iec104;

namespace Nexus.Iec104.Tests
{
    public class Iec104DepthTests
    {
        // ═══════════════════════════════════════════
        //  时钟同步帧构建
        // ═══════════════════════════════════════════

        [Fact]
        public void TypeId_C_CS_NA_1_Is_103()
        {
            Assert.Equal(103, (byte)TypeId.C_CS_NA_1);
        }

        [Fact]
        public void TypeId_C_CI_NA_1_Is_101()
        {
            Assert.Equal(101, (byte)TypeId.C_CI_NA_1);
        }

        [Fact]
        public void TypeId_C_TS_TA_1_Is_104()
        {
            Assert.Equal(104, (byte)TypeId.C_TS_TA_1);
        }

        [Fact]
        public void TypeId_M_IT_NA_1_Is_15()
        {
            Assert.Equal(15, (byte)TypeId.M_IT_NA_1);
        }

        [Fact]
        public void BuildClockSyncCommand_CorrectAsdu()
        {
            DateTime time = new DateTime(2025, 6, 15, 10, 30, 45, 123, DateTimeKind.Utc);
            var asdu = Iec104Asdu.BuildClockSyncCommand(1, time);

            Assert.Equal(TypeId.C_CS_NA_1, asdu.TypeId);
            Assert.Equal(CauseOfTransmission.Activation, asdu.Cause);
            Assert.Equal(1, asdu.CommonAddress);
            Assert.Single(asdu.Objects);
            Assert.Equal(0, asdu.Objects[0].Address);
            Assert.Equal(7, asdu.Objects[0].Data.Length); // CP56Time2a = 7 bytes
        }

        [Fact]
        public void BuildClockSyncCommand_EncodesTime()
        {
            DateTime time = new DateTime(2025, 3, 10, 14, 25, 30, 500, DateTimeKind.Utc);
            var asdu = Iec104Asdu.BuildClockSyncCommand(1, time);
            byte[] data = asdu.Objects[0].Data;

            int ms = data[0] | (data[1] << 8);
            int sec = ms / 1000;
            int milli = ms % 1000;
            Assert.Equal(30, sec);
            Assert.Equal(500, milli);

            int min = data[2] & 0x3F;
            Assert.Equal(25, min);

            int hour = data[3] & 0x1F;
            Assert.Equal(14, hour);
        }

        [Fact]
        public void EncodeCP56Time2a_RoundTrip()
        {
            DateTime time = new DateTime(2025, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc);
            byte[] encoded = Iec104Asdu.EncodeCP56Time2a(time);
            Assert.Equal(7, encoded.Length);

            DateTime decoded = Iec104Asdu.DecodeCP56Time2a(encoded, 0);
            Assert.Equal(time.Year, decoded.Year);
            Assert.Equal(time.Month, decoded.Month);
            Assert.Equal(time.Day, decoded.Day);
            Assert.Equal(time.Hour, decoded.Hour);
            Assert.Equal(time.Minute, decoded.Minute);
            Assert.Equal(time.Second, decoded.Second);
            Assert.InRange(Math.Abs((time - decoded).TotalMilliseconds), 0, 1);
        }

        [Fact]
        public void EncodeCP56Time2a_Midnight()
        {
            DateTime time = new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            byte[] encoded = Iec104Asdu.EncodeCP56Time2a(time);

            Assert.Equal(0, encoded[0]); // ms low
            Assert.Equal(0, encoded[1]); // ms high
            Assert.Equal(0, encoded[2]); // min
            Assert.Equal(0, encoded[3]); // hour
        }

        [Fact]
        public void ClockSyncCommand_EncodeDecode_Roundtrip()
        {
            DateTime time = new DateTime(2025, 6, 15, 10, 30, 45, 123, DateTimeKind.Utc);
            var asdu = Iec104Asdu.BuildClockSyncCommand(1, time);
            byte[] encoded = asdu.Encode();

            var decoded = Iec104Asdu.Decode(encoded, 0);
            Assert.Equal(TypeId.C_CS_NA_1, decoded.TypeId);
            Assert.Equal(CauseOfTransmission.Activation, decoded.Cause);
            Assert.Equal(1, decoded.CommonAddress);
            Assert.Single(decoded.Objects);
            Assert.Equal(7, decoded.Objects[0].Data.Length);
        }

        // ═══════════════════════════════════════════
        //  组召唤（特定组）
        // ═══════════════════════════════════════════

        [Fact]
        public void BuildGeneralInterrogation_Group0_StationWide()
        {
            var asdu = Iec104Asdu.BuildGeneralInterrogation(1, 0);
            Assert.Equal(20, asdu.Objects[0].Data[0]); // QOI=20 = station interrogation
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(8, 8)]
        [InlineData(16, 16)]
        public void BuildGeneralInterrogation_SpecificGroup(int group, int expectedQoi)
        {
            var asdu = Iec104Asdu.BuildGeneralInterrogation(1, (byte)group);
            Assert.Equal(TypeId.C_IC_NA_1, asdu.TypeId);
            Assert.Equal(expectedQoi, asdu.Objects[0].Data[0]);
        }

        [Fact]
        public void BuildGeneralInterrogation_EncodeDecode()
        {
            var asdu = Iec104Asdu.BuildGeneralInterrogation(1, 5);
            byte[] encoded = asdu.Encode();

            var decoded = Iec104Asdu.Decode(encoded, 0);
            Assert.Equal(TypeId.C_IC_NA_1, decoded.TypeId);
            Assert.Equal(CauseOfTransmission.Activation, decoded.Cause);
            Assert.Equal(5, decoded.Objects[0].Data[0]);
        }

        [Fact]
        public void GeneralInterrogation_Client_ConstructorDefaults()
        {
            var client = new Iec104Client("127.0.0.1");
            Assert.Equal(1, client.CommonAddress);
        }

        // ═══════════════════════════════════════════
        //  计数器读取
        // ═══════════════════════════════════════════

        [Fact]
        public void BuildCounterReadCommand_CorrectAsdu()
        {
            var asdu = Iec104Asdu.BuildCounterReadCommand(1);
            Assert.Equal(TypeId.C_CI_NA_1, asdu.TypeId);
            Assert.Equal(CauseOfTransmission.Activation, asdu.Cause);
            Assert.Equal(1, asdu.CommonAddress);
            Assert.Single(asdu.Objects);
            Assert.Equal(0, asdu.Objects[0].Address);
            Assert.Single(asdu.Objects[0].Data);
            Assert.Equal(5, asdu.Objects[0].Data[0]); // QCC=5 = general request
        }

        [Theory]
        [InlineData(0, 5)]   // general = 5
        [InlineData(1, 1)]   // group 1
        [InlineData(4, 4)]   // group 4
        [InlineData(16, 16)] // group 16
        public void BuildCounterReadCommand_Groups(int group, int expectedQcc)
        {
            var asdu = Iec104Asdu.BuildCounterReadCommand(1, (byte)group);
            Assert.Equal(expectedQcc, asdu.Objects[0].Data[0]);
        }

        [Fact]
        public void BuildCounterReadCommand_EncodeDecode()
        {
            var asdu = Iec104Asdu.BuildCounterReadCommand(1);
            byte[] encoded = asdu.Encode();

            var decoded = Iec104Asdu.Decode(encoded, 0);
            Assert.Equal(TypeId.C_CI_NA_1, decoded.TypeId);
            Assert.Equal(CauseOfTransmission.Activation, decoded.Cause);
            Assert.Equal(1, decoded.CommonAddress);
            Assert.Single(decoded.Objects);
            Assert.Equal(5, decoded.Objects[0].Data[0]);
        }

        // ═══════════════════════════════════════════
        //  测试命令
        // ═══════════════════════════════════════════

        [Fact]
        public void BuildTestCommand_CorrectAsdu()
        {
            DateTime time = new DateTime(2025, 6, 15, 10, 30, 45, 0, DateTimeKind.Utc);
            var asdu = Iec104Asdu.BuildTestCommand(1, 0x1234, time);

            Assert.Equal(TypeId.C_TS_TA_1, asdu.TypeId);
            Assert.Equal(CauseOfTransmission.Activation, asdu.Cause);
            Assert.Equal(1, asdu.CommonAddress);
            Assert.Single(asdu.Objects);
            Assert.Equal(0, asdu.Objects[0].Address);
            Assert.Equal(9, asdu.Objects[0].Data.Length); // TSC(2) + CP56Time2a(7)
        }

        [Fact]
        public void BuildTestCommand_EncodesCounter()
        {
            DateTime time = new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            var asdu = Iec104Asdu.BuildTestCommand(1, 0xABCD, time);
            byte[] data = asdu.Objects[0].Data;

            ushort counter = (ushort)(data[0] | (data[1] << 8));
            Assert.Equal(0xABCD, counter);
        }

        [Fact]
        public void BuildTestCommand_EncodeDecode()
        {
            DateTime time = new DateTime(2025, 6, 15, 10, 30, 45, 0, DateTimeKind.Utc);
            var asdu = Iec104Asdu.BuildTestCommand(1, 0x1234, time);
            byte[] encoded = asdu.Encode();

            var decoded = Iec104Asdu.Decode(encoded, 0);
            Assert.Equal(TypeId.C_TS_TA_1, decoded.TypeId);
            Assert.Equal(CauseOfTransmission.Activation, decoded.Cause);
            Assert.Equal(1, decoded.CommonAddress);
            Assert.Single(decoded.Objects);

            ushort counter = (ushort)(decoded.Objects[0].Data[0] | (decoded.Objects[0].Data[1] << 8));
            Assert.Equal(0x1234, counter);
        }

        // ═══════════════════════════════════════════
        //  GetDataLength 覆盖
        // ═══════════════════════════════════════════

        [Fact]
        public void GetDataLength_C_CS_NA_1_Is7()
        {
            // Build a clock sync and verify data length through encode/decode
            DateTime time = new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            var asdu = Iec104Asdu.BuildClockSyncCommand(1, time);
            byte[] encoded = asdu.Encode();
            var decoded = Iec104Asdu.Decode(encoded, 0);
            Assert.Equal(7, decoded.Objects[0].Data.Length);
        }

        [Fact]
        public void GetDataLength_C_CI_NA_1_Is1()
        {
            var asdu = Iec104Asdu.BuildCounterReadCommand(1);
            byte[] encoded = asdu.Encode();
            var decoded = Iec104Asdu.Decode(encoded, 0);
            Assert.Single(decoded.Objects[0].Data);
        }

        [Fact]
        public void GetDataLength_C_TS_TA_1_Is9()
        {
            DateTime time = new DateTime(2025, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            var asdu = Iec104Asdu.BuildTestCommand(1, 0, time);
            byte[] encoded = asdu.Encode();
            var decoded = Iec104Asdu.Decode(encoded, 0);
            Assert.Equal(9, decoded.Objects[0].Data.Length); // TSC(2) + CP56Time2a(7)
        }

        // ═══════════════════════════════════════════
        //  客户端命令方法（离线验证）
        // ═══════════════════════════════════════════

        [Fact]
        public void SynchronizeClock_WithoutConnect_Fails()
        {
            var client = new Iec104Client("127.0.0.1");
            var result = client.SynchronizeClock();
            Assert.False(result.IsSuccess);
            Assert.Contains("未激活", result.Message);
        }

        [Fact]
        public void ReadCounters_WithoutConnect_Fails()
        {
            var client = new Iec104Client("127.0.0.1");
            var result = client.ReadCounters();
            Assert.False(result.IsSuccess);
            Assert.Contains("未激活", result.Message);
        }

        [Fact]
        public void TestCommand_WithoutConnect_Fails()
        {
            var client = new Iec104Client("127.0.0.1");
            var result = client.TestCommand();
            Assert.False(result.IsSuccess);
            Assert.Contains("未激活", result.Message);
        }

        [Fact]
        public void SendGeneralInterrogation_Group5_WithoutConnect_Fails()
        {
            var client = new Iec104Client("127.0.0.1");
            var result = client.SendGeneralInterrogation(5);
            Assert.False(result.IsSuccess);
            Assert.Contains("未激活", result.Message);
        }
    }
}
