using Xunit;
using Nexus.Iec104;

namespace Nexus.Iec104.Tests;

public class Iec104AsduTests
{
    // ── TypeID enum ───────────────────────────────

    [Fact]
    public void TypeId_M_SP_NA_1_Is_1()
    {
        Assert.Equal(1, (byte)TypeId.M_SP_NA_1);
    }

    [Fact]
    public void TypeId_C_IC_NA_1_Is_100()
    {
        Assert.Equal(100, (byte)TypeId.C_IC_NA_1);
    }

    [Fact]
    public void TypeId_C_SC_NA_1_Is_45()
    {
        Assert.Equal(45, (byte)TypeId.C_SC_NA_1);
    }

    // ── CauseOfTransmission enum ──────────────────

    [Fact]
    public void Cot_Spontaneous_Is_3()
    {
        Assert.Equal(3, (byte)CauseOfTransmission.Spontaneous);
    }

    [Fact]
    public void Cot_Activation_Is_6()
    {
        Assert.Equal(6, (byte)CauseOfTransmission.Activation);
    }

    // ── ASDU Encode/Decode Round-trip ─────────────

    [Fact]
    public void Asdu_EncodeDecode_Roundtrip_SinglePoint()
    {
        var asdu = new Iec104Asdu
        {
            TypeId = TypeId.M_SP_NA_1,
            Vsq = 1,
            Cause = CauseOfTransmission.Spontaneous,
            OriginatorAddress = 0,
            CommonAddress = 1,
        };
        asdu.Objects.Add(new Iec104InformationObject
        {
            Address = 100,
            Data = new byte[] { 0x01 }
        });

        byte[] encoded = asdu.Encode();
        var decoded = Iec104Asdu.Decode(encoded, 0);

        Assert.Equal(TypeId.M_SP_NA_1, decoded.TypeId);
        Assert.Equal(CauseOfTransmission.Spontaneous, decoded.Cause);
        Assert.Equal(1, decoded.CommonAddress);
        Assert.Single(decoded.Objects);
        Assert.Equal(100, decoded.Objects[0].Address);
        Assert.Equal(0x01, decoded.Objects[0].Data[0]);
    }

    [Fact]
    public void Asdu_EncodeDecode_Roundtrip_MeasuredFloat()
    {
        var asdu = new Iec104Asdu
        {
            TypeId = TypeId.M_ME_NC_1,
            Vsq = 1,
            Cause = CauseOfTransmission.Periodic,
            OriginatorAddress = 0,
            CommonAddress = 2,
        };

        int raw;
        unsafe { float v = 3.14f; raw = *(int*)&v; }
        asdu.Objects.Add(new Iec104InformationObject
        {
            Address = 200,
            Data = new byte[]
            {
                (byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF),
                (byte)((raw >> 16) & 0xFF), (byte)((raw >> 24) & 0xFF),
                0x00
            }
        });

        byte[] encoded = asdu.Encode();
        var decoded = Iec104Asdu.Decode(encoded, 0);

        Assert.Equal(TypeId.M_ME_NC_1, decoded.TypeId);
        Assert.Equal(CauseOfTransmission.Periodic, decoded.Cause);
        Assert.Single(decoded.Objects);
        Assert.Equal(200, decoded.Objects[0].Address);

        var mv = Iec104Asdu.DecodeMeasuredFloat(decoded.Objects[0]);
        Assert.InRange(mv.Value, 3.13f, 3.15f);
    }

    [Fact]
    public void Asdu_EncodeDecode_Roundtrip_DoublePoint()
    {
        var asdu = new Iec104Asdu
        {
            TypeId = TypeId.M_DP_NA_1,
            Vsq = 1,
            Cause = CauseOfTransmission.Spontaneous,
            CommonAddress = 1,
        };
        asdu.Objects.Add(new Iec104InformationObject
        {
            Address = 50,
            Data = new byte[] { 0x02 }
        });

        byte[] encoded = asdu.Encode();
        var decoded = Iec104Asdu.Decode(encoded, 0);

        var dp = Iec104Asdu.DecodeDoublePoint(decoded.Objects[0]);
        Assert.Equal(50, dp.Address);
        Assert.True(dp.IsOn);
    }

    [Fact]
    public void Asdu_EncodeDecode_Roundtrip_MeasuredNormalized()
    {
        var asdu = new Iec104Asdu
        {
            TypeId = TypeId.M_ME_NA_1,
            Vsq = 1,
            Cause = CauseOfTransmission.Background,
            CommonAddress = 1,
        };
        asdu.Objects.Add(new Iec104InformationObject
        {
            Address = 300,
            Data = new byte[] { 0xFF, 0x7F, 0x00 } // max normalized value, good quality
        });

        byte[] encoded = asdu.Encode();
        var decoded = Iec104Asdu.Decode(encoded, 0);

        var mn = Iec104Asdu.DecodeMeasuredNormalized(decoded.Objects[0]);
        Assert.Equal(300, mn.Address);
        Assert.InRange(mn.Value, 0.99f, 1.01f);
    }

