using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// DrDxtAppCallerAuthenticator : n'autoriser que le processus DrDxt.App de confiance,
// refuser au moindre doute (fermé par défaut).
//
// La décision par comparaison de chemins se teste avec un faux résolveur PID → chemin
// d'image ; la vraie résolution GetProcessById/MainModule est la frontière vivante.
// -----------------------------------------------------------------------
public sealed class CallerAuthenticatorTests
{
    private const string ExpectedExe = @"C:\Program Files\DrDXT\DrDxt.App.exe";

    private static DrDxtAppCallerAuthenticator Auth(Func<int, string?> resolver) =>
        new(ExpectedExe, resolver);

    [Fact]
    public void Authorizes_when_client_image_is_the_expected_app()
    {
        var auth = Auth(_ => ExpectedExe);
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Authorizes_case_insensitively_and_normalizes_path()
    {
        var auth = Auth(_ => @"C:\Program Files\DrDXT\..\DrDXT\drdxt.app.EXE");
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Denies_when_client_image_is_a_different_process()
    {
        var auth = Auth(_ => @"C:\Windows\System32\cmd.exe");
        var r = auth.Authorize(new CallerInfo(1234));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Denies_when_no_pid_resolved()
    {
        var auth = Auth(_ => ExpectedExe);
        auth.Authorize(new CallerInfo(null)).IsOk.Should().BeFalse();
        auth.Authorize(default).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_pid_is_non_positive()
    {
        var auth = Auth(_ => ExpectedExe);
        auth.Authorize(new CallerInfo(0)).IsOk.Should().BeFalse();
        auth.Authorize(new CallerInfo(-5)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_image_path_unresolved()
    {
        var auth = Auth(_ => null);
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_resolver_throws()
    {
        var auth = Auth(_ => throw new InvalidOperationException("process exited"));
        var r = auth.Authorize(new CallerInfo(1234));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Constructor_rejects_empty_expected_path()
    {
        Action act = () => new DrDxtAppCallerAuthenticator("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DenyByDefault_denies_regardless_of_caller()
    {
        var auth = new DenyByDefaultCallerAuthenticator();
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
        auth.Authorize(default).IsOk.Should().BeFalse();
    }
}
