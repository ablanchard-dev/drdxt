using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// La cadence réelle des entrées : chaque rapport compte, y compris ceux groupés dans un même message.
public sealed class CadenceDesEntreesTests
{
    private static double[] Instants(int n, double pas) => Enumerable.Range(0, n).Select(i => i * pas).ToArray();

    [Fact]
    public void Une_souris_a_1000_Hz_donne_1000_rapports_par_seconde_et_une_mediane_de_1_ms()
    {
        var a = CadenceDesEntrees.Calculer("souris", false, Instants(1001, 0.001), Enumerable.Repeat(1, 1001).ToArray())!;
        CadenceDesEntrees.Cadence(a).Should().BeApproximately(1000, 1);
        a.MedianeMs.Should().BeApproximately(1, 1e-6);
        a.Trous.Should().Be(0);
    }

    // Notre règle : un message qui porte plusieurs rapports HID les compte tous ; horodater à la réception sans les
    // compter sous-estimerait une manette rapide d'autant.
    [Fact]
    public void Les_rapports_groupes_dans_un_message_comptent_tous()
    {
        var a = CadenceDesEntrees.Calculer("manette", false, Instants(1001, 0.001), Enumerable.Repeat(8, 1001).ToArray())!;
        CadenceDesEntrees.Cadence(a).Should().BeApproximately(8000, 8);
    }

    [Fact]
    public void Un_trou_est_un_intervalle_de_plus_de_trois_fois_la_mediane()
    {
        var t = Instants(100, 0.001).Concat([0.2]).ToArray();
        CadenceDesEntrees.Calculer("x", false, t, Enumerable.Repeat(1, t.Length).ToArray())!.Trous.Should().Be(1);
    }

    [Fact]
    public void Un_appareil_virtuel_est_dit_ce_n_est_pas_ta_manette_et_la_couche_est_dite()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var a = CadenceDesEntrees.Calculer("Controller (XBOX 360 For Windows)", true, Instants(200, 0.0006), Enumerable.Repeat(1, 200).ToArray())!;
        var c = CadenceDesEntrees.Juger([a], coucheActive: true, TimeSpan.FromSeconds(5));
        c.Explication.Should().Contain("appareil virtuel, pas ta manette").And.Contain("à travers une couche logicielle");
        c.Action.Should().Be(RepairAction.Aucune);
    }

    // Une manette virtuelle peut envoyer bien plus vite que l'appareil réel qu'elle recopie : elle est dite, jamais prise
    // pour le chiffre de tête. Sans appareil réel, rien n'est mesuré.
    [Fact]
    public void Le_chiffre_de_tete_vient_d_un_appareil_reel_jamais_d_un_virtuel()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var virtuel = CadenceDesEntrees.Calculer("Manette virtuelle", true, Instants(400, 0.0005), Enumerable.Repeat(1, 400).ToArray())!;
        var reel = CadenceDesEntrees.Calculer("Manette", false, Instants(200, 0.001), Enumerable.Repeat(1, 200).ToArray())!;

        CadenceDesEntrees.Juger([virtuel, reel], false, TimeSpan.FromSeconds(5)).ValeurCourante.Should().Be("1000 rapports/s");
        var seul = CadenceDesEntrees.Juger([virtuel], false, TimeSpan.FromSeconds(5));
        seul.Statut.Should().Be(RepairStatus.Unknown);
        seul.ValeurCourante.Should().BeNull();
    }

    [Fact]
    public void Rien_bouge_ou_ecoute_fermee_est_dit_non_mesure()
    {
        CadenceDesEntrees.Juger([], false, TimeSpan.FromSeconds(5)).Statut.Should().Be(RepairStatus.Unknown);
        CadenceDesEntrees.Juger(null, false, TimeSpan.FromSeconds(5)).Statut.Should().Be(RepairStatus.Unknown);
        CadenceDesEntrees.Calculer("x", false, [0, 0.001], [1, 1]).Should().BeNull();
    }

    // Raw Input n'admet qu'une fenêtre cible par type d'appareil et par processus : deux écoutes en même temps se
    // voleraient l'abonnement, et le retrait de l'une couperait l'autre. Elles se suivent.
    [Fact]
    public async Task Deux_ecoutes_du_meme_processus_se_suivent()
    {
        var duree = TimeSpan.FromMilliseconds(300);
        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var deux = new[] { Task.Run(() => CadenceDesEntrees.Ecouter(duree, CancellationToken.None)),
                           Task.Run(() => CadenceDesEntrees.Ecouter(duree, CancellationToken.None)) };
        await Task.WhenAll(deux);
        chrono.Elapsed.Should().BeGreaterThanOrEqualTo(duree * 2 - TimeSpan.FromMilliseconds(20));
    }
}
