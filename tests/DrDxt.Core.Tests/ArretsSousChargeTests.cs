using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// L'événement 41 ne dit pas si un jeu tournait, et il est daté du redémarrage. Une partie
/// restée ouverte a été coupée entre son dernier regard et le relevé suivant : le premier arrêt de cet intervalle est le
/// sien, et un seul.
/// </summary>
public class ArretsSousChargeTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);
    private static readonly PartieInterrompue Cod = new("cod.exe", T.AddHours(-1), T, ReleveeLe: T.AddMinutes(20));

    [Fact]
    public void Un_arret_entre_le_dernier_regard_et_le_releve_est_en_pleine_partie()
        => ClasseurDArrets.MarquerSousCharge([new ArretClasse(T.AddMinutes(12), FamilleDArret.SansCode)], [Cod])
            .Single().PendantLaPartieDe.Should().Be("cod.exe");

    [Theory]
    [InlineData(25)]  // après le relevé : le relevé avait repris, la partie était déjà comptée
    [InlineData(-30)] // avant le dernier regard : le jeu était encore vu vivant après
    public void Hors_de_l_intervalle_l_arret_reste_tel_quel(int minutes)
        => ClasseurDArrets.MarquerSousCharge([new ArretClasse(T.AddMinutes(minutes), FamilleDArret.SansCode)], [Cod])
            .Single().PendantLaPartieDe.Should().BeNull();

    // Des redémarrages en boucle comptaient la même partie pour chaque arrêt.
    [Fact]
    public void Une_partie_ne_marque_qu_un_arret_le_premier()
    {
        var marques = ClasseurDArrets.MarquerSousCharge(
            [new ArretClasse(T.AddMinutes(7), FamilleDArret.SansCode), new ArretClasse(T.AddMinutes(2), FamilleDArret.SansCode)], [Cod]);
        marques.Count(a => a.PendantLaPartieDe is not null).Should().Be(1);
        marques.Single(a => a.PendantLaPartieDe is not null).Quand.Should().Be(T.AddMinutes(2));
    }

    [Fact]
    public void Sans_heure_de_releve_ni_partie_rien_ne_change_et_une_entree_nulle_est_ignoree()
    {
        var arret = new ArretClasse(T.AddMinutes(5), FamilleDArret.SansCode);
        ClasseurDArrets.MarquerSousCharge([arret], [Cod with { ReleveeLe = null }]).Single().PendantLaPartieDe.Should().BeNull();
        ClasseurDArrets.MarquerSousCharge([arret], []).Single().PendantLaPartieDe.Should().BeNull();
        ClasseurDArrets.MarquerSousCharge([arret], [null, Cod]).Single().PendantLaPartieDe.Should().Be("cod.exe");
    }

    [Fact]
    public void La_carte_fermee_dit_combien_d_arrets_sont_tombes_en_partie()
    {
        var maintenant = DateTimeOffset.UtcNow;
        var d = DiagnosticDeScan.Construire(goulot: null, mesure: null, instabiliteGpu: null, arretsBrutaux:
            [new ArretClasse(maintenant.AddDays(-1), FamilleDArret.SansCode, PendantLaPartieDe: "cod.exe"),
             new ArretClasse(maintenant.AddDays(-2), FamilleDArret.SansCode)]);
        CandidateSelector.CarteFermeeParLesArrets(d, maintenant).Should().Contain("dont 1 en pleine partie (cod.exe)");
    }
}
