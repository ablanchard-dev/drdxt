using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>
/// Les traces d'autres outils de réglage : une liste fermée de clés documentées par Microsoft, datées et rangées, jamais
/// corrigées. Un constat neutre, qui ne nomme personne et n'écrit rien.
/// </summary>
public sealed class TracesDOptimiseursTests
{
    private static readonly DateTimeOffset Installation = new(2025, 4, 10, 1, 24, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Demarrage = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> Aucune = new HashSet<string>();

    [Fact]
    public void Chaque_cle_de_la_liste_porte_une_source_Microsoft()
    {
        TracesDOptimiseurs.Liste.Should().NotBeEmpty();
        TracesDOptimiseurs.Liste.Should().OnlyContain(c => c.Source.StartsWith("https://learn.microsoft.com/", StringComparison.Ordinal));
        TracesDOptimiseurs.SourceAffinite.Should().StartWith("https://learn.microsoft.com/");
    }

    // Les réglages que la barrière anti-mythe refuse et qu'aucune page Microsoft ne décrit ne deviennent pas des traces
    // « connues » : la liste ne cite que ce qu'une source primaire documente.
    [Theory]
    [InlineData("NetworkThrottlingIndex")]
    [InlineData("GlobalTimerResolutionRequests")]
    [InlineData("Win32PrioritySeparation")]
    public void Une_valeur_sans_page_Microsoft_n_entre_pas_dans_la_liste(string valeur)
    {
        TracesDOptimiseurs.Liste.SelectMany(c => c.Valeurs).Should().NotContain(valeur);
    }

    [Theory]
    [InlineData(0, 2, TracesDOptimiseurs.Origine.AuDemarrage)]
    [InlineData(0, 10, TracesDOptimiseurs.Origine.SansRepere)]
    [InlineData(-100, 0, TracesDOptimiseurs.Origine.SansRepere)]
    public void La_date_se_range_par_rapport_au_demarrage(int jours, int minutes, TracesDOptimiseurs.Origine attendue)
    {
        TracesDOptimiseurs.Classer(Demarrage.AddDays(jours).AddMinutes(minutes), Installation, Demarrage, false).Should().Be(attendue);
    }

    [Fact]
    public void La_date_se_range_par_rapport_a_l_installation_et_a_DrDXT()
    {
        TracesDOptimiseurs.Classer(Installation.AddHours(5), Installation, Demarrage, false).Should().Be(TracesDOptimiseurs.Origine.ApresInstallation);
        TracesDOptimiseurs.Classer(Installation.AddHours(30), Installation, Demarrage, false).Should().Be(TracesDOptimiseurs.Origine.SansRepere);
        TracesDOptimiseurs.Classer(Installation.AddHours(5), Installation, Demarrage, true).Should().Be(TracesDOptimiseurs.Origine.TenueParDrDxt);
        TracesDOptimiseurs.Classer(null, Installation, Demarrage, false).Should().Be(TracesDOptimiseurs.Origine.DateNonLue);
        // Relevé sur la machine de référence : la clé MMCSS date du 01/04/2024, Windows du 10/04/2025.
        TracesDOptimiseurs.Classer(new DateTimeOffset(2024, 4, 1, 9, 29, 0, TimeSpan.Zero), Installation, Demarrage, false)
            .Should().Be(TracesDOptimiseurs.Origine.AvantInstallation);
    }

    private static List<RepairFinding> Juger(params TracesDOptimiseurs.Lecture[] lectures)
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        return TracesDOptimiseurs.Juger(lectures, Installation, Demarrage, Aucune).ToList();
    }

    [Fact]
    public void Des_valeurs_de_delai_TDR_posees_sont_une_trace_datee_avec_sa_source()
    {
        var c = Juger(new TracesDOptimiseurs.Lecture("tdr", TracesDOptimiseurs.Liste[1].Chemin,
            new Dictionary<string, string> { ["TdrDelay"] = "10" }, Installation.AddDays(100))).Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.NonDefault, "des logiciels de rendu demandent de rallonger TdrDelay");
        c.Action.Should().Be(RepairAction.Aucune, "une trace se dit, DrDXT ne la retire pas");
        c.Explication.Should().Contain("TdrDelay = 10").And.Contain(TracesDOptimiseurs.SourceTdr).And.Contain("hors du démarrage");
    }

    [Fact]
    public void Une_cle_de_la_carte_sans_valeur_de_delai_ne_dit_rien()
    {
        Juger(new TracesDOptimiseurs.Lecture("tdr", TracesDOptimiseurs.Liste[1].Chemin, new Dictionary<string, string>(), Demarrage))
            .Should().BeEmpty();
    }

    private const string Affinite = @"SYSTEM\CurrentControlSet\Enum\PCI\VEN_10DE&DEV_2882\4&1\Device Parameters\Interrupt Management\Affinity Policy";

    [Fact]
    public void Des_processeurs_designes_un_a_un_sont_une_trace_qui_nomme_le_peripherique()
    {
        var c = Juger(new TracesDOptimiseurs.Lecture("affinite", Affinite,
            new Dictionary<string, string> { ["DevicePolicy"] = "4", ["AssignmentSetOverride"] = "0x0C" }, null, "NVIDIA GeForce RTX 4060"))
            .Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.NonDefault);
        c.Nom.Should().Contain("NVIDIA GeForce RTX 4060");
        c.Explication.Should().Contain("date non lue").And.Contain(TracesDOptimiseurs.SourcePolitique);
    }

    // Relevé sur la machine de référence le 17/10/2026 : NVMe et SATA à 5, Wi-Fi à 0, carte réseau à 1, quatre clés
    // écrites à la même minute. Le fichier d'installation d'un pilote peut poser sa politique (Microsoft le documente) :
    // les dire « traces » accuserait peut-être Windows.
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("5")]
    public void Une_politique_que_posent_les_pilotes_n_est_pas_une_trace(string politique)
    {
        Juger(new TracesDOptimiseurs.Lecture("affinite", Affinite, new Dictionary<string, string> { ["DevicePolicy"] = politique },
            DateTimeOffset.Now, "Standard NVM Express Controller")).Should().BeEmpty();
    }

    [Fact]
    public void Le_registre_non_lu_se_dit()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        TracesDOptimiseurs.Juger(null, Installation, Demarrage, Aucune).Should().ContainSingle()
            .Which.Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public void Une_cle_ou_DrDXT_tient_un_reglage_le_dit()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var chemin = TracesDOptimiseurs.Liste[1].Chemin;
        TracesDOptimiseurs.Juger([new TracesDOptimiseurs.Lecture("tdr", chemin, new Dictionary<string, string> { ["TdrLevel"] = "0" }, Demarrage.AddHours(2))],
                Installation, Demarrage, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chemin })
            .Single().Explication.Should().Contain("DrDXT y tient lui-même un réglage");
    }
}
