using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

// Vitesse et duplex forcés contre le lien négocié, d'après la table de Microsoft ; le reste dit sans levier.
public sealed class VitesseDeLaCarteTests
{
    [Theory]
    [InlineData(0L, null, null)]
    [InlineData(6L, 1000L, true)]
    [InlineData(3L, 100L, false)]
    [InlineData(2500L, 2500L, true)]
    [InlineData(11L, null, null)]
    public void Les_valeurs_suivent_la_table_de_Microsoft(long valeur, long? mbps, bool? integral)
        => VitesseDeLaCarte.Forcee(valeur).Should().Be(mbps is null ? null : (mbps.Value, integral!.Value));

    [Fact]
    public void Une_vitesse_forcee_differente_du_lien_negocie_appelle_un_geste()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = VitesseDeLaCarte.Juger([new("Ethernet", 2500, 1000, true, [])]).Single();
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("négociation automatique");
    }

    // Relevé sur la machine de référence : négociation automatique, contrôle de flux « Rx & Tx activé », 1 Gbit/s duplex.
    // Notre règle : les réglages du constructeur sont dits tels que le pilote les nomme, jamais jugés ni changés.
    [Fact]
    public void La_negociation_automatique_est_le_defaut_et_les_reglages_du_constructeur_sont_dits_sans_verdict()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = VitesseDeLaCarte.Juger([new("Ethernet", 0, 1000, true, [("Contrôle de flux", "Rx & Tx activé")])]).Single();
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().Contain("Contrôle de flux = Rx & Tx activé").And.Contain("ne les change pas");
    }

    [Fact]
    public void Une_valeur_hors_table_est_dite_telle_quelle_et_rien_de_lu_est_non_lu()
    {
        VitesseDeLaCarte.Juger([new("Ethernet", 11, 1000, true, [])]).Single().Statut.Should().Be(RepairStatus.Observed);
        VitesseDeLaCarte.Juger(null).Single().Statut.Should().Be(RepairStatus.Unknown);
    }
}
