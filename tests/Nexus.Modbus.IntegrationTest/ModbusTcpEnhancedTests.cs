using System;
using System.Threading;
using Xunit;
using Nexus.Modbus;

namespace Nexus.Modbus.IntegrationTest;

public class ModbusTcpEnhancedTests
{
    private const int TestPortBase = 15200;

    [Fact]
    public void Server_AllowedStationIds_FilterRejectsUnknownStation()
    {
        int port = TestPortBase + 100;
        var server = new ModbusTcpServer(port);
        server.AllowedStationIds.Add(1); // 只允许站号 1
        server.SetHoldingRegister(0, 0x1234);
        server.Start();

        try
        {
            // 站号 1 应该成功
            var client1 = new ModbusTcpClient("127.0.0.1", port, station: 1);
            var conn1 = client1.Connect();
            Assert.True(conn1.IsSuccess, conn1.Message);

            var r1 = client1.ReadInt16("0");
            Assert.True(r1.IsSuccess, r1.Message);
            Assert.Equal((short)0x1234, r1.Content);
            client1.Disconnect();
            client1.Dispose();

            // 站号 2 应该被拒绝
            var client2 = new ModbusTcpClient("127.0.0.1", port, station: 2);
            var conn2 = client2.Connect();
            Assert.True(conn2.IsSuccess); // TCP 连接成功
            var r2 = client2.ReadInt16("0");
            Assert.False(r2.IsSuccess); // 但 Modbus 操作被拒绝
            client2.Disconnect();
            client2.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Server_OnRequestReceived_FiresOnEachRequest()
    {
        int port = TestPortBase + 101;
        var server = new ModbusTcpServer(port);
        server.SetHoldingRegister(0, 0x5678);

        int requestCount = 0;
        byte lastFc = 0;
        server.OnRequestReceived += (s, e) =>
        {
            System.Threading.Interlocked.Increment(ref requestCount);
            lastFc = e.FunctionCode;
        };

        server.Start();

        try
        {
            var client = new ModbusTcpClient("127.0.0.1", port, station: 1);
            client.SetPersistentConnection();
            client.Connect();

            client.ReadInt16("0");
            client.ReadInt16("0");

            Assert.Equal(2, requestCount);
            Assert.Equal((byte)0x03, lastFc); // FC03 读保持寄存器

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_RetryConnect_RetriesAndFails()
    {
        // 连接一个不存在的端口，应重试后失败
        var client = new ModbusTcpClient("127.0.0.1", 19999, station: 1, timeout: 500);
        client.RetryCount = 2;
        client.RetryInterval = 100;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = client.Connect();
        sw.Stop();

        Assert.False(result.IsSuccess);
        // 至少重试了 2 次，总耗时应 > 100ms
        Assert.True(sw.ElapsedMilliseconds >= 100, $"Expected >= 100ms, got {sw.ElapsedMilliseconds}ms");
        client.Dispose();
    }

    [Fact]
    public void Client_SendCustomModbus_ReadHoldingRegisters()
    {
        int port = TestPortBase + 102;
        var server = new ModbusTcpServer(port);
        server.SetHoldingRegister(0, 0xABCD);
        server.Start();

        try
        {
            var client = new ModbusTcpClient("127.0.0.1", port, station: 1);
            client.SetPersistentConnection();
            client.Connect();

            // 手动构建 FC03 PDU: FC(1) + Addr(2) + Count(2)
            byte[] pdu = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01 };
            var result = client.SendCustomModbus(pdu);
            Assert.True(result.IsSuccess, result.Message);
            // 响应 PDU: FC(1) + ByteCount(1) + Data(2)
            Assert.Equal((byte)0x03, result.Content[0]); // FC03
            Assert.Equal((byte)0x02, result.Content[1]); // 2 bytes
            Assert.Equal((byte)0xAB, result.Content[2]); // 高字节
            Assert.Equal((byte)0xCD, result.Content[3]); // 低字节

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_DataMonitoring_TriggersOnDataChange()
    {
        int port = TestPortBase + 103;
        var server = new ModbusTcpServer(port);
        server.SetHoldingRegister(0, 100);
        server.Start();

        try
        {
            var client = new ModbusTcpClient("127.0.0.1", port, station: 1);
            client.SetPersistentConnection();
            client.Connect();

            // 确认初始值
            Assert.Equal((short)100, client.ReadInt16("0").Content);

            var mre = new ManualResetEventSlim();
            DataChangeEventArgs? changeEvent = null;
            client.OnDataChanged += (s, e) => { changeEvent = e; mre.Set(); };

            client.AddMonitor("0", dataType: "Int16");
            client.StartMonitoring(pollIntervalMs: 50);

            // 等待基线轮询完成（首次轮询只初始化基线，不触发事件）
            Thread.Sleep(300);

            // 改变数据
            server.SetHoldingRegister(0, 200);

            // 使用事件等待替代固定 Sleep，避免竞态条件
            bool detected = mre.Wait(TimeSpan.FromSeconds(3));

            client.StopMonitoring();
            client.Disconnect();
            client.Dispose();

            Assert.True(detected, "Data change event was not triggered within timeout");
            Assert.Equal("0", changeEvent!.Address);
            Assert.Equal((short)100, changeEvent.OldValue);
            Assert.Equal((short)200, changeEvent.NewValue);
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void Client_StringEncoding_Utf8ReadWrite()
    {
        int port = TestPortBase + 104;
        var server = new ModbusTcpServer(port);
        server.Start();

        try
        {
            var client = new ModbusTcpClient("127.0.0.1", port, station: 1);
            client.SetPersistentConnection();
            client.StringEncodingOption = StringEncoding.Utf8;
            client.Connect();

            // 写入 UTF-8 字符串
            var writeResult = client.WriteStringEncoded("0", "Hello");
            Assert.True(writeResult.IsSuccess, writeResult.Message);

            // 读回
            var readResult = client.ReadStringEncoded("0", 5);
            Assert.True(readResult.IsSuccess, readResult.Message);
            Assert.Equal("Hello", readResult.Content);

            client.Disconnect();
            client.Dispose();
        }
        finally
        {
            server.Stop();
            server.Dispose();
        }
    }
}
