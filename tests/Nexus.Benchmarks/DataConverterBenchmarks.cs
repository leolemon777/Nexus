using BenchmarkDotNet.Attributes;
using Nexus;

namespace Nexus.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class DataConverterBenchmarks
{
    private byte[] _data16 = null!;
    private byte[] _data32 = null!;
    private byte[] _data64 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data16 = new byte[] { 0x12, 0x34 };
        _data32 = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        _data64 = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 };
    }

    [Benchmark]
    public short ToInt16() => DataConverter.ToInt16(_data16, 0);

    [Benchmark]
    public int ToInt32() => DataConverter.ToInt32(_data32, 0);

    [Benchmark]
    public long ToInt64() => DataConverter.ToInt64(_data64, 0);

    [Benchmark]
    public float ToFloat() => DataConverter.ToFloat(_data32, 0);

    [Benchmark]
    public double ToDouble() => DataConverter.ToDouble(_data64, 0);

    [Benchmark]
    public byte[] GetBytesInt16() => DataConverter.GetBytes((short)12345);

    [Benchmark]
    public byte[] GetBytesFloat() => DataConverter.GetBytes(3.14f);

    [Benchmark]
    public byte[] GetBytesInt32() => DataConverter.GetBytes(12345678);

    [Benchmark]
    public string ToHexString() => DataConverter.ToHexString(_data64);

    [Benchmark]
    public short ToInt16LittleEndian() => DataConverter.ToInt16(_data16, 0, Endianness.LittleEndian);

    [Benchmark]
    public int ToInt32MidBigEndian() => DataConverter.ToInt32(_data32, 0, Endianness.MidBigEndian);
}
