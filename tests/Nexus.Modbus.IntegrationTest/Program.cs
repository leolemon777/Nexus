using System;
using Nexus;
using Nexus.Modbus;

Console.WriteLine("=== Nexus Modbus TCP Full Feature Test ===\n");

var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
listener.Start();
int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();

using var server = new ModbusTcpServer(port);
server.Start();

server.SetRegister(100, 1234);
server.SetRegister(101, 5678);
server.SetRegister(200, 0x1234);
server.SetCoil(50, true);
server.SetCoil(51, false);
server.SetDiscreteInput(10, true);
server.SetDiscreteInput(11, false);
server.SetInputRegister(20, 9999);

using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);
client.SetPersistentConnection();

int passed = 0, failed = 0;
void T(string name, Func<bool> fn) { try { if (fn()) { Console.WriteLine($"  OK {name}"); passed++; } else { Console.WriteLine($"  FAIL {name}"); failed++; } } catch (Exception ex) { Console.WriteLine($"  ERR {name}: {ex.Message}"); failed++; } }

Console.WriteLine("-- Connection & Events --");
bool connectedFired = false, msgSent = false;
client.OnConnected += (_, _) => connectedFired = true;
client.OnMessageSent += (_, _) => msgSent = true;
T("Connect succeeds", () => client.Connect().IsSuccess);
T("IsConnected", () => client.IsConnected);
T("OnConnected event fired", () => connectedFired);

Console.WriteLine("\n-- FC03 Read Holding Registers --");
T("D100 == 1234", () => client.ReadInt16("100").Content == 1234);
T("D101 == 5678", () => client.ReadInt16("101").Content == 5678);
T("D200 UInt16 == 0x1234", () => client.ReadUInt16("200").Content == 0x1234);
T("40100 prefix == D100", () => client.ReadInt16("40100").Content == 1234);

Console.WriteLine("\n-- FC04 Read Input Registers --");
T("Input 30020 == 9999", () => client.ReadInt16("30020").Content == 9999);
T("30020 (5-digit prefix) == 9999", () => client.ReadInt16("30020").Content == 9999);

Console.WriteLine("\n-- FC01 Read Coils --");
T("Coil50 == true", () => client.ReadBool("50").Content == true);
T("Coil51 == false", () => client.ReadBool("51").Content == false);
T("050 prefix == Coil50", () => client.ReadBool("050").Content == true);

Console.WriteLine("\n-- FC02 Read Discrete Inputs --");
T("10010 (5-digit DI prefix) == true", () => client.ReadBool("10010").Content == true);
T("DI111 == false", () => client.ReadBool("111").Content == false);

Console.WriteLine("\n-- FC06 Write Single Register --");
T("Write D102 = -9999", () => client.Write("102", (short)-9999).IsSuccess);
T("Read D102 = -9999", () => client.ReadInt16("102").Content == -9999);

Console.WriteLine("\n-- FC05 Write Single Coil --");
T("Write Coil60 = true", () => client.Write("60", true).IsSuccess);
T("Read Coil60 = true", () => client.ReadBool("60").Content == true);
T("Write Coil60 = false", () => client.Write("60", false).IsSuccess);
T("Read Coil60 = false", () => client.ReadBool("60").Content == false);

Console.WriteLine("\n-- FC16 Write Multiple Registers --");
T("Write D110 Int32 = 100000", () => client.Write("110", 100000).IsSuccess);
T("Read D110 = 100000", () => client.ReadInt32("110").Content == 100000);
T("Write D120 Float = 3.14", () => client.Write("120", 3.14f).IsSuccess);
T("Read D120 ~ 3.14", () => Math.Abs(client.ReadFloat("120").Content - 3.14f) < 0.001f);

Console.WriteLine("\n-- FC15 Write Multiple Coils --");
T("WriteMultiCoils [T,F,T]", () => client.WriteMultipleCoils(70, new bool[] { true, false, true }).IsSuccess);
var coils = client.ReadBools("70", 3);
T("ReadBools [T,F,T]", () => coils.Content[0] && !coils.Content[1] && coils.Content[2]);

Console.WriteLine("\n-- Batch Read --");
T("ReadRegistersBatch 2 regs", () => client.ReadRegistersBatch(100, 2).Content.Length == 4);

Console.WriteLine("\n-- FC23 Read/Write Multiple --");
T("ReadWriteMultiple atomic", () =>
{
    client.Write("130", (short)0);
    var writeData = DataConverter.GetBytes((short)42);
    var rw = client.ReadWriteMultipleRegisters(100, 1, 130, writeData);
    return rw.IsSuccess && rw.Content.Length == 2
        && DataConverter.ToInt16(rw.Content, 0) == 1234
        && server.GetRegister(130) == 42;
});

Console.WriteLine("\n-- Endianness --");
client.ByteOrder = Endianness.LittleEndian;
T("LittleEndian roundtrip", () => { client.Write("140", (short)0x1234); return client.ReadInt16("140").Content == 0x1234; });
client.ByteOrder = Endianness.BigEndian;
T("BigEndian roundtrip", () => { client.Write("140", (short)0x1234); return client.ReadInt16("140").Content == 0x1234; });

Console.WriteLine("\n-- Custom Message --");
T("SendCustomMessage raw FC03", () =>
{
    byte[] msg = new byte[] { 0, 1, 0, 0, 0, 6, 1, 0x03, 0, 100, 0, 1 };
    var r = client.SendCustomMessage(msg);
    return r.IsSuccess && r.Content.Length >= 11;
});

Console.WriteLine("\n-- Events & Logging --");
T("OnMessageSent fired", () => msgSent);

Console.WriteLine("\n-- OperateResult --");
T("Success IsSuccess", () => Nexus.OperateResult.Success().IsSuccess);
T("Failed !IsSuccess", () => !Nexus.OperateResult.Failed("err").IsSuccess);
T("Failed message", () => Nexus.OperateResult.Failed("err").Message == "err");

Console.WriteLine($"\n=== {passed} passed, {failed} failed ===");
server.Stop();
if (failed > 0) Environment.ExitCode = 1;
