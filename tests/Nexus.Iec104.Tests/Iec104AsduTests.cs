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
}
