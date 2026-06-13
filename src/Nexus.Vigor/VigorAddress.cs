using System;

namespace Nexus.Vigor
{
    public sealed class VigorAddress
    {
        public string Prefix { get; }
        public int Number { get; }
        public byte DataCode { get; }
        public bool IsBit { get; }

        private VigorAddress(string prefix, int number, byte dataCode, bool isBit)
        {
            Prefix = prefix;
            Number = number;
            DataCode = dataCode;
            IsBit = isBit;
        }

        public static VigorAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("Address is empty");

            string addr = address.Trim().ToUpperInvariant();

            if (addr.Length < 2)
                throw new ArgumentException($"Invalid Vigor address: {address}");

            int digitStart = 0;
            for (int i = 1; i < addr.Length; i++)
            {
                if (char.IsDigit(addr[i]))
                {
                    digitStart = i;
                    break;
                }
                if (i == addr.Length - 1)
                    throw new ArgumentException($"Invalid Vigor address: {address}");
            }

            string prefix = addr.Substring(0, digitStart);
            string numStr = addr.Substring(digitStart);
            if (!int.TryParse(numStr, out int num) || num < 0)
                throw new ArgumentException($"Invalid address number in: {address}");

            switch (prefix)
            {
                case "X":   return new VigorAddress("X", num, 0x90, true);
                case "Y":   return new VigorAddress("Y", num, 0x91, true);
                case "M":   return new VigorAddress("M", num, num >= 9000 ? (byte)0x94 : (byte)0x92, true);
                case "S":   return new VigorAddress("S", num, 0x93, true);
                case "SM":  return new VigorAddress("SM", num, 0x94, true);
                case "TC":  return new VigorAddress("TC", num, 0x98, true);
                case "TS":  return new VigorAddress("TS", num, 0x99, true);
                case "CC":  return new VigorAddress("CC", num, 0x9C, true);
                case "CS":  return new VigorAddress("CS", num, 0x9D, true);
                case "D":   return new VigorAddress("D", num, num >= 9000 ? (byte)0xA1 : (byte)0xA0, false);
                case "SD":  return new VigorAddress("SD", num, 0xA1, false);
                case "R":   return new VigorAddress("R", num, 0xA2, false);
                case "T":   return new VigorAddress("T", num, 0xA8, false);
                case "C":   return new VigorAddress("C", num, num >= 200 ? (byte)0xAD : (byte)0xAC, false);
                default:
                    throw new ArgumentException($"Unknown Vigor area prefix '{prefix}'. Valid: X/Y/M/S/SM/TC/TS/CC/CS/D/SD/R/T/C");
            }
        }

        public static byte[] EncodeBcdAddress(int address)
        {
            string digits = address.ToString("D6");
            return new byte[]
            {
                (byte)((HexCharToNibble(digits[0]) << 4) | HexCharToNibble(digits[1])),
                (byte)((HexCharToNibble(digits[2]) << 4) | HexCharToNibble(digits[3])),
                (byte)((HexCharToNibble(digits[4]) << 4) | HexCharToNibble(digits[5]))
            };
        }

        private static byte HexCharToNibble(char c)
        {
            if (c >= '0' && c <= '9') return (byte)(c - '0');
            if (c >= 'A' && c <= 'F') return (byte)(c - 'A' + 10);
            if (c >= 'a' && c <= 'f') return (byte)(c - 'a' + 10);
            return 0;
        }

        public static string IncrementAddress(string address, int offset = 1)
        {
            var parsed = Parse(address);
            return $"{parsed.Prefix}{parsed.Number + offset}";
        }
    }
}
