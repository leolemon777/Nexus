namespace OpenIndustrialComm.Core;

public sealed record OperationResult<T>(
    bool Success,
    T? Value,
    string? ErrorCode = null,
    string? Message = null,
    Exception? Exception = null)
{
    public static OperationResult<T> Ok(T value) => new(true, value);

    public static OperationResult<T> Fail(string errorCode, string message, Exception? exception = null) =>
        new(false, default, errorCode, message, exception);
}

public sealed record OperationResult(
    bool Success,
    string? ErrorCode = null,
    string? Message = null,
    Exception? Exception = null)
{
    public static OperationResult Ok() => new(true);

    public static OperationResult Fail(string errorCode, string message, Exception? exception = null) =>
        new(false, errorCode, message, exception);
}
