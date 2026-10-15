using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// La decision « 0 MHz / 100 % » lit la SURFACE ecrite, comme les vetos de stabilite. Le
/// catalogue entier est juge : la machine est choisie pour que rien d'autre ne refuse sur la famille
/// (frequence qui chute vraiment, bridage publie, aucun incident, aucun arret lu).
/// </summary>
public sealed class InterditGpuSurfaceTests
{
    private const string MotifInterdit = "écriture GPU interdite";

    private static readonly DiagnosticMachine Interdit = new(
        BottleneckState.MixedGpuLeaning, GpuLimiterObserved.ChuteDeFrequenceObservee, 2700, 2100,
        TuningGpuAutorise: false);

    private static IReadOnlyList<CandidateDescriptor> Catalogue()
        => CatalogueDeCandidats.Ids.Select(id => CatalogueDeCandidats.Decrire(id)!).ToList();

    private static IEnumerable<string> FermesParLInterdit(DiagnosticMachine d, IReadOnlyList<CandidateDescriptor> c)
        => CandidateSelector.Evaluer(d, c).Where(a => a.Motifs.Any(m => m.Contains(MotifInterdit))).Select(a => a.Id);

    [Fact]
    public void L_interdit_ferme_sur_le_catalogue_exactement_les_leviers_qui_ecrivent_sur_la_carte()
    {
        var catalogue = Catalogue();

        FermesParLInterdit(Interdit, catalogue).Should().BeEquivalentTo(
            new[] { "gpu.core_offset", "gpu.undervolt", "gpu.power_limit", "gpu.fan_curve" },
            "la decision porte sur la carte ; un reglage du pilote ou un guide n'ecrit ni MHz ni %");
        FermesParLInterdit(Interdit with { TuningGpuAutorise = true }, catalogue).Should().BeEmpty(
            "decision levee : ce motif-la disparait, et lui seul");
    }

    [Fact]
    public void Un_levier_qui_ne_declare_pas_sa_surface_reste_ferme_meme_s_il_se_dit_latence()
    {
        var sansSurface = new CandidateDescriptor("x.sans_surface", CandidateKind.AutoHot, 1, false, new[] { "latence" });

        FermesParLInterdit(Interdit, new[] { sansSurface }).Should().ContainSingle();
    }
}
