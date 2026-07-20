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
        {
            // A5: Content setter 是 protected,基类静态工厂不能直接 object-init。
            // 借助泛型类的内部 SetContent helper(可见 protected setter)构造实例。
            var result = new OperateResult<T>();
            result.SetContent(content);
            result.IsSuccess = true;
            return result;
        }

        public override string ToString()
            => IsSuccess ? "Success" : $"Failed[{ErrorCode}]: {Message}";
    }

    /// <summary>
    /// 带返回值的操作结果。
    /// </summary>
    public class OperateResult<T> : OperateResult
    {
        /// <summary>
        /// 操作返回值。仅成功时(<see cref="OperateResult.IsSuccess"/> == true)有意义。
        /// <para>
        /// <b>A5 修复</b>:setter 现在是 <c>protected</c>。原实现是 public,允许调用方修改
        /// 已构造的 Result,违反不可变契约 —— AGENTS.md 警告 "Content 是值类型,不要用 ?."
        /// 正是为了避免把 Content 当成可空引用来读,public setter 进一步鼓励了反模式。
        /// 现在 Content 只能在构造期间通过 <see cref="OperateResult.Success{T}"/> 设置,
        /// 或由子类通过 protected setter 设置。
        /// </para>
        /// </summary>
        public T Content { get; protected set; } = default!;

        /// <summary>子类/工厂用来设置 Content 的 helper(对应 protected setter)。</summary>
        protected internal void SetContent(T content) => Content = content;

        public static new OperateResult<T> Failed(string message, int errorCode = 0)
            => new OperateResult<T> { IsSuccess = false, Message = message, ErrorCode = errorCode };

        /// <summary>
        /// 返回一个内容已替换为新值的副本(原对象不变)。用于流式链式调用。
        /// </summary>
        public OperateResult<T> WithContent(T content)
            => new OperateResult<T>
            {
                IsSuccess = IsSuccess,
                Message = Message,
                ErrorCode = ErrorCode,
                Content = content
            };

        public override string ToString()
            => IsSuccess ? $"Success: {Content}" : $"Failed[{ErrorCode}]: {Message}";
    }
}
