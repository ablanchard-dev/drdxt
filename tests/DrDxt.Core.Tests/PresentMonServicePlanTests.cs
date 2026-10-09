using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le cycle de vie du service de mesure — trois cas, pas deux.
///
/// Le code precedent n'en connaissait que deux : « il existe donc demarre-le », sinon « cree-le ».
/// Ca laissait passer le cas qui compte : **un service enregistre sous ce nom qui pointe
/// ailleurs** — une ancienne installation de DrDXT, une app deplacee, ou un autre PresentMon
/// ayant pris le nom. Le demarrer revient a mesurer a travers un binaire dont on ignore la
/// version, et l'API vendorée y est sensible : mesure du 07/09/2026, client 2.4.1 vers un service
/// 2.5.1 rend `PM_STATUS_PIPE_ERROR`, alors que le meme client vers un service 2.4.1 reussit.
///
/// Et l'inverse compte autant : quand la configuration est deja bonne, **on ne reinstalle rien**.
/// Un service qu'on recree a chaque mesure est un produit qui se bat contre le systeme.
/// </summary>
public class PresentMonServicePlanTests
{
    [Fact]
    public void Absent_il_faut_l_INSTALLER()
    {
        PresentMonServicePlan.Decide(exists: false, currentBinPath: null, expectedBinary: @"C:\app\PresentMonService.exe")
            .Should().Be(PresentMonServiceAction.Install);
    }

    [Fact]
    public void Deja_configure_correctement_on_le_DEMARRE_seulement()
    {
        // Le cas nominal, et le plus frequent. Rien a ecrire.
        PresentMonServicePlan.Decide(true, PresentMonServicePlan.BinPath(@"C:\app\PresentMonService.exe"),
                @"C:\app\PresentMonService.exe")
            .Should().Be(PresentMonServiceAction.StartOnly);
    }

    [Fact]
    public void Pointant_vers_un_binaire_INCONNU_il_faut_le_REPARER()
    {
        // Le cas qui compte : le service existe, mais rien ne dit quelle version il execute —
        // et c'est la version qui decide si le client peut lui parler.
        PresentMonServicePlan.Decide(true, "\"C:\\ailleurs\\PresentMonService.exe\"", @"C:\app\PresentMonService.exe")
            .Should().Be(PresentMonServiceAction.Repair);
    }

    [Fact]
    public void Une_configuration_ILLISIBLE_se_repare_au_lieu_de_se_croire_bonne()
    {
        // Ne pas avoir pu lire le binPath n'est pas « il est correct ». Meme doctrine que partout
        // ailleurs dans ce depot : Unknown ne deduit rien de favorable.
        PresentMonServicePlan.Decide(true, null, @"C:\app\PresentMonService.exe")
            .Should().Be(PresentMonServiceAction.Repair);
    }

    [Fact]
    public void La_comparaison_de_chemin_ignore_la_casse()
    {
        // Windows ne distingue pas la casse des chemins.
        PresentMonServicePlan.Decide(true, PresentMonServicePlan.BinPath(@"C:\APP\presentmonservice.exe"),
                @"C:\app\PresentMonService.exe")
            .Should().Be(PresentMonServiceAction.StartOnly);
    }

    // ── LA REPARATION : un arret rate n'est pas une reparation ─────────────────────────────

    [Fact]
    public void Un_ARRET_qui_echoue_fait_echouer_la_reparation_et_ne_reconfigure_rien()
    {
        // Avant : l'arret n'etait pas lu, `sc config` passait, `Start` rendait Ok (deja Running)
        // et la mesure suivante traversait l'ANCIEN binaire en le croyant repare.
        var reconfigure = 0;
        var r = PresentMonServicePlan.Reparer(
            () => Result.Fail(ErrorCodes.HelperOpFailed, "arret refuse"),
            () => { reconfigure++; return Result.Ok(); });

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Be("arret refuse", "la cause de l'echec doit survivre");
        reconfigure.Should().Be(0);
    }

    [Fact]
    public void Un_arret_reussi_reconfigure_une_fois_et_rend_son_resultat()
    {
        var reconfigure = 0;
        var r = PresentMonServicePlan.Reparer(
            () => Result.Ok(),
            () => { reconfigure++; return Result.Fail(ErrorCodes.HelperOpFailed, "sc config refuse"); });

        reconfigure.Should().Be(1);
        r.Error!.Value.Message.Should().Be("sc config refuse");
    }
}
