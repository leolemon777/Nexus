using System;
using System.Text;
using Nexus;
using Xunit;

namespace Nexus.Core.Tests
{
    /// <summary>
    /// PR #B1 回归测试 — 验证 4 种字节序的 IByteTransform 实现读/写正确。
    ///
    /// 测试策略:
    /// 1. <b>往返对称性</b>:GetBytes 后再 ToXxx 必须返回原值。
    /// 2. <b>已知字节流 → 已知数值</b>:固定字节 + 固定字节序 = 固定数值(交叉验证字节排布)。
    /// 3. <b>16/32 位与 DataConverter 兼容</b>:DataConverter 对 16/32 位的语义清晰,
    ///    ByteTransform 必须一致。64 位 DataConverter 自身语义存在边界不一致,
    ///    ByteTransform 用纯字内交换的自洽语义,测试不要求 64 位与 DataConverter 字节布局相同。
    /// </summary>
    public class ByteTransformTests
    {
        private static readonly Endianness[] AllOrders =
        {
            Endianness.BigEndian,
            Endianness.LittleEndian,
            Endianness.MidBigEndian,
            Endianness.MidLittleEndian,
        };

        private static IByteTransform TransformFor(Endianness bo)
            => ByteTransformFactory.ForEndianness(bo);

        // ── 往返对称性 ────────────────────────────

