using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

public class ErrorTests
{
    // --- Error est un record struct : égalité de valeur ---

    [Fact]
    public void Error_ValueEquality_SameFields_AreEqual()
    {
        var a = new Error("A", "b");
        var b = new Error("A", "b");

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Error_ValueEquality_DifferentCode_AreNotEqual()
    {
        var a = new Error("X", "b");
        var b = new Error("Y", "b");

        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Error_ValueEquality_DifferentMessage_AreNotEqual()
    {
        var a = new Error("A", "first");
        var b = new Error("A", "second");

        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }

    // --- Les propriétés d'Error tiennent leurs valeurs ---

    [Fact]
    public void Error_Code_IsStoredCorrectly()
    {
        var error = new Error("MY_CODE", "my message");

        error.Code.Should().Be("MY_CODE");
    }

    [Fact]
    public void Error_Message_IsStoredCorrectly()
    {
        var error = new Error("MY_CODE", "my message");

        error.Message.Should().Be("my message");
    }
}
