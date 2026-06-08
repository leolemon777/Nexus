using System;
using System.Collections.Generic;

namespace Nexus.Iec104
{
    public class Iec104InformationObject
    {
        public int Address { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();

        public override string ToString()
            => $"IOA={Address} Data={BitConverter.ToString(Data)}";
    }

    public class Iec104Asdu
    {
        public TypeId TypeId { get; set; }
        public byte Vsq { get; set; }
        public CauseOfTransmission Cause { get; set; }
        public byte OriginatorAddress { get; set; }
        public int CommonAddress { get; set; }
        public bool IsNegative { get; set; }
        public bool IsTest { get; set; }
        public List<Iec104InformationObject> Objects { get; set; } = new List<Iec104InformationObject>();

        public int ObjectCount => Vsq & 0x7F;
        public bool IsSequence => (Vsq & 0x80) != 0;

        public byte[] Encode()
        {
            bool seq = IsSequence;
            int count = Objects.Count;
            int asduLen = 1 + 1 + 2 + 2; // TypeID + VSQ + COT + CA

            if (seq)
            {
                asduLen += 3; // first IOA only
                for (int i = 0; i < count; i++)
                    asduLen += Objects[i].Data.Length;
            }
            else
            {
                for (int i = 0; i < count; i++)
                    asduLen += 3 + Objects[i].Data.Length;
            }

            byte[] buf = new byte[asduLen];
            int pos = 0;

            buf[pos++] = (byte)TypeId;
            buf[pos++] = (byte)(count & 0x7F | (seq ? 0x80 : 0));

            byte cotByte = (byte)((byte)Cause & 0x3F);
            if (IsNegative) cotByte |= 0x40;
            if (IsTest) cotByte |= 0x80;
            buf[pos++] = cotByte;
            buf[pos++] = OriginatorAddress;

            buf[pos++] = (byte)(CommonAddress & 0xFF);
            buf[pos++] = (byte)((CommonAddress >> 8) & 0xFF);

            if (seq && count > 0)
            {
                var first = Objects[0];
                buf[pos++] = (byte)(first.Address & 0xFF);
                buf[pos++] = (byte)((first.Address >> 8) & 0xFF);
                buf[pos++] = (byte)((first.Address >> 16) & 0xFF);
                Buffer.BlockCopy(first.Data, 0, buf, pos, first.Data.Length);
                pos += first.Data.Length;
                for (int i = 1; i < count; i++)
                {
                    Buffer.BlockCopy(Objects[i].Data, 0, buf, pos, Objects[i].Data.Length);
                    pos += Objects[i].Data.Length;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    var obj = Objects[i];
                    buf[pos++] = (byte)(obj.Address & 0xFF);
                    buf[pos++] = (byte)((obj.Address >> 8) & 0xFF);
                    buf[pos++] = (byte)((obj.Address >> 16) & 0xFF);
                    Buffer.BlockCopy(obj.Data, 0, buf, pos, obj.Data.Length);
                    pos += obj.Data.Length;
                }
            }

            return buf;
        }

        public static Iec104Asdu Decode(byte[] data, int offset)
        {
            if (data == null || data.Length - offset < 6)
                throw new ArgumentException("ASDU 数据长度不足");

            var asdu = new Iec104Asdu();
            int pos = offset;

            asdu.TypeId = (TypeId)data[pos++];
            asdu.Vsq = data[pos++];

            byte cotByte = data[pos++];
            asdu.Cause = (CauseOfTransmission)(cotByte & 0x3F);
            asdu.IsNegative = (cotByte & 0x40) != 0;
            asdu.IsTest = (cotByte & 0x80) != 0;
            asdu.OriginatorAddress = data[pos++];

            asdu.CommonAddress = data[pos] | (data[pos + 1] << 8);
            pos += 2;

            int count = asdu.Vsq & 0x7F;
            bool seq = (asdu.Vsq & 0x80) != 0;

            int dataBytesPerObject = GetDataLength(asdu.TypeId);

            if (seq && count > 0)
            {
                if (data.Length - pos < 3) return asdu;
                int baseAddr = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16);
                pos += 3;

                for (int i = 0; i < count; i++)
                {
                    if (data.Length - pos < dataBytesPerObject) break;
                    var obj = new Iec104InformationObject
                    {
                        Address = baseAddr + i,
                        Data = new byte[dataBytesPerObject]
                    };
                    Buffer.BlockCopy(data, pos, obj.Data, 0, dataBytesPerObject);
                    pos += dataBytesPerObject;
                    asdu.Objects.Add(obj);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    if (data.Length - pos < 3 + dataBytesPerObject) break;
                    int addr = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16);
                    pos += 3;
                    var obj = new Iec104InformationObject
                    {
                        Address = addr,
                        Data = new byte[dataBytesPerObject]
                    };
                    Buffer.BlockCopy(data, pos, obj.Data, 0, dataBytesPerObject);
                    pos += dataBytesPerObject;
                    asdu.Objects.Add(obj);
                }
            }

            return asdu;
        }

