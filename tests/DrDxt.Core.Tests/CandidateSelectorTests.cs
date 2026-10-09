using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le root-cause doit choisir, pas seulement s'afficher.
///
/// Le classifieur de goulot existait depuis des semaines sans jamais voir une session de jeu, et
/// la telemetrie materielle, branchee le 08/09/2026, s'affichait sans rien decider. L'autopilote
/// aurait essaye les memes leviers sur une machine que le GPU sature et sur une machine ou il
/// attend — a quatre minutes et demie de temps de jeu la tentative.
///
/// Ces tests gravent la seule chose que ce selecteur a le droit de faire : ELIMINER ce que la
/// mesure disqualifie deja, en disant pourquoi. Il n'autorise aucun keep et n'annonce aucun gain.
/// </summary>
public class CandidateSelectorTests
{
    private static CandidateDescriptor Levier(string id, params string[] grandeurs) =>
        new(id, CandidateKind.AutoHot, RiskLevel: 1, NeedsStability: false, grandeurs);

    private static DiagnosticMachine Machine(
        BottleneckState goulot = BottleneckState.MixedGpuLeaning,
        GpuLimiterObserved bridage = GpuLimiterObserved.RienDeBridant,
        double? mediane = 2715, double? minimum = 2715,
        bool tuningGpuAutorise = true) =>
        new(goulot, bridage, mediane, minimum, tuningGpuAutorise);

    private static CandidateAppraisal Juger(DiagnosticMachine d, CandidateDescriptor c) =>
        CandidateSelector.Evaluer(d, new[] { c }).Single();

    [Fact]
    public void UNE_FREQUENCE_QUI_NE_BOUGE_PAS_disqualifie_un_levier_de_frequence()
    {
        // La mesure reelle du 08/09/2026 : mediane 2715 MHz, minimum 2715 MHz, aucun bridage
        // publie. Forcer l'etat P maximal n'aide que si la carte redescend. Le savoir coute une
        // lecture ; l'ignorer coute quatre minutes et demie de jeu.
        var appreciation = Juger(Machine(), Levier("gpu.prefer_max_perf", "gpu.clock"));

        appreciation.Retenu.Should().BeFalse();
        appreciation.Motif.Should().Contain("2715").And.Contain("ne redescend jamais");
    }

    [Fact]
    public void UNE_FREQUENCE_QUI_CHUTE_LAISSE_LE_LEVIER_OUVERT()
    {
        // Le controle POSITIF : le selecteur ne doit pas devenir une facon elegante de ne rien
        // essayer. Quand la carte redescend vraiment, le levier redevient pertinent.
        var appreciation = Juger(
            Machine(mediane: 2700, minimum: 2100, bridage: GpuLimiterObserved.ChuteDeFrequenceObservee),
            Levier("gpu.prefer_max_perf", "gpu.clock"));

        appreciation.Retenu.Should().BeTrue();
    }

    [Fact]
    public void SANS_LECTURE_DE_FREQUENCE_ON_NE_DISQUALIFIE_RIEN()
    {
        // Unknown n'est pas une preuve d'inertie. Une carte non lue n'est pas une carte plate,
        // et confondre les deux ferait refuser des leviers sur toute machine sans telemetrie.
        var appreciation = Juger(
            Machine(mediane: null, minimum: null),
            Levier("gpu.prefer_max_perf", "gpu.clock"));

        appreciation.Retenu.Should().BeTrue();
    }

    [Fact]
    public void UNE_DECISION_DE_L_UTILISATEUR_FERME_LA_FAMILLE_ENTIERE()
    {
        // « On reste a 0 MHz / 100 % tant que les arrets non propres ne sont pas expliques » n'est
        // pas une preference a repeser a chaque session.
        var appreciation = Juger(
            Machine(tuningGpuAutorise: false, mediane: 2700, minimum: 2100),
            Levier("gpu.essai", "gpu.clock"));

        appreciation.Retenu.Should().BeFalse();
        appreciation.Motif.Should().Contain("arrêts non propres");
    }

