using System;

namespace Nexus.Bacnet
{
    public enum BacnetObjectType : ushort
    {
        AnalogInput = 0,
        AnalogOutput = 1,
        AnalogValue = 2,
        BinaryInput = 3,
        BinaryOutput = 4,
        BinaryValue = 5,
        Calendar = 6,
        Command = 7,
        Device = 8,
        EventEnrollment = 9,
        File = 10,
        Group = 11,
        Loop = 12,
        MultiStateInput = 13,
        MultiStateOutput = 14,
        NotificationClass = 15,
        Program = 16,
        Schedule = 17,
        Averaging = 18,
        MultiStateValue = 19,
        TrendLog = 20,
        LifeSafetyPoint = 21,
        LifeSafetyZone = 22,
        Accumulator = 23,
        PulseConverter = 24,
        EventLog = 25,
        GlobalGroup = 26,
        TrendLogMultiple = 27,
        LoadControl = 28,
        StructuredView = 29,
        AccessPoint = 30,
        AccessZone = 31,
        AccessUser = 32,
        AccessRights = 33,
        AccessCredential = 34,
        CredentialDataInput = 35,
        NetworkSecurity = 36,
        BitstringValue = 37,
        CharacterstringValue = 38,
        DatePatternValue = 39,
        DateValue = 40,
        DateTimePatternValue = 41,
        DateTimeValue = 42,
        IntegerValue = 43,
        LargeAnalogValue = 44,
        PositiveIntegerValue = 45,
        TimePatternValue = 46,
        TimeValue = 47,
        NotificationForwarder = 48,
        AlertEnrollment = 49,
        Channel = 50,
        LightingOutput = 51,
        BinaryLightingOutput = 52,
        NetworkPort = 53,
        ElevatorGroup = 54,
        Escalator = 55,
        Lift = 56
    }

    public enum BacnetPropertyId : uint
    {
        AckedTransitions = 0,
        AckRequired = 1,
        Action = 2,
        ActionText = 3,
        ActiveText = 4,
        ActiveVtSessions = 5,
        AlarmValue = 6,
        AlarmValues = 7,
        All = 8,
        AllWritesSuccessful = 9,
        ApduSegmentTimeout = 10,
        ApduTimeout = 11,
        ApplicationSoftwareVersion = 12,
        Archive = 13,
        Bias = 14,
        ChangeOfStateCount = 15,
        ChangeOfStateTime = 16,
        NotificationClass = 17,
        ControlledVariableReference = 19,
        ControlledVariableUnits = 20,
        ControlledVariableValue = 21,
        CovIncrement = 22,
        DateList = 23,
        DaylightSavingsStatus = 24,
        Deadband = 25,
        DerivativeConstant = 26,
        DerivativeConstantUnits = 27,
        Description = 28,
        DescriptionOfHalt = 29,
        DeviceAddressBinding = 30,
        DeviceType = 31,
        EffectivePeriod = 32,
        ElapsedActiveTime = 33,
        ErrorLimit = 34,
        EventEnable = 35,
        EventState = 36,
        EventTimeStamps = 37,
        EventType = 38,
        ExceptionSchedule = 39,
        FaultValues = 40,
        FeedbackValue = 41,
        FileAccessMethod = 42,
        FileSize = 43,
        FileType = 44,
        FirmwareRevision = 45,
        HighLimit = 46,
        InactiveText = 47,
        InProcess = 48,
        InstanceOf = 49,
        IntegralConstant = 50,
        IntegralConstantUnits = 51,
        LimitEnable = 52,
        ListOfGroupMembers = 53,
        ListOfObjectPropertyReferences = 54,
        ListOfSessionKeys = 55,
        LocalDate = 56,
        LocalTime = 57,
        Location = 58,
        LowLimit = 59,
        ManipulatedVariableReference = 60,
        MaximumOutput = 61,
        MaxApduLengthAccepted = 62,
        MaxInfoFrames = 63,
        MaxMaster = 64,
        MaxPresValue = 65,
        MinimumOffTime = 66,
        MinimumOnTime = 67,
        MinimumOutput = 68,
        MinPresValue = 69,
        ModelName = 70,
        ModificationDate = 71,
        NotifyType = 72,
        NumberOfApduRetries = 73,
        NumberOfStates = 74,
        ObjectIdentifier = 75,
        ObjectList = 76,
        ObjectName = 77,
        ObjectType = 78,
        Optional = 79,
        OutOfService = 80,
        OutputUnits = 81,
        EventParameters = 83,
        Polarity = 84,
        PresentValue = 85,
        Priority = 86,
        PriorityArray = 87,
        PriorityForWriting = 88,
        ProcessIdentifier = 89,
        ProgramChange = 90,
        ProgramLocation = 91,
        ProgramState = 92,
        ProportionalConstant = 93,
        ProportionalConstantUnits = 94,
        ProtocolObjectTypesSupported = 96,
        ProtocolRevision = 139,
        ProtocolServicesSupported = 97,
        ProtocolVersion = 98,
        ReadOnly = 99,
        ReasonForHalt = 100,
        Recipient = 101,
        RecipientList = 102,
        Reliability = 103,
        RelinquishDefault = 104,
        Required = 105,
        Resolution = 106,
        SegmentationSupported = 107,
        Setpoint = 108,
        SetpointReference = 109,
        StateText = 110,
        StatusFlags = 111,
        SystemStatus = 112,
        TimeDelay = 113,
        TimeOfActiveTimeReset = 114,
        TimeOfStateCountReset = 115,
        TimeSynchronizationRecipients = 116,
        Units = 117,
        UpdateInterval = 118,
        UtcOffset = 119,
        VendorIdentifier = 120,
        VendorName = 121,
        VtClassesSupported = 122,
        WeeklySchedule = 123,
        AttemptedSamples = 124,
        AverageValue = 125,
        BufferSize = 126,
        ClientCovIncrement = 127,
        CovResubscriptionInterval = 128,
        EventTimeStampsSynchronized = 145,
        LogBuffer = 131,
        LogDeviceObjectProperty = 132,
        Enable = 133,
        LogInterval = 134,
        MaximumValue = 135,
        MinimumValue = 136,
        NotificationThreshold = 137,
        PreviousNotifyTime = 138,
        ProtocolRevision2 = 139,
        RecordsSinceNotification = 140,
        RecordCount = 141,
        StartTime = 142,
        StopTime = 143,
        StopWhenFull = 144,
        TotalRecordCount = 146,
        ValidSamples = 147,
        WindowInterval = 148,
        WindowSamples = 149,
        MaximumValueTimestamp = 150,
        MinimumValueTimestamp = 151,
        VarianceValue = 152,
        ActiveCovSubscriptions = 153,
        SlaveProxyEnable = 154,
        ManualSlaveAddressBinding = 155,
        AutoSlaveDiscovery = 156,
        SlaveAddressBinding = 157,
        LastRestoreTime = 158,
        BackupFailureTimeout = 159,
        BackupPreparationTime = 160,
        RestoreCompletionTime = 161,
        RestorePreparationTime = 162,
        BitMask = 163,
        BitText = 164,
        IsUtc = 165,
        GroupMembers = 166,
        MemberOf = 167,
        NetworkNumber = 168,
        NetworkNumberQuality = 169,
        RoutingTable = 170,
        MaximumBvlcLengthAccepted = 171,
        MaximumNpduLengthAccepted = 172,
        SlaveAddressBinding2 = 173,
        VirtualMacAddressTable = 174,
        RoutingTable2 = 175
    }

