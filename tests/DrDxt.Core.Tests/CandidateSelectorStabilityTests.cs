using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Les arrets brutaux ferment les familles materielles.
///
/// Un arret sec (Kernel-Power 41) ne laisse aucun dump. Ecrire sur le processeur, la memoire ou la
/// carte d'une machine qui s'arrete deja sans prevenir rendrait le prochain incident
/// inattribuable : il viendrait de notre reglage ou de la condition preexistante. La machine de
/// Reference en compte 11 sur 90 jours.
/// </summary>
public class CandidateSelectorStabilityTests
{
    private static readonly DateTimeOffset Maintenant = DateTimeOffset.UtcNow;

    // Frequence NON plate (2715 / 1800) et rien de bridant : aucun autre refus ne vient masquer le
    // veto qu'on eprouve.
    private static DiagnosticMachine Machine(IReadOnlyList<ArretClasse>? arrets) =>
        new(BottleneckState.CpuBoundStrong, GpuLimiterObserved.RienDeBridant, 2715, 1800,
            TuningGpuAutorise: true, ArretsBrutaux: arrets);

    private static CandidateDescriptor Levier(string id, string grandeur,
        SurfaceTouchee surface = SurfaceTouchee.NonDeclaree) =>
        new(id, CandidateKind.AutoHot, RiskLevel: 1, NeedsStability: false, new[] { grandeur }, Surface: surface);

    private static IReadOnlyList<ArretClasse> IlYA(int nombre, int joursMin) =>
        Enumerable.Range(0, nombre).Select(i => ArretClasse.SansDetail(Maintenant.AddDays(-(joursMin + i)))).ToList();

    private static CandidateAppraisal Juger(IReadOnlyList<ArretClasse>? arrets, string id, string grandeur,
        SurfaceTouchee surface = SurfaceTouchee.NonDeclaree) =>
        CandidateSelector.Evaluer(Machine(arrets), new[] { Levier(id, grandeur, surface) }).Single();

    [Theory]
    [InlineData("gpu.essai", "gpu.clock")]
    [InlineData("fixit.power_plan", "cpu.power")]
    [InlineData("ram.xmp", "ram.frequency")]
    // Le levier de laboratoire ne déclare pas sa surface : c'est lui que les arrêts ferment, quelles que soient ses grandeurs.
    public void Des_arrets_brutaux_REPETES_ferment_un_levier_sans_surface_declaree(string id, string grandeur)
    {
        var a = Juger(IlYA(11, 1), id, grandeur);

        a.Retenu.Should().BeFalse();
        a.Motifs.Should().Contain(m => m.Contains("arrêt") && m.Contains("inattribuable"));
    }

    // Un levier de latence peut toucher le materiel : `latency.reflex` se dit « latence » et ecrit
    // l'etat du pilote NVIDIA. Le veto ne lit pas la famille, il lit la SURFACE ecrite.
    [Fact]
    public void Un_levier_qui_n_ecrit_QUE_la_session_utilisateur_reste_ouvert()
        => Juger(IlYA(11, 1), "latency.x", "latence", SurfaceTouchee.OsUtilisateur).Retenu.Should().BeTrue();

    // Le pilote graphique passe sous la garde d'incident comme le reste ; seul un levier qui ne déclare pas
    // ce qu'il écrit reste fermé par les arrêts (la garde ne saurait pas quoi rendre).
    [Fact]
    public void Un_levier_qui_ne_dit_pas_ce_qu_il_ecrit_reste_ferme()
        => Juger(IlYA(11, 1), "latency.x", "latence", SurfaceTouchee.NonDeclaree).Retenu.Should().BeFalse();

    [Fact]
    public void Un_levier_qui_ecrit_le_pilote_passe_sous_garde()
        => Juger(IlYA(11, 1), "latency.x", "latence", SurfaceTouchee.PiloteGraphique).Retenu.Should().BeTrue();

    [Fact]
    public void Des_arrets_hors_veille_ne_refusent_plus_le_processeur()
        // La garde d'incident remplace le refus.
        => Juger(IlYA(11, 1), "cpu.x", "cpu", SurfaceTouchee.Processeur).Retenu.Should().BeTrue();

    [Fact]
    public void Un_seul_arret_est_un_accident_pas_une_condition()
        => Juger(IlYA(1, 1), "gpu.essai", "gpu.clock").Retenu.Should().BeTrue();

    [Fact]
    public void NON_LU_ne_ferme_rien()
        // « Je n'ai pas pu lire le journal » n'est pas « la machine s'arrete ».
        => Juger(null, "gpu.essai", "gpu.clock").Retenu.Should().BeTrue();

    [Fact]
    public void Des_arrets_ANCIENS_sont_un_fait_d_archive_pas_un_veto()
        => Juger(IlYA(11, 100), "gpu.essai", "gpu.clock").Retenu.Should().BeTrue();
    // Ni tension, ni fréquence, ni ventilateur de la carte. La carte rouverte par
    // machine ne rouvre que la limite de puissance : sur une machine sans aucun arrêt, le décalage d'horloge,
    // l'undervolt (qui décale la fréquence) et la courbe du ventilateur restent refusés, avec leur motif.
    [Theory]
    [InlineData("gpu.core_offset", true)]
    [InlineData("gpu.undervolt", true)]
    [InlineData("gpu.fan_curve", true)]
    [InlineData("gpu.power_limit", false)]
    public void Tension_frequence_et_ventilateur_de_la_carte_ne_s_ecrivent_jamais(string id, bool interdit)
    {
        var a = CandidateSelector.Evaluer(Machine([]), new[] { CatalogueDeCandidats.Decrire(id)! }).Single();

        a.Motifs.Any(m => m.Contains("règle de la carte")).Should().Be(interdit);
        if (interdit) a.Retenu.Should().BeFalse();
    }

}
