using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;

namespace DrDxt.Reconciler.Adapters.Tests;

// -----------------------------------------------------------------------
// Aller-retour canonique figé : un DWORD de registre connu relu par la sérialisation de la sonde
// doit égaler la chaîne exacte que le catalogue Application stockerait en DesiredValueJson du
// manifeste ; sinon la comparaison ordinale voulu/réel du réconciliateur
// (StringComparison.Ordinal) verrait une fausse dérive.
//
// NvidiaUltraLowLatencyTweak écrit Ultra = 2 via value.ToString(InvariantCulture) → « 2 ».
// La sonde doit relire un DWORD 2 exactement comme « 2 ».
// -----------------------------------------------------------------------
public sealed class RegistryDesiredRoundTripTests
{
    // La valeur voulue qu'applique le NvidiaUltraLowLatencyTweak de l'Application (Ultra = 2),
    // sérialisée comme le levier la sérialise pour le manifeste.
    private const uint UltraLowLatency = 2;
    private static readonly string DesiredJson =
        UltraLowLatency.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Un faux lecteur de registre brut qui donne à la sonde un DWORD déjà canonisé par le même
    // canoniseur que le vrai Win32RegistryValueReader : preuve que le dispatch fait remonter
    // exactement cette chaîne, inchangée.
    private sealed class CanonicalizingFakeRegistry : IRawRegistryValueReader
    {
        private readonly uint _dword;
        public CanonicalizingFakeRegistry(uint dword) => _dword = dword;
        public Result<string> ReadCanonical(RegistryDescriptor descriptor)
            => Result<string>.Ok(ReconcilableValueCanonicalizer.Dword(_dword));
    }

    [Fact]
    public async Task ReflexDword_RoundTripsToTheExactDesiredJson()
    {
        var probe = new SurfaceSettingProbe(
            new DefaultReconcilableTweakCatalog(),
            new CanonicalizingFakeRegistry(UltraLowLatency),
            new ThrowingPower(),
            new ThrowingService());

        var r = await probe.ReadCurrentValueAsync("latency.reflex.lowLatencyMode", default);

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be(DesiredJson);   // égalité exacte, comparable à l'octet près
        r.Value.Should().Be("2");
    }

    [Fact]
    public void Canonicalizer_IsIdenticalToTheTweaksSerialization()
        => ReconcilableValueCanonicalizer.Dword(UltraLowLatency).Should().Be(DesiredJson);

    private sealed class ThrowingPower : IRawPowerPlanReader
    {
        public Result<string> ReadActiveSchemeCanonical()
            => Result<string>.Fail(ErrorCodes.PowerFail, "unused");
    }

    private sealed class ThrowingService : IRawServiceStateReader
    {
        public Result<string> ReadStateCanonical(string serviceName)
            => Result<string>.Fail(ErrorCodes.IoFailure, "unused");
    }
}