    // ── ASDU Sequence mode ────────────────────────

    [Fact]
    public void Asdu_EncodeDecode_Sequence_MultiplePoints()
    {
        var asdu = new Iec104Asdu
        {
            TypeId = TypeId.M_SP_NA_1,
            Vsq = (byte)(3 | 0x80), // 3 objects, sequential
            Cause = CauseOfTransmission.Spontaneous,
            CommonAddress = 1,
        };
        asdu.Objects.Add(new Iec104InformationObject { Address = 10, Data = new byte[] { 0x01 } });
        asdu.Objects.Add(new Iec104InformationObject { Address = 11, Data = new byte[] { 0x00 } });
        asdu.Objects.Add(new Iec104InformationObject { Address = 12, Data = new byte[] { 0x01 } });

        byte[] encoded = asdu.Encode();
        var decoded = Iec104Asdu.Decode(encoded, 0);

        Assert.Equal(3, decoded.Objects.Count);
        Assert.Equal(10, decoded.Objects[0].Address);
        Assert.Equal(11, decoded.Objects[1].Address);
        Assert.Equal(12, decoded.Objects[2].Address);
    }

    // ── Build helpers ─────────────────────────────

    [Fact]
    public void BuildSingleCommand_CorrectAsdu()
    {
        var asdu = Iec104Asdu.BuildSingleCommand(1, 42, true);

        Assert.Equal(TypeId.C_SC_NA_1, asdu.TypeId);
        Assert.Equal(CauseOfTransmission.Activation, asdu.Cause);
        Assert.Equal(1, asdu.CommonAddress);
        Assert.Single(asdu.Objects);
        Assert.Equal(42, asdu.Objects[0].Address);
        Assert.Equal(0x01, asdu.Objects[0].Data[0]);
    }

    [Fact]
    public void BuildDoubleCommand_CorrectAsdu()
    {
        var asdu = Iec104Asdu.BuildDoubleCommand(1, 55, false);

        Assert.Equal(TypeId.C_DC_NA_1, asdu.TypeId);
        Assert.Equal(55, asdu.Objects[0].Address);
        Assert.Equal(0x01, asdu.Objects[0].Data[0]); // off = 1
    }

    [Fact]
    public void BuildGeneralInterrogation_CorrectAsdu()
    {
        var asdu = Iec104Asdu.BuildGeneralInterrogation(1);

        Assert.Equal(TypeId.C_IC_NA_1, asdu.TypeId);
        Assert.Equal(CauseOfTransmission.Activation, asdu.Cause);
        Assert.Equal(0, asdu.Objects[0].Address);
        Assert.Equal(20, asdu.Objects[0].Data[0]); // QOI=20 = station interrogation
    }

    [Fact]
    public void BuildReadCommand_CorrectAsdu()
    {
        var asdu = Iec104Asdu.BuildReadCommand(1, 123);

        Assert.Equal(TypeId.C_RD_NA_1, asdu.TypeId);
        Assert.Equal(CauseOfTransmission.Request, asdu.Cause);
        Assert.Equal(123, asdu.Objects[0].Address);
        Assert.Empty(asdu.Objects[0].Data);
    }

    [Fact]
    public void BuildSetpointNormalized_CorrectAsdu()
    {
        var asdu = Iec104Asdu.BuildSetpointNormalized(1, 10, 0.5f);

        Assert.Equal(TypeId.C_SE_NA_1, asdu.TypeId);
        Assert.Equal(CauseOfTransmission.Activation, asdu.Cause);
        Assert.Equal(10, asdu.Objects[0].Address);
        Assert.Equal(3, asdu.Objects[0].Data.Length); // NVA(2) + QOS(1)
    }

    // ── Quality Flags ─────────────────────────────

    [Fact]
    public void QualityFlags_Combination()
    {
        QualityFlags q = QualityFlags.Invalid | QualityFlags.Blocked;
        Assert.True(q.HasFlag(QualityFlags.Invalid));
        Assert.True(q.HasFlag(QualityFlags.Blocked));
        Assert.False(q.HasFlag(QualityFlags.Overflow));
    }

    // ── Point Info Structs ────────────────────────