        [Theory]
        [InlineData((short)0)]
        [InlineData((short)1)]
        [InlineData((short)0x1234)]
        [InlineData((short)-21555)]
        [InlineData(short.MaxValue)]
        [InlineData(short.MinValue)]
        public void Int16_RoundTrip_AllByteOrders(short value)
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes(value);
                Assert.True(t.ToInt16(bytes, 0) == value,
                    $"{bo}: bytes=[{bytes[0]:X2},{bytes[1]:X2}] → 0x{(ushort)t.ToInt16(bytes, 0):X4} != 0x{(ushort)value:X4}");
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(0x12345678)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void Int32_RoundTrip_AllByteOrders(int value)
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes(value);
                Assert.Equal(value, t.ToInt32(bytes, 0));
            }
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(0x0123456789ABCDEFL)]
        [InlineData(-1L)]
        [InlineData(long.MaxValue)]
        public void Int64_RoundTrip_AllByteOrders(long value)
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes(value);
                Assert.Equal(value, t.ToInt64(bytes, 0));
            }
        }

        [Theory]
        [InlineData(0.0f)]
        [InlineData(1.0f)]
        [InlineData(3.14159f)]
        [InlineData(-2.5f)]
        [InlineData(float.MaxValue)]
        public void Float_RoundTrip_AllByteOrders(float value)
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes(value);
                Assert.Equal(value, t.ToSingle(bytes, 0));
            }
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        [InlineData(3.14159265358979)]
        [InlineData(-2.5)]
        public void Double_RoundTrip_AllByteOrders(double value)
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes(value);
                Assert.Equal(value, t.ToDouble(bytes, 0));
            }
        }

        [Theory]
        [InlineData((ushort)0)]
        [InlineData((ushort)0xABCD)]
        [InlineData(ushort.MaxValue)]
        public void UInt16_RoundTrip_AllOrders(ushort value)
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes(value);
                Assert.Equal(value, t.ToUInt16(bytes, 0));
            }
        }

        [Theory]
        [InlineData((uint)0)]
        [InlineData((uint)0xDEADBEEF)]
        [InlineData(uint.MaxValue)]
        public void UInt32_RoundTrip_AllOrders(uint value)
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes(value);
                Assert.Equal(value, t.ToUInt32(bytes, 0));
            }
        }

        [Theory]
        [InlineData((ulong)0)]
        [InlineData((ulong)0x0123456789ABCDEF)]
        [InlineData(ulong.MaxValue)]
        public void UInt64_RoundTrip_AllOrders(ulong value)
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes(value);
                Assert.Equal(value, t.ToUInt64(bytes, 0));
            }
        }

        // ── 16/32 位与 DataConverter 兼容(DataConverter 对这两个长度语义清晰)────

        [Theory]
        [InlineData((short)0)]
        [InlineData((short)0x1234)]
        [InlineData((short)-21555)]
        [InlineData(short.MaxValue)]
        public void Int16_Decode_MatchesDataConverter(short value)
        {
            foreach (var bo in AllOrders)
            {
                // 用 DataConverter.GetBytes 生成"标准字节流",然后用 ByteTransform 解读。
                byte[] bytes = DataConverter.GetBytes(value, bo);
                var t = TransformFor(bo);
                short viaT = t.ToInt16(bytes, 0);
                short viaDC = DataConverter.ToInt16(bytes, 0, bo);

                Assert.True(viaT == viaDC && viaT == value,
                    $"{bo}: bytes=[{bytes[0]:X2},{bytes[1]:X2}] T=0x{(ushort)viaT:X4} DC=0x{(ushort)viaDC:X4} expected=0x{(ushort)value:X4}");
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(0x12345678)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void Int32_Decode_MatchesDataConverter(int value)
        {
            foreach (var bo in AllOrders)
            {
                byte[] bytes = DataConverter.GetBytes(value, bo);
                var t = TransformFor(bo);
                int viaT = t.ToInt32(bytes, 0);
                int viaDC = DataConverter.ToInt32(bytes, 0, bo);

                Assert.True(viaT == viaDC && viaT == value,
                    $"{bo}: T=0x{viaT:X8} DC=0x{viaDC:X8} expected=0x{value:X8}");
            }
        }

        // ── 已知字节流: 0x12345678 ────────────────────

        /// <summary>
        /// 关键测试:固定值 0x12345678 在 4 种字节序下应产生可预测的字节布局。
        /// 这是工业协议调试题最常用的 debug 值。
        /// </summary>
        [Fact]
        public void Int32_KnownValue_KnownByteLayout_AllOrders()
        {
            const int value = 0x12345678;

            // ABCD (大端): [12, 34, 56, 78]
            var big = TransformFor(Endianness.BigEndian);
            Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, big.GetBytes(value));

            // DCBA (小端): [78, 56, 34, 12]
            var little = TransformFor(Endianness.LittleEndian);
            Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, little.GetBytes(value));
        }

        /// <summary>反向验证:固定字节布局 → 固定数值。</summary>
        [Fact]
        public void Int32_KnownBytes_KnownValue_AllOrders()
        {
            // 同一组字节 [12,34,56,78] 在不同字节序下读出不同数值。
            byte[] bytes = { 0x12, 0x34, 0x56, 0x78 };

            Assert.Equal(0x12345678, TransformFor(Endianness.BigEndian).ToInt32(bytes, 0));
            Assert.Equal(0x78563412, TransformFor(Endianness.LittleEndian).ToInt32(bytes, 0));
        }

        // ── 字符串 ─────────────────────────────────

        [Fact]
        public void String_RoundTrip_DefaultAscii()
        {
            foreach (var bo in AllOrders)
            {
                var t = TransformFor(bo);
                byte[] bytes = t.GetBytes("hello");
                Assert.Equal("hello", t.GetString(bytes, 0, bytes.Length));
            }
        }

        [Fact]
        public void String_TrimsTrailingNullAndSpace()
        {
            var t = TransformFor(Endianness.BigEndian);
            byte[] bytes = Encoding.ASCII.GetBytes("hi\0 ");
            Assert.Equal("hi", t.GetString(bytes, 0, bytes.Length));
        }

        [Fact]
        public void String_Utf8Encoding_RoundTrip()
        {
            var t = TransformFor(Endianness.BigEndian);
            const string chinese = "你好";
            byte[] bytes = t.GetBytes(chinese, Encoding.UTF8);
            Assert.Equal(chinese, t.GetString(bytes, 0, bytes.Length, Encoding.UTF8));
        }

        // ── 偏移量 ─────────────────────────────────

        [Fact]
        public void Int32_NonZeroOffset_DecodesCorrectly()
        {
            var t = TransformFor(Endianness.BigEndian);
            byte[] buffer = { 0xFF, 0xFF, 0x12, 0x34, 0x56, 0x78, 0xFF };
            Assert.Equal(0x12345678, t.ToInt32(buffer, 2));
        }

        // ── 布尔/字节 ──────────────────────────────

        [Fact]
        public void Bool_RoundTrip()
        {
            var t = TransformFor(Endianness.BigEndian);
            Assert.Equal(new byte[] { 1 }, t.GetBytes(true));
            Assert.Equal(new byte[] { 0 }, t.GetBytes(false));
            Assert.True(t.ToBool(new byte[] { 1 }, 0));
            Assert.False(t.ToBool(new byte[] { 0 }, 0));
        }

        [Fact]
        public void Byte_RoundTrip()
        {
            var t = TransformFor(Endianness.BigEndian);
            Assert.Equal(new byte[] { 0xAB }, t.GetBytes((byte)0xAB));
            Assert.Equal(0xAB, t.ToByte(new byte[] { 0xAB }, 0));
        }

        // ── 工厂与单例 ─────────────────────────────

        [Fact]
        public void Factory_ReturnsCorrectByteOrder()
        {
            Assert.Equal(Endianness.BigEndian, ByteTransformFactory.ForEndianness(Endianness.BigEndian).ByteOrder);
            Assert.Equal(Endianness.LittleEndian, ByteTransformFactory.ForEndianness(Endianness.LittleEndian).ByteOrder);
            Assert.Equal(Endianness.MidBigEndian, ByteTransformFactory.ForEndianness(Endianness.MidBigEndian).ByteOrder);
            Assert.Equal(Endianness.MidLittleEndian, ByteTransformFactory.ForEndianness(Endianness.MidLittleEndian).ByteOrder);
        }

        [Fact]
        public void Factory_ThrowsOnInvalidEndianness()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ByteTransformFactory.ForEndianness((Endianness)99));
        }

        [Fact]
        public void Singletons_AreStateless_AndReusable()
        {
            var t1 = RegularByteTransform.Instance;
            var t2 = RegularByteTransform.Instance;
            Assert.Same(t1, t2);

            Assert.Equal(0x1234, t1.ToInt16(t1.GetBytes((short)0x1234), 0));
            Assert.Equal(0x12345678, t1.ToInt32(t1.GetBytes(0x12345678), 0));
        }

        [Fact]
        public void ReverseWordTransform_RejectsInvalidEndianness()
        {
            Assert.Throws<ArgumentException>(() => new ReverseWordTransform(Endianness.BigEndian));
            Assert.Throws<ArgumentException>(() => new ReverseWordTransform(Endianness.LittleEndian));
        }

        // ── ByteOrder 标记 ─────────────────────────

        [Fact]
        public void EachTransform_ReportsCorrectByteOrder()
        {
            Assert.Equal(Endianness.BigEndian, RegularByteTransform.Instance.ByteOrder);
            Assert.Equal(Endianness.LittleEndian, ReverseBytesTransform.Instance.ByteOrder);
            Assert.Equal(Endianness.MidBigEndian, ReverseWordTransform.MidBigEndianInstance.ByteOrder);
            Assert.Equal(Endianness.MidLittleEndian, ReverseWordTransform.MidLittleEndianInstance.ByteOrder);
        }
    }
}
