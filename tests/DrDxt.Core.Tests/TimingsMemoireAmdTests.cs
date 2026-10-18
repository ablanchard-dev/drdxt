using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Les timings que le contrôleur mémoire AMD applique : registres synthétiques construits selon la disposition citée par
// TimingsMemoireAmd (aucune machine AMD ici). La DDR5 reste marquée non vérifiée.
public sealed class TimingsMemoireAmdTests
{
    private static readonly GenerationAmd.Ligne Zen4 = GenerationAmd.Table.First(l => l.Generation == "Zen 4");

    // Valeurs inventées. DDR4-3467 17-20-20-41, tRC 61, tRFC 610, 1T, Gear Down actif, Power Down coupé.
    private static Dictionary<uint, uint> Ddr4() => new()
    {
        [0x50200] = 52 | (1u << 11),
        [0x50204] = 17 | (41u << 8) | (20u << 16) | (20u << 24),
        [0x50208] = 61 | (20u << 16),
        [0x5012C] = 0,
        [0x50260] = 610,
        [0x50264] = 610,
    };

    // Valeurs inventées. DDR5-5800 34-41-42-90, tRC 132, tRFC 820, 1T, Gear Down actif, Power Down actif ; le premier
    // registre de tRFC au repos.
    private static Dictionary<uint, uint> Ddr5() => new()
    {
        [0x50200] = 2900 | (1u << 18),
        [0x50204] = 34 | (90u << 8) | (41u << 16),
        [0x50208] = 132 | (42u << 16),
        [0x5012C] = 1u << 28,
        [0x50260] = 0x00C00138,
        [0x50264] = 820,
    };

    [Fact]
    public void Un_canal_DDR4_se_decode_champ_par_champ()
    {
        TimingsMemoireAmd.DecoderCanal(0, TypeMemoireAmd.Ddr4, Ddr4()).Should().Be(
            new TimingsCanal(0, 3467, 17, 20, 20, 41, 61, 610, Commande2T: false, GearDown: true, PowerDown: false));
    }

    [Fact]
    public void Un_canal_DDR5_se_decode_et_reste_marque_non_verifie()
    {
        var t = TimingsMemoireAmd.Decoder(TypeMemoireAmd.Ddr5, [Ddr5(), Ddr5()])!;

        t.Canaux.Should().HaveCount(2).And.AllSatisfy(c => c.Should().BeEquivalentTo(
            new { DebitMts = 5800, Cl = 34, Rcd = 41, Rp = 42, Ras = 90, Rc = 132, Rfc = 820, Commande2T = false, GearDown = true, PowerDown = true }));
        t.DispositionVerifiee.Should().BeFalse();
        TimingsMemoireAmd.Decoder(TypeMemoireAmd.Ddr4, [Ddr4()])!.DispositionVerifiee.Should().BeTrue();
    }

    [Fact]
    public void Le_tRFC_DDR4_ecarte_le_registre_au_repos()
    {
        var r = Ddr4();
        r[0x50260] = 0x21060138;

        TimingsMemoireAmd.DecoderCanal(0, TypeMemoireAmd.Ddr4, r)!.Rfc.Should().Be(610);
    }

    [Fact]
    public void Une_valeur_hors_bornes_ou_un_registre_manquant_rend_non_lu_et_un_seul_canal_suffit()
    {
        var hors = Ddr4();
        hors[0x50204] = 2 | (41u << 8) | (20u << 16);
        var manque = Ddr4();
        manque.Remove(0x50208);

        TimingsMemoireAmd.DecoderCanal(0, TypeMemoireAmd.Ddr4, hors).Should().BeNull("CL 2 n'est pas un timing");
        TimingsMemoireAmd.DecoderCanal(0, TypeMemoireAmd.Ddr4, manque).Should().BeNull();
        TimingsMemoireAmd.Decoder(TypeMemoireAmd.Ddr4, [Ddr4(), hors]).Should().BeNull("tous les canaux, jamais une lecture partielle");
    }

    [Fact]
    public void Le_canal_n_vit_a_n_fois_un_mega_octet_du_canal_0()
    {
        TimingsMemoireAmd.Adresse(0, 0x50200).Should().Be(0x50200u);
        TimingsMemoireAmd.Adresse(1, 0x50200).Should().Be(0x150200u);
    }

    [Theory]
    [InlineData(new[] { 26, 26 }, TypeMemoireAmd.Ddr4)]
    [InlineData(new[] { 34, 34 }, TypeMemoireAmd.Ddr5)]
    public void Le_type_se_lit_quand_toutes_les_barrettes_le_partagent(int[] types, TypeMemoireAmd attendu)
    {
        TimingsMemoireAmd.Type(types).Should().Be(attendu);
    }

    [Theory]
    [InlineData(new[] { 26, 34 })]
    [InlineData(new[] { 35, 35 })]
    [InlineData(new[] { 0 })]
    [InlineData(new int[0])]
    public void Un_melange_une_LPDDR_ou_un_type_non_lu_n_est_pas_decode(int[] types)
    {
        TimingsMemoireAmd.Type(types).Should().BeNull();
    }

    [Fact]
    public void Le_constat_dit_non_lu_quand_la_porte_est_fermee_et_ne_parle_que_d_AMD()
    {
        TimingsMemoireAmd.Juger(null, [34, 34], null).Should().BeEmpty();
        var ferme = TimingsMemoireAmd.Juger(Zen4, [34, 34], canaux: null).Single();
        ferme.Statut.Should().Be(RepairStatus.Unknown);
        ferme.Explication.Should().Be(Docteur.T("TMA.NonLus"));
        TimingsMemoireAmd.Juger(Zen4, [26, 34], null).Single().Explication.Should().Be(Docteur.T("TMA.TypeNonDecode"));

        var lu = TimingsMemoireAmd.Juger(Zen4, [34, 34], [Ddr5()]).Single();
        lu.Statut.Should().Be(RepairStatus.Observed);
        lu.ValeurCourante.Should().Be("A 5800 MT/s 34-41-42-90 tRC 132 tRFC 820 1T GDM on PD on");
        lu.Explication.Should().Be(Docteur.T("TMA.LusDdr5NonVerifiee", lu.ValeurCourante!));
    }
}
