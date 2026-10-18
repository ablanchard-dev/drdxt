using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// La résidence en C-states : un signal daté et comparé, jamais un levier.
public sealed class EtatsDeVeilleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static EtatsDeVeille.Releve R(double c1, double c2, double c3, double repos = 0.95, int jours = 0)
        => new(T0.AddDays(jours), c1, c2, c3, repos, 5000);

    [Fact]
    public void Deux_releves_bruts_donnent_les_parts_et_les_transitions()
    {
        // Deux secondes : 20 000 000 de centaines de ns, C2 pendant 19 690 000, 12 776 entrées.
        var a = new EtatsDeVeille.Compteurs(0, 0, 0, 0, 1_000_000_000, 0, 0, 0, 0, 10_000_000);
        var b = new EtatsDeVeille.Compteurs(36_000, 19_690_000, 0, 19_800_000, 1_020_000_000, 628, 12_148, 0, 20_000_000, 10_000_000);
        var r = EtatsDeVeille.Calculer(a, b, T0)!;
        r.C2.Should().BeApproximately(0.9845, 1e-6);
        r.C1.Should().BeApproximately(0.0018, 1e-6);
        r.Transitions.Should().BeApproximately(6388, 1e-6);
    }

    [Fact]
    public void Un_compteur_qui_recule_n_est_pas_lu()
    {
        var a = new EtatsDeVeille.Compteurs(10, 10, 10, 10, 100, 1, 1, 1, 100, 10);
        EtatsDeVeille.Calculer(a, a with { C2 = 5, Sys100Ns = 200, PerfTime = 200 }, T0).Should().BeNull();
    }

    // La hausse de C3 de 0,08 % à 27,69 % entre deux jours, au repos : un saut, daté. Aucune sauvegarde du BIOS n'est
    // nommée : sa date n'est lue dans aucun format prouvé.
    [Fact]
    public void Un_saut_de_C3_au_repos_est_signale_sans_date_de_BIOS_inventee()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = EtatsDeVeille.Juger(R(0.02, 0.65, 0.2769, jours: 1), R(0.01, 0.48, 0.0008)).Single();
        c.Explication.Should().Contain("Changement net").And.NotContain("BIOS a été sauvegardé");
    }

    [Fact]
    public void Une_variation_faible_ou_un_releve_sous_charge_n_est_pas_un_saut()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        EtatsDeVeille.Juger(R(0.02, 0.70, 0.05, jours: 1), R(0.02, 0.65, 0.01)).Single().Explication.Should().NotContain("Changement net");
        EtatsDeVeille.Juger(R(0.02, 0.10, 0.30, repos: 0.4, jours: 1), R(0.02, 0.65, 0.0)).Single().Explication.Should().NotContain("Changement net");
    }

    // Notre règle : le constat ne propose jamais de couper les C-states, il n'a ni geste ni remède.
    [Fact]
    public void Le_constat_n_est_jamais_un_conseil_de_couper_les_C_states()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
        var c = EtatsDeVeille.Juger(R(0.02, 0.65, 0.2769, jours: 1), R(0.01, 0.48, 0.0008)).Single();
        c.Action.Should().Be(RepairAction.Aucune);
        c.Remede.Should().BeNull();
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("never suggests it");
    }

    [Fact]
    public void Non_lu_est_dit_non_lu()
        => EtatsDeVeille.Juger(null, null).Single().Statut.Should().Be(RepairStatus.Unknown);
}
