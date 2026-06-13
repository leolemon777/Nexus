using System;

namespace Nexus.Siemens
{
    public class SiemensS7Address : IDataAddress
    {
        public string Original { get; set; } = string.Empty;
        public S7Area Area { get; set; }
        public int DBNumber { get; set; }
        public int ByteAddress { get; set; }
        public int BitOffset { get; set; }
        public int DataSize { get; set; }

        public static SiemensS7Address Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new AddressParseException(address ?? "", "地址不能为空");
            string original = address;
            address = address.ToUpper().Trim();

            SiemensS7Address result;

            if (address.StartsWith("DB"))
            {
                int dotIdx = address.IndexOf('.');
                if (dotIdx < 0) throw new AddressParseException(original, "无效DB地址格式");
                int dbNum = int.Parse(address.Substring(2, dotIdx - 2));
                string subAddr = address.Substring(dotIdx + 1);
                if (subAddr.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
                    subAddr = subAddr.Substring(2);
                result = ParseSubAddress(subAddr, S7Area.DB, dbNum);
            }
            else if (address.StartsWith("I") || address.StartsWith("EB"))
            {
                result = ParseSubAddress(address.TrimStart('I', 'E', 'B'), S7Area.PE, 0);
            }
            else if (address.StartsWith("Q") || address.StartsWith("AB"))
            {
                string trimmed = address.TrimStart('Q', 'A', 'B');
                result = ParseSubAddress(trimmed, S7Area.PA, 0);
            }
            else if (address.StartsWith("M") || address.StartsWith("MB"))
            {
                result = ParseSubAddress(address.TrimStart('M', 'B'), S7Area.MK, 0);
            }
            else if (address.StartsWith("V"))
            {
                // V 区原生映射为 S7Area.V (0x85)，DBNumber 为 0
                result = ParseSubAddress(address.Substring(1), S7Area.V, 0);
            }
            else if (address.StartsWith("T"))
            {
                // 定时器区 (S7Area.TM = 0x1D)
                result = new SiemensS7Address
                {
                    Area = S7Area.TM,
                    DBNumber = 0,
                    ByteAddress = int.Parse(address.Substring(1)),
                    BitOffset = 0,
                    DataSize = 2
                };
            }
            else if (address.StartsWith("C"))
            {
                // 计数器区 (S7Area.CT = 0x1C)
                result = new SiemensS7Address
                {
                    Area = S7Area.CT,
                    DBNumber = 0,
                    ByteAddress = int.Parse(address.Substring(1)),
                    BitOffset = 0,
                    DataSize = 2
                };
            }
            else
            {
                throw new AddressParseException(original, "不支持的地址格式");
            }

            result.Original = original;
            return result;
        }

        public static bool TryParse(string address, out SiemensS7Address? parsed)
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

        private static SiemensS7Address ParseSubAddress(string sub, S7Area area, int db)
        {
            int bitOffset = 0;

            if (sub.Contains("."))
            {
                var parts = sub.Split('.');
                int byteAddr = int.Parse(parts[0].TrimStart('X'));
                bitOffset = int.Parse(parts[1]);
                return new SiemensS7Address
                {
                    Area = area,
                    DBNumber = db,
                    ByteAddress = byteAddr,
                    BitOffset = bitOffset,
                    DataSize = 1
                };
            }

            if (sub.StartsWith("W"))
                return new SiemensS7Address
                {
                    Area = area,
                    DBNumber = db,
                    ByteAddress = int.Parse(sub.Substring(1)),
                    BitOffset = 0,
                    DataSize = 2
                };
            if (sub.StartsWith("D"))
                return new SiemensS7Address
                {
                    Area = area,
                    DBNumber = db,
                    ByteAddress = int.Parse(sub.Substring(1)),
                    BitOffset = 0,
                    DataSize = 4
                };
            if (sub.StartsWith("B"))
                return new SiemensS7Address
                {
                    Area = area,
                    DBNumber = db,
                    ByteAddress = int.Parse(sub.Substring(1)),
                    BitOffset = 0,
                    DataSize = 1
                };

            return new SiemensS7Address
            {
                Area = area,
                DBNumber = db,
                ByteAddress = int.Parse(sub),
                BitOffset = 0,
                DataSize = 2
            };
        }

        public override string ToString()
            => Original ?? $"{Area} DB{DBNumber} Byte{ByteAddress}.{BitOffset}";
    }
}
