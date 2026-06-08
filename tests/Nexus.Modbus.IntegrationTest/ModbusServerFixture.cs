using System.Net;
using System.Net.Sockets;
using Nexus.Modbus;

namespace Nexus.Modbus.IntegrationTest;

/// <summary>
/// 共享 ModbusTcpServer 生命周期：xUnit IClassFixture 在测试类所有 Fact 执行前启动，
/// 最后一个 Fact 结束后释放。端口由 OS 分配（0）避免 502 占用。
/// </summary>
public sealed class ModbusServerFixture : IDisposable
{
    public int Port { get; }
    public ModbusTcpServer Server { get; }

    public ModbusServerFixture()
    {
        // 找一个 OS 分配的空闲端口
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Server = new ModbusTcpServer(Port);
        Server.Start();

        // 预设测试数据
        Server.SetRegister(100, 1234);
        Server.SetRegister(101, 5678);
        Server.SetRegister(102, 0);
        Server.SetRegister(110, 0);
        Server.SetRegister(120, 0);
        Server.SetRegister(130, 0);
        Server.SetRegister(140, 0);
        Server.SetRegister(200, 0x1234);
        Server.SetCoil(50, true);
        Server.SetCoil(51, false);
        Server.SetCoil(60, false);
        Server.SetCoil(70, false);
        Server.SetDiscreteInput(10, true);
        Server.SetDiscreteInput(11, false);
        Server.SetInputRegister(20, 9999);
    }

    public void Dispose() => Server.Dispose();
}
