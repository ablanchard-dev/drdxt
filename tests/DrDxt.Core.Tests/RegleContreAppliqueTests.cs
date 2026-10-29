using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le réglé contre l'appliqué : tous les canaux, « Auto » ignoré, et un mode de secours qu'on ne devine pas.
public sealed class RegleContreAppliqueTests
{
    private static readonly GenerationAmd.Ligne Zen4 = GenerationAmd.Table.First(l => l.Generation == "Zen 4");

    // Valeurs inventées.
    private static TimingsCanal Canal(int i, int debit = 5800, int cl = 34) =>
        new(i, debit, cl, 41, 42, 90, 132, 820, Commande2T: false, GearDown: true, PowerDown: false);

    private static TimingsMemoire Ddr5(params TimingsCanal[] c) => new(TypeMemoireAmd.Ddr5, c, DispositionVerifiee: false);

    private static BiosSettingReading Lu(string nom, string libelle) => new(nom, 0, libelle, false);

    [Fact]
    public void Chaque_canal_est_compare_pas_seulement_le_canal_A()
    {
        var ecarts = RegleContreApplique.Comparer([Lu("tCL", "34"), Lu("Power Down Enable", "Enabled")], Ddr5(Canal(0), Canal(1, cl: 37)));

        ecarts.Should().Equal(
            new EcartRegleApplique("tCL", "34", "37", 'B'),
            new EcartRegleApplique("Power Down Enable", "Enabled", "Disabled", 'A'),
            new EcartRegleApplique("Power Down Enable", "Enabled", "Disabled", 'B'));
    }

    [Fact]
    public void Un_timing_en_Auto_n_est_pas_compare()
    {
        RegleContreApplique.Comparer([Lu("tCL", "Auto"), Lu("Command Rate", "1T")], Ddr5(Canal(0, cl: 40))).Should().BeEmpty();
    }

    [Fact]
    public void Le_mode_de_secours_demande_un_profil_et_tous_les_canaux_au_socle()
    {
        RegleContreApplique.ModeDeSecours([Lu("EXPO", "EXPO I")], Ddr5(Canal(0, 4800), Canal(1, 4800)), 5800).Should().BeTrue();
        RegleContreApplique.ModeDeSecours([Lu("EXPO", "EXPO I")], Ddr5(Canal(0, 5800), Canal(1, 4800)), 5800).Should().BeFalse();
    }

    [Fact]
    public void Une_memoire_lente_reglee_a_la_main_n_est_pas_un_mode_de_secours()
    {
        // Profil coupé, mémoire réglée à la main sous le socle : jamais un entraînement raté.
        var ddr4 = new TimingsMemoire(TypeMemoireAmd.Ddr4, [Canal(0, 2800, 17)], true);

        RegleContreApplique.ModeDeSecours([Lu("EXPO", "Disabled")], ddr4, 3467).Should().BeFalse();
    }

    [Fact]
    public void Un_kit_dont_la_vitesse_annoncee_egale_le_socle_ne_tranche_pas()
    {
        RegleContreApplique.ModeDeSecours([Lu("EXPO", "EXPO I")], Ddr5(Canal(0, 4800)), 4800).Should().BeNull();
        RegleContreApplique.ModeDeSecours([], Ddr5(Canal(0, 4800)), 5800).Should().BeNull("profil non lu");
    }

    [Fact]
    public void Le_constat_dit_ce_qui_manque_puis_le_secours_puis_les_ecarts()
    {
        RegleContreApplique.Juger(null, [], Ddr5(Canal(0)), 5800).Should().BeEmpty();
        RegleContreApplique.Juger(Zen4, [Lu("EXPO", "EXPO I")], null, 5800).Single().Explication
            .Should().Be(Docteur.T("RCA.AppliquesNonLus"));
        RegleContreApplique.Juger(Zen4, null, Ddr5(Canal(0)), 5800).Single().Explication.Should().Be(Docteur.T("RCA.BiosNonNomme"));

        var secours = RegleContreApplique.Juger(Zen4, [Lu("EXPO", "EXPO I")], Ddr5(Canal(0, 4800)), 5800).Single();
        secours.Statut.Should().Be(RepairStatus.Suspicious);
        secours.Explication.Should().Be(Docteur.T("RCA.Secours", "EXPO I", 4800, 5800));

        RegleContreApplique.Juger(Zen4, [Lu("EXPO", "EXPO I"), Lu("tCL", "34")], Ddr5(Canal(0), Canal(1)), 5800).Single().Statut
            .Should().Be(RepairStatus.Observed);
        RegleContreApplique.Juger(Zen4, [Lu("tCL", "32")], Ddr5(Canal(0)), 5800).Single().Statut.Should().Be(RepairStatus.NonDefault);
    }

    // Une barrette dont Windows ne dit pas la vitesse rend la vitesse du kit 0 (le plus lent des kits lus). Profil demandé,
    // tous les canaux au socle : un entraînement raté ne se distingue plus, et le constat ne peut pas dire « conforme ».
    [Fact]
    public void Sans_vitesse_du_kit_un_profil_au_socle_ne_se_dit_pas_conforme()
    {
        var constat = RegleContreApplique.Juger(Zen4, [Lu("EXPO", "EXPO I")], Ddr5(Canal(0, 4800), Canal(1, 4800)), 0).Single();

        constat.Statut.Should().Be(RepairStatus.Unknown);
        constat.Explication.Should().Be(Docteur.T("RCA.KitNonLu", "EXPO I", 4800));
    }
}