    [Fact]
    public void DoublePointInfo_IsOn_IsOff()
    {
        var dpOn = new DoublePointInfo { Value = 2 };
        Assert.True(dpOn.IsOn);
        Assert.False(dpOn.IsOff);

        var dpOff = new DoublePointInfo { Value = 1 };
        Assert.True(dpOff.IsOff);
        Assert.False(dpOff.IsOn);

        var dpIndet = new DoublePointInfo { Value = 0 };
        Assert.True(dpIndet.IsIndeterminate);
    }

    [Fact]
    public void SinglePointInfo_ToString()
    {
        var sp = new SinglePointInfo { Address = 42, Value = true, Quality = QualityFlags.None };
        Assert.Contains("42", sp.ToString());
        Assert.Contains("True", sp.ToString());
    }

    // ── ASDU COT flags ────────────────────────────

    [Fact]
    public void Asdu_EncodeDecode_NegativeFlag()
    {
        var asdu = new Iec104Asdu
        {
            TypeId = TypeId.C_SC_NA_1,
            Vsq = 1,
            Cause = CauseOfTransmission.ActivationCon,
            IsNegative = true,
            CommonAddress = 1,
        };
        asdu.Objects.Add(new Iec104InformationObject { Address = 1, Data = new byte[] { 0x00 } });

        byte[] encoded = asdu.Encode();
        var decoded = Iec104Asdu.Decode(encoded, 0);

        Assert.True(decoded.IsNegative);
        Assert.Equal(CauseOfTransmission.ActivationCon, decoded.Cause);
    }

    [Fact]
    public void Asdu_EncodeDecode_TestFlag()
    {
        var asdu = new Iec104Asdu
        {
            TypeId = TypeId.M_SP_NA_1,
            Vsq = 1,
            Cause = CauseOfTransmission.Spontaneous,
            IsTest = true,
            CommonAddress = 1,
        };
        asdu.Objects.Add(new Iec104InformationObject { Address = 1, Data = new byte[] { 0x01 } });

        byte[] encoded = asdu.Encode();
        var decoded = Iec104Asdu.Decode(encoded, 0);

        Assert.True(decoded.IsTest);
    }

    // ── ASDU Multiple objects non-sequence ────────

    [Fact]
    public void Asdu_EncodeDecode_NonSequence_MultipleObjects()
    {
        var asdu = new Iec104Asdu
        {
            TypeId = TypeId.M_SP_NA_1,
            Vsq = 3, // 3 objects, non-sequential
            Cause = CauseOfTransmission.Spontaneous,
            CommonAddress = 1,
        };
        asdu.Objects.Add(new Iec104InformationObject { Address = 10, Data = new byte[] { 0x01 } });
        asdu.Objects.Add(new Iec104InformationObject { Address = 50, Data = new byte[] { 0x00 } });
        asdu.Objects.Add(new Iec104InformationObject { Address = 99, Data = new byte[] { 0x01 } });

        byte[] encoded = asdu.Encode();
        var decoded = Iec104Asdu.Decode(encoded, 0);

        Assert.Equal(3, decoded.Objects.Count);
        Assert.Equal(10, decoded.Objects[0].Address);
        Assert.Equal(50, decoded.Objects[1].Address);
        Assert.Equal(99, decoded.Objects[2].Address);
    }

    // ── DataPoint ─────────────────────────────────

    [Fact]
    public void Iec104DataPoint_ToString()
    {
        var point = new Iec104DataPoint
        {
            Address = 42,
            Type = TypeId.M_ME_NC_1,
            Value = 3.14f,
            Quality = QualityFlags.None,
        };
        var s = point.ToString();
        Assert.Contains("42", s);
        Assert.Contains("M_ME_NC_1", s);
    }

    // ── Client 构造器 ─────────────────────────────

