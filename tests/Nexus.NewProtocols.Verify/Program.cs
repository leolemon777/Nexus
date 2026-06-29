using System;
using Nexus.Schneider.Modicon;
using Nexus.Inovance.H5u;
using Nexus.Mitsubishi.Fx5u;
using Nexus.Siemens.S7Plus;
using Nexus.Omron.NxNj;
using Nexus.EtherNetIp;
using Nexus.CcLinkIe;
using Nexus.Hart;
using Nexus.ProfinetIO;
using Nexus.Siemens.MPI;
using Nexus.BacnetIp;

int pass = 0, fail = 0;

void Test(string name, Action action)
{
    try { action(); pass++; Console.WriteLine($"  [PASS] {name}"); }
    catch (Exception ex) { fail++; Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); }
}

// ── Schneider Modicon ──────────────────
Console.WriteLine("\n=== Schneider Modicon ===");
var schneiderParser = new SchneiderModiconAddressParser();
Test("Parse %MW100", () => { var a = schneiderParser.Parse("%MW100"); if (a.StartAddress != 100) throw new Exception($"Expected 100, got {a.StartAddress}"); });
Test("Parse %MX100.5", () => { var a = schneiderParser.Parse("%MX100.5"); if (!a.IsBit || a.BitOffset != 5) throw new Exception("Bit parse failed"); });
Test("Parse %IW0", () => { var a = schneiderParser.Parse("%IW0"); if (a.ReadFunctionCode != 4) throw new Exception($"Expected FC4, got {a.ReadFunctionCode}"); });
Test("Parse %QW0", () => { var a = schneiderParser.Parse("%QW0"); if (a.ReadFunctionCode != 3) throw new Exception($"Expected FC3, got {a.ReadFunctionCode}"); });
Test("Create SchneiderModiconClient", () => { var c = new SchneiderModiconClient("127.0.0.1", 502, 1, 1000); if (c == null) throw new Exception("null"); });

// ── Inovance H5U ──────────────────
Console.WriteLine("\n=== Inovance H5U ===");
var inovanceParser = new InovanceH5uAddressParser();
Test("Parse D100", () => { var a = inovanceParser.Parse("D100"); if (a.StartAddress != 100) throw new Exception($"Expected 100, got {a.StartAddress}"); });
Test("Parse M0", () => { var a = inovanceParser.Parse("M0"); if (a.Area != InovanceArea.Coil) throw new Exception("Expected Coil"); });
Test("Parse X10", () => { var a = inovanceParser.Parse("X10"); if (a.Area != InovanceArea.DiscreteInput) throw new Exception("Expected DiscreteInput"); });
Test("Create InovanceH5uClient", () => { var c = new InovanceH5uClient("127.0.0.1", 502, 1, 1000); if (c == null) throw new Exception("null"); });

// ── Mitsubishi FX5U ──────────────────
Console.WriteLine("\n=== Mitsubishi FX5U ===");
var fx5uParser = new Fx5uAddressParser();
Test("Parse D100", () => { var a = fx5uParser.Parse("D100"); if (a.StartAddress != 100 || a.DeviceType != Fx5uDeviceType.D) throw new Exception("Parse failed"); });
Test("Parse M0", () => { var a = fx5uParser.Parse("M0"); if (a.DeviceType != Fx5uDeviceType.M) throw new Exception("Expected M"); });
Test("Parse SM100", () => { var a = fx5uParser.Parse("SM100"); if (a.DeviceType != Fx5uDeviceType.SM) throw new Exception("Expected SM"); });
Test("Create Fx5uClient", () => { var c = new Fx5uClient("127.0.0.1", 4999, 1000); if (c == null) throw new Exception("null"); });

// ── Siemens S7 Plus ──────────────────
Console.WriteLine("\n=== Siemens S7 Plus ===");
var s7plusParser = new S7PlusAddressParser();
Test("Parse DB1.DBW0", () => { var a = s7plusParser.Parse("DB1.DBW0"); if (a.DbNumber != 1 || a.StartByte != 0) throw new Exception("Parse failed"); });
Test("Parse DB1.DBX0.5", () => { var a = s7plusParser.Parse("DB1.DBX0.5"); if (a.BitOffset != 5) throw new Exception("Bit parse failed"); });
Test("Parse I0.0", () => { var a = s7plusParser.Parse("I0.0"); if (a.Area != S7PlusArea.I) throw new Exception("Expected I"); });
Test("Parse M0", () => { var a = s7plusParser.Parse("M0"); if (a.Area != S7PlusArea.M) throw new Exception("Expected M"); });
Test("Create S7PlusClient", () => { var c = new S7PlusClient("127.0.0.1", 102, 1000); if (c == null) throw new Exception("null"); });

// ── Omron NX/NJ ──────────────────
Console.WriteLine("\n=== Omron NX/NJ ===");
var omronParser = new OmronNxNjAddressParser();
Test("Parse D100", () => { var a = omronParser.Parse("D100"); if (a.WordAddress != 100 || a.AreaCode != "D") throw new Exception("Parse failed"); });
Test("Parse W0", () => { var a = omronParser.Parse("W0"); if (a.AreaCode != "W") throw new Exception("Expected W"); });
Test("Parse CIO100", () => { var a = omronParser.Parse("CIO100"); if (a.AreaCode != "CIO") throw new Exception("Expected CIO"); });
Test("Parse E0.100", () => { var a = omronParser.Parse("E0.100"); if (a.AreaCode != "E") throw new Exception("Expected E"); });
Test("Create OmronNxNjClient", () => { var c = new OmronNxNjClient("127.0.0.1", 9600, 1000); if (c == null) throw new Exception("null"); });