    // 08/10/2026 (chantier V4) : le juge etait inverse. `gpu.power_limit` ABAISSE la limite (90 %, profil
    // option A : moins de chaleur et de consommation). Il n'etait propose que si la carte butait deja sur sa limite --
    // le seul cas ou l'abaisser coute a coup sur des images -- et refuse partout ailleurs (« rien a relever »).
    [Fact]
    public void UNE_CARTE_QUI_BUTE_DEJA_SUR_SA_LIMITE_ne_se_voit_pas_baisser_la_limite()
    {
        var appreciation = Juger(
            Machine(bridage: GpuLimiterObserved.PlafondPuissanceObserve),
            Levier("gpu.power_limit", "gpu.power"));

        appreciation.Retenu.Should().BeFalse();
        appreciation.Motif.Should().Contain("bute déjà sur sa limite de puissance");
    }

    [Theory]
    [InlineData(GpuLimiterObserved.RienDeBridant)]
    [InlineData(GpuLimiterObserved.GpuSature)]
    [InlineData(GpuLimiterObserved.Inconnu)]
    public void SANS_PLAFOND_OBSERVE_la_limite_peut_etre_essayee_plus_bas(GpuLimiterObserved bridage)
    {
        var appreciation = Juger(Machine(bridage: bridage), Levier("gpu.power_limit", "gpu.power"));

        appreciation.Retenu.Should().BeTrue("abaisser une limite que la carte n'atteint pas coute peu, et la mesure tranchera");
    }

    [Fact]
    public void UN_PRONOSTIC_DEFAVORABLE_N_INTERDIT_RIEN()
    {
        // La nuance qui compte. Le classifieur peut se tromper ; une mesure vaut mieux qu'un
        // pronostic. Un levier GPU sur une machine CPU-bound est dit peu prometteur et ESSAYE.
        var appreciation = Juger(
            Machine(goulot: BottleneckState.CpuBoundStrong, mediane: 2700, minimum: 2100),
            Levier("gpu.essai", "gpu.clock"));

        appreciation.Retenu.Should().BeTrue();
        appreciation.Motif.Should().Contain("peu prometteur");
    }

    [Fact]
    public void UN_LEVIER_QUI_DEMANDE_UN_REBOOT_N_EST_PAS_TENTE()
    {
        var appreciation = CandidateSelector.Evaluer(Machine(), new[]
        {
            new CandidateDescriptor("latency.hags", CandidateKind.AutoReboot, 1, false, new[] { "latence" }),
        }).Single();

        appreciation.Retenu.Should().BeFalse();
        appreciation.Motif.Should().Contain("AutoReboot");
    }

    [Fact]
    public void UN_LEVIER_DE_LATENCE_N_EST_PAS_JUGE_SUR_LA_FREQUENCE_GPU()
    {
        // Le controle negatif du selecteur lui-meme : une carte plate ne dit rien d'un levier qui
        // ne touche pas a la frequence. Confondre les deux le rendrait aveugle a toute la famille
        // latence sur cette machine.
        var appreciation = Juger(Machine(), Levier("latency.reflex", "latence"));

        appreciation.Retenu.Should().BeTrue();
    }

    [Fact]
    public void UN_BLOCAGE_CONSTATE_SUR_LA_MACHINE_ECARTE_LE_LEVIER()
    {
        // Le probe de selection a annonce « 2 candidats retenus » alors que les deux etaient
        // inapplicables : il jugeait sur les grandeurs touchees et ignorait le `AppliesTo` de
        // chaque tweak. Un selecteur qui SURESTIME ce qu'il peut essayer est pire qu'un selecteur
        // absent — il promet du temps de jeu qui ne rapportera rien.
        var appreciation = Juger(Machine(), new CandidateDescriptor(
            "latency.reflex", CandidateKind.AutoHot, 1, false, new[] { "latence" },
            BlocageConstate: "NVTweak\\LowLatencyMode est ABSENT : le tweak refuse d'ecrire une "
                             + "valeur qu'il ne saurait pas annuler"));

        appreciation.Retenu.Should().BeFalse();
        appreciation.Motif.Should().Contain("ABSENT");
    }

