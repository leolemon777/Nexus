using BenchmarkDotNet.Attributes;
using Nexus.Modbus;

namespace Nexus.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ModbusBenchmarks
{
    private ModbusTcpServer _server = null!;
    private ModbusTcpClient _client = null!;
    private int _port;

    [GlobalSetup]
    public void Setup()
    {
        _port = 15020;
        _server = new ModbusTcpServer(_port);
        _server.Start();
        _client = new ModbusTcpClient("127.0.0.1", _port);
        _client.SetPersistentConnection();
        _client.Connect();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Disconnect();
        _server.Stop();
        _server.Dispose();
        _client.Dispose();
    }

    [Benchmark]
    public short ReadInt16()
    {
        return _client.ReadInt16("0").Content;
    }

    [Benchmark]
    public int ReadInt32()
    {
        return _client.ReadInt32("0").Content;
    }

    [Benchmark]
    public float ReadFloat()
    {
        return _client.ReadFloat("0").Content;
    }

    [Benchmark]
    public void WriteInt16()
    {
        _client.Write("0", (short)123);
    }

    [Benchmark]
    public byte[] ReadBytes100()
    {
        return _client.ReadBytes("0", 100).Content;
    }
}
