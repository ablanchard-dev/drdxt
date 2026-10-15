using System.Collections.Generic;
using System.Linq;
using DrDxt.App;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// AppliedTweakManifestMapper : transforme les issues gardées d'une application validée en
// entrées AppliedTweak d'état voulu du réconciliateur.
//
// Deux écarts qu'il comble (tous deux réels, tous deux en table explicite) :
//   * l'espace de noms des identifiants : le catalogue Application indexe les leviers par
//     « fixit.power_plan » / « latency.reflex » ; le réconciliateur (politique + catalogue de
//     descripteurs) par « fixit.sanePowerPlan » / « latency.reflex.lowLatencyMode ». Le mappeur traduit.
//   * la valeur voulue : l'instantané d'ApplyReport ne porte que la valeur d'origine (l'op
//     inverse) ; le jeton voulu canonique que le réconciliateur réapplique est connu par
//     levier et vit dans cette table fermée.
//
// Seules les issues gardées (Applied && !AutoReverted) qui mènent à une surface réconciliable
// sont émises. Les leviers annulés, non mappés (réglage GPU par exemple) et non réconciliables
// sont exclus : le manifeste ne porte que des leviers validés et réconciliables.
// -----------------------------------------------------------------------
public sealed class AppliedTweakManifestMapperTests
{
    private static ApplyOutcome Kept(string id, string originalJson = "{}")
        => new(id, Applied: true, AutoReverted: false, new TweakSnapshot(id, originalJson), Error: null);

    private static ApplyOutcome AutoReverted(string id)
        => new(id, Applied: true, AutoReverted: true, new TweakSnapshot(id, "{}"), Error: null);

    private static ApplyReport Report(params ApplyOutcome[] outcomes)
        => new(outcomes.ToList(), Verdict: null);

    [Fact]
    public void Maps_keptPowerPlan_toReconcilerIdAndDesiredToken()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(Kept("fixit.power_plan", "\"balanced\"")));

        tweaks.Should().ContainSingle();
        var t = tweaks[0];
        t.TweakId.Should().Be("fixit.sanePowerPlan", "the reconciler keys power plan by this id");
        t.DesiredValueJson.Should().Be(AppliedTweakManifestMapper.HighPerformancePlanToken,
            "the desired token is High Performance — what the reconciler re-applies");
        t.OriginalValueJson.Should().Be("\"balanced\"", "the original is taken from the kept snapshot");
    }

    [Fact]
    public void Maps_keptReflex_toReconcilerIdAndUltraToken()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(Kept("latency.reflex")));

        tweaks.Should().ContainSingle();
        tweaks[0].TweakId.Should().Be("latency.reflex.lowLatencyMode");
        tweaks[0].DesiredValueJson.Should().Be("2", "Ultra low-latency = 2");
    }

    [Fact]
    public void Maps_keptX3d_toReconcilerServiceIdAndRunningToken()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(Kept("fixit.x3d_ccd")));

        tweaks.Should().ContainSingle();
        tweaks[0].TweakId.Should().Be("fixit.x3dVCacheService");
        tweaks[0].DesiredValueJson.Should().Be("running");
    }

    [Fact]
    public void Maps_keptHags_toReconcilerIdAndOnToken()
    {
        var tweaks = new AppliedTweakManifestMapper().ToDesiredTweaks(Report(Kept("latency.hagsOn")));

        tweaks.Should().ContainSingle();
        tweaks[0].TweakId.Should().Be("os.hags");
        tweaks[0].DesiredValueJson.Should().Be("2", "HAGS on = HwSchMode 2");
    }

    [Fact]
    public void Excludes_hkcuTweaks_from_localSystem_reconcile()
    {
        // Le réconciliateur du démarrage tourne en LocalSystem (HKLM seulement). Les leviers HKCU, Game DVR
        // (DWORD HKCU), Mode Jeu (composite HKCU), VRR/flip (REG_SZ HKCU fusionnée), sont exclus à
        // dessein : une écriture HKCU en LocalSystem touche la ruche de SYSTEM. Seul HAGS (HKLM)
        // est commité au manifeste. Ils persistent nativement et l'App les réapplique.
        var tweaks = new AppliedTweakManifestMapper().ToDesiredTweaks(Report(
            Kept("latency.hagsOn"),
            Kept("latency.gameDvrBackgroundOff"),
            Kept("latency.gameModeOn"),
            Kept("latency.vrrFlipModelOn")));

        tweaks.Select(t => t.TweakId).Should().BeEquivalentTo(new[] { "os.hags" });
    }

    [Fact]
    public void Excludes_autoRevertedTweaks()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(
            Kept("fixit.power_plan"),
            AutoReverted("latency.reflex"))); // régression mesurée → annulé → non commité

        tweaks.Select(t => t.TweakId).Should().BeEquivalentTo(new[] { "fixit.sanePowerPlan" });
    }

    [Fact]
    public void Excludes_unmappedTweaks_likeGpuTuning()
    {
        var mapper = new AppliedTweakManifestMapper();

        var tweaks = mapper.ToDesiredTweaks(Report(
            Kept("fixit.power_plan"),
            Kept("gpu.undervolt"))); // pas une surface OS réconciliable → pas dans la table

        tweaks.Select(t => t.TweakId).Should().BeEquivalentTo(new[] { "fixit.sanePowerPlan" });
    }

    [Fact]
    public void Excludes_outcomesWithoutASnapshot()
    {
        var mapper = new AppliedTweakManifestMapper();

        var noSnapshot = new ApplyOutcome("fixit.power_plan",
            Applied: true, AutoReverted: false, Snapshot: null, Error: null);

        mapper.ToDesiredTweaks(Report(noSnapshot)).Should().BeEmpty(
            "without a snapshot we cannot record an honest original value → exclude");
    }

    // Chaque jeton voulu que le mappeur émet doit être accepté par la politique de
    // réconciliabilité de production (pour qu'un vrai CommitAsync ne rejette jamais ce que le mappeur a construit).
    [Fact]
    public void EveryMappedId_isReconcilableUnderTheProductionPolicy()
    {
        var mapper = new AppliedTweakManifestMapper();
        DrDxt.Reconciler.IReconcilabilityPolicy policy = new DrDxt.Reconciler.DefaultReconcilabilityPolicy();

        var tweaks = mapper.ToDesiredTweaks(Report(
            Kept("fixit.power_plan"),
            Kept("latency.reflex"),
            Kept("fixit.x3d_ccd")));

        tweaks.Should().HaveCount(3);
        foreach (var t in tweaks)
            policy.IsReconcilable(t.TweakId).Should().BeTrue($"'{t.TweakId}' must be reconcilable");
    }
}
