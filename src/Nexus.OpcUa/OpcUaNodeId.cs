#nullable disable warnings
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Nexus.OpcUa
{
    public enum OpcUaNodeIdType
    {
        TwoByte,
        FourByte,
        Numeric,
        String,
        Guid,
        Opaque
    }

    public class OpcUaNodeId
    {
        public ushort NamespaceIndex { get; set; }
        public object Identifier { get; set; }
        public OpcUaNodeIdType IdType { get; set; }

        public OpcUaNodeId() { }

        public OpcUaNodeId(ushort ns, uint numericId)
        {
            NamespaceIndex = ns;
            Identifier = numericId;
            IdType = OpcUaNodeIdType.Numeric;
        }

        public OpcUaNodeId(ushort ns, string stringId)
        {
            NamespaceIndex = ns;
            Identifier = stringId;
            IdType = OpcUaNodeIdType.String;
        }

        public OpcUaNodeId(ushort ns, Guid guidId)
        {
            NamespaceIndex = ns;
            Identifier = guidId;
            IdType = OpcUaNodeIdType.Guid;
        }

        public OpcUaNodeId(ushort ns, byte[] opaqueId)
        {
            NamespaceIndex = ns;
            Identifier = opaqueId;
            IdType = OpcUaNodeIdType.Opaque;
        }

        public static OpcUaNodeId Parse(string nodeIdString)
        {
            if (string.IsNullOrEmpty(nodeIdString))
                throw new ArgumentException("NodeId string cannot be empty");

            var result = new OpcUaNodeId();
            var parts = nodeIdString.Split(';');

            if (parts.Length == 2)
            {
                var nsPart = parts[0].Trim();
                var idPart = parts[1].Trim();

                if (!nsPart.StartsWith("ns=", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Invalid NodeId format: missing 'ns='");
                result.NamespaceIndex = ushort.Parse(nsPart.Substring(3), CultureInfo.InvariantCulture);

                ParseIdentifier(result, idPart);
            }
            else if (parts.Length == 1)
            {
                result.NamespaceIndex = 0;
                ParseIdentifier(result, parts[0].Trim());
            }
            else
            {
                throw new FormatException("Invalid NodeId format: " + nodeIdString);
            }

            return result;
        }

        private static void ParseIdentifier(OpcUaNodeId nodeId, string idPart)
        {
            if (idPart.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
            {
                nodeId.Identifier = idPart.Substring(2);
                nodeId.IdType = OpcUaNodeIdType.String;
            }
            else if (idPart.StartsWith("i=", StringComparison.OrdinalIgnoreCase))
            {
                nodeId.Identifier = uint.Parse(idPart.Substring(2), CultureInfo.InvariantCulture);
                nodeId.IdType = OpcUaNodeIdType.Numeric;
            }
            else if (idPart.StartsWith("g=", StringComparison.OrdinalIgnoreCase))
            {
                nodeId.Identifier = Guid.Parse(idPart.Substring(2));
                nodeId.IdType = OpcUaNodeIdType.Guid;
            }
            else if (uint.TryParse(idPart, out uint numId))
            {
                nodeId.Identifier = numId;
                nodeId.IdType = OpcUaNodeIdType.Numeric;
            }
            else
            {
                nodeId.Identifier = idPart;
                nodeId.IdType = OpcUaNodeIdType.String;
            }
        }

        public byte[] Encode()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                EncodeTo(w);
                return ms.ToArray();
            }
        }

        public void EncodeTo(BinaryWriter w)
        {
            switch (IdType)
            {
                case OpcUaNodeIdType.Numeric:
                    uint id = Convert.ToUInt32(Identifier);
                    if (NamespaceIndex == 0 && id <= 255)
                    {
                        w.Write((byte)0x00);
                        w.Write((byte)id);
                    }
                    else if (NamespaceIndex <= 255 && id <= 65535)
                    {
                        w.Write((byte)0x01);
                        w.Write((byte)NamespaceIndex);
                        w.Write((ushort)id);
                    }
                    else
                    {
                        w.Write((byte)0x02);
                        w.Write(NamespaceIndex);
                        w.Write(id);
                    }
                    break;

                case OpcUaNodeIdType.String:
                    w.Write((byte)0x03);
                    w.Write(NamespaceIndex);
                    WriteString(w, (string)Identifier);
                    break;

                case OpcUaNodeIdType.Guid:
                    w.Write((byte)0x04);
                    w.Write(NamespaceIndex);
                    var guidBytes = ((Guid)Identifier).ToByteArray();
                    w.Write(guidBytes);
                    break;

                case OpcUaNodeIdType.Opaque:
                    w.Write((byte)0x05);
                    w.Write(NamespaceIndex);
                    var opaque = (byte[])Identifier;
                    w.Write(opaque.Length);
                    w.Write(opaque);
                    break;

                default:
                    w.Write((byte)0x00);
                    w.Write((byte)0);
                    break;
            }
        }

        internal static void WriteString(BinaryWriter w, string value)
        {
            if (value == null)
            {
                w.Write(-1);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                w.Write(bytes.Length);
                w.Write(bytes);
            }
        }

        internal static string ReadString(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length < 0) return null;
            if (length == 0) return string.Empty;
            var bytes = r.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static readonly long OpcUaEpochTicks = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        internal static void WriteDateTime(BinaryWriter w, DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            long opcTicks = utc.Ticks - OpcUaEpochTicks;
            w.Write(opcTicks);
        }

        internal static DateTime FromOpcUaTimestamp(long opcTicks)
        {
            return new DateTime(opcTicks + OpcUaEpochTicks, DateTimeKind.Utc).ToLocalTime();
        }

        public override string ToString()
        {
            switch (IdType)
            {
                case OpcUaNodeIdType.Numeric:
                    return $"ns={NamespaceIndex};i={Identifier}";
                case OpcUaNodeIdType.String:
                    return $"ns={NamespaceIndex};s={Identifier}";
                case OpcUaNodeIdType.Guid:
                    return $"ns={NamespaceIndex};g={Identifier}";
                case OpcUaNodeIdType.Opaque:
                    return $"ns={NamespaceIndex};b={BitConverter.ToString((byte[])Identifier).Replace("-", "")}";
                default:
                    return $"ns={NamespaceIndex};i={Identifier}";
            }
        }

        public override bool Equals(object obj)
        {
            if (!(obj is OpcUaNodeId other)) return false;
            if (NamespaceIndex != other.NamespaceIndex) return false;
            if (IdType != other.IdType) return false;
            switch (IdType)
            {
                case OpcUaNodeIdType.Numeric:
                    return Convert.ToUInt32(Identifier) == Convert.ToUInt32(other.Identifier);
                case OpcUaNodeIdType.String:
                    return string.Equals((string)Identifier, (string)other.Identifier, StringComparison.Ordinal);
                case OpcUaNodeIdType.Guid:
                    return (Guid)Identifier == (Guid)other.Identifier;
                case OpcUaNodeIdType.Opaque:
                    byte[] a = (byte[])Identifier;
                    byte[] b = (byte[])other.Identifier;
                    if (a.Length != b.Length) return false;
                    for (int i = 0; i < a.Length; i++) { if (a[i] != b[i]) return false; }
                    return true;
                default:
                    return Equals(Identifier, other.Identifier);
            }
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + NamespaceIndex;
                hash = hash * 31 + (int)IdType;
                if (Identifier != null)
                {
                    switch (IdType)
                    {
                        case OpcUaNodeIdType.Numeric:
                            hash = hash * 31 + Convert.ToUInt32(Identifier).GetHashCode();
                            break;
                        case OpcUaNodeIdType.String:
                            hash = hash * 31 + ((string)Identifier).GetHashCode();
                            break;
                        case OpcUaNodeIdType.Guid:
                            hash = hash * 31 + ((Guid)Identifier).GetHashCode();
                            break;
                        default:
                            hash = hash * 31 + Identifier.GetHashCode();
                            break;
                    }
                }
                return hash;
            }
        }
    }
}
