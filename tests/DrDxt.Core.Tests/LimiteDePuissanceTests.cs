using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>PL1 sous la puissance de base = la carte mère bride, dit avec le geste BIOS ; PL1 à la base =
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

        var ouverte = LimiteDePuissance.Juger(new LimiteDePuissanceLue(45, 125, 65, Verrou: false, ChaleurEnPartieMaxC: 80));
        ouverte.Statut.Should().Be(RepairStatus.Suspicious);
        ouverte.Explication.Should().Contain("Optimiser").And.Contain("125 W").And.Contain("BIOS");

        // À la base mais sous une PL2 plus haute, le docteur dit qu'Optimiser peut l'essayer.
        LimiteDePuissance.Juger(new LimiteDePuissanceLue(65, 219, 65, Verrou: false, ChaleurEnPartieMaxC: 80)).Explication.Should().Contain("Optimiser");
        LimiteDePuissance.Juger(new LimiteDePuissanceLue(65, 219, 65, Verrou: false)).Explication
            .Should().NotContain("Optimiser", "aucune température de partie relevée : « inconnu » n'est pas « pas chaud »");
        LimiteDePuissance.Juger(new LimiteDePuissanceLue(65, 65, 65, Verrou: false)).Explication.Should().NotContain("Optimiser", "aucune marge");
    }

    [Fact]
    public void A_la_base_c_est_la_valeur_d_Intel_pas_un_defaut()
    {
        var f = LimiteDePuissance.Juger(new LimiteDePuissanceLue(65, 219, 65, false));

        f.Statut.Should().Be(RepairStatus.Observed);
        f.Action.Should().Be(RepairAction.Aucune);
        f.Explication.Should().Contain("profil de base d'Intel").And.Contain("hors de la spécification", "65 W de base : un non-K");

        // Seul un K (125 W de base) a un profil Intel plus haut.
        LimiteDePuissance.Juger(new LimiteDePuissanceLue(125, 253, 125, false)).Explication
            .Should().Contain("profil Performance").And.Contain("pas une réparation").And.NotContain("hors de la spécification");
    }

    [Fact]
    public void Au_dessus_de_la_base_c_est_observe_avec_la_temperature_a_surveiller()
    {
        var f = LimiteDePuissance.Juger(new LimiteDePuissanceLue(253, 253, 65, false));

        f.Statut.Should().Be(RepairStatus.Observed);
        f.Explication.Should().Contain("au-dessus de la puissance de base").And.Contain("température");
    }

    /// <summary>Une PL1 au maximum du registre (4 095,875 W) ou désactivée n'est pas une lecture ratée : la
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

    /// <summary>Le bit 15 ne décide jamais seul. Une PL1 sous la base avec le bit à zéro reste un
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

    // La promesse « Optimiser peut la monter » suit la cible du levier, une seule source de vérité.
    [Fact]
    public void Optimiser_n_est_promis_que_si_le_levier_monterait_et_jusqu_a_sa_vraie_cible()
    {
        var sous = new LimiteDePuissanceLue(45, 219, 65, false, ChaleurEnPartieMaxC: 80);
        LimiteDePuissance.Juger(sous).Explication.Should().Contain("jusqu'à 219 W");
        LimiteDePuissance.Juger(sous with { MicrocodeVminPerime = true }).Explication.Should().NotContain("Optimiser");
        LimiteDePuissance.Juger(sous with { ChaleurEnPartieMaxC = 97 }).Explication.Should().NotContain("Optimiser");
        LimiteDePuissance.Juger(sous with { MaximaleW = 180 }).Explication.Should().Contain("jusqu'à 180 W", "bornée par la puissance maximale publiée");
        LimiteDePuissance.CibleDeMontee(sous with { CopieMmioPl1W = 120 }).Should().Be(120);
    }
    // Une PL2 « sans limite » (champ 0x7FFF) sans puissance maximale publiée ne borne rien, et le helper refuse alors toute
    // montée : le levier ne la propose pas. Avec un maximum publié, la montée s'y arrête.
    [Fact]
    public void Sous_une_PL2_sans_limite_sans_maximum_publie_aucune_montee_n_est_proposee()
    {
        var lu = RegistreDeLimiteCpu.Decoder(0x3, (0x7FFFUL << 32) | (1UL << 47) | (1UL << 15) | 520UL, 520UL)! with { ChaleurEnPartieMaxC = 80 };

        lu.Pl2SansLimite.Should().BeTrue();
        LimiteDePuissance.CibleDeMontee(lu).Should().BeNull();
        LimiteDePuissance.CibleDeMontee(lu with { MaximaleW = 253 }).Should().Be(253);

        RegistreDeLimiteCpu.Decoder(0x3, (0x7FF8UL << 32) | (1UL << 47) | (1UL << 15) | 520UL, 520UL)!.Pl2SansLimite
            .Should().BeTrue("0x7FF8 dit 4095 W, que rien ne tire");
        RegistreDeLimiteCpu.Decoder(0x3, (2024UL << 32) | (1UL << 47) | (1UL << 15) | 520UL, 520UL)!.Pl2SansLimite
            .Should().BeFalse("témoin : 253 W est une vraie PL2");
    }

    // Sur un portable, une PL1 sous la puissance de base est celle du fabricant (batterie, chauffe) : à regarder, sans geste
    // de montée ni de BIOS.
    [Fact]
    public void Sur_un_portable_une_limite_sous_la_base_est_a_regarder()
    {
        var f = LimiteDePuissance.Juger(new LimiteDePuissanceLue(28, 64, 45, Verrou: false, ChaleurEnPartieMaxC: 80), portable: true);

        f.Statut.Should().Be(RepairStatus.NonDefault);
        f.Explication.Should().Contain("portable").And.NotContain("Optimiser").And.NotContain("guide 5.3");
        LimiteDePuissance.Juger(new LimiteDePuissanceLue(28, 64, 45, Verrou: false, ChaleurEnPartieMaxC: 80))
            .Statut.Should().Be(RepairStatus.Suspicious, "témoin : sur un fixe, la même limite bride");
    }
}
