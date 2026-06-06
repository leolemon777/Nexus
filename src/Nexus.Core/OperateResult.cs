using System;

namespace Nexus
{
    /// <summary>
    /// 操作结果基类 — 所有设备通讯操作都返回此类型，避免异常驱动控制流。
    /// </summary>
    public class OperateResult
    {
        public bool IsSuccess { get; protected set; }
        public string Message { get; protected set; } = string.Empty;
        public int ErrorCode { get; protected set; }

        public static OperateResult Success() => new OperateResult { IsSuccess = true };

        public static OperateResult Failed(string message, int errorCode = 0)
            => new OperateResult { IsSuccess = false, Message = message, ErrorCode = errorCode };

        public static OperateResult<T> Success<T>(T content)
            => new OperateResult<T> { IsSuccess = true, Content = content };

        public override string ToString()
            => IsSuccess ? "Success" : $"Failed[{ErrorCode}]: {Message}";
    }

    /// <summary>
    /// 带返回值的操作结果。
    /// </summary>
    public class OperateResult<T> : OperateResult
    {
        public T Content { get; set; } = default!;

        public static new OperateResult<T> Failed(string message, int errorCode = 0)
            => new OperateResult<T> { IsSuccess = false, Message = message, ErrorCode = errorCode };

        public override string ToString()
            => IsSuccess ? $"Success: {Content}" : $"Failed[{ErrorCode}]: {Message}";
    }
}
