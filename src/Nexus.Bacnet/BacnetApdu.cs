using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Bacnet
{
    public enum BacnetPduType : byte
    {
        ConfirmedRequest = 0,
        UnconfirmedRequest = 1,
        SimpleAck = 2,
        ComplexAck = 3,
        SegmentAck = 4,
        Error = 5,
        Reject = 6,
        Abort = 7
    }

    public enum BacnetUnconfirmedService : byte
    {
        IAm = 0,
        IHave = 1,
        UnconfirmedEventNotification = 2,
        UnconfirmedPrivateTransfer = 3,
        UnconfirmedTextMessage = 4,
        TimeSynchronization = 5,
        WhoHas = 6,
        WhoIs = 7,
        WriteGroup = 8,
        UtcTimeSynchronization = 9,
        WhoAmI = 10,
        YouAre = 11
    }

    public enum BacnetConfirmedService : byte
    {
        AcknowledgeAlarm = 0,
        ConfirmedEventNotification = 1,
        GetAlarmSummary = 2,
        GetEnrollmentSummary = 3,
        SubscribeCOV = 5,
        AtomicReadFile = 6,
        AtomicWriteFile = 7,
        AddListElement = 8,
        RemoveListElement = 9,
        CreateObject = 10,
        DeleteObject = 11,
        ReadProperty = 12,
        ReadPropertyConditional = 13,
        ReadPropertyMultiple = 14,
        WriteProperty = 15,
        WritePropertyMultiple = 16,
        DeviceCommunicationControl = 17,
        ConfirmedPrivateTransfer = 18,
        ConfirmedTextMessage = 19,
        ReinitializeDevice = 20,
        Vitualize = 21,
        ReadRange = 26,
        LifeSafetyOperation = 27,
        SubscribeCOVProperty = 28,
        GetEventInformation = 29,
        SubscribeCOVPropertyMultiple = 30,
        ConfirmedCOVNotificationMultiple = 31
    }

    public enum BacnetRejectReason : byte
    {
        Other = 0,
        BufferOverflow = 1,
        InconsistentParameters = 2,
        InvalidParameterDataType = 3,
        InvalidTag = 4,
        MissingRequiredParameter = 5,
        ParameterOutOfRange = 6,
        TooManyArguments = 7,
        UndefinedEnumeration = 8,
        UnrecognizedService = 9
    }

    public enum BacnetAbortReason : byte
    {
        Other = 0,
        BufferOverflow = 1,
        InvalidApduInThisState = 2,
        PreemptedByHigherPriorityTask = 3,
        SegmentationNotSupported = 4,
        SecurityError = 5,
        InsufficientSecurity = 6,
        MessageTooLong = 7
    }

    public enum BacnetErrorClass : ushort
    {
        Device = 0,
        Object = 1,
        Property = 2,
        Resources = 3,
        Security = 4,
        Services = 5,
        Vt = 6,
        Communication = 7
    }

    public enum BacnetErrorCode : ushort
    {
        Other = 0,
        AuthenticationFailed = 1,
        CharacterSetNotSupported = 4,
        CommunicationDisabled = 5,
        DatatypeNotSupported = 6,
        DuplicateName = 7,
        DuplicateObjectId = 8,
        DynamicCreationNotSupported = 9,
        FileAccessDenied = 10,
        IncompatibleSecurityLevels = 12,
        InconsistentParameters = 13,
        InconsistentSelectionCriterion = 14,
        InvalidDataType = 15,
        InvalidFileAccessMethod = 18,
        InvalidFileStartPosition = 19,
        InvalidOperatorName = 22,
        InvalidParameterDataType = 23,
        InvalidTimeStamp = 24,
        KeyGenerationError = 25,
        MissingRequiredParameter = 26,
        NoObjectsSpecified = 27,
        NoSpaceForObject = 28,
        NoSpaceToAddListElement = 29,
        NoSpaceToWriteProperty = 30,
        NoVtSessionsAvailable = 32,
        ObjectDeletionNotPermitted = 33,
        ObjectIdentifierAlreadyExists = 34,
        OperationalProblem = 35,
        Other2 = 36,
        PasswordFailure = 37,
        PropertyIsNotAList = 43,
        ReadAccessDenied = 45,
        SecurityNotSupported = 46,
        ServiceRequestDenied = 47,
        Timeout = 49,
        UnknownObject = 51,
        UnknownProperty = 52,
        UnknownRoute = 60,
        ValueNotInitialized = 63,
        ValueOutOfRange = 64,
        VtSessionAlreadyClosed = 65,
        VtSessionTerminationFailure = 66,
        WriteAccessDenied = 67
    }

    public class BacnetApdu
    {
        private static void EncodeObjectId(List<byte> buffer, BacnetObjectId id)
        {
            uint value = id.AsUint32;
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static BacnetObjectId DecodeObjectId(byte[] data, ref int offset)
        {
            uint value = (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
            offset += 4;
            return BacnetObjectId.FromUint32(value);
        }

        private static void EncodeUnsigned(List<byte> buffer, uint value)
        {
            if (value <= 0xFF)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Unsigned << 4 | 0));
                buffer.Add((byte)value);
            }
            else if (value <= 0xFFFF)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Unsigned << 4 | 1));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
            else if (value <= 0xFFFFFF)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Unsigned << 4 | 2));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
            else
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Unsigned << 4 | 3));
                buffer.Add((byte)(value >> 24));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
        }

        private static uint DecodeUnsigned(byte[] data, ref int offset)
        {
            byte tag = data[offset];
            int len = (tag & 0x07) + 1;
            offset++;
            uint value = 0;
            for (int i = 0; i < len; i++)
                value = (value << 8) | data[offset + i];
            offset += len;
            return value;
        }

        private static void EncodeEnumerated(List<byte> buffer, uint value)
        {
            if (value <= 0xFF)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Enumerated << 4 | 0));
                buffer.Add((byte)value);
            }
            else if (value <= 0xFFFF)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Enumerated << 4 | 1));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
            else if (value <= 0xFFFFFF)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Enumerated << 4 | 2));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
            else
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Enumerated << 4 | 3));
                buffer.Add((byte)(value >> 24));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
        }

        private static uint DecodeEnumerated(byte[] data, ref int offset)
        {
            return DecodeUnsigned(data, ref offset);
        }

        private static void EncodeReal(List<byte> buffer, float value)
        {
            buffer.Add((byte)((int)BacnetApplicationTag.Real << 4 | 3));
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                buffer.Add(bytes[3]); buffer.Add(bytes[2]);
                buffer.Add(bytes[1]); buffer.Add(bytes[0]);
            }
            else
            {
                buffer.Add(bytes[0]); buffer.Add(bytes[1]);
                buffer.Add(bytes[2]); buffer.Add(bytes[3]);
            }
        }

        private static void EncodeDouble(List<byte> buffer, double value)
        {
            buffer.Add((byte)((int)BacnetApplicationTag.Double << 4 | 7));
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                for (int i = 7; i >= 0; i--)
                    buffer.Add(bytes[i]);
            }
            else
            {
                for (int i = 0; i < 8; i++)
                    buffer.Add(bytes[i]);
            }
        }

        private static void EncodeBoolean(List<byte> buffer, bool value)
        {
            buffer.Add((byte)((int)BacnetApplicationTag.Boolean << 4 | (value ? 1 : 0)));
        }

        private static void EncodeCharacterString(List<byte> buffer, string value, byte encoding = 0)
        {
            byte[] strBytes = Encoding.UTF8.GetBytes(value);
            buffer.Add((byte)((int)BacnetApplicationTag.CharacterString << 4 | (strBytes.Length > 4 ? 5 : strBytes.Length)));
            if (strBytes.Length > 4)
            {
                buffer.Add((byte)strBytes.Length);
            }
            buffer.Add(encoding);
            for (int i = 0; i < strBytes.Length; i++)
                buffer.Add(strBytes[i]);
        }

        private static void EncodeOctetString(List<byte> buffer, byte[] value)
        {
            buffer.Add((byte)((int)BacnetApplicationTag.OctetString << 4 | (value.Length > 4 ? 5 : value.Length)));
            if (value.Length > 4)
            {
                buffer.Add((byte)value.Length);
            }
            for (int i = 0; i < value.Length; i++)
                buffer.Add(value[i]);
        }

        private static void EncodeContextObjectId(List<byte> buffer, byte contextNumber, BacnetObjectId id)
        {
            buffer.Add((byte)(contextNumber << 4 | 4));
            EncodeObjectId(buffer, id);
        }

        private static void EncodeContextUnsigned(List<byte> buffer, byte contextNumber, uint value)
        {
            if (value <= 0xFF)
            {
                buffer.Add((byte)(contextNumber << 4 | 1));
                buffer.Add((byte)value);
            }
            else if (value <= 0xFFFF)
            {
                buffer.Add((byte)(contextNumber << 4 | 2));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
            else if (value <= 0xFFFFFF)
            {
                buffer.Add((byte)(contextNumber << 4 | 3));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
            else
            {
                buffer.Add((byte)(contextNumber << 4 | 4));
                buffer.Add((byte)(value >> 24));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
        }

        private static void EncodeOpeningTag(List<byte> buffer, byte contextNumber)
        {
            buffer.Add((byte)(contextNumber << 4 | 0x0E));
        }

        private static void EncodeClosingTag(List<byte> buffer, byte contextNumber)
        {
            buffer.Add((byte)(contextNumber << 4 | 0x0F));
        }

        // ── Who-Is (Unconfirmed) ──────────────────────

        public static byte[] EncodeWhoIs(int lowLimit = -1, int highLimit = -1)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.UnconfirmedRequest << 4));
            apdu.Add((byte)BacnetUnconfirmedService.WhoIs);

            if (lowLimit >= 0 && highLimit >= 0)
            {
                EncodeContextUnsigned(apdu, 0, (uint)lowLimit);
                EncodeContextUnsigned(apdu, 1, (uint)highLimit);
            }

            return apdu.ToArray();
        }

        // ── I-Am (Unconfirmed) ────────────────────────

        public static byte[] EncodeIAm(BacnetObjectId deviceId, uint maxApdu, BacnetSegmentation segmentationSupported, uint vendorId)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.UnconfirmedRequest << 4));
            apdu.Add((byte)BacnetUnconfirmedService.IAm);

            EncodeObjectId(apdu, deviceId);
            EncodeUnsigned(apdu, maxApdu);
            EncodeEnumerated(apdu, (uint)segmentationSupported);
            EncodeUnsigned(apdu, vendorId);

            return apdu.ToArray();
        }

        // ── ReadProperty (Confirmed) ──────────────────

        public static byte[] EncodeReadProperty(byte invokeId, BacnetObjectId objectId, BacnetPropertyId propertyId, uint arrayIndex = uint.MaxValue)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.ConfirmedRequest << 4));
            apdu.Add(0x05);
            apdu.Add(invokeId);
            apdu.Add((byte)BacnetConfirmedService.ReadProperty);

            EncodeContextObjectId(apdu, 0, objectId);
            EncodeContextUnsigned(apdu, 1, (uint)propertyId);

            if (arrayIndex != uint.MaxValue)
                EncodeContextUnsigned(apdu, 2, arrayIndex);

            return apdu.ToArray();
        }

        // ── ReadPropertyMultiple (Confirmed) ──────────

        public static byte[] EncodeReadPropertyMultiple(byte invokeId, BacnetPropertyReference[] references)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.ConfirmedRequest << 4));
            apdu.Add(0x05);
            apdu.Add(invokeId);
            apdu.Add((byte)BacnetConfirmedService.ReadPropertyMultiple);

            for (int i = 0; i < references.Length; i++)
            {
                EncodeOpeningTag(apdu, 0);
                EncodeObjectId(apdu, references[i].ObjectIdentifier);
                EncodeOpeningTag(apdu, 1);
                EncodeContextUnsigned(apdu, 0, (uint)references[i].PropertyId);
                if (references[i].ArrayIndex != uint.MaxValue)
                    EncodeContextUnsigned(apdu, 1, references[i].ArrayIndex);
                EncodeClosingTag(apdu, 1);
                EncodeClosingTag(apdu, 0);
            }

            return apdu.ToArray();
        }

        // ── WriteProperty (Confirmed) ─────────────────

        public static byte[] EncodeWriteProperty(byte invokeId, BacnetObjectId objectId, BacnetPropertyId propertyId, BacnetValue value, uint priority = 0)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.ConfirmedRequest << 4));
            apdu.Add(0x05);
            apdu.Add(invokeId);
            apdu.Add((byte)BacnetConfirmedService.WriteProperty);

            EncodeContextObjectId(apdu, 0, objectId);
            EncodeContextUnsigned(apdu, 1, (uint)propertyId);

            EncodeOpeningTag(apdu, 3);
            EncodeApplicationValue(apdu, value);
            EncodeClosingTag(apdu, 3);

            if (priority > 0 && priority <= 16)
                EncodeContextUnsigned(apdu, 4, priority);

            return apdu.ToArray();
        }

        // ── WritePropertyMultiple (Confirmed) ─────────

        public static byte[] EncodeWritePropertyMultiple(byte invokeId, BacnetObjectId objectId, BacnetPropertyValue[] values)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.ConfirmedRequest << 4));
            apdu.Add(0x05);
            apdu.Add(invokeId);
            apdu.Add((byte)BacnetConfirmedService.WritePropertyMultiple);

            EncodeContextObjectId(apdu, 0, objectId);

            for (int i = 0; i < values.Length; i++)
            {
                EncodeOpeningTag(apdu, 1);
                EncodeContextUnsigned(apdu, 0, (uint)values[i].PropertyId);
                EncodeOpeningTag(apdu, 3);
                EncodeApplicationValue(apdu, values[i].Value);
                EncodeClosingTag(apdu, 3);
                EncodeClosingTag(apdu, 1);
            }

            return apdu.ToArray();
        }

        // ── SubscribeCOV (Confirmed) ──────────────────

        public static byte[] EncodeSubscribeCov(byte invokeId, uint subscriberProcessId, BacnetObjectId monitoredObjectId, bool issueConfirmedNotifications, uint lifetime)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.ConfirmedRequest << 4));
            apdu.Add(0x05);
            apdu.Add(invokeId);
            apdu.Add((byte)BacnetConfirmedService.SubscribeCOV);

            EncodeContextUnsigned(apdu, 0, subscriberProcessId);
            EncodeContextObjectId(apdu, 1, monitoredObjectId);

            if (issueConfirmedNotifications)
                apdu.Add((byte)(2 << 4 | 1));
            else
                apdu.Add((byte)(2 << 4 | 0));

            EncodeContextUnsigned(apdu, 3, lifetime);

            return apdu.ToArray();
        }

        // ── AtomicReadFile (Confirmed) ────────────────

        public static byte[] EncodeAtomicReadFile(byte invokeId, BacnetObjectId fileId, bool isRecordAccess, int startPosition, int count)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.ConfirmedRequest << 4));
            apdu.Add(0x05);
            apdu.Add(invokeId);
            apdu.Add((byte)BacnetConfirmedService.AtomicReadFile);

            EncodeContextObjectId(apdu, 0, fileId);

            if (isRecordAccess)
            {
                EncodeOpeningTag(apdu, 2);
                EncodeContextUnsigned(apdu, 0, (uint)startPosition);
                EncodeContextUnsigned(apdu, 1, (uint)count);
                EncodeClosingTag(apdu, 2);
            }
            else
            {
                EncodeOpeningTag(apdu, 1);
                EncodeContextUnsigned(apdu, 0, (uint)startPosition);
                EncodeContextUnsigned(apdu, 1, (uint)count);
                EncodeClosingTag(apdu, 1);
            }

            return apdu.ToArray();
        }

        // ── AtomicWriteFile (Confirmed) ───────────────

        public static byte[] EncodeAtomicWriteFile(byte invokeId, BacnetObjectId fileId, bool isRecordAccess, int startPosition, byte[] data)
        {
            var apdu = new List<byte>();
            apdu.Add((byte)((int)BacnetPduType.ConfirmedRequest << 4));
            apdu.Add(0x05);
            apdu.Add(invokeId);
            apdu.Add((byte)BacnetConfirmedService.AtomicWriteFile);

            EncodeContextObjectId(apdu, 0, fileId);

            if (isRecordAccess)
            {
                EncodeOpeningTag(apdu, 2);
                EncodeContextUnsigned(apdu, 0, (uint)startPosition);
                EncodeOpeningTag(apdu, 1);
                for (int i = 0; i < data.Length; i++)
                    apdu.Add(data[i]);
                EncodeClosingTag(apdu, 1);
                EncodeClosingTag(apdu, 2);
            }
            else
            {
                EncodeOpeningTag(apdu, 1);
                EncodeContextUnsigned(apdu, 0, (uint)startPosition);
                EncodeOpeningTag(apdu, 1);
                for (int i = 0; i < data.Length; i++)
                    apdu.Add(data[i]);
                EncodeClosingTag(apdu, 1);
                EncodeClosingTag(apdu, 1);
            }

            return apdu.ToArray();
        }

        // ── SimpleAck ─────────────────────────────────

        public static byte[] EncodeSimpleAck(byte invokeId, BacnetConfirmedService service)
        {
            return new byte[]
            {
                (byte)((int)BacnetPduType.SimpleAck << 4),
                invokeId,
                (byte)service
            };
        }

        // ── Application value encoding ────────────────

        private static void EncodeApplicationValue(List<byte> buffer, BacnetValue value)
        {
            switch (value.Tag)
            {
                case BacnetApplicationTag.Null:
                    buffer.Add((byte)((int)BacnetApplicationTag.Null << 4 | 0));
                    break;
                case BacnetApplicationTag.Boolean:
                    EncodeBoolean(buffer, value.Data is bool b && b);
                    break;
                case BacnetApplicationTag.Unsigned:
                    EncodeUnsigned(buffer, Convert.ToUInt32(value.Data));
                    break;
                case BacnetApplicationTag.Signed:
                    EncodeSigned(buffer, Convert.ToInt32(value.Data));
                    break;
                case BacnetApplicationTag.Real:
                    EncodeReal(buffer, Convert.ToSingle(value.Data));
                    break;
                case BacnetApplicationTag.Double:
                    EncodeDouble(buffer, Convert.ToDouble(value.Data));
                    break;
                case BacnetApplicationTag.CharacterString:
                    EncodeCharacterString(buffer, Convert.ToString(value.Data) ?? "");
                    break;
                case BacnetApplicationTag.OctetString:
                    EncodeOctetString(buffer, (byte[])value.Data);
                    break;
                case BacnetApplicationTag.Enumerated:
                    EncodeEnumerated(buffer, Convert.ToUInt32(value.Data));
                    break;
                case BacnetApplicationTag.ObjectId:
                    buffer.Add((byte)((int)BacnetApplicationTag.ObjectId << 4 | 4));
                    EncodeObjectId(buffer, (BacnetObjectId)value.Data);
                    break;
            }
        }

        private static void EncodeSigned(List<byte> buffer, int value)
        {
            if (value >= -128 && value <= 127)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Signed << 4 | 0));
                buffer.Add((byte)value);
            }
            else if (value >= -32768 && value <= 32767)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Signed << 4 | 1));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
            else if (value >= -8388608 && value <= 8388607)
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Signed << 4 | 2));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
            else
            {
                buffer.Add((byte)((int)BacnetApplicationTag.Signed << 4 | 3));
                buffer.Add((byte)(value >> 24));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)value);
            }
        }

        // ── Response decoding ─────────────────────────

        public static BacnetApduResponse DecodeApdu(byte[] data, int offset, int length)
        {
            var response = new BacnetApduResponse();
            if (length < 2)
            {
                response.IsValid = false;
                return response;
            }

            int pos = offset;
            response.PduType = (BacnetPduType)((data[pos] >> 4) & 0x0F);

            switch (response.PduType)
            {
                case BacnetPduType.UnconfirmedRequest:
                    pos++;
                    response.ServiceChoice = data[pos++];
                    response.Values = DecodeApplicationValues(data, ref pos, offset + length);
                    break;

                case BacnetPduType.ConfirmedRequest:
                    response.MaxSegments = (byte)((data[pos] >> 4) & 0x07);
                    pos++;
                    response.MaxApduLength = data[pos++];
                    response.InvokeId = data[pos++];
                    response.ConfirmedService = (BacnetConfirmedService)data[pos++];
                    response.Values = DecodeApplicationValues(data, ref pos, offset + length);
                    break;

                case BacnetPduType.SimpleAck:
                    pos++;
                    response.InvokeId = data[pos++];
                    response.ConfirmedService = (BacnetConfirmedService)data[pos++];
                    break;

                case BacnetPduType.ComplexAck:
                    pos++;
                    response.InvokeId = data[pos++];
                    response.ConfirmedService = (BacnetConfirmedService)data[pos++];
                    response.Values = DecodeApplicationValues(data, ref pos, offset + length);
                    break;

                case BacnetPduType.Error:
                    pos++;
                    response.InvokeId = data[pos++];
                    response.ConfirmedService = (BacnetConfirmedService)data[pos++];
                    if (pos + 2 < offset + length)
                    {
                        response.ErrorClass = (BacnetErrorClass)((data[pos] << 8) | data[pos + 1]);
                        pos += 2;
                        if (pos + 2 < offset + length)
                        {
                            response.ErrorCode = (BacnetErrorCode)((data[pos] << 8) | data[pos + 1]);
                            pos += 2;
                        }
                    }
                    break;

                case BacnetPduType.Reject:
                    pos++;
                    response.InvokeId = data[pos++];
                    if (pos < offset + length)
                        response.RejectReason = (BacnetRejectReason)data[pos++];
                    break;

                case BacnetPduType.Abort:
                    pos++;
                    response.InvokeId = data[pos++];
                    if (pos < offset + length)
                        response.AbortReason = (BacnetAbortReason)data[pos++];
                    break;
            }

            response.IsValid = true;
            return response;
        }

        private static BacnetValue[] DecodeApplicationValues(byte[] data, ref int offset, int endOffset)
        {
            var values = new List<BacnetValue>();

            while (offset < endOffset)
            {
                byte tagByte = data[offset];
                int tagNumber = (tagByte >> 4) & 0x0F;

                if (tagNumber == 0x0F)
                {
                    offset++;
                    continue;
                }

                int lenValue = tagByte & 0x07;
                bool isContext = (tagByte & 0x08) != 0;

                if (isContext)
                {
                    offset++;
                    values.Add(new BacnetValue
                    {
                        Tag = (BacnetApplicationTag)0xFF,
                        Data = DecodeContextValue(data, ref offset, tagNumber, lenValue)
                    });
                }
                else
                {
                    var tag = (BacnetApplicationTag)tagNumber;
                    offset++;
                    object data2;

                    switch (tag)
                    {
                        case BacnetApplicationTag.Null:
                            data2 = null;
                            break;

                        case BacnetApplicationTag.Boolean:
                            data2 = lenValue != 0;
                            break;

                        case BacnetApplicationTag.Unsigned:
                            data2 = DecodeLengthValue(data, ref offset, lenValue);
                            break;

                        case BacnetApplicationTag.Signed:
                            data2 = DecodeSignedValue(data, ref offset, lenValue);
                            break;

                        case BacnetApplicationTag.Real:
                            {
                                byte[] b = new byte[4];
                                for (int i = 0; i < 4; i++) b[i] = data[offset + i];
                                if (BitConverter.IsLittleEndian)
                                {
                                    byte t = b[0]; b[0] = b[3]; b[3] = t;
                                    t = b[1]; b[1] = b[2]; b[2] = t;
                                }
                                offset += 4;
                                data2 = BitConverter.ToSingle(b, 0);
                            }
                            break;

                        case BacnetApplicationTag.Double:
                            {
                                byte[] b = new byte[8];
                                for (int i = 0; i < 8; i++) b[i] = data[offset + i];
                                if (BitConverter.IsLittleEndian)
                                {
                                    for (int i = 0; i < 4; i++)
                                    {
                                        byte t = b[i]; b[i] = b[7 - i]; b[7 - i] = t;
                                    }
                                }
                                offset += 8;
                                data2 = BitConverter.ToDouble(b, 0);
                            }
                            break;

                        case BacnetApplicationTag.OctetString:
                            {
                                int strLen = lenValue;
                                if (strLen == 5)
                                {
                                    strLen = data[offset];
                                    offset++;
                                }
                                byte[] octets = new byte[strLen];
                                Buffer.BlockCopy(data, offset, octets, 0, strLen);
                                offset += strLen;
                                data2 = octets;
                            }
                            break;

                        case BacnetApplicationTag.CharacterString:
                            {
                                int strLen = lenValue;
                                if (strLen == 5)
                                {
                                    strLen = data[offset];
                                    offset++;
                                }
                                byte encoding = data[offset++];
                                strLen--;
                                string str = Encoding.UTF8.GetString(data, offset, strLen);
                                offset += strLen;
                                data2 = str;
                            }
                            break;

                        case BacnetApplicationTag.Enumerated:
                            data2 = DecodeLengthValue(data, ref offset, lenValue);
                            break;

                        case BacnetApplicationTag.ObjectId:
                            data2 = DecodeObjectId(data, ref offset);
                            break;

                        case BacnetApplicationTag.Date:
                            {
                                int year = data[offset] == 0xFF ? 0 : data[offset] + 1900;
                                int month = data[offset + 1];
                                int day = data[offset + 2];
                                int dow = data[offset + 3];
                                offset += 4;
                                data2 = new DateTime(year, month == 0xFF ? 1 : month, day == 0xFF ? 1 : day);
                            }
                            break;

                        case BacnetApplicationTag.Time:
                            {
                                int hour = data[offset] == 0xFF ? 0 : data[offset];
                                int min = data[offset + 1] == 0xFF ? 0 : data[offset + 1];
                                int sec = data[offset + 2] == 0xFF ? 0 : data[offset + 2];
                                offset += 4;
                                data2 = new TimeSpan(hour, min, sec);
                            }
                            break;

                        default:
                            offset += lenValue;
                            data2 = null;
                            break;
                    }

                    values.Add(new BacnetValue { Tag = tag, Data = data2 });
                }
            }

            return values.ToArray();
        }

        private static object DecodeContextValue(byte[] data, ref int offset, int contextNumber, int lenValue)
        {
            if (lenValue == 4)
            {
                return DecodeObjectId(data, ref offset);
            }
            else if (lenValue >= 1 && lenValue <= 4)
            {
                return DecodeLengthValue(data, ref offset, lenValue);
            }
            else
            {
                offset += lenValue;
                return contextNumber;
            }
        }

        private static uint DecodeLengthValue(byte[] data, ref int offset, int length)
        {
            uint value = 0;
            for (int i = 0; i < length + 1; i++)
                value = (value << 8) | data[offset + i];
            offset += length + 1;
            return value;
        }

        private static int DecodeSignedValue(byte[] data, ref int offset, int length)
        {
            int byteCount = length + 1;
            int value = 0;
            bool negative = (data[offset] & 0x80) != 0;
            for (int i = 0; i < byteCount; i++)
                value = (value << 8) | data[offset + i];
            if (negative)
            {
                switch (byteCount)
                {
                    case 1: value |= unchecked((int)0xFFFFFF00); break;
                    case 2: value |= unchecked((int)0xFFFF0000); break;
                    case 3: value |= unchecked((int)0xFF000000); break;
                }
            }
            offset += byteCount;
            return value;
        }
    }

    public class BacnetApduResponse
    {
        public bool IsValid { get; set; }
        public BacnetPduType PduType { get; set; }
        public byte InvokeId { get; set; }
        public int ServiceChoice { get; set; }
        public BacnetConfirmedService ConfirmedService { get; set; }
        public BacnetRejectReason RejectReason { get; set; }
        public BacnetAbortReason AbortReason { get; set; }
        public BacnetErrorClass ErrorClass { get; set; }
        public BacnetErrorCode ErrorCode { get; set; }
        public byte MaxSegments { get; set; }
        public byte MaxApduLength { get; set; }
        public BacnetValue[] Values { get; set; } = Array.Empty<BacnetValue>();
    }

    public enum BacnetSegmentation : byte
    {
        Both = 0,
        Transmit = 1,
        Receive = 2,
        NoSegmentation = 3
    }
}