        private static int GetDataLength(TypeId typeId)
        {
            switch (typeId)
            {
                case TypeId.M_SP_NA_1: return 1; // SIQ
                case TypeId.M_DP_NA_1: return 1; // DIQ
                case TypeId.M_ME_NA_1: return 3; // NVA(2) + QDS(1)
                case TypeId.M_ME_NC_1: return 5; // Float(4) + QDS(1)
                case TypeId.C_SC_NA_1: return 1; // SCO
                case TypeId.C_DC_NA_1: return 1; // DCO
                case TypeId.C_SE_NA_1: return 3; // NVA(2) + QOS(1)
                case TypeId.C_IC_NA_1: return 1; // QOI
                case TypeId.C_RD_NA_1: return 0; // no data
                default: return 0;
            }
        }

        // ── Single Point ──────────────────────────

        public static SinglePointInfo DecodeSinglePoint(Iec104InformationObject obj)
        {
            byte siq = obj.Data[0];
            return new SinglePointInfo
            {
                Address = obj.Address,
                Value = (siq & 0x01) != 0,
                Quality = (QualityFlags)(siq & 0x70),
            };
        }

        // ── Double Point ──────────────────────────

        public static DoublePointInfo DecodeDoublePoint(Iec104InformationObject obj)
        {
            byte diq = obj.Data[0];
            return new DoublePointInfo
            {
                Address = obj.Address,
                Value = (byte)(diq & 0x03),
                Quality = (QualityFlags)(diq & 0x70),
            };
        }

        // ── Measured Value Normalized ──────────────

        public static MeasuredValueInfo DecodeMeasuredNormalized(Iec104InformationObject obj)
        {
            short raw = (short)(obj.Data[0] | (obj.Data[1] << 8));
            float value = raw / 32767.0f;
            QualityFlags quality = (QualityFlags)(obj.Data[2] & 0x1F);
            return new MeasuredValueInfo
            {
                Address = obj.Address,
                Value = value,
                Quality = quality,
            };
        }

        // ── Measured Value Float ───────────────────

        public static MeasuredValueInfo DecodeMeasuredFloat(Iec104InformationObject obj)
        {
            int raw = obj.Data[0] | (obj.Data[1] << 8) | (obj.Data[2] << 16) | (obj.Data[3] << 24);
            float value;
            unsafe { value = *(float*)&raw; }
            QualityFlags quality = (QualityFlags)(obj.Data[4] & 0x1F);
            return new MeasuredValueInfo
            {
                Address = obj.Address,
                Value = value,
                Quality = quality,
            };
        }

        // ── Build helpers ─────────────────────────

        public static Iec104Asdu BuildSingleCommand(int commonAddr, int ioa, bool value, byte originator = 0)
        {
            var asdu = new Iec104Asdu
            {
                TypeId = TypeId.C_SC_NA_1,
                Vsq = 1,
                Cause = CauseOfTransmission.Activation,
                OriginatorAddress = originator,
                CommonAddress = commonAddr,
            };
            asdu.Objects.Add(new Iec104InformationObject
            {
                Address = ioa,
                Data = new byte[] { (byte)(value ? 0x01 : 0x00) }
            });
            return asdu;
        }

        public static Iec104Asdu BuildDoubleCommand(int commonAddr, int ioa, bool on, byte originator = 0)
        {
            var asdu = new Iec104Asdu
            {
                TypeId = TypeId.C_DC_NA_1,
                Vsq = 1,
                Cause = CauseOfTransmission.Activation,
                OriginatorAddress = originator,
                CommonAddress = commonAddr,
            };
            asdu.Objects.Add(new Iec104InformationObject
            {
                Address = ioa,
                Data = new byte[] { (byte)(on ? 0x02 : 0x01) }
            });
            return asdu;
        }

        public static Iec104Asdu BuildSetpointNormalized(int commonAddr, int ioa, float value, byte originator = 0)
        {
            short raw = (short)(value * 32767.0f);
            var asdu = new Iec104Asdu
            {
                TypeId = TypeId.C_SE_NA_1,
                Vsq = 1,
                Cause = CauseOfTransmission.Activation,
                OriginatorAddress = originator,
                CommonAddress = commonAddr,
            };
            asdu.Objects.Add(new Iec104InformationObject
            {
                Address = ioa,
                Data = new byte[] { (byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF), 0x00 }
            });
            return asdu;
        }

        public static Iec104Asdu BuildGeneralInterrogation(int commonAddr, byte groupNumber = 0, byte originator = 0)
        {
            var asdu = new Iec104Asdu
            {
                TypeId = TypeId.C_IC_NA_1,
                Vsq = 1,
                Cause = CauseOfTransmission.Activation,
                OriginatorAddress = originator,
                CommonAddress = commonAddr,
            };
            asdu.Objects.Add(new Iec104InformationObject
            {
                Address = 0,
                Data = new byte[] { (byte)(groupNumber == 0 ? 20 : (groupNumber & 0x1F)) }
            });
            return asdu;
        }

        public static Iec104Asdu BuildReadCommand(int commonAddr, int ioa, byte originator = 0)
        {
            var asdu = new Iec104Asdu
            {
                TypeId = TypeId.C_RD_NA_1,
                Vsq = 1,
                Cause = CauseOfTransmission.Request,
                OriginatorAddress = originator,
                CommonAddress = commonAddr,
            };
            asdu.Objects.Add(new Iec104InformationObject
            {
                Address = ioa,
                Data = Array.Empty<byte>()
            });
            return asdu;
        }
    }
}
