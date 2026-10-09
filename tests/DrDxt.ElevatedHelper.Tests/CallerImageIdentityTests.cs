using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// M5 caller-auth hardening: the dev-path check now verifies the connecting
// image's IDENTITY (path + image hash), not just its path. A peer that sits at
// the same path but is a DIFFERENT binary is rejected — closing the gap where
// path equality alone would trust a swapped exe.
//
// The real Authenticode (WinVerifyTrust + publisher pin) check for SIGNED release
// builds is still a documented TODO; this layer is the UNSIGNED dev-build identity
// gate (exe-path + image-hash match). It never fails open: any doubt -> deny.
//
// Identity resolution is behind a seam (ICallerImageIdentity) so this is unit-
// tested with fakes; the real path+hash resolution is the live boundary.
// -----------------------------------------------------------------------
public sealed class CallerImageIdentityTests
{
    private const string ExpectedExe = @"C:\Program Files\DrDXT\DrDxt.App.exe";
    private static readonly byte[] ExpectedHash = { 1, 2, 3, 4, 5 };

    // A fake identity resolver: maps a PID to (path, hash) or returns null.
    private sealed class FakeIdentity : ICallerImageIdentity
    {
        private readonly (string path, byte[]? hash)? _result;
        private readonly bool _throws;
        public FakeIdentity((string, byte[]?)? result = null, bool throws = false)
        {
            _result = result;
            _throws = throws;
        }
        public CallerImage? Resolve(int pid)
        {
            if (_throws) throw new InvalidOperationException("boom");
            return _result is { } r ? new CallerImage(r.path, r.hash) : null;
        }
    }

    private static DrDxtAppCallerAuthenticator Auth(ICallerImageIdentity identity) =>
        DrDxtAppCallerAuthenticator.WithImageIdentity(ExpectedExe, ExpectedHash, identity);

    [Fact]
    public void Authorizes_when_path_and_hash_both_match()
    {
        var auth = Auth(new FakeIdentity((ExpectedExe, ExpectedHash)));
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Denies_when_path_matches_but_hash_differs()
    {
        // Same path, DIFFERENT binary (swapped exe) -> deny (this is the gap closed).
        var auth = Auth(new FakeIdentity((ExpectedExe, new byte[] { 9, 9, 9 })));
        var r = auth.Authorize(new CallerInfo(1234));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Denies_when_path_differs_even_if_hash_matches()
    {
        var auth = Auth(new FakeIdentity((@"C:\Windows\System32\cmd.exe", ExpectedHash)));
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_hash_could_not_be_computed()
    {
        // No hash resolved (file unreadable) -> cannot prove identity -> deny.
        var auth = Auth(new FakeIdentity((ExpectedExe, (byte[]?)null)));
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_identity_unresolved()
    {
        var auth = Auth(new FakeIdentity(result: null));
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_identity_resolver_throws()
    {
        var auth = Auth(new FakeIdentity(throws: true));
        var r = auth.Authorize(new CallerInfo(1234));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Denies_when_no_pid()
    {
        var auth = Auth(new FakeIdentity((ExpectedExe, ExpectedHash)));
        auth.Authorize(new CallerInfo(null)).IsOk.Should().BeFalse();
        auth.Authorize(default).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Constructor_rejects_empty_expected_hash()
    {
        Action act = () => DrDxtAppCallerAuthenticator.WithImageIdentity(
            ExpectedExe, Array.Empty<byte>(), new FakeIdentity((ExpectedExe, ExpectedHash)));
        act.Should().Throw<ArgumentException>();
    }
}
