using Nexus;
using Nexus.Modbus;

Console.WriteLine("=== 数据采集 + CSV 日志示例 ===\n");

// 1. 创建 Modbus 客户端
using var client = new ModbusTcpClient("127.0.0.1", port: 502, station: 1);
client.SetPersistentConnection();
client.AutoReconnect = true;

Console.WriteLine("正在连接 ...");
var result = client.Connect();
if (!result.IsSuccess) { Console.WriteLine($"连接失败: {result.Message}"); return; }
Console.WriteLine("连接成功!\n");

// 2. 创建数据采集引擎
using var engine = new DataAcquisitionEngine();

// 3. 添加数据接收器（控制台输出）
using var sink = new ConsoleDataSink();
engine.AddSink(sink);

// 4. 注册设备
var config = new PollConfig { IntervalMs = 1000 };
engine.RegisterDevice("plc1", client, config);

// 5. 添加采集点
engine.AddPoint("plc1", "40001", "Int16", tag: "temperature");
engine.AddPoint("plc1", "40003", "Float", tag: "pressure");
engine.AddPoint("plc1", "00001", "Bool",  tag: "motor_running");

Console.WriteLine("已添加 3 个采集点:");
Console.WriteLine("  temperature (40001) - Int16");
Console.WriteLine("  pressure    (40003) - Float");
Console.WriteLine("  motor_running (00001) - Bool\n");

// 6. 监听数据变化事件
engine.OnSample += (s, e) =>
    Console.WriteLine($"  [事件] {e.Sample.Tag} = {e.Sample.Value} ({e.Sample.Quality})");

// 7. 启动采集
engine.Start();
Console.WriteLine("采集已启动，运行 10 秒 ...\n");

await Task.Delay(10000);

// 8. 停止采集
engine.Stop();
Console.WriteLine("\n采集已停止");

// 9. 导出 CSV
engine.ExportToCsv("data_log.csv");
Console.WriteLine("已导出 data_log.csv");

client.Disconnect();
Console.WriteLine("已断开连接");
