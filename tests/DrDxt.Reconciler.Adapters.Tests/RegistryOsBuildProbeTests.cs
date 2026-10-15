using DrDxt.Core;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;

namespace DrDxt.Reconciler.Adapters.Tests;

// -----------------------------------------------------------------------
// RegistryOsBuildProbe : lit HKLM\...\CurrentVersion → « {CurrentBuild}.{UBR} ».
// La lecture brute du registre est derrière une couture interne, pour tester la logique de
// formatage et d'échec sans toucher au vrai registre ; le mince vrai lecteur est le seul
// bord non testé.
// -----------------------------------------------------------------------
public sealed class RegistryOsBuildProbeTests
{
    // Un faux lecteur brut qui rend des valeurs CurrentBuild / UBR toutes faites.
    private sealed class FakeRawReader : IRawOsVersionReader
    {
        public string? CurrentBuild { get; init; }
        public uint? Ubr { get; init; }
        public bool Throw { get; init; }

        public Result<(string? CurrentBuild, uint? Ubr)> Read()
            => Throw
                ? Result<(string?, uint?)>.Fail(ErrorCodes.RegistryFail, "read failed (test)")
                : Result<(string?, uint?)>.Ok((CurrentBuild, Ubr));
    }

    [Fact]
    public void FormatsBuildAndUbr_AsDottedToken()
    {
        var probe = new RegistryOsBuildProbe(new FakeRawReader { CurrentBuild = "22631", Ubr = 3447 });

        var r = probe.ReadCurrentBuild();

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be("22631.3447");
    }

    [Fact]
    public void MissingCurrentBuild_Fails()
    {
        var probe = new RegistryOsBuildProbe(new FakeRawReader { CurrentBuild = null, Ubr = 3447 });

        var r = probe.ReadCurrentBuild();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.RegistryFail);
    }

    [Fact]
    public void MissingUbr_Fails()
    {
        var probe = new RegistryOsBuildProbe(new FakeRawReader { CurrentBuild = "22631", Ubr = null });

        var r = probe.ReadCurrentBuild();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.RegistryFail);
    }

    [Fact]
    public void RawReadFailure_PropagatesFail()
    {
        var probe = new RegistryOsBuildProbe(new FakeRawReader { Throw = true });

        var r = probe.ReadCurrentBuild();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.RegistryFail);
    }

    [Fact]
    public void BlankCurrentBuild_Fails()
    {
        var probe = new RegistryOsBuildProbe(new FakeRawReader { CurrentBuild = "   ", Ubr = 1 });

        var r = probe.ReadCurrentBuild();

        r.IsOk.Should().BeFalse();
    }
}
