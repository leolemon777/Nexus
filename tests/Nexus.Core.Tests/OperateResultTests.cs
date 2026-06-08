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
}
