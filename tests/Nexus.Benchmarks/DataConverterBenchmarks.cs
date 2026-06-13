using BenchmarkDotNet.Attributes;
using Nexus;

namespace Nexus.Benchmarks;

[MemoryDiagnoser]
public class DataConverterBenchmarks
{
    private byte[] _data = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

    [Benchmark]
    public short ToInt16_BigEndian() => DataConverter.ToInt16(_data, 0);

    [Benchmark]
    public int ToInt32_BigEndian() => DataConverter.ToInt32(_data, 0);

    [Benchmark]
    public long ToInt64_BigEndian() => DataConverter.ToInt64(_data, 0);

    [Benchmark]
    public float ToFloat_BigEndian() => DataConverter.ToFloat(_data, 0);

    [Benchmark]
    public double ToDouble_BigEndian() => DataConverter.ToDouble(_data, 0);

    [Benchmark]
    public short ToInt16_LittleEndian() => DataConverter.ToInt16(_data, 0, Endianness.LittleEndian);

    [Benchmark]
    public int ToInt32_MidLittle() => DataConverter.ToInt32(_data, 0, Endianness.MidLittleEndian);
}
