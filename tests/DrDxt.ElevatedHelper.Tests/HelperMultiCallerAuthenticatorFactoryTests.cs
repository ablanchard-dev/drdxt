using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// HelperAuthenticatorFactory.BuildMulti — binds the helper's authenticator from a
// SET of trusted caller images (the interactive App + the reconciler host), each by
// path + SHA-256 (FIX 2 hardening, applied per image).
//
// Selection / fail-closed rules (mirrors the single-image Build, per member):
//   * each provisioned path whose image hashes at startup → a path+hash member;
//   * the composite authorizes a peer iff it matches ONE of those members;
//   * a peer that is none of them (third-party OR a swapped binary at a trusted
//     path) → DENY;
//   * if NO path resolves/hashes (all null/empty/unhashable) → deny-by-default
//     (never fail open, never path-only, never allow-all).
//
// File-hash + the connecting-peer image resolution are behind seams (no real exe).
// -----------------------------------------------------------------------
public sealed class HelperMultiCallerAuthenticatorFactoryTests
{
    private const string AppPath = @"C:\Program Files\DrDXT\DrDxt.App.exe";
    private const string ReconPath = @"C:\Program Files\DrDXT\DrDxt.ReconcilerHost.exe";
    private const string ThirdPartyPath = @"C:\Temp\evil.exe";

    private static readonly byte[] AppHash = { 1, 1, 1, 1 };
    private static readonly byte[] ReconHash = { 2, 2, 2, 2 };

    // Resolves the CONNECTING peer's (path, hash) by pid. Each trusted pid maps to its
    // real image; an unknown pid resolves to a third-party image.
    private sealed class FakeIdentity : ICallerImageIdentity
    {
        public CallerImage? Resolve(int pid) => pid switch
        {
            1 => new CallerImage(AppPath, AppHash),
            2 => new CallerImage(ReconPath, ReconHash),
            3 => new CallerImage(ThirdPartyPath, new byte[] { 9, 9, 9 }),
            // a binary SWAPPED at the trusted App path (right path, wrong hash):
            4 => new CallerImage(AppPath, new byte[] { 8, 8, 8 }),
            _ => null,
        };
    }

    // The startup hasher: hashes each TRUSTED path to its expected hash.
    private static byte[]? HashFile(string path) => path switch
    {
        AppPath => AppHash,
        ReconPath => ReconHash,
        _ => null,
    };

    private static ICallerAuthenticator BuildBoth() =>
        HelperAuthenticatorFactory.BuildMulti(
            new[] { AppPath, ReconPath }, hashFile: HashFile, imageIdentity: new FakeIdentity());

    [Fact]
    public void App_caller_is_authorized()
    {
        BuildBoth().Authorize(new CallerInfo(1)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Reconciler_caller_is_authorized()
    {
        BuildBoth().Authorize(new CallerInfo(2)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Third_party_caller_is_denied()
    {
        var r = BuildBoth().Authorize(new CallerInfo(3));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Swapped_binary_at_a_trusted_path_is_denied()
    {
        BuildBoth().Authorize(new CallerInfo(4)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Unhashable_paths_are_skipped_but_resolvable_ones_still_authorize()
    {
        // ReconPath cannot be hashed at startup → it is skipped, but App still works.
        var auth = HelperAuthenticatorFactory.BuildMulti(
            new[] { AppPath, ReconPath },
            hashFile: p => p == AppPath ? AppHash : null,
            imageIdentity: new FakeIdentity());

        auth.Authorize(new CallerInfo(1)).IsOk.Should().BeTrue("App still hashes");
        auth.Authorize(new CallerInfo(2)).IsOk.Should().BeFalse("Recon was skipped (unhashable)");
    }

    [Fact]
    public void Denies_by_default_when_no_path_resolves()
    {
        HelperAuthenticatorFactory.BuildMulti(
            new[] { AppPath, ReconPath }, hashFile: _ => null, imageIdentity: new FakeIdentity())
            .Should().BeOfType<DenyByDefaultCallerAuthenticator>();
    }

    [Fact]
    public void Denies_by_default_when_the_set_is_empty_or_all_blank()
    {
        HelperAuthenticatorFactory.BuildMulti(Array.Empty<string>(), hashFile: HashFile)
            .Should().BeOfType<DenyByDefaultCallerAuthenticator>();
        HelperAuthenticatorFactory.BuildMulti(new[] { "  ", "" }, hashFile: HashFile)
            .Should().BeOfType<DenyByDefaultCallerAuthenticator>();
    }

    [Fact]
    public void A_single_trusted_path_still_works_through_BuildMulti()
    {
        var auth = HelperAuthenticatorFactory.BuildMulti(
            new[] { AppPath }, hashFile: HashFile, imageIdentity: new FakeIdentity());
        auth.Authorize(new CallerInfo(1)).IsOk.Should().BeTrue();
        auth.Authorize(new CallerInfo(2)).IsOk.Should().BeFalse();
    }
}
