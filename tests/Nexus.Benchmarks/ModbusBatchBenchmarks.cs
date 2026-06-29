using BenchmarkDotNet.Attributes;
using Nexus.Modbus;

namespace Nexus.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ModbusBatchBenchmarks
{
    private ModbusTcpServer _server = null!;
    private ModbusTcpClient _client = null!;
    private int _port;
    private string[] _addresses10 = null!;
    private string[] _addresses50 = null!;
    private string[] _addresses100 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _port = 15022;
        _server = new ModbusTcpServer(_port);
        for (int i = 0; i < 200; i++)
            _server.SetHoldingRegister((ushort)i, (ushort)(i * 10));
        _server.Start();

        _client = new ModbusTcpClient("127.0.0.1", _port);
        _client.SetPersistentConnection();
        _client.Connect();

        _addresses10 = Enumerable.Range(0, 10).Select(i => $"4000{i + 1}").ToArray();
        _addresses50 = Enumerable.Range(0, 50).Select(i => $"4000{i + 1}").ToArray();
        _addresses100 = Enumerable.Range(0, 100).Select(i => $"4000{i + 1}").ToArray();
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
    public void BatchRead10()
    {
        _client.BatchRead(_addresses10);
    }

    [Benchmark]
    public void BatchRead50()
    {
        _client.BatchRead(_addresses50);
    }

    [Benchmark]
    public void BatchRead100()
    {
        _client.BatchRead(_addresses100);
    }

    [Benchmark]
    public void SequentialRead10()
    {
        for (int i = 0; i < 10; i++)
            _client.ReadInt16($"4000{i + 1}");
    }
}
