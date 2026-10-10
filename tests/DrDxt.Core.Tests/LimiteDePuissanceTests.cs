using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>J-MECA (09/10/2026) : PL1 sous la puissance de base = la carte mère bride, dit avec le geste BIOS ; PL1 à la base =
/// la valeur d'Intel, pas un défaut ; au-dessus = relevé par le BIOS, observé ; non lu = non lu.</summary>
public sealed class LimiteDePuissanceTests
{
    [Fact]
    public void Sous_la_base_la_carte_mere_bride_et_le_geste_est_au_BIOS()
    {
        var f = LimiteDePuissance.Juger(new LimiteDePuissanceLue(45, 125, 65, Verrou: true));

        f.Statut.Should().Be(RepairStatus.Suspicious);
        f.Action.Should().Be(RepairAction.Recommandation);
        f.ValeurCourante.Should().Be("PL1 45 W, PL2 125 W, verrouillée");
        f.Explication.Should().Contain("BIOS").And.Contain("verrouillé").And.NotContain("Optimiser", "verrouillée : seul le BIOS la change");

        var ouverte = LimiteDePuissance.Juger(new LimiteDePuissanceLue(45, 125, 65, Verrou: false));
        ouverte.Statut.Should().Be(RepairStatus.Suspicious);
        ouverte.Explication.Should().Contain("Optimiser").And.Contain("125 W").And.Contain("BIOS");

        // B1 : à la base mais sous une PL2 plus haute, le docteur dit qu'Optimiser peut l'essayer.
        LimiteDePuissance.Juger(new LimiteDePuissanceLue(65, 219, 65, Verrou: false)).Explication.Should().Contain("Optimiser");
        LimiteDePuissance.Juger(new LimiteDePuissanceLue(65, 65, 65, Verrou: false)).Explication.Should().NotContain("Optimiser", "aucune marge");
    }

    [Fact]
    public void A_la_base_c_est_la_valeur_d_Intel_pas_un_defaut()
    {
        var f = LimiteDePuissance.Juger(new LimiteDePuissanceLue(65, 219, 65, false));

        f.Statut.Should().Be(RepairStatus.Observed);
        f.Action.Should().Be(RepairAction.Aucune);
        f.Explication.Should().Contain("profil de base d'Intel").And.Contain("pas une réparation");
    }

    [Fact]
    public void Au_dessus_de_la_base_c_est_observe_avec_la_temperature_a_surveiller()
    {
        var f = LimiteDePuissance.Juger(new LimiteDePuissanceLue(253, 253, 65, false));

        f.Statut.Should().Be(RepairStatus.Observed);
        f.Explication.Should().Contain("au-dessus de la puissance de base").And.Contain("température");
    }

    /// <summary>Revue du 09/10 : une PL1 au maximum du registre (4 095,875 W) ou désactivée n'est pas une lecture ratée : la
    /// carte ne tient pas le processeur en puissance, et c'est dit comme tel.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registre_au_maximum_c_est_sans_limite_pas_rate_quel_que_soit_le_bit_15(bool active)
    {
        var f = LimiteDePuissance.Juger(new LimiteDePuissanceLue(4095.875, 4095.875, 125, false, Pl1Active: active, RegistreAuMaximum: true));

        f.Statut.Should().Be(RepairStatus.Observed);
        f.Action.Should().Be(RepairAction.Aucune);
        f.Explication.Should().Contain("ne tient pas le processeur en puissance").And.Contain("température");
    }

    /// <summary>Contre-revue du 09/10 : le bit 15 ne décide jamais seul. Une PL1 sous la base avec le bit à zéro reste un
    /// bridage à dire, pas une « limite désactivée » qui rassurerait à tort.</summary>
    [Fact]
    public void Le_bit_d_activation_a_zero_ne_masque_pas_un_bridage()
    {
        var f = LimiteDePuissance.Juger(new LimiteDePuissanceLue(35, 65, 65, false, Pl1Active: false));

        f.Statut.Should().Be(RepairStatus.Suspicious);
        f.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void Non_lue_se_dit_non_lue()
    {
        var f = LimiteDePuissance.Juger(null);
        f.Statut.Should().Be(RepairStatus.Unknown);
        f.Explication.Should().Contain("PawnIO");
    }
}
