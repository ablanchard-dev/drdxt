using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Un profil doit savoir ou il est ne.
///
/// Une « partie reelle, zone calme » est une vraie scene de gameplay ; sans contrat, rien ne la
/// distinguerait d'une session prise dans un lobby.
///
/// Le silence ne vaut jamais « gameplay ». Le defaut est `Unknown` et il interdit le keep
/// durable, inconfortable exprès : c'est ce qui force a declarer.
/// </summary>
public class SceneContractTests
{
    [Fact]
    public void Par_defaut_on_ne_sait_pas_et_aucun_keep_durable_n_est_permis()
    {
        SceneContract.Inconnue.KeepDurable.Should().BeFalse();
        SceneContract.Inconnue.CeQuiManque.Should().Contain("personne n'a dit ce qui était joué");
    }

    [Fact]
    public void Un_MENU_ne_defend_aucun_profil()
    {
        // Le PC n'y fait pas le travail d'une partie : un gain trouve la ne dit rien de ce que le
        // joueur ressentira.
        var menu = new SceneContract(SceneRepresentativity.MenuLobby, SceneRole.Validation);

        menu.KeepDurable.Should().BeFalse("meme en role de validation, un lobby ne valide rien");
        menu.CeQuiManque.Should().Contain("menu");
    }

    [Fact]
    public void Une_zone_calme_DECOUVRE_un_candidat_mais_ne_conclut_pas_pour_tout_le_jeu()
    {
        // Le piege : une scene stable est le BON endroit pour trouver un candidat (elle bruite peu,
        // donc elle resout mieux) et le MAUVAIS pour affirmer que le
        // profil vaut pour toute la carte.
        var decouverte = new SceneContract(
            SceneRepresentativity.Gameplay, SceneRole.Decouverte, "partie reelle, zone calme");

        decouverte.KeepDurable.Should().BeFalse();
        decouverte.CeQuiManque.Should().Contain("revalider");
    }

    [Fact]
    public void Une_VALIDATION_en_gameplay_est_le_seul_chemin_vers_un_keep_durable()
    {
        var validation = new SceneContract(
            SceneRepresentativity.Gameplay, SceneRole.Validation, "partie reelle, rotation");

        validation.KeepDurable.Should().BeTrue();
        validation.CeQuiManque.Should().BeNull("il ne manque rien : c'est le cas nominal");
    }

    [Fact]
    public void Un_TERRAIN_D_ENTRAINEMENT_decouvre_mais_ne_valide_jamais()
    {
        var entrainement = new SceneContract(SceneRepresentativity.Entrainement, SceneRole.Validation);

        entrainement.KeepDurable.Should().BeFalse();
        entrainement.CeQuiManque.Should().Contain("pas pour conclure sur le jeu");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Le premier scan ne se fait pas en jeu, et rien ne permettait de le dire.
    //
    // Les quatre reponses proposees decrivaient toutes une session de JEU (partie, entrainement,
    // menu) ou l'absence de reponse. Quelqu'un qui lance son premier scan sans avoir ouvert le
    // moindre jeu n'avait que « je ne sais pas » — un aveu d'ignorance a la place d'un fait qu'il
    // connait parfaitement. Le produit lisait donc « scene inconnue » la ou la verite est « aucun
    // jeu ne tournait », deux choses que rien ne distinguait.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void HORS_JEU_se_declare_et_ne_se_confond_pas_avec_une_scene_inconnue()
    {
        var repos = new SceneContract(SceneRepresentativity.HorsJeu);

        repos.KeepDurable.Should().BeFalse("au repos, la machine ne fait pas le travail d'une partie");
        repos.CeQuiManque.Should().Contain("aucun jeu ne tournait");
        repos.CeQuiManque.Should().NotBe(SceneContract.Inconnue.CeQuiManque,
            "« je n'etais pas en jeu » est un FAIT, « je ne sais pas » est une ignorance");
    }

    [Fact]
    public void Aucune_scene_ne_peut_etre_ajoutee_sans_dire_ce_qu_elle_permet_de_conclure()
    {
        // Le garde structurel : un nouveau membre de l'enum qui tomberait dans le `_ =>` du switch
        // heriterait EN SILENCE de la phrase du gameplay — donc d'une permission qu'il n'a pas.
        foreach (var representativite in Enum.GetValues<SceneRepresentativity>())
        {
            var scene = new SceneContract(representativite, SceneRole.Validation);
            if (representativite == SceneRepresentativity.Gameplay)
            {
                scene.CeQuiManque.Should().BeNull("une validation en partie reelle est le cas nominal");
                continue;
            }

            scene.KeepDurable.Should().BeFalse($"{representativite} ne defend pas un profil");
            scene.CeQuiManque.Should().NotBeNullOrWhiteSpace(
                $"{representativite} doit dire ce qui lui manque, sinon elle passe pour du gameplay");
        }
    }
}
