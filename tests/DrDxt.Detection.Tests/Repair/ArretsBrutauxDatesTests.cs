using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// « 7 pendant une mise en veille ou une extinction » sans dates passerait pour un probleme en
/// cours, alors qu'ils peuvent tous etre anciens. Les dates font partie du fait (releves sur la
/// machine de reference : tous entre 22/07 et 05/08, plus rien ensuite).
/// </summary>
public class ArretsBrutauxDatesTests
{
    private static ArretBrutal Le(int mois, int jour, int code = 0, bool transition = false)
        // À midi, heure de la machine : les dates s'affichent dans son fuseau, le jour attendu ne change pas avec lui.
        => new(new DateTimeOffset(new DateTime(2026, mois, jour, 12, 0, 0, DateTimeKind.Local)), code, false, transition);

    [Fact]
    public void Les_arrets_en_transition_portent_leur_plage_de_dates()
    {
        var texte = RepairAuditService.Repartition(
        [
            Le(9, 4, 0xEF), Le(9, 2, 0x154), Le(8, 18),
            Le(7, 22, transition: true), Le(7, 23, transition: true), Le(7, 26, transition: true),
            Le(7, 28, transition: true), Le(7, 30, transition: true), Le(8, 4, transition: true), Le(8, 5, transition: true),
        ], bouton: 0);

        texte.Should().Contain("0x000000EF CRITICAL_PROCESS_DIED le 04/09").And.Contain("0x00000154 UNEXPECTED_STORE_EXCEPTION le 02/09")
            .And.Contain("7 avec une mise en veille signalée en cours, sens que Microsoft ne documente pas (du 22/07 au 05/08)")
            .And.Contain("1 arrêt sans aucun code (gel, coupure de courant ou alimentation ; le 18/08)");
    }

    // Un arrêt tombé juste après le dernier regard du relevé sur une partie restée ouverte.
    [Fact]
    public void Un_arret_tombe_en_pleine_partie_se_dit_avec_le_jeu()
    {
        var arret = Le(10, 5);
        var texte = RepairAuditService.Repartition([arret, Le(10, 1)], bouton: 0,
            [new DrDxt.Core.PartieInterrompue("cod.exe", arret.Quand.AddHours(-1), arret.Quand.AddMinutes(-3), arret.Quand.AddMinutes(5))]);
        texte.Should().Contain("1 en pleine partie (cod.exe, 05/10)");
        RepairAuditService.Repartition([arret], bouton: 0).Should().NotContain("en pleine partie");
    }
}
