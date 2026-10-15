using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le nom d'un constat est la clé du journal : deux scans se comparent par Domaine + Nom. Il reste en français quelle que
// soit la langue de Windows, et seul l'affichage le traduit ; sinon chaque constat « disparaît puis réapparaît » au
// changement de langue, et ce qui cherche un nom par son texte (le bilan des disques) le manque.
public sealed class LeNomDUnConstatNeChangePasAvecLaLangueTests
{
    [Fact]
    public void En_anglais_le_nom_reste_francais_et_s_affiche_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        var nom = Docteur.Nom("BR.Nom.Lien", "RTX 4060");
        nom.Should().Be("lien PCIe de RTX 4060");
        RepairLibelles.Nom(nom).Should().Be("PCIe link of RTX 4060");
        RepairLibelles.Nom(Docteur.Nom("SW.ErreursNom", "Samsung 990")).Should().Be("Uncorrected errors · Samsung 990");
    }

    [Fact]
    public void Une_partie_variable_qui_est_un_texte_du_docteur_se_traduit_aussi()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
        var nom = Docteur.Nom("IM.NomChanges", Docteur.Nom("IM.Famille.logiciels"));
        RepairLibelles.Nom(nom).Should().NotContain("logiciels").And.EndWith("changed");
    }

    [Fact]
    public void En_francais_rien_ne_change()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        RepairLibelles.Nom(Docteur.Nom("BR.Nom.Lien", "RTX 4060")).Should().Be("Lien PCIe de RTX 4060");
    }
}