    [Fact]
    public void Iec104Client_Constructor_SetsDefaults()
    {
        var client = new Iec104Client("192.168.1.1");
        Assert.Equal(1, client.CommonAddress);
        Assert.Equal(30000, client.T0);
        Assert.Equal(15000, client.T1);
        Assert.Equal(10000, client.T2);
        Assert.Equal(20000, client.T3);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Iec104Client_Constructor_CustomPort()
    {
        var client = new Iec104Client("192.168.1.1", 2404, 5000);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Iec104Client_CommonAddress_CanBeSet()
    {
        var client = new Iec104Client("192.168.1.1") { CommonAddress = 5 };
        Assert.Equal(5, client.CommonAddress);
    }

    [Fact]
    public void Iec104Client_Timers_CanBeSet()
    {
        var client = new Iec104Client("192.168.1.1");
        client.T0 = 60000;
        client.T1 = 30000;
        client.T2 = 20000;
        client.T3 = 40000;
        Assert.Equal(60000, client.T0);
        Assert.Equal(30000, client.T1);
    }

    [Fact]
    public void Iec104Client_Dispose_WithoutConnect()
    {
        var client = new Iec104Client("192.168.1.1");
        client.Dispose();
    }

    [Fact]
    public void Iec104Client_Disconnect_WithoutConnect_DoesNotThrow()
    {
        var client = new Iec104Client("192.168.1.1");
        client.Disconnect();
    }

    // ── More enum coverage ─────────────────────────

    [Theory]
    [InlineData(TypeId.M_SP_NA_1, 1)]
    [InlineData(TypeId.M_DP_NA_1, 3)]
    [InlineData(TypeId.M_ME_NA_1, 9)]
    [InlineData(TypeId.M_ME_NC_1, 13)]
    [InlineData(TypeId.C_SC_NA_1, 45)]
    [InlineData(TypeId.C_DC_NA_1, 46)]
    [InlineData(TypeId.C_SE_NA_1, 48)]
    [InlineData(TypeId.C_IC_NA_1, 100)]
    [InlineData(TypeId.C_RD_NA_1, 102)]
    public void TypeId_Values_Correct(TypeId typeId, byte expected)
    {
        Assert.Equal(expected, (byte)typeId);
    }

    [Theory]
    [InlineData(CauseOfTransmission.Periodic, 1)]
    [InlineData(CauseOfTransmission.Background, 2)]
    [InlineData(CauseOfTransmission.Spontaneous, 3)]
    [InlineData(CauseOfTransmission.Initialized, 4)]
    [InlineData(CauseOfTransmission.Request, 5)]
    [InlineData(CauseOfTransmission.Activation, 6)]
    [InlineData(CauseOfTransmission.ActivationCon, 7)]
    [InlineData(CauseOfTransmission.Deactivation, 8)]
    [InlineData(CauseOfTransmission.DeactivationCon, 9)]
    [InlineData(CauseOfTransmission.ActivationTerm, 10)]
    public void Cot_Values_Correct(CauseOfTransmission cot, byte expected)
    {
        Assert.Equal(expected, (byte)cot);
    }

    // ── InformationObject ──────────────────────────

    [Fact]
    public void InformationObject_Defaults()
    {
        var obj = new Iec104InformationObject();
        Assert.Equal(0, obj.Address);
        Assert.Empty(obj.Data);
    }

    [Fact]
    public void InformationObject_ToString()
    {
        var obj = new Iec104InformationObject { Address = 42, Data = new byte[] { 0x01, 0x02 } };
        Assert.Contains("42", obj.ToString());
    }

    // ── MeasuredValueInfo ───────────────────────────

    [Fact]
    public void MeasuredValueInfo_ToString()
    {
        var mv = new MeasuredValueInfo { Address = 100, Value = 99.5f, Quality = QualityFlags.None };
        Assert.Contains("100", mv.ToString());
        Assert.Contains("99", mv.ToString());
    }

    // ── DoublePointInfo edge cases ─────────────────

    [Fact]
    public void DoublePointInfo_Value3_IsIndeterminate()
    {
        var dp = new DoublePointInfo { Value = 3 };
        Assert.True(dp.IsIndeterminate);
        Assert.False(dp.IsOn);
        Assert.False(dp.IsOff);
    }

    // ── QualityFlags combinations ──────────────────

    [Fact]
    public void QualityFlags_AllSet()
    {
        var q = QualityFlags.Overflow | QualityFlags.Blocked | QualityFlags.Substituted |
                QualityFlags.NotTopical | QualityFlags.Invalid;
        Assert.Equal((QualityFlags)0x1F, q);
    }

    [Fact]
    public void QualityFlags_None_IsZero()
    {
        Assert.Equal((QualityFlags)0, QualityFlags.None);
    }

    // ── PointType enum ─────────────────────────────

    [Fact]
    public void PointType_Values_Exist()
    {
        Assert.True(Enum.IsDefined(typeof(PointType), PointType.SinglePoint));
        Assert.True(Enum.IsDefined(typeof(PointType), PointType.DoublePoint));
        Assert.True(Enum.IsDefined(typeof(PointType), PointType.MeasuredNormalized));
        Assert.True(Enum.IsDefined(typeof(PointType), PointType.MeasuredFloat));
        Assert.True(Enum.IsDefined(typeof(PointType), PointType.SingleCommand));
        Assert.True(Enum.IsDefined(typeof(PointType), PointType.DoubleCommand));
        Assert.True(Enum.IsDefined(typeof(PointType), PointType.SetpointNormalized));
    }
}
