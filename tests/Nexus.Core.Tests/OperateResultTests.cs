using Xunit;

namespace Nexus.Core.Tests;

public class OperateResultTests
{
    [Fact]
    public void Success_IsSuccess_True()
    {
        var r = OperateResult.Success();
        Assert.True(r.IsSuccess);
        Assert.Equal(string.Empty, r.Message);
        Assert.Equal(0, r.ErrorCode);
    }

    [Fact]
    public void Failed_IsSuccess_False_Message_Preserved()
    {
        var r = OperateResult.Failed("something went wrong");
        Assert.False(r.IsSuccess);
        Assert.Equal("something went wrong", r.Message);
        Assert.Equal(0, r.ErrorCode);
    }

    [Fact]
    public void Failed_WithErrorCode_PreservesCode()
    {
        var r = OperateResult.Failed("illegal address", 2);
        Assert.False(r.IsSuccess);
        Assert.Equal("illegal address", r.Message);
        Assert.Equal(2, r.ErrorCode);
    }

    [Fact]
    public void Generic_Success_ContainsContent()
    {
        var r = OperateResult.Success<int>(42);
        Assert.True(r.IsSuccess);
        Assert.Equal(42, r.Content);
    }

    [Fact]
    public void Generic_Failed_ContainsContentDefault()
    {
        var r = OperateResult<int>.Failed("oops");
        Assert.False(r.IsSuccess);
        Assert.Equal("oops", r.Message);
        Assert.Equal(0, r.Content);
    }

    [Fact]
    public void ToString_Success_FormatsCorrectly()
    {
        var r = OperateResult.Success();
        Assert.Equal("Success", r.ToString());
    }

    [Fact]
    public void ToString_Generic_Success_IncludesContent()
    {
        var r = OperateResult.Success<int>(123);
        Assert.Equal("Success: 123", r.ToString());
    }

    [Fact]
    public void ToString_Failed_FormatsErrorCodeAndMessage()
    {
        var r = OperateResult.Failed("bad addr", 2);
        Assert.Equal("Failed[2]: bad addr", r.ToString());
    }

    /// <summary>
    /// A5 回归:OperateResult&lt;T&gt;.Content 不可变契约 — public setter 改为 protected 后,
    /// 调用方无法直接修改 Content,只能通过工厂方法构造。验证编译期契约有效。
    /// (这只是文档化测试,真正约束在编译期。)
    /// </summary>
    [Fact]
    public void Content_Preserved_AfterSuccessConstruction()
    {
        // 通过工厂构造的 Content 应保留。
        var r = OperateResult.Success<string>("hello");
        Assert.Equal("hello", r.Content);
        Assert.True(r.IsSuccess);

        // Failed 的 Content 应为 default。
        var f = OperateResult<string>.Failed("error");
        Assert.Null(f.Content);
        Assert.False(f.IsSuccess);
    }

    /// <summary>
    /// A5 回归:WithContent 流式构造 — 用新 Content 创建副本,原对象不变。
    /// </summary>
    [Fact]
    public void WithContent_CreatesCopy_WithNewContent()
    {
        var original = OperateResult.Success<int>(42);
        var copy = original.WithContent(99);

        Assert.Equal(42, original.Content); // 原对象不变
        Assert.Equal(99, copy.Content);
        Assert.True(copy.IsSuccess);
    }

    /// <summary>
    /// A5 回归:Failed 状态的 WithContent 应保留 Failed 状态。
    /// </summary>
    [Fact]
    public void WithContent_OnFailed_PreservesFailedState()
    {
        var failed = OperateResult<int>.Failed("oops", 42);
        var copy = failed.WithContent(99);

        Assert.False(copy.IsSuccess);
        Assert.Equal("oops", copy.Message);
        Assert.Equal(42, copy.ErrorCode);
        Assert.Equal(99, copy.Content);
    }
}
