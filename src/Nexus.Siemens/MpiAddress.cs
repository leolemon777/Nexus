using System;

namespace Nexus.Siemens
{
    public class MpiAddress : IDataAddress
    {
        public string Original { get; set; } = string.Empty;
        public byte AreaCode { get; set; }
        public int DBNumber { get; set; }
        public int ByteAddress { get; set; }
        public int BitOffset { get; set; }
        public bool IsBit { get; set; }
        public int DataSize { get; set; } = 2;

        public static MpiAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address ?? "", "MPI 地址不能为空");

            string original = address;
            string upper = address.ToUpperInvariant().Trim();

            MpiAddress result;

            if (upper.StartsWith("DB"))
            {
                int dotIdx = upper.IndexOf('.');
                if (dotIdx < 0) throw new AddressParseException(original, "无效的 DB 地址格式，示例: DB100.DBW0");
                int dbNum = int.Parse(upper.Substring(2, dotIdx - 2));
                string sub = upper.Substring(dotIdx + 1);
                if (sub.StartsWith("DB"))
                    sub = sub.Substring(2);

                result = ParseSubAddress(sub, 0x84, dbNum);
            }
            else if (upper.StartsWith("E"))
            {
                result = ParseSubAddress(upper.Substring(1), 0x81, 0);
            }
            else if (upper.StartsWith("A"))
            {
                result = ParseSubAddress(upper.Substring(1), 0x82, 0);
            }
            else if (upper.StartsWith("M"))
            {
                result = ParseSubAddress(upper.Substring(1), 0x83, 0);
            }
            else if (upper.StartsWith("T"))
            {
                result = ParseSubAddress(upper.Substring(1), 0x1D, 0);
            }
            else if (upper.StartsWith("C"))
            {
                result = ParseSubAddress(upper.Substring(1), 0x1C, 0);
            }
            else
            {
                throw new AddressParseException(original, "不支持的 MPI 地址格式，支持: DB, E, A, M, T, C");
            }

            result.Original = original;
            return result;
        }

        public static bool TryParse(string address, out MpiAddress? parsed)
        {
            try
            {
                parsed = Parse(address);
                return true;
            }
            catch
            {
                parsed = null;
                return false;
            }
        }

        private static MpiAddress ParseSubAddress(string sub, byte areaCode, int dbNum)
        {
            if (sub.Contains("."))
            {
                var parts = sub.Split('.');
                if (parts.Length != 2)
                    throw new AddressParseException(sub, "无效的地址格式");

                string first = parts[0];
                string second = parts[1];

                int byteAddr;
                if (first.StartsWith("X"))
                    byteAddr = int.Parse(first.Substring(1));
                else
                    byteAddr = int.Parse(first);

                int bitOffset = int.Parse(second);
                if (bitOffset > 7) throw new AddressParseException(sub, "位偏移必须在 0-7 之间");
                return new MpiAddress
                {
                    AreaCode = areaCode,
                    DBNumber = dbNum,
                    ByteAddress = byteAddr,
                    BitOffset = bitOffset,
                    IsBit = true,
                    DataSize = 1
                };
            }

            if (sub.StartsWith("W"))
            {
                return new MpiAddress
                {
                    AreaCode = areaCode,
                    DBNumber = dbNum,
                    ByteAddress = int.Parse(sub.Substring(1)),
                    DataSize = 2
                };
            }

            if (sub.StartsWith("D"))
            {
                return new MpiAddress
                {
                    AreaCode = areaCode,
                    DBNumber = dbNum,
                    ByteAddress = int.Parse(sub.Substring(1)),
                    DataSize = 4
                };
            }

            if (sub.StartsWith("B"))
            {
                return new MpiAddress
                {
                    AreaCode = areaCode,
                    DBNumber = dbNum,
                    ByteAddress = int.Parse(sub.Substring(1)),
                    DataSize = 1
                };
            }

            return new MpiAddress
            {
                AreaCode = areaCode,
                DBNumber = dbNum,
                ByteAddress = int.Parse(sub),
                DataSize = 2
            };
        }

        public override string ToString()
            => Original ?? $"Area=0x{AreaCode:X2} DB{DBNumber} Byte{ByteAddress}.{BitOffset}";
    }
}
