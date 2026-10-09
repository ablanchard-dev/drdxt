using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// 25/09/2026, machine de reference : 27 echecs pour 575 reussites sur 90 jours. KB5124008 a echoue
/// deux fois puis s'est installe ; trois applications du Store restent en echec (0x80073D02).
/// Le compte seul ferait peur pour rien : seul ce qui reste en echec est accuse.
/// </summary>
public class MisesAJourEnEchecTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static EssaiDeMiseAJour Essai(int jour, bool reussi, string titre, string? code = null)
        => new(T0.AddDays(jour), reussi, titre, code);

    [Fact]
    public void Aucun_echec_est_le_defaut_attendu()
        => MisesAJourEnEchec.Juger([Essai(0, true, "KB1")], 90).Should().ContainSingle()
            .Which.Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Un_correctif_rate_puis_installe_ne_reste_pas_en_echec()
    {
        var constats = MisesAJourEnEchec.Juger(
        [
            Essai(10, false, "Correctif (KB5124008)", "0x800704c7"),
            Essai(10, false, "Correctif (KB5124008)", "0x800704c7"),
            Essai(11, true, "Correctif (KB5124008)"),
        ], 90);

        constats.Should().ContainSingle("seul le bilan : rien ne reste en échec");
        constats[0].Explication.Should().StartWith("2 de ces échecs ont été rattrapés");
    }

    /// <summary>27/09/2026, vu sur photo : « Échecs : 27 sur 90 jours » au-dessus de quatre lignes à un échec
    /// chacune. Le rattrapage n'était dit qu'en info-bulle ; le compte visible doit porter sa part rattrapée.</summary>
    [Fact]
    public void Le_compte_visible_dit_combien_d_echecs_ont_ete_rattrapes()
    {
        var bilan = MisesAJourEnEchec.Juger(
        [
            Essai(10, false, "Correctif (KB5124008)", "0x800704c7"),
            Essai(10, false, "Correctif (KB5124008)", "0x800704c7"),
            Essai(11, true, "Correctif (KB5124008)"),
            Essai(12, false, "Defender", "0x8024001e"),
        ], 90).Single(c => c.Nom == "echecs");

        bilan.ValeurCourante.Should().Be("3 échecs en 90 jours, dont 2 rattrapés ensuite");
    }

    [Fact]
    public void Une_appli_du_Store_qui_echoue_a_chaque_essai_est_a_regarder_et_son_code_est_nomme()
    {
        var constats = MisesAJourEnEchec.Juger(
        [
            Essai(1, false, "9NMPJ99VJBWV-Microsoft.YourPhone", "0x80073d02"),
            Essai(8, false, "9NMPJ99VJBWV-Microsoft.YourPhone", "0x80073d02"),
            Essai(15, false, "9NMPJ99VJBWV-Microsoft.YourPhone", "0x80073d02"),
        ], 90);

        // 29/09/2026, troisième critique : le nom de paquet du Store (« 9NMPJ99VJBWV-… ») et le code en tête
        // étaient illisibles pour un joueur. Le titre dit l'application, la valeur dit les échecs, le code reste
        // dans l'explication, après sa première phrase (donc sous « Pourquoi ? »).
        var appli = constats.Single(c => c.Nom == "Mise à jour de l'application YourPhone");
        // UI1 (critique du 08/10) : l'appli marche toujours ; en rouge, elle se rangeait avec un service exploitable.
        appli.Statut.Should().Be(RepairStatus.Observed, "à regarder, pas à corriger");
        appli.Action.Should().Be(RepairAction.Recommandation, "elle reste à l'écran avec son geste");
        appli.ValeurCourante.Should().StartWith("3 échecs").And.NotContain("0x");
        appli.Explication.Should().Contain("0x80073d02").And.NotContain("ERROR_");
        appli.Explication.IndexOf("0x80073d02", StringComparison.Ordinal).Should().BeGreaterThan(appli.Explication.IndexOf(". ", StringComparison.Ordinal));
    }

    [Fact]
    public void Une_mise_a_jour_de_Windows_qui_echoue_a_chaque_essai_reste_a_corriger()
        => MisesAJourEnEchec.Juger([Essai(1, false, "KB5050009", "0x800f0922"), Essai(8, false, "KB5050009", "0x800f0922")], 90)
            .Single(c => c.Nom == "KB5050009").Statut.Should().Be(RepairStatus.Suspicious);

    // Revue n° 15 : Gaming Services ne se range pas avec AppliX : un jeu Game Pass peut ne plus se lancer.
    [Fact]
    public void Gaming_Services_qui_echoue_reste_a_corriger()
        => MisesAJourEnEchec.Juger([Essai(1, false, "9MWPM2CQNLHN-Microsoft.GamingServices", "0x80073d02"),
                                    Essai(8, false, "9MWPM2CQNLHN-Microsoft.GamingServices", "0x80073d02")], 90)
            .Single(c => c.Nom.Contains("GamingServices")).Statut.Should().Be(RepairStatus.Suspicious);

    [Fact]
    public void Un_seul_echec_est_observe_pas_accuse()
        => MisesAJourEnEchec.Juger([Essai(3, false, "Defender", "0x8024001e")], 90)
            .Single(c => c.Nom == "Defender").Statut.Should().Be(RepairStatus.Observed);

    // 04/10/2026 (plan 3.3) : relevé sur la machine de référence. AppliX 26.930 déposé le 02/10 à 18:48:08, échec noté à
    // 18:48:42 en 0x80073D02 : la version installée EST la mise à jour, elle n'est pas en échec.
    [Fact]
    public void Une_appli_du_Store_deposee_au_moment_de_l_echec_n_est_pas_en_echec()
    {
        var echec = new DateTimeOffset(2026, 10, 2, 16, 48, 42, TimeSpan.Zero);
        var essais = new[]
        {
            new EssaiDeMiseAJour(echec.AddDays(-4), false, "9PLM9XGG6VKS-Editeur.AppliX", "0x80073d02"),
            new EssaiDeMiseAJour(echec, false, "9PLM9XGG6VKS-Editeur.AppliX", "0x80073d02"),
        };
        MisesAJourEnEchec.Juger(essais, 90, _ => echec.AddSeconds(-34)).Should().ContainSingle("seul le bilan reste : rattrapée");
        MisesAJourEnEchec.Juger(essais, 90, _ => echec.AddDays(-10)).Should().HaveCount(2, "témoin : version installée bien avant l'échec");
        MisesAJourEnEchec.Juger(essais, 90, _ => null).Should().HaveCount(2, "témoin : paquet non lu");
        MisesAJourEnEchec.NomDePaquet("9PLM9XGG6VKS-Editeur.AppliX").Should().Be("Editeur.AppliX");
        MisesAJourEnEchec.NomDePaquet("Mise à jour de sécurité KB5124008").Should().BeNull();
    }
    // DOC-QUALITE (09/10/2026) : « recommandation » sans geste sur la machine de référence (AppliX, Photos). Ce qui échoue à
    // chaque essai dit comment le relancer : par le Store pour une appli, par Windows Update pour le reste.
    [Fact]
    public void Ce_qui_echoue_a_chaque_essai_dit_comment_le_relancer()
    {
        var constats = MisesAJourEnEchec.Juger(
        [
            Essai(1, false, "9NMPJ99VJBWV-Microsoft.YourPhone", "0x80073d02"),
            Essai(8, false, "9NMPJ99VJBWV-Microsoft.YourPhone", "0x80073d02"),
            Essai(2, false, "Correctif (KB5000001)", "0x8024001e"),
            Essai(9, false, "Correctif (KB5000001)", "0x8024001e"),
        ], 90);

        constats.Single(c => c.Nom == "Mise à jour de l'application YourPhone").Explication
            .Should().EndWith("Pour la relancer : ferme l'application, puis Microsoft Store > Bibliothèque > Obtenir les mises à jour.");
        constats.Single(c => c.Nom == "Correctif (KB5000001)").Explication
            .Should().EndWith("Pour la relancer : Paramètres > Windows Update > Rechercher des mises à jour.");
    }

    // Revue adverse du docteur (09/10/2026, R10) : les définitions de Defender et l'outil de suppression changent de titre
    // à chaque version ; un échec n'était jamais rattrapé par la version suivante réussie et restait « en échec » 90 jours.
    [Fact]
    public void Une_version_suivante_reussie_rattrape_l_echec_d_une_mise_a_jour_versionnee()
    {
        var constats = MisesAJourEnEchec.Juger(
        [
            Essai(1, false, "Mise à jour pour Microsoft Defender Antivirus – KB2267602 (version 1.459.153.0)", "0x8024001e"),
            Essai(2, true, "Mise à jour pour Microsoft Defender Antivirus – KB2267602 (version 1.459.160.0)"),
            Essai(3, false, "Outil de suppression de logiciels malveillants Windows x64 - v5.145 (KB890830)", "0x8024001e"),
            Essai(30, true, "Outil de suppression de logiciels malveillants Windows x64 - v5.146 (KB890830)"),
        ], 90);

        constats.Should().ContainSingle("les deux échecs sont rattrapés par la version suivante");
        constats[0].ValeurCourante.Should().Be("2 échecs en 90 jours, dont 2 rattrapés ensuite");
    }
}
