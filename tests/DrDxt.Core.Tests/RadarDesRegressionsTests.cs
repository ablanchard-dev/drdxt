using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le radar des régressions connues : une règle publiée (mise à jour de Windows, plage de pilote, processeur) ne se dit
/// que si chaque critère qu'elle donne correspond à ce qui est lu ; chaque verdict porte la source et sa date. Aucune
/// règle sans adresse, et ce qui n'est pas lu n'est jamais jugé.
/// </summary>
public sealed class RadarDesRegressionsTests
{
    private static RegleDeRegression Regle(string id = "r1", string? kb = "KB5000001", string? corrigee = null,
        string? carte = null, string? depuis = null, string? jusqua = null, string? processeur = null)
        => new(id, kb, corrigee, carte, depuis, jusqua, processeur, "Phrase de la règle.", "Rule sentence.",
            "https://learn.microsoft.com/windows/release-health/exemple", new DateOnly(2026, 10, 16));

    private static TablesDeDonnees Tables(params RegleDeRegression[] regles) => new(4, new DateOnly(2026, 10, 17), regles);

    private static readonly IReadOnlyList<(string Nom, string? Version)> UneNvidia = [("NVIDIA GeForce RTX 4060", "32.0.15.6094")];

    private static IReadOnlyList<RepairFinding> Juger(TablesDeDonnees t, IReadOnlyList<string>? kb = null,
        IReadOnlyList<(string, string?)>? cartes = null, string? processeur = "Intel(R) Core(TM) i7-14700F")
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        return RadarDesRegressions.Juger(t, kb ?? ["KB5000001"], cartes ?? UneNvidia, processeur).ToList();
    }

    [Fact]
    public void Une_regle_dont_le_KB_est_installe_se_dit_avec_sa_source_et_sa_date()
    {
        var c = Juger(Tables(Regle())).Should().ContainSingle(f => f.Statut == RepairStatus.Suspicious).Subject;
        c.Explication.Should().Contain("Phrase de la règle.").And.Contain("https://learn.microsoft.com/windows/release-health/exemple")
            .And.Contain("16/10/2026").And.Contain("version 4");
    }

    [Fact]
    public void La_phrase_suit_la_langue_de_l_App()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
        RadarDesRegressions.Juger(Tables(Regle()), ["KB5000001"], UneNvidia, null)
            .Should().Contain(f => f.Explication.Contains("Rule sentence."));
    }

    [Fact]
    public void Un_KB_absent_ne_concerne_pas_ce_PC()
    {
        Juger(Tables(Regle()), kb: ["KB5999999"]).Should().NotContain(f => f.Statut == RepairStatus.Suspicious)
            .And.ContainSingle(f => f.Statut == RepairStatus.Observed);
    }

    [Fact]
    public void Le_correctif_installe_met_la_machine_hors_de_cause()
    {
        Juger(Tables(Regle(corrigee: "KB5000002")), kb: ["KB5000001", "KB5000002"])
            .Should().NotContain(f => f.Statut == RepairStatus.Suspicious);
    }

    [Theory]
    [InlineData("32.0.15.6094", true)]
    [InlineData("32.0.15.6100", false)]
    [InlineData("31.0.15.5000", false)]
    public void Une_plage_de_pilote_se_compare_nombre_a_nombre(string version, bool concerne)
    {
        var regle = Regle(kb: null, carte: "NVIDIA", depuis: "32.0.15.6000", jusqua: "32.0.15.6094");
        Juger(Tables(regle), cartes: [("NVIDIA GeForce RTX 4060", version)])
            .Any(f => f.Statut == RepairStatus.Suspicious).Should().Be(concerne);
    }

    [Fact]
    public void Tous_les_criteres_donnes_doivent_correspondre()
    {
        // Le KB est là, mais la règle ne vise que les Ryzen : ce PC Intel n'est pas concerné.
        Juger(Tables(Regle(processeur: "Ryzen"))).Should().NotContain(f => f.Statut == RepairStatus.Suspicious);
        Juger(Tables(Regle(processeur: "Ryzen")), processeur: "AMD Ryzen 5 1234X 6-Core Processor")
            .Should().Contain(f => f.Statut == RepairStatus.Suspicious);
    }

    [Fact]
    public void Ce_qui_n_est_pas_lu_n_est_jamais_juge_sain()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        RadarDesRegressions.Juger(Tables(Regle()), null, UneNvidia, null)
            .Should().ContainSingle().Which.Statut.Should().Be(RepairStatus.Unknown);

        // Une règle sur le pilote, cartes non lues : non jugée, et le compte le dit.
        var c = RadarDesRegressions.Juger(Tables(Regle(kb: null, carte: "NVIDIA", depuis: "1.0")), ["KB1000000"], null, null).ToList();
        c.Should().NotContain(f => f.Statut == RepairStatus.Suspicious);
        c.Should().ContainSingle(f => f.Statut == RepairStatus.Unknown);
    }

    [Fact]
    public void Un_radar_vide_le_dit_au_lieu_de_se_taire()
    {
        var c = Juger(TablesDeDonnees.Embarquees).Should().ContainSingle().Subject;
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("Aucune règle");
    }
    // La table vide se reconnaît à sa forme, pas à son texte : un constat dit dans une langue reste reconnu dans l'autre.
    [Fact]
    public void La_table_vide_se_reconnait_dans_une_autre_langue_que_la_sienne()
    {
        RepairFinding vide, aucune;
        using (Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("en-US")))
        {
            vide = RadarDesRegressions.Juger(Tables(), ["KB5000001"], UneNvidia, null).Single();
            aucune = RadarDesRegressions.Juger(Tables(Regle()), ["KB5999999"], UneNvidia, null).Single();
        }
        using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

        RadarDesRegressions.TableVide(vide).Should().BeTrue();
        RadarDesRegressions.TableVide(aucune).Should().BeFalse("témoin : une table lue sans règle concernée n'est pas vide");
    }
}
