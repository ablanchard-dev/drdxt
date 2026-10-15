using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Autorisation multi-appelants (canal réconciliateur ↔ helper).
//
// Le helper doit autoriser un ensemble d'images appelantes distinctes (DrDxt.App
// interactive et l'hôte réconciliateur peu privilégié), chacune par chemin + SHA-256. Le
// composite accepte si un membre autorise le pair (le pair est exactement l'une des images
// de confiance) ; il refuse quand aucun ne le fait. Jamais ouvert :
//   * un ensemble vide refuse tout (aucune image de confiance provisionnée),
//   * une image inconnue est refusée,
//   * un membre qui lève ne fait pas tomber la décision (compté comme n'autorisant pas) :
//     défense en profondeur.
// -----------------------------------------------------------------------
public sealed class CompositeCallerAuthenticatorTests
{
    private sealed class AllowOnePid : ICallerAuthenticator
    {
        private readonly int _allowedPid;
        public AllowOnePid(int allowedPid) => _allowedPid = allowedPid;
        public Result Authorize(CallerInfo caller) =>
            caller.ClientProcessId == _allowedPid
                ? Result.Ok()
                : Result.Fail(ErrorCodes.ElevationDenied, "not my pid");
    }

    private sealed class AlwaysDeny : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) =>
            Result.Fail(ErrorCodes.ElevationDenied, "denied");
    }

    private sealed class Throws : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Authorizes_when_the_first_member_allows()
    {
        var auth = new CompositeCallerAuthenticator(new AllowOnePid(11), new AlwaysDeny());
        auth.Authorize(new CallerInfo(11)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Authorizes_when_a_later_member_allows()
    {
        // L'authentificateur App refuse le pid 22 ; celui du réconciliateur l'accepte.
        var auth = new CompositeCallerAuthenticator(new AllowOnePid(11), new AllowOnePid(22));
        auth.Authorize(new CallerInfo(22)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Denies_when_no_member_allows()
    {
        var auth = new CompositeCallerAuthenticator(new AllowOnePid(11), new AllowOnePid(22));
        var r = auth.Authorize(new CallerInfo(99));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Empty_set_denies_everything_never_fails_open()
    {
        var auth = new CompositeCallerAuthenticator(Array.Empty<ICallerAuthenticator>());
        auth.Authorize(new CallerInfo(11)).IsOk.Should().BeFalse();
        auth.Authorize(default).IsOk.Should().BeFalse();
    }

    [Fact]
    public void A_throwing_member_does_not_crash_and_a_good_member_still_authorizes()
    {
        var auth = new CompositeCallerAuthenticator(new Throws(), new AllowOnePid(11));
        auth.Authorize(new CallerInfo(11)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void A_throwing_member_alone_denies_fail_closed()
    {
        var auth = new CompositeCallerAuthenticator(new Throws());
        auth.Authorize(new CallerInfo(11)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Null_members_are_rejected()
    {
        Action act = () => new CompositeCallerAuthenticator((ICallerAuthenticator[])null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
