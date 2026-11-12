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

    // Relevés sur la machine de référence : l'écran de verrouillage, une console et l'outil Capture, pris pour des parties
    // avant d'être écartés du relevé. Restés dans le fichier, ils ne disent pas qu'un arrêt est tombé en pleine partie.
    [Theory]
    [InlineData("LockApp.exe")]
    [InlineData("powershell.exe")]
    [InlineData("SnippingTool.exe")]
    public void Une_marque_laissee_par_un_programme_qui_n_est_pas_un_jeu_ne_marque_aucun_arret(string exe)
        => ClasseurDArrets.MarquerSousCharge([new ArretClasse(T.AddMinutes(12), FamilleDArret.SansCode)], [Cod with { Jeu = exe }])
            .Single().PendantLaPartieDe.Should().BeNull();

    // Un nom hors du format strict ou inconnu reste une partie : seul un nom lu et connu pour ne pas être un jeu s'écarte.
    [Theory]
    [InlineData("Among Us.exe")]
    [InlineData("UnJeuInconnu-Win64-Shipping.exe")]
    public void Un_jeu_que_la_liste_ne_connait_pas_marque_toujours_son_arret(string exe)
        => ClasseurDArrets.MarquerSousCharge([new ArretClasse(T.AddMinutes(12), FamilleDArret.SansCode)], [Cod with { Jeu = exe }])
            .Single().PendantLaPartieDe.Should().Be(exe);

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
