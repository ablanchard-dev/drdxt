using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Multi-caller authorization (M5 reconciler↔helper channel).
//
// The helper must authorize a SET of distinct caller images — the interactive
// DrDxt.App AND the low-priv reconciler host — each by path + SHA-256. The
// composite is OK if ANY member authorizes the peer (the peer is exactly one of
// the trusted images); it DENIES when none do. It never fails open:
//   * an EMPTY set denies everything (no trusted image provisioned),
//   * an unknown image denies,
//   * a member that throws does not crash the decision (treated as that member
//     not authorizing) — defense in depth.
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
        // App authenticator denies pid 22, but the reconciler authenticator allows it.
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