    public enum BacnetApplicationTag : byte
    {
        Null = 0,
        Boolean = 1,
        Unsigned = 2,
        Signed = 3,
        Real = 4,
        Double = 5,
        OctetString = 6,
        CharacterString = 7,
        BitString = 8,
        Enumerated = 9,
        Date = 10,
        Time = 11,
        ObjectId = 12,
        Reserved13 = 13,
        Reserved14 = 14,
        Reserved15 = 15
    }

    public struct BacnetObjectId
    {
        public BacnetObjectType Type;
        public uint Instance;

        public BacnetObjectId(BacnetObjectType type, uint instance)
        {
            Type = type;
            Instance = instance;
        }

        public uint AsUint32 => ((uint)Type << 22) | (Instance & 0x3FFFFF);

        public static BacnetObjectId FromUint32(uint value)
        {
            var type = (BacnetObjectType)(value >> 22);
            uint instance = value & 0x3FFFFF;
            return new BacnetObjectId(type, instance);
        }

        public override string ToString() => $"{Type}:{Instance}";

        public override bool Equals(object obj)
            => obj is BacnetObjectId other && Type == other.Type && Instance == other.Instance;

        public override int GetHashCode() => AsUint32.GetHashCode();

        public static bool operator ==(BacnetObjectId left, BacnetObjectId right)
            => left.Type == right.Type && left.Instance == right.Instance;

        public static bool operator !=(BacnetObjectId left, BacnetObjectId right)
            => !(left == right);
    }

    public struct BacnetPropertyReference
    {
        public BacnetObjectId ObjectIdentifier;
        public BacnetPropertyId PropertyId;
        public uint ArrayIndex;

        public BacnetPropertyReference(BacnetObjectId objectId, BacnetPropertyId propertyId, uint arrayIndex = uint.MaxValue)
        {
            ObjectIdentifier = objectId;
            PropertyId = propertyId;
            ArrayIndex = arrayIndex;
        }
    }

    public struct BacnetPropertyValue
    {
        public BacnetObjectId ObjectIdentifier;
        public BacnetPropertyId PropertyId;
        public BacnetValue Value;
    }

    public struct BacnetValue
    {
        public BacnetApplicationTag Tag;
        public object? Data;

        public BacnetValue(BacnetApplicationTag tag, object data)
        {
            Tag = tag;
            Data = data;
        }

        public override string ToString() => $"[{Tag}] {Data}";
    }

    public struct BacnetAddress
    {
        public byte NetworkType;
        public byte[] MacAddress;

        public BacnetAddress(byte[] mac)
        {
            NetworkType = 0;
            MacAddress = mac ?? new byte[0];
        }
    }

    public class BacnetDeviceObject
    {
        public BacnetObjectId ObjectId { get; set; }
        public string ObjectName { get; set; } = "";
        public BacnetObjectType ObjectType { get; set; }
        public BacnetValue[] Properties { get; set; } = Array.Empty<BacnetValue>();
    }
}
