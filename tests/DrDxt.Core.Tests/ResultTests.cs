using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

public class ResultTests
{
    // --- Result.Ok() ---

    [Fact]
    public void Result_Ok_IsOk_IsTrue()
    {
        var result = Result.Ok();

        result.IsOk.Should().BeTrue();
    }

    [Fact]
    public void Result_Ok_Error_IsNull()
    {
        var result = Result.Ok();

        result.Error.Should().BeNull();
    }

    // --- Result.Fail() ---

    [Fact]
    public void Result_Fail_IsOk_IsFalse()
    {
        var result = Result.Fail("ERR_CODE", "something went wrong");

        result.IsOk.Should().BeFalse();
    }

    [Fact]
    public void Result_Fail_Error_HasExpectedCode()
    {
        var result = Result.Fail("ERR_CODE", "something went wrong");

        result.Error.Should().NotBeNull();
        result.Error!.Value.Code.Should().Be("ERR_CODE");
    }

    [Fact]
    public void Result_Fail_Error_HasExpectedMessage()
    {
        var result = Result.Fail("ERR_CODE", "something went wrong");

        result.Error.Should().NotBeNull();
        result.Error!.Value.Message.Should().Be("something went wrong");
    }

    // --- Result<T>.Ok() avec un type référence (string) ---

    [Fact]
    public void ResultT_Ok_String_IsOk_IsTrue()
    {
        var result = Result<string>.Ok("hello");

        result.IsOk.Should().BeTrue();
    }

    [Fact]
    public void ResultT_Ok_String_Value_EqualsInput()
    {
        var result = Result<string>.Ok("hello");

        result.Value.Should().Be("hello");
    }

    [Fact]
    public void ResultT_Ok_String_Error_IsNull()
    {
        var result = Result<string>.Ok("hello");

        result.Error.Should().BeNull();
    }

    // --- Result<T>.Ok() avec un type valeur (int) ---

    [Fact]
    public void ResultT_Ok_Int_IsOk_IsTrue()
    {
        var result = Result<int>.Ok(42);

        result.IsOk.Should().BeTrue();
    }

    [Fact]
    public void ResultT_Ok_Int_Value_EqualsInput()
    {
        var result = Result<int>.Ok(42);

        result.Value.Should().Be(42);
    }

    [Fact]
    public void ResultT_Ok_Int_Error_IsNull()
    {
        var result = Result<int>.Ok(42);

        result.Error.Should().BeNull();
    }

    // --- Result<T>.Fail() ---

    [Fact]
    public void ResultT_Fail_IsOk_IsFalse()
    {
        var result = Result<string>.Fail("ERR", "msg");

        result.IsOk.Should().BeFalse();
    }

    [Fact]
    public void ResultT_Fail_Error_IsPopulated()
    {
        var result = Result<string>.Fail("ERR", "msg");

        result.Error.Should().NotBeNull();
        result.Error!.Value.Code.Should().Be("ERR");
        result.Error!.Value.Message.Should().Be("msg");
    }

    [Fact]
    public void ResultT_Fail_String_Value_IsDefault()
    {
        var result = Result<string>.Fail("ERR", "msg");

        result.Value.Should().BeNull();
    }

    [Fact]
    public void ResultT_Fail_Int_Value_IsDefault()
    {
        var result = Result<int>.Fail("ERR", "msg");

        result.Value.Should().Be(default(int));
    }

    // --- Héritage : Result<T> est assignable à Result ---

    [Fact]
    public void ResultT_IsAssignableTo_Result()
    {
        Result<string> typed = Result<string>.Ok("x");

        Result baseResult = typed;

        baseResult.IsOk.Should().BeTrue();
        baseResult.Error.Should().BeNull();
    }

    [Fact]
    public void ResultT_Fail_AsBaseResult_ReportsCorrectState()
    {
        Result<int> typed = Result<int>.Fail("CODE", "msg");

        Result baseResult = typed;

        baseResult.IsOk.Should().BeFalse();
        baseResult.Error.Should().NotBeNull();
        baseResult.Error!.Value.Code.Should().Be("CODE");
    }

    // --- Result<T>.Fail rend Result<T> (le static new masque correctement la base) ---

    [Fact]
    public void ResultT_Fail_ReturnsResultT_NotBaseResult()
    {
        var result = Result<string>.Fail("ERR", "msg");

        result.Should().BeOfType<Result<string>>();
    }
}
