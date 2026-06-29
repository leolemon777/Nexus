using System;
using Nexus;

namespace Nexus.Siemens.S7Plus
{
    public sealed class S7PlusAddress : IDataAddress
    {
        public string Original { get; }
        public S7PlusArea Area { get; }
        public ushort DbNumber { get; }
        public ushort StartByte { get; }
        public byte BitOffset { get; }
        public string SymbolicName { get; }

        public S7PlusAddress(string original, S7PlusArea area, ushort dbNumber, ushort startByte, byte bitOffset = 0, string symbolicName = "")
        {
            Original = original;
            Area = area;
            DbNumber = dbNumber;
            StartByte = startByte;
            BitOffset = bitOffset;
            SymbolicName = symbolicName;
        }
    }

    public enum S7PlusArea { DB, I, Q, M, T, C, V }

    public sealed class S7PlusAddressParser : IAddressParser<S7PlusAddress>
    {
        public S7PlusAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = AddressContext.ExtractCoreAddress(address).Trim().ToUpperInvariant();

            if (address.StartsWith("DB"))
            {
                int dotIdx = address.IndexOf('.', 2);
                if (dotIdx < 0) throw new AddressParseException(address, "DB 地址格式: DB1.DBX0.0");
                ushort db = ushort.Parse(address.Substring(2, dotIdx - 2));
                string rest = address.Substring(dotIdx + 1);
                if (rest.StartsWith("DBX"))
                {
                    string[] parts = rest.Substring(3).Split('.');
                    ushort byteAddr = ushort.Parse(parts[0]);
                    byte bit = parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0;
                    return new S7PlusAddress(original, S7PlusArea.DB, db, byteAddr, bit);
                }
                if (rest.StartsWith("DBW"))
                    return new S7PlusAddress(original, S7PlusArea.DB, db, ushort.Parse(rest.Substring(3)));
                if (rest.StartsWith("DBD"))
                    return new S7PlusAddress(original, S7PlusArea.DB, db, ushort.Parse(rest.Substring(3)));
                return new S7PlusAddress(original, S7PlusArea.DB, db, ushort.Parse(rest));
            }

            if (address.StartsWith("I") && address.Length > 1)
            {
                string[] parts = address.Substring(1).Split('.');
                return new S7PlusAddress(original, S7PlusArea.I, 0, ushort.Parse(parts[0]), parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0);
            }
            if (address.StartsWith("Q") && address.Length > 1)
            {
                string[] parts = address.Substring(1).Split('.');
                return new S7PlusAddress(original, S7PlusArea.Q, 0, ushort.Parse(parts[0]), parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0);
            }
            if (address.StartsWith("M") && address.Length > 1)
            {
                string[] parts = address.Substring(1).Split('.');
                return new S7PlusAddress(original, S7PlusArea.M, 0, ushort.Parse(parts[0]), parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0);
            }
            if (address.StartsWith("V") && address.Length > 1)
            {
                string[] parts = address.Substring(1).Split('.');
                return new S7PlusAddress(original, S7PlusArea.V, 0, ushort.Parse(parts[0]), parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0);
            }
            if (address.StartsWith("T") && address.Length > 1)
                return new S7PlusAddress(original, S7PlusArea.T, 0, ushort.Parse(address.Substring(1)));
            if (address.StartsWith("C") && address.Length > 1)
                return new S7PlusAddress(original, S7PlusArea.C, 0, ushort.Parse(address.Substring(1)));

            throw new AddressParseException(address, $"不支持的 S7 Plus 地址格式: {address}");
        }

        public bool TryParse(string address, out S7PlusAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
