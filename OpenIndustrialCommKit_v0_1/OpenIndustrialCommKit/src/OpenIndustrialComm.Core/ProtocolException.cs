namespace OpenIndustrialComm.Core;

public sealed class ProtocolException : Exception
{
    public ProtocolException(string protocol, string message, Exception? innerException = null) : base(message, innerException)
    {
        Protocol = protocol;
    }

    public string Protocol { get; }
}
