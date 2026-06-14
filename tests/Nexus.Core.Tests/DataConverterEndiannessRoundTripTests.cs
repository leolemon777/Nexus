using System;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// M5 字节序往返测试 — 固化 GetBytes(value, Endianness) 与 ToXxx(bytes, Endianness) 的对称性。
    /// 风险背景：4 种字节序（Big/Little/MidBig/MidLittle）的读/写是两套独立实现，
    /// 任一方向实现错误都会"静默返回错误数值"（IsSuccess 仍为 true），工业场景下可能误写设备。
    /// 本测试覆盖 16/32/64 位 × 4 种字节序的双向往返。
    /// </summary>
    public class DataConverterEndiannessRoundTripTests
    {
        private static readonly Endianness[] AllOrders =
        {
            Endianness.BigEndian,
            Endianness.LittleEndian,
            Endianness.MidBigEndian,
            Endianness.MidLittleEndian,
        };

        // ── 16 位往返 ──────────────────────────────

        [Theory]
        [InlineData((short)0)]
        [InlineData((short)1)]
        [InlineData((short)0x1234)]
        [InlineData((short)-21555)]            // 0xABCD 的 short 表示
        [InlineData((short)-1)]
        [InlineData(short.MaxValue)]
        [InlineData(short.MinValue)]
        public void RoundTrip_Int16_AllByteOrders(short value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] encoded = DataConverter.GetBytes(value, bo);
                short decoded = DataConverter.ToInt16(encoded, 0, bo);
                Assert.True(decoded == value,
                    $"Int16 round-trip failed for {bo}: value=0x{value:X4}, got 0x{decoded:X4} bytes=[{BitConverter.ToString(encoded)}]");
            }
        }

        [Theory]
        [InlineData((ushort)0)]
        [InlineData((ushort)0x1234)]
        [InlineData((ushort)0xABCD)]
        [InlineData(ushort.MaxValue)]
        public void RoundTrip_UInt16_AllByteOrders(ushort value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] encoded = DataConverter.GetBytes(value, bo);
                ushort decoded = DataConverter.ToUInt16(encoded, 0, bo);
                Assert.True(decoded == value,
                    $"UInt16 round-trip failed for {bo}: value=0x{value:X4}, got 0x{decoded:X4}");
            }
        }

        // ── 32 位往返 ──────────────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(0x12345678)]
        [InlineData(unchecked((int)0x9ABCDEF0))]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void RoundTrip_Int32_AllByteOrders(int value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] encoded = DataConverter.GetBytes(value, bo);
                int decoded = DataConverter.ToInt32(encoded, 0, bo);
                Assert.True(decoded == value,
                    $"Int32 round-trip failed for {bo}: value=0x{value:X8}, got 0x{decoded:X8} bytes=[{BitConverter.ToString(encoded)}]");
            }
        }

        [Theory]
        [InlineData((uint)0)]
        [InlineData((uint)0x12345678)]
        [InlineData((uint)0x9ABCDEF0)]
        [InlineData(uint.MaxValue)]
        public void RoundTrip_UInt32_AllByteOrders(uint value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] encoded = DataConverter.GetBytes(value, bo);
                uint decoded = DataConverter.ToUInt32(encoded, 0, bo);
                Assert.True(decoded == value,
                    $"UInt32 round-trip failed for {bo}: value=0x{value:X8}, got 0x{decoded:X8}");
            }
        }

        // ── 64 位往返 ──────────────────────────────

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(0x123456789ABCDEF0L)]
        [InlineData(-1L)]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public void RoundTrip_Int64_AllByteOrders(long value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] encoded = DataConverter.GetBytes(value, bo);
                long decoded = DataConverter.ToInt64(encoded, 0, bo);
                Assert.True(decoded == value,
                    $"Int64 round-trip failed for {bo}: value=0x{value:X16}, got 0x{decoded:X16} bytes=[{BitConverter.ToString(encoded)}]");
            }
        }

        [Theory]
        [InlineData((ulong)0)]
        [InlineData((ulong)0x123456789ABCDEF0)]
        [InlineData(ulong.MaxValue)]
        public void RoundTrip_UInt64_AllByteOrders(ulong value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] encoded = DataConverter.GetBytes(value, bo);
                ulong decoded = DataConverter.ToUInt64(encoded, 0, bo);
                Assert.True(decoded == value,
                    $"UInt64 round-trip failed for {bo}: value=0x{value:X16}, got 0x{decoded:X16}");
            }
        }

        // ── 浮点往返（验证 unsafe 指针转换在所有字节序下一致）────────

        [Theory]
        [InlineData(0f)]
        [InlineData(1f)]
        [InlineData(3.14159f)]
        [InlineData(-2.71828f)]
        [InlineData(float.MaxValue)]
        [InlineData(float.MinValue)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void RoundTrip_Float_AllByteOrders(float value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] encoded = DataConverter.GetBytes(value, bo);
                float decoded = DataConverter.ToFloat(encoded, 0, bo);
                Assert.True(
                    (decoded == value) || (float.IsNaN(decoded) && float.IsNaN(value)),
                    $"Float round-trip failed for {bo}: value={value}, got {decoded}");
            }
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        [InlineData(2.7182818284590452)]
        [InlineData(-3.1415926535897932)]
        [InlineData(double.MaxValue)]
        public void RoundTrip_Double_AllByteOrders(double value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] encoded = DataConverter.GetBytes(value, bo);
                double decoded = DataConverter.ToDouble(encoded, 0, bo);
                Assert.True(decoded == value,
                    $"Double round-trip failed for {bo}: value={value}, got {decoded}");
            }
        }

        // ── 不同字节序产生不同字节序列（防退化）──────
        // 防止有人误把所有字节序都实现成大端而通过往返测试。

        [Fact]
        public void DifferentByteOrders_Produce_DifferentByteSequences_Int32()
        {
            int value = 0x12345678;
            var big = DataConverter.GetBytes(value, Endianness.BigEndian);
            var little = DataConverter.GetBytes(value, Endianness.LittleEndian);
            var midBig = DataConverter.GetBytes(value, Endianness.MidBigEndian);
            var midLittle = DataConverter.GetBytes(value, Endianness.MidLittleEndian);

            // 大端: 12 34 56 78
            Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, big);
            // 小端: 78 56 34 12
            Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, little);
            // MidBig (BADC): 34 12 78 56
            Assert.Equal(new byte[] { 0x34, 0x12, 0x78, 0x56 }, midBig);
            // MidLittle (CDAB): 56 78 12 34
            Assert.Equal(new byte[] { 0x56, 0x78, 0x12, 0x34 }, midLittle);

            // 四者互不相同
            Assert.NotEqual(BitConverter.ToString(big), BitConverter.ToString(little));
            Assert.NotEqual(BitConverter.ToString(big), BitConverter.ToString(midBig));
            Assert.NotEqual(BitConverter.ToString(big), BitConverter.ToString(midLittle));
        }

        [Fact]
        public void DifferentByteOrders_Produce_DifferentByteSequences_Int16()
        {
            short value = 0x1234;
            Assert.Equal(new byte[] { 0x12, 0x34 }, DataConverter.GetBytes(value, Endianness.BigEndian));
            Assert.Equal(new byte[] { 0x34, 0x12 }, DataConverter.GetBytes(value, Endianness.LittleEndian));
            // 16 位时 MidBig 等价于 Little（字内字节交换），MidLittle 等价于 Big
            Assert.Equal(new byte[] { 0x34, 0x12 }, DataConverter.GetBytes(value, Endianness.MidBigEndian));
            Assert.Equal(new byte[] { 0x12, 0x34 }, DataConverter.GetBytes(value, Endianness.MidLittleEndian));
        }
    }
}
