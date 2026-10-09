using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>UI1 (08/10/2026, vu sur capture) : « 2 échec(s), aucun vidage gardé », « 9 dossier(s) de jeux »… une vingtaine
/// de textes du moteur. Ils s'accordent une fois, à l'affichage ; le constat d'origine (comparé d'un scan à l'autre) ne change pas.</summary>
public sealed class ConstatsAccordesALAffichageTests
{
    [Fact]
    public void Valeur_et_explication_s_accordent_a_l_affichage_sans_toucher_au_constat()
    {
        var f = new RepairFinding("STABILITE", "création de vidage", "2 échec(s), aucun vidage gardé", null, RepairStatus.Suspicious,
            "Windows a enregistré 1 erreur(s) matérielle(s) corrigée(s).");

        var vm = new RepairFindingViewModel(f);

        vm.ValeurCourante.Should().Be("2 échecs, aucun vidage gardé");
        vm.Explication.Should().Be("Windows a enregistré 1 erreur matérielle corrigée.");
        f.ValeurCourante.Should().Be("2 échec(s), aucun vidage gardé", "l'historique compare la valeur d'origine");
    }

    // UI1 (critique du 08/10) : « Incident noyau conservé » titrait deux incidents différents (USB 3 et carte graphique) ;
    // on croyait lire deux versions du même fait. Le titre affiché dit lequel ; la clé de l'historique ne change pas.
    [Theory]
    [InlineData("1x 0x144 BUGCODE_USB3_DRIVER, contrôleur USB 3 (vidage à chaud)", "incident isolé, conservé par Windows. ", "Incident du pilote USB 3, conservé")]
    [InlineData("3x 0x193 VIDEO_DXGKRNL_LIVEDUMP", "3 fois la même signature. Il vient de la carte graphique et de son pilote.", "Incident du pilote graphique, conservé")]
    [InlineData("aucun", "aucun rapport noyau conserve par WER", "Incident noyau conservé")]
    public void Chaque_incident_noyau_porte_son_titre(string valeur, string explication, string titre)
    {
        var f = new RepairFinding("STABILITE", "incident noyau conserve", valeur, null, RepairStatus.Observed, explication);
        new RepairFindingViewModel(f).Nom.Should().Be(titre);
        f.Nom.Should().Be("incident noyau conserve");
    }

    [Fact]
    public void La_liste_Mesure_prend_le_meme_titre()
        => ScanViewModel.LigneVerifiee(new RepairFinding("STABILITE", "incident noyau conserve",
                "1x 0x144 BUGCODE_USB3_DRIVER, contrôleur USB 3 (vidage à chaud)", null, RepairStatus.Observed, "incident isolé."))
            .Should().StartWith("Incident du pilote USB 3, conservé : 1x 0x144");

    // UI1 (critique du 08/10) : la pastille montrait « IgnoreAllFailures » seul. Elle dit ce que la valeur veut dire, le nom
    // technique entre parenthèses ; la valeur gardée pour l'historique ne change pas.
    [Fact]
    public void Une_valeur_BCD_se_dit_en_clair_avec_son_nom_technique()
    {
        var f = new RepairFinding("BCD", "bootstatuspolicy", "IgnoreAllFailures", "IgnoreShutdownFailures", RepairStatus.Suspicious, "x");
        new RepairFindingViewModel(f).ValeurCourante.Should().Be("erreurs de démarrage ignorées (IgnoreAllFailures)");
        ScanViewModel.LigneVerifiee(f).Should().EndWith("erreurs de démarrage ignorées (IgnoreAllFailures)");
        f.ValeurCourante.Should().Be("IgnoreAllFailures");
    }

    [Fact]
    public void Le_pilote_dit_l_installe_et_le_disponible_sur_une_ligne()
    {
        var f = new RepairFinding("GPU DRIVER", "version du pilote graphique", "NVIDIA 617.14", "617.42", RepairStatus.Suspicious, "x");
        new RepairFindingViewModel(f).ValeurCourante.Should().Be("NVIDIA 617.14 installé → 617.42 disponible");
        f.ValeurCourante.Should().Be("NVIDIA 617.14");
    }
}