    [Fact]
    public void UN_CANDIDAT_DISQUALIFIE_DEUX_FOIS_DIT_LES_DEUX_RAISONS()
    {
        // Le premier motif trouve masquait les autres. Mesure du 08/09/2026 :
        // `latency.prefer_max_perf` ressortait « ecriture GPU interdite » alors que la raison la
        // plus forte etait mesuree — la frequence ne bouge pas d'un MHz. Les deux sont vraies, et
        // c'est celle-la qu'on relira dans six mois pour juger si la regle etait bonne.
        var appreciation = Juger(
            Machine(tuningGpuAutorise: false),
            Levier("latency.prefer_max_perf", "gpu.clock"));

        appreciation.Retenu.Should().BeFalse();
        appreciation.Motifs.Should().HaveCount(2);
        appreciation.Motif.Should().Contain("arrêts non propres").And.Contain("2715");
    }

    [Fact]
    public void CHAQUE_APPRECIATION_PORTE_SON_MOTIF()
    {
        // Un refus sans motif est une autorite : ni le joueur ni nous ne saurions si le selecteur
        // s'est trompe.
        var appreciations = CandidateSelector.Evaluer(Machine(), new[]
        {
            Levier("gpu.prefer_max_perf", "gpu.clock"),
            Levier("latency.reflex", "latence"),
            new CandidateDescriptor("bios.xmp", CandidateKind.GuidedBios, 3, false, new[] { "ram.debit" }),
        });

        appreciations.Should().HaveCount(3);
        appreciations.Should().OnlyContain(a => a.Motifs.Count > 0);
        appreciations.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Motif));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // La stabilite retient la main — et pour son propre motif
    //
    // La machine de reference a garde trois `0x193 VIDEO_DXGKRNL_LIVEDUMP`, meme parametre 1
    // (0x80e), les 15, 18 et 22/08/2026. Ecrire sur une carte dans cet etat rend le prochain
    // incident inattribuable : il viendra de notre reglage ou de cette condition, et plus
    // personne ne pourra trancher. C'est l'attribution qu'on perd, pas seulement la stabilite.
    // ══════════════════════════════════════════════════════════════════════════════════════

    private static InstabiliteGpuObservee Watchdogs(int occurrences, int ilYaJours) =>
        new(occurrences, DateTimeOffset.UtcNow.AddDays(-ilYaJours),
            "0x193 VIDEO_DXGKRNL_LIVEDUMP (parametre 1 : 0x80e)");

    [Fact]
    public void UNE_SIGNATURE_REPETEE_ET_RECENTE_FERME_L_ECRITURE_GPU()
    {
        var appreciation = Juger(
            Machine() with { InstabiliteGpu = Watchdogs(occurrences: 3, ilYaJours: 17) },
            Levier("gpu.power_limit", "gpu.power"));

        appreciation.Retenu.Should().BeFalse();
        appreciation.Motif.Should().Contain("VIDEO_DXGKRNL_LIVEDUMP").And.Contain("inattribuable");
    }

    [Fact]
    public void LE_MOTIF_DE_STABILITE_SURVIT_A_LA_LEVEE_DE_LA_DECISION_DU_MAINTENEUR()
    {
        // Le point de tout le decoupage. `TuningGpuAutorise` porte une DECISION ; l'instabilite
        // porte une mesure. Le jour ou cette decision est levee, il ne faut pas lever du meme geste un
        // garde-fou qu'il n'a pas regarde. Ici la decision est LEVEE (tuningGpuAutorise: true) et
        // le refus tient quand meme.
        //
        // La machine est choisie pour que rien D'autre ne refuse : frequence qui chute vraiment,
        // bridage publie. Ecrit d'abord avec `gpu.power` sur une machine sans plafond, ce test
        // passait sur le motif « aucun plafond de puissance observe » — vert sans jamais toucher a
        // la porte de stabilite. La mutation du seuil l'a demasque en ne tuant qu'un test sur deux.
        var appreciation = Juger(
            Machine(tuningGpuAutorise: true, mediane: 2700, minimum: 2100,
                    bridage: GpuLimiterObserved.ChuteDeFrequenceObservee)
                with { InstabiliteGpu = Watchdogs(3, 17) },
            Levier("gpu.prefer_max_perf", "gpu.clock"));

        appreciation.Retenu.Should().BeFalse();
        appreciation.Motifs.Should().ContainSingle()
            .Which.Should().Contain("VIDEO_DXGKRNL_LIVEDUMP",
                "le refus doit venir de la stabilite, et d'elle seule");
    }

    [Fact]
    public void UN_INCIDENT_ISOLE_NE_FERME_RIEN()
    {
        // Le controle NEGATIF. Une seule fois est un accident : fermer une famille entiere la-dessus
        // ferait du selecteur une facon elegante de ne jamais rien essayer.
        Juger(Machine(mediane: 2700, minimum: 2100,
                      bridage: GpuLimiterObserved.ChuteDeFrequenceObservee)
                  with { InstabiliteGpu = Watchdogs(occurrences: 1, ilYaJours: 3) },
              Levier("gpu.prefer_max_perf", "gpu.clock"))
            .Retenu.Should().BeTrue();
    }

    [Fact]
    public void UNE_INSTABILITE_ANCIENNE_EST_UN_FAIT_D_ARCHIVE_ET_NON_UN_VETO()
    {
        // Un pilote a pu etre mis a jour depuis. Passe la fenetre, l'incident reste affichable,
        // mais il n'interdit plus — sinon un incident de 2024 gelerait la carte pour toujours.
        Juger(Machine(mediane: 2700, minimum: 2100,
                      bridage: GpuLimiterObserved.ChuteDeFrequenceObservee)
                  with { InstabiliteGpu = Watchdogs(occurrences: 3, ilYaJours: 200) },
              Levier("gpu.prefer_max_perf", "gpu.clock"))
            .Retenu.Should().BeTrue();
    }

    [Fact]
    public void UN_LEVIER_QUI_NE_TOUCHE_PAS_LA_CARTE_N_EST_PAS_CONCERNE()
    {
        // La porte protege l'ecriture GPU. Un levier scheduler ne devient pas interdit parce que le
        // pilote graphique a crie : ce serait punir une famille pour la faute d'une autre.
        // 11/09/2026 : le veto lit la SURFACE ecrite. Ce levier la declare (processeur) ; sans surface il
        // serait refuse, comme tout levier qui ne dit pas ce qu'il ecrit.
        Juger(Machine() with { InstabiliteGpu = Watchdogs(3, 17) },
              Levier("cpu.affinity_hint", "cpu.residence") with { Surface = SurfaceTouchee.Processeur })
            .Retenu.Should().BeTrue();
    }

    [Fact]
    public void UNE_INSTABILITE_NON_LUE_N_EST_PAS_UNE_ABSENCE_D_INSTABILITE()
    {
        // `null` veut dire « pas lu ». Le traiter comme « aucun incident » serait la meme faute
        // que de rendre 0 pour un compteur illisible.
        var d = Machine(mediane: 2700, minimum: 2100,
                        bridage: GpuLimiterObserved.ChuteDeFrequenceObservee);

        d.InstabiliteGpu.Should().BeNull();
        Juger(d, Levier("gpu.prefer_max_perf", "gpu.clock")).Retenu.Should().BeTrue();
    }
}
