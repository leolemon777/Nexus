namespace OpenIndustrialComm.Core;

public enum QualityCode
{
    Good,
    Uncertain,
    Bad,
    Timeout,
    Disconnected,
    AddressError,
    AccessDenied,
    ProtocolError
}

public sealed record DataPointValue<T>(
    T? Value,
    QualityCode Quality,
    DateTimeOffset Timestamp,
    string? Unit = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);
