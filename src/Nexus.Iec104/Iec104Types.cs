using System;

namespace Nexus.Iec104
{
    public enum TypeId : byte
    {
        M_SP_NA_1 = 1,
        M_DP_NA_1 = 3,
        M_ME_NA_1 = 9,
        M_ME_NC_1 = 13,
        M_IT_NA_1 = 15,
        C_SC_NA_1 = 45,
        C_DC_NA_1 = 46,
        C_SE_NA_1 = 48,
        C_IC_NA_1 = 100,
        C_CI_NA_1 = 101,
        C_RD_NA_1 = 102,
        C_CS_NA_1 = 103,
        C_TS_TA_1 = 104,
    }

    public enum CauseOfTransmission : byte
    {
        Periodic = 1,
        Background = 2,
        Spontaneous = 3,
        Initialized = 4,
        Request = 5,
        Activation = 6,
        ActivationCon = 7,
        Deactivation = 8,
        DeactivationCon = 9,
        ActivationTerm = 10,
    }

    public enum PointType
    {
        SinglePoint,
        DoublePoint,
        MeasuredNormalized,
        MeasuredFloat,
        SingleCommand,
        DoubleCommand,
        SetpointNormalized,
    }

    [Flags]
    public enum QualityFlags : byte
    {
        None = 0,
        Overflow = 0x01,
        Blocked = 0x02,
        Substituted = 0x04,
        NotTopical = 0x08,
        Invalid = 0x10,
    }

    public struct SinglePointInfo
    {
        public int Address;
        public bool Value;
        public QualityFlags Quality;

        public override string ToString()
            => $"SP[{Address}]={Value} Q={Quality}";
    }

    public struct DoublePointInfo
    {
        public int Address;
        public byte Value;
        public QualityFlags Quality;

        public bool IsOn => Value == 2;
        public bool IsOff => Value == 1;
        public bool IsIndeterminate => Value == 0 || Value == 3;

        public override string ToString()
            => $"DP[{Address}]={Value} Q={Quality}";
    }

    public struct MeasuredValueInfo
    {
        public int Address;
        public float Value;
        public QualityFlags Quality;

        public override string ToString()
            => $"MV[{Address}]={Value:F4} Q={Quality}";
    }

    public class Iec104DataPoint
    {
        public int Address { get; set; }
        public TypeId Type { get; set; }
        public object Value { get; set; } = 0;
        public QualityFlags Quality { get; set; }
        public DateTime Timestamp { get; set; }

        public override string ToString()
            => $"IOA={Address} Type={Type} Value={Value} Q={Quality}";
    }
}
