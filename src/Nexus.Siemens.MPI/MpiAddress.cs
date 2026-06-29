using System;
using Nexus;

namespace Nexus.Siemens.MPI
{
    public sealed class MpiAddress : IDataAddress
    {
        public string Original { get; }
        public MpiArea Area { get; }
        public ushort DbNumber { get; }
        public ushort StartByte { get; }
        public byte BitOffset { get; }

        public MpiAddress(string original, MpiArea area, ushort dbNumber, ushort startByte, byte bitOffset = 0)
        {
            Original = original;
            Area = area;
            DbNumber = dbNumber;
            StartByte = startByte;
            BitOffset = bitOffset;
        }
    }

    public enum MpiArea
    {
        I, Q, M, DB, T, C, V
    }

    public sealed class MpiAddressParser : IAddressParser<MpiAddress>
    {
        public MpiAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = AddressContext.ExtractCoreAddress(address).Trim().ToUpperInvariant();

            if (address.StartsWith("DB"))
            {
                int dotIdx = address.IndexOf('.', 2);
                if (dotIdx < 0) throw new AddressParseException(address, "DB 格式: DB1.0 或 DB1.DBW0");
                ushort db = ushort.Parse(address.Substring(2, dotIdx - 2));
                string rest = address.Substring(dotIdx + 1);
                if (rest.StartsWith("DBX"))
                {
                    string[] parts = rest.Substring(3).Split('.');
                    ushort byteAddr = ushort.Parse(parts[0]);
                    byte bit = parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0;
                    return new MpiAddress(original, MpiArea.DB, db, byteAddr, bit);
                }
                if (rest.StartsWith("DBW"))
                    return new MpiAddress(original, MpiArea.DB, db, ushort.Parse(rest.Substring(3)));
                if (rest.StartsWith("DBD"))
                    return new MpiAddress(original, MpiArea.DB, db, ushort.Parse(rest.Substring(3)));
                return new MpiAddress(original, MpiArea.DB, db, ushort.Parse(rest));
            }

            if (address.StartsWith("I") && address.Length > 1)
            {
                string[] parts = address.Substring(1).Split('.');
                return new MpiAddress(original, MpiArea.I, 0, ushort.Parse(parts[0]), parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0);
            }
            if (address.StartsWith("Q") && address.Length > 1)
            {
                string[] parts = address.Substring(1).Split('.');
                return new MpiAddress(original, MpiArea.Q, 0, ushort.Parse(parts[0]), parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0);
            }
            if (address.StartsWith("M") && address.Length > 1)
            {
                string[] parts = address.Substring(1).Split('.');
                return new MpiAddress(original, MpiArea.M, 0, ushort.Parse(parts[0]), parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0);
            }
            if (address.StartsWith("V") && address.Length > 1)
            {
                string[] parts = address.Substring(1).Split('.');
                return new MpiAddress(original, MpiArea.V, 0, ushort.Parse(parts[0]), parts.Length > 1 ? byte.Parse(parts[1]) : (byte)0);
            }
            if (address.StartsWith("T") && address.Length > 1)
                return new MpiAddress(original, MpiArea.T, 0, ushort.Parse(address.Substring(1)));
            if (address.StartsWith("C") && address.Length > 1)
                return new MpiAddress(original, MpiArea.C, 0, ushort.Parse(address.Substring(1)));

            throw new AddressParseException(address, $"不支持的 MPI 地址: {address}");
        }

        public bool TryParse(string address, out MpiAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
