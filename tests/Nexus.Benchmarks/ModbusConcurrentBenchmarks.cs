using BenchmarkDotNet.Attributes;
using Nexus.Modbus;

namespace Nexus.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ModbusConcurrentBenchmarks
{
    private ModbusTcpServer _server = null!;
    private ModbusTcpConnectionPool _pool = null!;
    private int _port;

    [Params(1, 4, 8)]
    public int ThreadCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _port = 15021;
        _server = new ModbusTcpServer(_port);
        _server.SetHoldingRegister(0, 12345);
        _server.Start();
        _pool = new ModbusTcpConnectionPool("127.0.0.1", _port, maxPoolSize: 8);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    [Benchmark]
    public void ConcurrentReadInt16()
    {
        var tasks = new Task[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                    _pool.ReadInt16("40001");
            });
        }
        Task.WaitAll(tasks);
    }

    [Benchmark]
    public void ConcurrentReadWrite()
    {
        var tasks = new Task[ThreadCount];
        for (int t = 0; t < ThreadCount; t++)
        {
            int tid = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    _pool.Write("40001", (short)(tid * 1000 + i));
                    _pool.ReadInt16("40001");
                }
            });
        }
        Task.WaitAll(tasks);
    }
}
