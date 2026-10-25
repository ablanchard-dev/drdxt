using DrDxt.Detection.Gpu.Adlx;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// Le choix de la carte AMD lue et écrite, d'après le type que ADLX dit de chaque carte (0 indéfini, 1 intégrée, 2 dédiée).
public sealed class AdlxChoixDeLaCarteTests
{
    [Fact]
    public void La_carte_dediee_est_choisie_meme_derriere_la_puce_integree()
        => AdlxNative.IndexDeLaCarte(new int?[] { 1, 2 }).Should().Be(1);

    [Fact]
    public void Une_seule_carte_au_type_illisible_ou_indefini_est_la_carte()
    {
        AdlxNative.IndexDeLaCarte(new int?[] { null }).Should().Be(0);
        AdlxNative.IndexDeLaCarte(new int?[] { 0 }).Should().Be(0);
    }

    // Un Ryzen à puce graphique avec une RTX : ADLX ne voit que la puce. Anti-Lag ne s'y écrit pas.
    [Fact]
    public void Une_seule_carte_dite_integree_n_est_pas_choisie()
        => AdlxNative.IndexDeLaCarte(new int?[] { 1 }).Should().BeNull();

    [Fact]
    public void Plusieurs_cartes_sans_dediee_dite_ne_choisissent_rien()
        => AdlxNative.IndexDeLaCarte(new int?[] { null, 1 }).Should().BeNull();
}
