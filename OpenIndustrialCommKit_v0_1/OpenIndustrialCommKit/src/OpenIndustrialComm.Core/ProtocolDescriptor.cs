namespace OpenIndustrialComm.Core;

[Flags]
public enum ProtocolCapability
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    BatchRead = 1 << 2,
    BatchWrite = 1 << 3,
    Subscribe = 1 << 4,
    Discover = 1 << 5,
    Server = 1 << 6,
    SecureTransport = 1 << 7,
    RawFrame = 1 << 8,
}

public sealed record ProtocolDescriptor(
    string Id,
    string DisplayName,
    string[] Aliases,
    ProtocolCapability Capabilities,
    string[] DefaultTransports,
    string? SpecificationUrl = null,
    string? Notes = null);
