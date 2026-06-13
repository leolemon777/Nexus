using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nexus.VirtualPlc;
using Xunit;

namespace Nexus.VirtualPlc.Tests
{
    public class VirtualPlcMemoryTests
    {
        // ── Bool 操作 ─────────────────────────────

        [Fact]
        public void GetBool_DefaultFalse()
        {
            using var mem = new VirtualPlcMemory();
            Assert.False(mem.GetBool(0));
        }

        [Fact]
        public void SetBool_True()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetBool(100, true);
            Assert.True(mem.GetBool(100));
        }

        [Fact]
        public void SetBool_Overwrite()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetBool(0, true);
            mem.SetBool(0, false);
            Assert.False(mem.GetBool(0));
        }

        // ── Int16 操作 ───────────────────────────

        [Fact]
        public void GetInt16_DefaultZero()
        {
            using var mem = new VirtualPlcMemory();
            Assert.Equal((short)0, mem.GetInt16(0));
        }

        [Fact]
        public void SetInt16_Positive()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(100, 1234);
            Assert.Equal((short)1234, mem.GetInt16(100));
        }

        [Fact]
        public void SetInt16_Negative()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, -1000);
            Assert.Equal((short)-1000, mem.GetInt16(0));
        }

        // ── UInt16 操作 ──────────────────────────

        [Fact]
        public void SetUInt16_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetUInt16(50, 50000);
            Assert.Equal((ushort)50000, mem.GetUInt16(50));
        }

        // ── Int32 操作 ───────────────────────────

        [Fact]
        public void SetInt32_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt32(0, 123456);
            Assert.Equal(123456, mem.GetInt32(0));
        }

        [Fact]
        public void SetInt32_Negative()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt32(0, -99999);
            Assert.Equal(-99999, mem.GetInt32(0));
        }

        // ── Float 操作 ───────────────────────────

        [Fact]
        public void SetFloat_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetFloat(0, 25.5f);
            Assert.Equal(25.5f, mem.GetFloat(0), 0.001f);
        }

        [Fact]
        public void SetFloat_Negative()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetFloat(10, -3.14f);
            Assert.Equal(-3.14f, mem.GetFloat(10), 0.001f);
        }

        // ── Double 操作 ──────────────────────────

        [Fact]
        public void SetDouble_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetDouble(0, 123.456789);
            Assert.Equal(123.456789, mem.GetDouble(0), 0.000001);
        }

        // ── Int64 操作 ───────────────────────────

        [Fact]
        public void SetInt64_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt64(0, 1234567890123L);
            Assert.Equal(1234567890123L, mem.GetInt64(0));
        }

        // ── 批量操作 ─────────────────────────────

        [Fact]
        public void GetBools_Range()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetBool(0, true);
            mem.SetBool(1, false);
            mem.SetBool(2, true);
            var result = mem.GetBools(0, 3);
            Assert.True(result[0]);
            Assert.False(result[1]);
            Assert.True(result[2]);
        }

        [Fact]
        public void SetBools_Batch()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetBools(10, new bool[] { true, true, false });
            Assert.True(mem.GetBool(10));
            Assert.True(mem.GetBool(11));
            Assert.False(mem.GetBool(12));
        }

        [Fact]
        public void GetInt16s_Range()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, 10);
            mem.SetInt16(1, 20);
            mem.SetInt16(2, 30);
            var result = mem.GetInt16s(0, 3);
            Assert.Equal(new short[] { 10, 20, 30 }, result);
        }

        [Fact]
        public void SetInt16s_Batch()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16s(50, new short[] { 100, 200, 300 });
            Assert.Equal((short)100, mem.GetInt16(50));
            Assert.Equal((short)200, mem.GetInt16(51));
            Assert.Equal((short)300, mem.GetInt16(52));
        }

        // ── 字节数组 ─────────────────────────────

        [Fact]
        public void SetBytes_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetBytes(0, new byte[] { 0x12, 0x34, 0xAB, 0xCD });
            Assert.Equal((short)0x1234, mem.GetInt16(0));
            Assert.Equal(unchecked((short)0xABCD), mem.GetInt16(1));

            var bytes = mem.GetBytes(0, 2);
            Assert.Equal(new byte[] { 0x12, 0x34, 0xAB, 0xCD }, bytes);
        }

        // ── 清除 ─────────────────────────────────

        [Fact]
        public void Clear_All()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, 100);
            mem.SetBool(0, true);
            mem.Clear();
            Assert.Equal((short)0, mem.GetInt16(0));
            Assert.False(mem.GetBool(0));
        }

        [Fact]
        public void ClearRegisters_Range()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, 10);
            mem.SetInt16(1, 20);
            mem.SetInt16(2, 30);
            mem.ClearRegisters(0, 2);
            Assert.Equal((short)0, mem.GetInt16(0));
            Assert.Equal((short)0, mem.GetInt16(1));
            Assert.Equal((short)30, mem.GetInt16(2));
        }

        [Fact]
        public void ClearCoils_Range()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetBool(5, true);
            mem.SetBool(6, true);
            mem.ClearCoils(5, 1);
            Assert.False(mem.GetBool(5));
            Assert.True(mem.GetBool(6));
        }

        // ── 事件 ─────────────────────────────────

        [Fact]
        public void OnWrite_FiredOnSetInt16()
        {
            using var mem = new VirtualPlcMemory();
            VirtualPlcWriteEventArgs? args = null;
            mem.OnWrite += (_, e) => args = e;
            mem.SetInt16(42, 999);
            Assert.NotNull(args);
            Assert.Equal(42, args!.Address);
            Assert.Equal(VirtualPlcDataType.Int16, args.DataType);
        }

        [Fact]
        public void OnWrite_FiredOnSetBool()
        {
            using var mem = new VirtualPlcMemory();
            VirtualPlcWriteEventArgs? args = null;
            mem.OnWrite += (_, e) => args = e;
            mem.SetBool(7, true);
            Assert.NotNull(args);
            Assert.Equal(7, args!.Address);
            Assert.Equal(VirtualPlcDataType.Bool, args.DataType);
        }

        // ── Dispose ──────────────────────────────

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var mem = new VirtualPlcMemory();
            mem.Dispose();
            mem.Dispose();
        }
    }

    public class ScenarioScriptTests
    {
        [Fact]
        public void BuiltIn_TemperatureSensor_HasData()
        {
            var scenario = BuiltInScenarios.TemperatureSensor();
            Assert.Equal("温度传感器", scenario.Name);
            Assert.Equal(3, scenario.RegisterPresets.Count);
            Assert.Single(scenario.CoilPresets);
            Assert.Single(scenario.Rules);
        }

        [Fact]
        public void BuiltIn_MotorControl_HasData()
        {
            var scenario = BuiltInScenarios.MotorControl();
            Assert.Equal("电机控制", scenario.Name);
            Assert.Equal(3, scenario.RegisterPresets.Count);
            Assert.Equal(3, scenario.CoilPresets.Count);
        }

        [Fact]
        public void BuiltIn_ConveyorBelt_HasData()
        {
            var scenario = BuiltInScenarios.ConveyorBelt();
            Assert.Equal("传送带", scenario.Name);
            Assert.Equal(3, scenario.RegisterPresets.Count);
            Assert.Equal(2, scenario.CoilPresets.Count);
        }

        [Fact]
        public void BuiltIn_Blank_IsEmpty()
        {
            var scenario = BuiltInScenarios.Blank();
            Assert.Equal("空白", scenario.Name);
            Assert.Empty(scenario.RegisterPresets);
            Assert.Empty(scenario.CoilPresets);
            Assert.Empty(scenario.Rules);
        }

        [Fact]
        public void Apply_SetsRegisters()
        {
            var scenario = BuiltInScenarios.TemperatureSensor();
            using var mem = new VirtualPlcMemory();
            scenario.Apply(mem);
            Assert.Equal((short)250, mem.GetInt16(0));
            Assert.Equal((short)500, mem.GetInt16(1));
        }

        [Fact]
        public void Apply_SetsCoils()
        {
            var scenario = BuiltInScenarios.TemperatureSensor();
            using var mem = new VirtualPlcMemory();
            scenario.Apply(mem);
            Assert.False(mem.GetBool(0));
        }

        [Fact]
        public void Apply_ClearsFirst()
        {
            var scenario = BuiltInScenarios.Blank();
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(999, 123);
            scenario.Apply(mem);
            Assert.Equal((short)0, mem.GetInt16(999));
        }

        [Fact]
        public void Apply_NullMemory_Throws()
        {
            var scenario = BuiltInScenarios.Blank();
            Assert.Throws<ArgumentNullException>(() => scenario.Apply(null!));
        }

        [Fact]
        public void RegisterPreset_Defaults()
        {
            var preset = new RegisterPreset();
            Assert.Equal(0, preset.Address);
            Assert.Equal((short)0, preset.Value);
            Assert.Equal("", preset.Comment);
        }

        [Fact]
        public void CoilPreset_Defaults()
        {
            var preset = new CoilPreset();
            Assert.Equal(0, preset.Address);
            Assert.False(preset.Value);
            Assert.Equal("", preset.Comment);
        }

        [Fact]
        public void ScenarioRule_Defaults()
        {
            var rule = new ScenarioRule();
            Assert.Equal("", rule.Name);
            Assert.Equal(0, rule.WatchAddress);
            Assert.Equal(0, rule.TriggerValue);
            Assert.Equal(0, rule.TargetAddress);
            Assert.Equal((short)0, rule.TargetValue);
            Assert.False(rule.IsBoolTarget);
            Assert.Equal(0, rule.DelayMs);
        }
    }

    public class VirtualPlcRuleEngineTests
    {
        [Fact]
        public void Constructor_NullMemory_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new VirtualPlcRuleEngine(null!));
        }

        [Fact]
        public void AddRule_IncrementsCount()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);
            Assert.Equal(0, engine.RuleCount);
            engine.AddRule(new ScenarioRule { Name = "R1" });
            Assert.Equal(1, engine.RuleCount);
        }

        [Fact]
        public void RemoveRule_Existing()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);
            engine.AddRule(new ScenarioRule { Name = "R1" });
            Assert.True(engine.RemoveRule("R1"));
            Assert.Equal(0, engine.RuleCount);
        }

        [Fact]
        public void RemoveRule_NonExisting()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);
            Assert.False(engine.RemoveRule("missing"));
        }

        [Fact]
        public void ClearRules()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);
            engine.AddRule(new ScenarioRule { Name = "R1" });
            engine.AddRule(new ScenarioRule { Name = "R2" });
            engine.ClearRules();
            Assert.Equal(0, engine.RuleCount);
        }

        [Fact]
        public void AddRule_Null_Throws()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);
            Assert.Throws<ArgumentNullException>(() => engine.AddRule(null!));
        }

        [Fact]
        public void EvaluateNow_TriggerValueMatches_FiresAction()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);

            var rule = new ScenarioRule
            {
                Name = "SetD10",
                WatchAddress = 0,
                TriggerValue = 100,
                TargetAddress = 10,
                TargetValue = 999,
                IsBoolTarget = false
            };
            engine.AddRule(rule);

            var fired = new List<RuleFiredEventArgs>();
            engine.OnRuleFired += (_, e) => fired.Add(e);

            // Set watch address to trigger value
            mem.SetInt16(0, 100);
            engine.EvaluateNow();

            Assert.Single(fired);
            Assert.Equal("SetD10", fired[0].RuleName);
            Assert.Equal(10, fired[0].TargetAddress);
            Assert.Equal((short)999, mem.GetInt16(10));
        }

        [Fact]
        public void EvaluateNow_TriggerValueMismatch_NoAction()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);

            var rule = new ScenarioRule
            {
                Name = "R1",
                WatchAddress = 0,
                TriggerValue = 100,
                TargetAddress = 10,
                TargetValue = 999
            };
            engine.AddRule(rule);

            mem.SetInt16(0, 50); // wrong value
            engine.EvaluateNow();

            Assert.Equal((short)0, mem.GetInt16(10));
            Assert.Equal(0, engine.FireCount);
        }

        [Fact]
        public void EvaluateNow_BoolTarget()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);

            var rule = new ScenarioRule
            {
                Name = "SetBool",
                WatchAddress = 0,
                TriggerValue = 1,
                TargetAddress = 5,
                TargetValue = 1,
                IsBoolTarget = true
            };
            engine.AddRule(rule);

            mem.SetInt16(0, 1);
            engine.EvaluateNow();

            Assert.True(mem.GetBool(5));
            Assert.Equal(1, engine.FireCount);
        }

        [Fact]
        public void EvaluateNow_AnyChangeTrigger()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);

            var rule = new ScenarioRule
            {
                Name = "OnAnyChange",
                WatchAddress = 0,
                TriggerValue = -1, // any change
                TargetAddress = 20,
                TargetValue = 777
            };
            engine.AddRule(rule);

            mem.SetInt16(0, 42);
            engine.EvaluateNow(); // first time: change detected (from int.MinValue)
            Assert.Equal((short)777, mem.GetInt16(20));

            mem.SetInt16(20, 0); // reset target
            engine.EvaluateNow(); // no change since last eval
            Assert.Equal((short)0, mem.GetInt16(20));

            mem.SetInt16(0, 99); // change!
            engine.EvaluateNow();
            Assert.Equal((short)777, mem.GetInt16(20));
        }

        [Fact]
        public void LoadFromScenario_AddsRules()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);

            var scenario = new ScenarioScript
            {
                Name = "Test",
                Rules = new List<ScenarioRule>
                {
                    new ScenarioRule { Name = "R1" },
                    new ScenarioRule { Name = "R2" }
                }
            };

            engine.LoadFromScenario(scenario);
            Assert.Equal(2, engine.RuleCount);
        }

        [Fact]
        public void LoadFromScenario_Null_Throws()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);
            Assert.Throws<ArgumentNullException>(() => engine.LoadFromScenario(null!));
        }

        [Fact]
        public void StartStop_DoesNotThrow()
        {
            using var mem = new VirtualPlcMemory();
            using var engine = new VirtualPlcRuleEngine(mem);
            engine.Start();
            Thread.Sleep(50);
            engine.Stop();
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var mem = new VirtualPlcMemory();
            var engine = new VirtualPlcRuleEngine(mem);
            engine.Dispose();
            engine.Dispose();
            mem.Dispose();
        }
    }

    public class VirtualPlcHostTests
    {
        [Fact]
        public void Constructor_SetsName()
        {
            using var host = new VirtualPlcHost("TestPLC");
            Assert.Equal("TestPLC", host.Name);
            Assert.False(host.IsRunning);
        }

        [Fact]
        public void Constructor_DefaultName()
        {
            using var host = new VirtualPlcHost();
            Assert.Equal("VirtualPlc", host.Name);
        }

        [Fact]
        public void Constructor_NullName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new VirtualPlcHost(null!));
        }

        [Fact]
        public void StartStop()
        {
            using var host = new VirtualPlcHost();
            host.Start();
            Assert.True(host.IsRunning);
            host.Stop();
            Assert.False(host.IsRunning);
        }

        [Fact]
        public void LoadScenario_AppliesData()
        {
            using var host = new VirtualPlcHost();
            var scenario = BuiltInScenarios.TemperatureSensor();
            host.LoadScenario(scenario);
            Assert.Equal((short)250, host.Memory.GetInt16(0));
            Assert.Equal("温度传感器", host.CurrentScenario?.Name);
        }

        [Fact]
        public void LoadScenario_Null_Throws()
        {
            using var host = new VirtualPlcHost();
            Assert.Throws<ArgumentNullException>(() => host.LoadScenario(null!));
        }

        [Fact]
        public void Reset_RestoresInitial()
        {
            using var host = new VirtualPlcHost();
            var scenario = BuiltInScenarios.TemperatureSensor();
            host.LoadScenario(scenario);

            host.Memory.SetInt16(0, 0); // modify
            host.Reset();
            Assert.Equal((short)250, host.Memory.GetInt16(0));
        }

        [Fact]
        public void Reset_NoScenario_ClearsMemory()
        {
            using var host = new VirtualPlcHost();
            host.Memory.SetInt16(0, 999);
            host.Reset();
            Assert.Equal((short)0, host.Memory.GetInt16(0));
        }

        [Fact]
        public void GetSnapshot_ReturnsState()
        {
            using var host = new VirtualPlcHost("MyPLC");
            host.LoadScenario(BuiltInScenarios.MotorControl());
            var snap = host.GetSnapshot();
            Assert.Equal("MyPLC", snap.HostName);
            Assert.Equal("电机控制", snap.ScenarioName);
            Assert.Equal(0, snap.RuleCount); // MotorControl has no rules with triggers
        }

        [Fact]
        public void Snapshot_ToString()
        {
            using var host = new VirtualPlcHost("PLC1");
            var snap = host.GetSnapshot();
            Assert.Contains("PLC1", snap.ToString());
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var host = new VirtualPlcHost();
            host.Dispose();
            host.Dispose();
        }

        [Fact]
        public void Memory_Accessible()
        {
            using var host = new VirtualPlcHost();
            Assert.NotNull(host.Memory);
            host.Memory.SetInt16(0, 42);
            Assert.Equal((short)42, host.Memory.GetInt16(0));
        }

        [Fact]
        public void Engine_Accessible()
        {
            using var host = new VirtualPlcHost();
            Assert.NotNull(host.Engine);
            Assert.Equal(0, host.Engine.RuleCount);
        }
    }

    public class VirtualPlcWriteEventArgsTests
    {
        [Fact]
        public void Constructor_SetsProperties()
        {
            var args = new VirtualPlcWriteEventArgs(100, 42, VirtualPlcDataType.Int16);
            Assert.Equal(100, args.Address);
            Assert.Equal(42, args.Value);
            Assert.Equal(VirtualPlcDataType.Int16, args.DataType);
            Assert.True(args.Timestamp <= DateTime.Now);
            Assert.True(args.Timestamp > DateTime.Now.AddSeconds(-1));
        }
    }

    public class RuleFiredEventArgsTests
    {
        [Fact]
        public void Constructor_SetsProperties()
        {
            var args = new RuleFiredEventArgs("TestRule", 50);
            Assert.Equal("TestRule", args.RuleName);
            Assert.Equal(50, args.TargetAddress);
            Assert.True(args.Timestamp <= DateTime.Now);
        }
    }

    public class VirtualPlcSnapshotTests
    {
        [Fact]
        public void Defaults()
        {
            var snap = new VirtualPlcSnapshot();
            Assert.Equal("", snap.HostName);
            Assert.Equal("", snap.ScenarioName);
            Assert.False(snap.IsRunning);
            Assert.Equal(0, snap.RuleCount);
            Assert.Equal(0, snap.FireCount);
        }
    }

    public class ModbusMemoryTests
    {
        [Fact]
        public void SetHoldingRegister_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetHoldingRegister(100, 12345);
            Assert.Equal((ushort)12345, mem.GetHoldingRegister(100));
        }

        [Fact]
        public void GetHoldingRegister_DefaultZero()
        {
            using var mem = new VirtualPlcMemory();
            Assert.Equal((ushort)0, mem.GetHoldingRegister(0));
        }

        [Fact]
        public void SetCoil_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetCoil(50, true);
            Assert.True(mem.GetCoil(50));
            mem.SetCoil(50, false);
            Assert.False(mem.GetCoil(50));
        }

        [Fact]
        public void GetCoil_DefaultFalse()
        {
            using var mem = new VirtualPlcMemory();
            Assert.False(mem.GetCoil(0));
        }

        [Fact]
        public void SetInputRegister_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInputRegister(200, 9999);
            Assert.Equal((ushort)9999, mem.GetInputRegister(200));
        }

        [Fact]
        public void GetInputRegister_DefaultZero()
        {
            using var mem = new VirtualPlcMemory();
            Assert.Equal((ushort)0, mem.GetInputRegister(0));
        }

        [Fact]
        public void SetDiscreteInput_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetDiscreteInput(10, true);
            Assert.True(mem.GetDiscreteInput(10));
        }

        [Fact]
        public void GetDiscreteInput_DefaultFalse()
        {
            using var mem = new VirtualPlcMemory();
            Assert.False(mem.GetDiscreteInput(0));
        }

        [Fact]
        public void Clear_ClearsModbusAreas()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetHoldingRegister(0, 100);
            mem.SetCoil(0, true);
            mem.SetInputRegister(0, 200);
            mem.SetDiscreteInput(0, true);
            mem.Clear();
            Assert.Equal((ushort)0, mem.GetHoldingRegister(0));
            Assert.False(mem.GetCoil(0));
            Assert.Equal((ushort)0, mem.GetInputRegister(0));
            Assert.False(mem.GetDiscreteInput(0));
        }

        [Fact]
        public void MultipleAddresses_Independent()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetHoldingRegister(0, 111);
            mem.SetHoldingRegister(1, 222);
            mem.SetHoldingRegister(2, 333);
            Assert.Equal((ushort)111, mem.GetHoldingRegister(0));
            Assert.Equal((ushort)222, mem.GetHoldingRegister(1));
            Assert.Equal((ushort)333, mem.GetHoldingRegister(2));
        }
    }

    public class S7MemoryTests
    {
        [Fact]
        public void SetDbValue_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            mem.SetDbValue(1, 0, data);
            var result = mem.GetDbValue(1, 0, 4);
            Assert.Equal(data, result);
        }

        [Fact]
        public void GetDbValue_DefaultZero()
        {
            using var mem = new VirtualPlcMemory();
            var result = mem.GetDbValue(1, 0, 4);
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, result);
        }

        [Fact]
        public void DifferentDbNumbers_Independent()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetDbValue(1, 0, new byte[] { 0xAA });
            mem.SetDbValue(2, 0, new byte[] { 0xBB });
            Assert.Equal(new byte[] { 0xAA }, mem.GetDbValue(1, 0, 1));
            Assert.Equal(new byte[] { 0xBB }, mem.GetDbValue(2, 0, 1));
        }

        [Fact]
        public void SetMerker_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetMerker(0, new byte[] { 0xFF, 0x00 });
            var result = mem.GetDbValue(-1, 0, 2);
            Assert.Equal(new byte[] { 0xFF, 0x00 }, result);
        }

        [Fact]
        public void SetInput_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInput(0, new byte[] { 0x55 });
            var result = mem.GetDbValue(-2, 0, 1);
            Assert.Equal(new byte[] { 0x55 }, result);
        }

        [Fact]
        public void SetOutput_RoundTrip()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetOutput(0, new byte[] { 0x77 });
            var result = mem.GetDbValue(-3, 0, 1);
            Assert.Equal(new byte[] { 0x77 }, result);
        }

        [Fact]
        public void Clear_ClearsDbBytes()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetDbValue(1, 0, new byte[] { 0xAA, 0xBB });
            mem.Clear();
            var result = mem.GetDbValue(1, 0, 2);
            Assert.Equal(new byte[] { 0, 0 }, result);
        }
    }

    public class JsonScenarioLoadingTests
    {
        [Fact]
        public void LoadScenarioFromJsonString_BasicScenario()
        {
            var json = @"{
                ""name"": ""TestScenario"",
                ""description"": ""A test scenario"",
                ""registers"": [
                    { ""address"": 0, ""value"": 100, ""comment"": ""D0"" },
                    { ""address"": 1, ""value"": 200, ""comment"": ""D1"" }
                ],
                ""coils"": [
                    { ""address"": 0, ""value"": true, ""comment"": ""M0"" }
                ],
                ""rules"": [
                    {
                        ""name"": ""Rule1"",
                        ""watchAddress"": 0,
                        ""triggerValue"": 100,
                        ""targetAddress"": 10,
                        ""targetValue"": 999,
                        ""isBoolTarget"": false,
                        ""delayMs"": 0
                    }
                ]
            }";

            using var host = new VirtualPlcHost();
            host.LoadScenarioFromJsonString(json);

            Assert.Equal("TestScenario", host.CurrentScenario?.Name);
            Assert.Equal((short)100, host.Memory.GetInt16(0));
            Assert.Equal((short)200, host.Memory.GetInt16(1));
            Assert.True(host.Memory.GetBool(0));
        }

        [Fact]
        public void LoadScenarioFromJson_File()
        {
            var json = @"{
                ""name"": ""FileScenario"",
                ""description"": ""From file"",
                ""registers"": [
                    { ""address"": 5, ""value"": 555 }
                ],
                ""coils"": [],
                ""rules"": []
            }";

            var path = Path.Combine(Path.GetTempPath(), $"scenario_{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, json);
                using var host = new VirtualPlcHost();
                host.LoadScenarioFromJson(path);
                Assert.Equal("FileScenario", host.CurrentScenario?.Name);
                Assert.Equal((short)555, host.Memory.GetInt16(5));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void LoadScenarioFromJson_FileNotFound_Throws()
        {
            using var host = new VirtualPlcHost();
            Assert.Throws<FileNotFoundException>(() =>
                host.LoadScenarioFromJson(@"C:\nonexistent\scenario.json"));
        }

        [Fact]
        public void LoadScenarioFromJson_NullPath_Throws()
        {
            using var host = new VirtualPlcHost();
            Assert.Throws<ArgumentNullException>(() =>
                host.LoadScenarioFromJson(null!));
        }

        [Fact]
        public void LoadScenarioFromJson_WithActionRules()
        {
            var json = @"{
                ""name"": ""ActionScenario"",
                ""description"": ""With actions"",
                ""registers"": [
                    { ""address"": 0, ""value"": 300 }
                ],
                ""coils"": [],
                ""rules"": [
                    {
                        ""name"": ""RandomWalk"",
                        ""watchAddress"": -1,
                        ""triggerValue"": -1,
                        ""targetAddress"": 0,
                        ""targetValue"": 0,
                        ""action"": ""random_walk(100, 500, 5)""
                    }
                ]
            }";

            using var host = new VirtualPlcHost();
            host.LoadScenarioFromJsonString(json);
            Assert.Equal("ActionScenario", host.CurrentScenario?.Name);
            Assert.Single(host.CurrentScenario!.Rules);
            Assert.Equal("random_walk(100, 500, 5)", host.CurrentScenario.Rules[0].Action);
        }
    }

    public class SimulationRuleTests
    {
        [Fact]
        public void RandomWalk_WithinBounds()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, 300);

            var rule = new ScenarioRule
            {
                Name = "RW",
                WatchAddress = -1,
                TriggerValue = -1,
                TargetAddress = 0,
                TargetValue = 0,
                Action = "random_walk(100, 500, 10)"
            };

            for (int i = 0; i < 100; i++)
            {
                rule.Execute(mem);
                short val = mem.GetInt16(0);
                Assert.True(val >= 100 && val <= 500,
                    $"Value {val} out of range [100, 500] at iteration {i}");
            }
        }

        [Fact]
        public void RandomWalk_ChangesValue()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, 300);

            var rule = new ScenarioRule
            {
                Name = "RW",
                WatchAddress = -1,
                TriggerValue = -1,
                TargetAddress = 0,
                TargetValue = 0,
                Action = "random_walk(100, 500, 50)"
            };

            bool changed = false;
            for (int i = 0; i < 50; i++)
            {
                short before = mem.GetInt16(0);
                rule.Execute(mem);
                if (mem.GetInt16(0) != before) changed = true;
            }
            Assert.True(changed, "Random walk should change value at least once");
        }

        [Fact]
        public void Sine_ProducesValues()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, 300);

            var rule = new ScenarioRule
            {
                Name = "Sine",
                WatchAddress = -1,
                TriggerValue = -1,
                TargetAddress = 0,
                TargetValue = 0,
                Action = "sine(100, 5000, 300)"
            };

            rule.Execute(mem);
            short val = mem.GetInt16(0);
            Assert.True(val >= 200 && val <= 400,
                $"Sine value {val} expected in [200, 400]");
        }

        [Fact]
        public void Pid_AdjustsTowardSetpoint()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, 200);  // current temp
            mem.SetInt16(1, 250);  // setpoint
            mem.SetInt16(3, 50);   // Kp = 5.0
            mem.SetInt16(4, 10);   // Ki = 1.0
            mem.SetInt16(5, 20);   // Kd = 2.0

            var rule = new ScenarioRule
            {
                Name = "PID",
                WatchAddress = -1,
                TriggerValue = -1,
                TargetAddress = 0,
                TargetValue = 0,
                Action = "pid(0, 1, 3, 4, 5)"
            };

            for (int i = 0; i < 10; i++)
                rule.Execute(mem);

            short result = mem.GetInt16(0);
            Assert.True(result > 200,
                $"PID should increase value toward setpoint, got {result}");
        }

        [Fact]
        public void InvalidAction_DoesNotThrow()
        {
            using var mem = new VirtualPlcMemory();
            mem.SetInt16(0, 100);

            var rule = new ScenarioRule
            {
                Name = "Bad",
                WatchAddress = -1,
                TriggerValue = -1,
                TargetAddress = 0,
                TargetValue = 0,
                Action = "invalid_action()"
            };

            rule.Execute(mem);
            Assert.Equal((short)100, mem.GetInt16(0));
        }

        [Fact]
        public void EmptyAction_FallsBackToDirectWrite()
        {
            using var mem = new VirtualPlcMemory();

            var rule = new ScenarioRule
            {
                Name = "Direct",
                WatchAddress = 0,
                TriggerValue = 0,
                TargetAddress = 10,
                TargetValue = 42,
                Action = ""
            };

            rule.Execute(mem);
            Assert.Equal((short)42, mem.GetInt16(10));
        }
    }

    public class ScenarioDefinitionTests
    {
        [Fact]
        public void ToScenarioScript_Converts()
        {
            var def = new ScenarioDefinition
            {
                Name = "Test",
                Description = "Desc",
                Registers = new List<RegisterPreset>
                {
                    new RegisterPreset { Address = 0, Value = 100 }
                },
                Coils = new List<CoilPreset>
                {
                    new CoilPreset { Address = 0, Value = true }
                },
                Rules = new List<ScenarioRule>
                {
                    new ScenarioRule { Name = "R1" }
                }
            };

            var script = def.ToScenarioScript();
            Assert.Equal("Test", script.Name);
            Assert.Equal("Desc", script.Description);
            Assert.Single(script.RegisterPresets);
            Assert.Single(script.CoilPresets);
            Assert.Single(script.Rules);
        }
    }

    public class BuiltInScenarioEnhancedTests
    {
        [Fact]
        public void PidTemperature_HasExpectedStructure()
        {
            var scenario = BuiltInScenarios.PidTemperature();
            Assert.Equal("PID温控", scenario.Name);
            Assert.Equal(6, scenario.RegisterPresets.Count);
            Assert.Single(scenario.Rules);
            Assert.StartsWith("pid(", scenario.Rules[0].Action);
        }

        [Fact]
        public void RandomWalkSensor_HasExpectedStructure()
        {
            var scenario = BuiltInScenarios.RandomWalkSensor();
            Assert.Equal("随机游走", scenario.Name);
            Assert.Single(scenario.RegisterPresets);
            Assert.Single(scenario.Rules);
            Assert.StartsWith("random_walk(", scenario.Rules[0].Action);
        }

        [Fact]
        public void SineWaveSensor_HasExpectedStructure()
        {
            var scenario = BuiltInScenarios.SineWaveSensor();
            Assert.Equal("正弦波", scenario.Name);
            Assert.Single(scenario.RegisterPresets);
            Assert.Single(scenario.Rules);
            Assert.StartsWith("sine(", scenario.Rules[0].Action);
        }
    }
}
