namespace Nexus.Vigor
{
    public enum VigorCommand : byte
    {
        ReadWord = 0x20,
        ReadBit = 0x21,
        WriteWord = 0x28,
        WriteBit = 0x29,
    }

    public static class VigorConstants
    {
        public const byte STX = 0x10;
        public const byte CODE = 0x02;
        public const byte ETX = 0x03;
        public const int MaxWordReadCount = 32;
        public const int MaxBitReadCount = 1024;
        public const int MaxDWord32ReadCount = 16;
        public const int FixedDataLen = 8;
    }
}
