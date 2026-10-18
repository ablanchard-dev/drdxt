using DrDxt.Core;
using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

// Un seul canal probable : dit par la mesure multi-fil, dans les mêmes unités que le plafond, avec les canaux lus.
public sealed class UnSeulCanalMesureTests
{
    private static MemoryModule Barrette(int mts) => new("barrette", "fabricant", mts, mts);

    // Notre règle : la mesure et le plafond sont dans la même unité (Go/s décimaux). DDR4-3800 : 3800 × 8 / 1000 = 30,4.
    [Fact]
    public void Le_plafond_d_un_canal_est_dans_la_meme_unite_que_la_mesure()
        => MemoryInference.PeakGBs(3800, 1).Should().BeApproximately(30.4, 1e-9);

    // Relevé sur la machine de référence le 17/10/2026 : deux barrettes DDR4-3800, 50 à 53 Go/s sur plusieurs fils.
    [Fact]
    public void Deux_canaux_mesures_ne_sont_jamais_dits_un_seul()
        => MemoryInference.UnSeulCanalProbable(51.3, [Barrette(3800), Barrette(3800)]).Should().BeFalse();

    [Fact]
    public void Plusieurs_fils_sous_le_plafond_d_un_canal_avec_deux_barrettes_disent_un_seul_canal_probable()
        => MemoryInference.UnSeulCanalProbable(26.0, [Barrette(3800), Barrette(3800)]).Should().BeTrue();

    // Notre règle : les canaux ne sont jamais supposés à deux. Une barrette seule est le simple canal, rien à dire ; sans
    // mesure multi-fil ou sans fréquence lue, rien n'est conclu.
    [Fact]
    public void Sans_deux_barrettes_sans_mesure_ou_sans_frequence_rien_n_est_conclu()
    {
        MemoryInference.UnSeulCanalProbable(20, [Barrette(3800)]).Should().BeFalse();
        MemoryInference.UnSeulCanalProbable(0, [Barrette(3800), Barrette(3800)]).Should().BeFalse();
        MemoryInference.UnSeulCanalProbable(20, [Barrette(0), Barrette(0)]).Should().BeFalse();
    }

    [Fact]
    public void La_preuve_des_canaux_prend_le_meilleur_des_deux_debits()
    {
        var m = new MemoryMeasurement(25, 1, 70, 1, [], BandwidthMultiFilGBs: 51);
        MemoryInference.ProvenMinimumChannels(MemoryInference.MeilleurDebit(m), [Barrette(3800), Barrette(3800)]).Should().Be(2);
    }

    // Chaque fil lit son tampon de 64 Mio : jamais plus de la moitié de la mémoire libre, comme le test mémoire. Moins de
    // deux fils ne saturent pas le contrôleur : sans eux, pas de mesure, donc jamais « un seul canal ».
    [Theory]
    [InlineData(28, 32UL << 30, 8)]
    [InlineData(28, 512UL << 20, 4)]
    [InlineData(28, 200UL << 20, 0)]
    [InlineData(1, 32UL << 30, 0)]
    public void Les_fils_du_debit_sont_bornes_par_la_memoire_libre(int coeurs, ulong libre, int attendu)
        => MemoryBench.FilsDuDebit(coeurs, libre).Should().Be(attendu);

    [Fact]
    public void Sans_lecture_de_la_memoire_libre_le_debit_multi_fil_n_est_pas_mesure()
        => MemoryBench.FilsDuDebit(28, null).Should().Be(0);
}
