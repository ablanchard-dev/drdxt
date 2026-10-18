using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Le docteur dit ce que Windows Update a en attente, pas seulement ses échecs passés. Relevé sur la machine
// de référence (API officielle, hors ligne, 2,4 s) :
// une définition Defender et trois pilotes en attente, dernière vérification relevée le 05/10 à 19:02.
public sealed class MisesAJourEnAttenteTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 6, 6, 0, 0, TimeSpan.FromHours(2));

    private static MiseAJourEnAttente Pilote(string titre) => new(titre, GenreDeMiseAJour.Pilote);

    // Ma capture de Paramètres « Windows 11, version 26H2 est disponible · Télécharger et
    // installer ». L'API la rend en DeploymentAction='OptionalInstallation' (mesuré : catégorie Upgrades) ; la recherche
    // de 3b.7 ne la voyait pas.
    [Fact]
    public void Une_nouvelle_version_de_Windows_proposee_est_dite_avec_la_version_installee()
    {
        var c = MisesAJourEnAttente.JugerProposees(
            [new("Windows 11, version 26H2", GenreDeMiseAJour.NouvelleVersion)], "25H2").Single();

        c.Nom.Should().Be("nouvelle version de Windows proposée");
        c.ValeurCourante.Should().Be("Windows 11, version 26H2");
        c.ValeurAttendue.Should().Be("25H2 installée");
        c.Statut.Should().Be(RepairStatus.Observed, "ce n'est pas un correctif de sécurité");
        c.Explication.Should().Contain("Télécharger et installer").And.Contain("hors partie").And.Contain("rien ne presse");
    }

    // Relevé sur la machine de référence : la même version proposée deux fois sous le même titre. Une seule ligne.
    [Fact]
    public void Une_version_proposee_deux_fois_sous_le_meme_titre_n_est_dite_qu_une_fois()
        => MisesAJourEnAttente.JugerProposees(
            [new("Windows 11, version 26H2", GenreDeMiseAJour.NouvelleVersion), new("Windows 11, version 26H2", GenreDeMiseAJour.NouvelleVersion)], "25H2")
            .Should().ContainSingle().Which.ValeurCourante.Should().Be("Windows 11, version 26H2");

    // La seconde recherche en panne est « non lu », pas « rien de proposé ».
    [Fact]
    public void Une_recherche_des_propositions_ratee_se_dit_non_lue()
        => MisesAJourEnAttente.JugerProposees(null, "25H2").Single().Statut.Should().Be(RepairStatus.Unknown);

    // Une préversion facultative n'est ni une nouvelle version ni une sécurité : rien n'est dit.
    [Fact]
    public void Une_preversion_facultative_n_est_pas_un_constat()
        => MisesAJourEnAttente.JugerProposees(
            [new("2026-09 Mise à jour de la préversion (KB5124010) (26200.9550)", GenreDeMiseAJour.Autre)], "25H2")
            .Should().BeEmpty();

    [Fact]
    public void Ce_qui_attend_est_dit_par_genre_avec_la_date_de_verification()
    {
        var etat = new EtatWindowsUpdate([
            new("Mise à jour de la sélection disjointe pour Microsoft Defender Antivirus", GenreDeMiseAJour.Definition),
            Pilote("STMicroelectronics - Other hardware - STM Device in DFU Mode"),
            Pilote("Realtek SoftwareComponent Driver Update (1.0.1026.0)"),
            Pilote("Realtek SoftwareComponent Driver Update (11.0.6000.398)"),
        ], Maintenant.AddHours(-11));

        // Le fuseau est donné, pas celui de la machine de test.
        var c = MisesAJourEnAttente.Juger(etat, Maintenant, fuseau: TimeZoneInfo.Utc);

        c.Statut.Should().Be(RepairStatus.Observed, "des pilotes et une définition n'ont rien d'urgent");
        c.Explication.Should().Contain("4 mises à jour attendent").And.Contain("3 pilotes").And.Contain("1 définition")
            .And.Contain("STMicroelectronics").And.Contain("et 1 autre").And.Contain("05/10 à 17:00")
            .And.Contain("DrDXT n'installe rien").And.NotContain("Paramètres > Windows Update");
        c.Remede.Should().StartWith("1) Paramètres > Windows Update").And.Contain("2) ");
        c.Explication.Should().NotContain("Realtek SoftwareComponent Driver Update (11.0.6000.398)", "3 titres au plus");
    }

    [Fact]
    public void Rien_en_attente_mais_pas_verifie_depuis_longtemps_donne_le_geste_a_part()
    {
        var c = MisesAJourEnAttente.Juger(new EtatWindowsUpdate([], Maintenant.AddDays(-30)), Maintenant);

        c.Explication.Should().Contain("rien en attente").And.NotContain("Rechercher des mises à jour");
        c.Remede.Should().StartWith("1) Paramètres > Windows Update > Rechercher des mises à jour");
        // Le geste « chercher maintenant » ne s'attache qu'à une vérification ancienne.
        c.Cible.Should().Be(MisesAJourEnAttente.CibleRecherche);
        MisesAJourEnAttente.Juger(new EtatWindowsUpdate([], Maintenant.AddHours(-3)), Maintenant).Cible.Should().BeNull();
    }

    // Windows ne compte que ses propres recherches (Microsoft Learn, LastSearchSuccessDate : « Only the AutomaticUpdates
    // object will update this property »). Celle que DrDXT a lancée compte aussi : la plus récente des deux.
    [Fact]
    public void La_derniere_recherche_est_la_plus_recente_de_Windows_et_de_DrDXT()
    {
        var windows = Maintenant.AddDays(-30);
        LecteurWindowsUpdate.LaPlusRecente(windows, Maintenant.AddHours(-1).ToString("O")).Should().Be(Maintenant.AddHours(-1));
        LecteurWindowsUpdate.LaPlusRecente(windows, Maintenant.AddDays(-60).ToString("O")).Should().Be(windows);
        LecteurWindowsUpdate.LaPlusRecente(windows, "pas une date").Should().Be(windows, "une marque illisible ne vaut rien");
        LecteurWindowsUpdate.LaPlusRecente(null, null).Should().BeNull();
        LecteurWindowsUpdate.LaPlusRecente(null, Maintenant.ToString("O")).Should().Be(Maintenant);
    }

    [Fact]
    public void Un_correctif_de_securite_en_attente_est_a_regarder()
        => MisesAJourEnAttente.Juger(new EtatWindowsUpdate([new("2026-10 Mise à jour cumulative", GenreDeMiseAJour.Securite)],
                Maintenant.AddHours(-2)), Maintenant)
            .Statut.Should().Be(RepairStatus.Suspicious);

    [Fact]
    public void Une_nouvelle_version_de_Windows_proposee_est_nommee()
        => MisesAJourEnAttente.Juger(new EtatWindowsUpdate([new("Windows 11, version 26H1", GenreDeMiseAJour.NouvelleVersion)],
                Maintenant.AddHours(-2)), Maintenant)
            .Explication.Should().Contain("nouvelle version de Windows").And.Contain("Windows 11, version 26H1");

    [Fact]
    public void Rien_en_attente_et_verifie_recemment_est_le_bon_etat()
        => MisesAJourEnAttente.Juger(new EtatWindowsUpdate([], Maintenant.AddHours(-3)), Maintenant)
            .Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Windows_qui_n_a_pas_verifie_depuis_plus_d_une_semaine_est_a_regarder()
    {
        var c = MisesAJourEnAttente.Juger(new EtatWindowsUpdate([], Maintenant.AddDays(-12)), Maintenant);

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("12 jours");
    }

    // Jamais vérifié n'est pas « depuis 0 jours ».
    [Fact]
    public void Jamais_verifie_le_dit()
    {
        var c = MisesAJourEnAttente.Juger(new EtatWindowsUpdate([], null), Maintenant);

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("jamais réussi à vérifier").And.NotContain("0 jours");
    }

    // Le nom d'une catégorie est traduit selon la langue de Windows ; son identifiant non.
    [Theory]
    [InlineData(1, "0fa1201d-4330-4fa8-8ae9-b877473b6441", GenreDeMiseAJour.Securite)]
    [InlineData(1, "e6cf1350-c01b-414d-a61f-263d14d133b4", GenreDeMiseAJour.Securite)]
    [InlineData(1, "3689bdc8-b205-4af4-8d4a-a63924c5e9d5", GenreDeMiseAJour.NouvelleVersion)]
    [InlineData(1, "e0789628-ce08-4437-be74-2495b842f43b", GenreDeMiseAJour.Definition)]
    [InlineData(2, "00000000-0000-0000-0000-000000000000", GenreDeMiseAJour.Pilote)]
    [InlineData(1, "cd5ffd1e-e932-4e3a-bf74-18bf0b1bbd83", GenreDeMiseAJour.Autre)]
    public void Le_genre_se_lit_par_l_identifiant_de_categorie(int type, string categorie, GenreDeMiseAJour attendu)
        => LecteurWindowsUpdate.Genre(type, [categorie]).Should().Be(attendu);

    [Fact]
    public void Non_lu_n_est_jamais_rien_en_attente()
    {
        var c = MisesAJourEnAttente.Juger(null, Maintenant, "service arrêté");

        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("service arrêté");
    }
}