// ── EtherNet/IP ──────────────────
Console.WriteLine("\n=== EtherNet/IP ===");
var enipParser = new EtherNetIpAddressParser();
Test("Parse MyTag", () => { var a = enipParser.Parse("MyTag"); if (a.TagName != "MyTag") throw new Exception("Parse failed"); });
Test("Parse MyArray[0]", () => { var a = enipParser.Parse("MyArray[0]"); if (a.ArrayIndex != 0) throw new Exception("Array parse failed"); });
Test("Create EtherNetIpClient", () => { var c = new EtherNetIpClient("127.0.0.1", 44818, 1000); if (c == null) throw new Exception("null"); });

// ── CC-Link IE ──────────────────
Console.WriteLine("\n=== CC-Link IE ===");
var cclinkParser = new CcLinkIeAddressParser();
Test("Parse D100", () => { var a = cclinkParser.Parse("D100"); if (a.StartAddress != 100 || a.DeviceType != CcLinkIeDeviceType.D) throw new Exception("Parse failed"); });
Test("Parse R0", () => { var a = cclinkParser.Parse("R0"); if (a.DeviceType != CcLinkIeDeviceType.R) throw new Exception("Expected R"); });
Test("Parse WR100", () => { var a = cclinkParser.Parse("WR100"); if (a.DeviceType != CcLinkIeDeviceType.WR) throw new Exception("Expected WR"); });
Test("Create CcLinkIeClient", () => { var c = new CcLinkIeClient("127.0.0.1", 4999, 1000); if (c == null) throw new Exception("null"); });

// ── HART ──────────────────
Console.WriteLine("\n=== HART ===");
var hartParser = new HartAddressParser();
Test("Parse short address 0", () => { var a = hartParser.Parse("0"); if (!a.UseShortAddress || a.ShortAddress != 0) throw new Exception("Parse failed"); });
Test("Parse short address 15", () => { var a = hartParser.Parse("15"); if (a.ShortAddress != 15) throw new Exception("Parse failed"); });
Test("Parse long address", () => { var a = hartParser.Parse("0x1234567890"); if (a.UseShortAddress) throw new Exception("Expected long address"); });

// ── Profinet IO ──────────────────
Console.WriteLine("\n=== Profinet IO ===");
var profinetParser = new ProfinetAddressParser();
Test("Parse 0:1:0:0", () => { var a = profinetParser.Parse("0:1:0:0"); if (a.Api != 0 || a.Slot != 1 || a.Subslot != 0 || a.Offset != 0) throw new Exception("Parse failed"); });
Test("Parse 1:0", () => { var a = profinetParser.Parse("1:0"); if (a.Slot != 1 || a.Offset != 0) throw new Exception("Parse failed"); });
Test("Parse 1:0:5", () => { var a = profinetParser.Parse("1:0:5"); if (a.Slot != 1 || a.Subslot != 0 || a.Offset != 5) throw new Exception("Parse failed"); });
Test("Create ProfinetIOClient", () => { var c = new ProfinetIOClient("127.0.0.1", 34964, 1000); if (c == null) throw new Exception("null"); });

// ── Siemens MPI ──────────────────
Console.WriteLine("\n=== Siemens MPI ===");
var mpiParser = new MpiAddressParser();
Test("Parse I0.0", () => { var a = mpiParser.Parse("I0.0"); if (a.Area != MpiArea.I || a.StartByte != 0 || a.BitOffset != 0) throw new Exception("Parse failed"); });
Test("Parse Q0.5", () => { var a = mpiParser.Parse("Q0.5"); if (a.Area != MpiArea.Q || a.BitOffset != 5) throw new Exception("Parse failed"); });
Test("Parse M10", () => { var a = mpiParser.Parse("M10"); if (a.Area != MpiArea.M || a.StartByte != 10) throw new Exception("Parse failed"); });
Test("Parse DB1.DBW0", () => { var a = mpiParser.Parse("DB1.DBW0"); if (a.Area != MpiArea.DB || a.DbNumber != 1) throw new Exception("Parse failed"); });
Test("Parse DB1.DBX0.5", () => { var a = mpiParser.Parse("DB1.DBX0.5"); if (a.BitOffset != 5) throw new Exception("Parse failed"); });
Test("Parse V0", () => { var a = mpiParser.Parse("V0"); if (a.Area != MpiArea.V) throw new Exception("Expected V"); });

// ── BACnet IP ──────────────────
Console.WriteLine("\n=== BACnet IP ===");
var bacnetParser = new BacnetIpAddressParser();
Test("Parse 1001.0:0.85", () => { var a = bacnetParser.Parse("1001.0:0.85"); if (a.DeviceId != 1001 || a.ObjectType != 0 || a.PropertyId != 85) throw new Exception("Parse failed"); });
Test("Parse 1001.2:0", () => { var a = bacnetParser.Parse("1001.2:0"); if (a.DeviceId != 1001 || a.ObjectType != 2 || a.PropertyId != 85) throw new Exception("Expected default property 85"); });
Test("Parse 1:1001.0:0.85", () => { var a = bacnetParser.Parse("1:1001.0:0.85"); if (a.Network != 1 || a.DeviceId != 1001) throw new Exception("Parse failed"); });
Test("Create BacnetIpClient", () => { var c = new BacnetIpClient("127.0.0.1", 47808, 1000); if (c == null) throw new Exception("null"); });

// ── Summary ──────────────────
Console.WriteLine($"\n=== 结果: {pass} 通过, {fail} 失败 ===");
return fail;
