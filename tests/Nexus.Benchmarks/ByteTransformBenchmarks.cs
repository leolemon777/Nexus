using BenchmarkDotNet.Attributes;
using Nexus;

namespace Nexus.Benchmarks;

/// <summary>
/// E-5 基准测试 — 对比 IByteTransform(Phase B 新接口)vs DataConverter(旧静态方法)。
/// 验证新接口在无额外开销的前提下提供更灵活的字节序处理。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ByteTransformBenchmarks
{
    private byte[] _data16 = null!;
    private byte[] _data32 = null!;
    private byte[] _data64 = null!;
    private byte[] _hexData = null!;

    private IByteTransform _bigEndian = null!;
    private IByteTransform _littleEndian = null!;
    private IByteTransform _midBigEndian = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data16 = new byte[] { 0x12, 0x34 };
        _data32 = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        _data64 = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 };
        _hexData = new byte[64];
        for (int i = 0; i < _hexData.Length; i++) _hexData[i] = (byte)i;

        _bigEndian = RegularByteTransform.Instance;
        _littleEndian = ReverseBytesTransform.Instance;
        _midBigEndian = ReverseWordTransform.MidBigEndianInstance;
    }

    // ── DataConverter(旧)vs IByteTransform(新)大端 ──

    [Benchmark(Baseline = true)]
    public short DataConverter_ToInt16() => DataConverter.ToInt16(_data16, 0);

    [Benchmark]
    public short ByteTransform_ToInt16() => _bigEndian.ToInt16(_data16, 0);

    [Benchmark]
    public int DataConverter_ToInt32() => DataConverter.ToInt32(_data32, 0);

    [Benchmark]
    public int ByteTransform_ToInt32() => _bigEndian.ToInt32(_data32, 0);

    // ── 不同字节序(只有 IByteTransform 支持)──

    [Benchmark]
    public short ByteTransform_LittleEndian_ToInt16() => _littleEndian.ToInt16(_data16, 0);

    [Benchmark]
    public int ByteTransform_MidBigEndian_ToInt32() => _midBigEndian.ToInt32(_data32, 0);

    // ── ToHexString(优化后)──

    [Benchmark]
    public string ToHexString_8Bytes() => DataConverter.ToHexString(_data64);

    [Benchmark]
    public string ToHexString_64Bytes() => DataConverter.ToHexString(_hexData);

    // ── GetBytes ──

    [Benchmark]
    public byte[] ByteTransform_GetBytesInt32() => _bigEndian.GetBytes(0x12345678);

    [Benchmark]
    public byte[] DataConverter_GetBytesInt32() => DataConverter.GetBytes(0x12345678);
}
