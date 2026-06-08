namespace OpenIndustrialComm.Core;

public interface IDataAddress
{
    string Original { get; }
}

public sealed record DataAddress(string Original, string Area, int Offset, int? Bit = null) : IDataAddress;

public interface IAddressParser<out TAddress> where TAddress : IDataAddress
{
    TAddress Parse(string address);
}

public sealed class AddressParseException : Exception
{
    public AddressParseException(string address, string message) : base($"Invalid address '{address}': {message}")
    {
        Address = address;
    }

    public string Address { get; }
}
