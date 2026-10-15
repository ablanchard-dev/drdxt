using DrDxt.Core;
using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

/// <summary>
/// La carte de la machine de reference porte `Ethernet vert` = Active et `Power Saving Mode` = Active
/// (releve). Sans ces lectures, le scanner dirait « rien a signaler » sur des reglages bien presents.
///
/// Ce que ce constat n'a pas le droit de faire : promettre un gain. La recherche documentee
/// (`docs/LEVIERS-REPOS-ET-RESEAU.md`, N-01 a N-03) ne trouve aucun chiffre pour le jeu : le
/// reveil du lien se compte en microsecondes, plusieurs ordres de grandeur sous notre plancher de
/// bruit de 9,9 %. On signale, on ne chiffre pas.
/// </summary>
public class LinkPowerSavingTests
{
    private static NetworkAdapterState Carte(
        TriState eee = TriState.Off, TriState economies = TriState.Off) => new(
        "Ethernet", "Realtek Gaming 2.5GbE Family Controller", IsWired: true,
        InterruptModeration: TriState.Off, Rss: TriState.On,
        EnergyEfficientEthernet: eee, LinkPowerSaving: economies);

    [Fact]
    public void Une_economie_d_energie_du_lien_ACTIVE_est_signalee()
    {
        NetworkAdvisor.Advise(new[] { Carte(economies: TriState.On) })
            .Should().ContainSingle(g => g.MessageKey == "Finding.Network.LinkPowerSavingOn");
    }

    [Fact]
    public void EEE_actif_declenche_le_meme_constat_car_c_est_la_meme_famille()
    {
        // EEE etait LU depuis toujours et n'etait utilise nulle part : un champ mort.
        NetworkAdvisor.Advise(new[] { Carte(eee: TriState.On) })
            .Should().ContainSingle(g => g.MessageKey == "Finding.Network.LinkPowerSavingOn");
    }

    [Fact]
    public void Deux_economies_actives_ne_font_pas_DEUX_constats()
    {
        // Le client n'a pas besoin qu'on lui repete la meme chose parce que sa carte expose deux
        // mots-cles pour la meme idee.
        NetworkAdvisor.Advise(new[] { Carte(eee: TriState.On, economies: TriState.On) })
            .Where(g => g.MessageKey == "Finding.Network.LinkPowerSavingOn")
            .Should().HaveCount(1);
    }

    [Fact]
    public void Tout_desactive_ne_dit_RIEN()
    {
        NetworkAdvisor.Advise(new[] { Carte() })
            .Should().NotContain(g => g.MessageKey == "Finding.Network.LinkPowerSavingOn");
    }

    [Fact]
    public void Un_reglage_JAMAIS_LU_ne_conseille_rien()
    {
        // La regle de tout ce depot : Unknown ne deduit rien. Une carte qui n'expose pas ces
        // mots-cles n'est pas une carte dont les economies sont coupees.
        NetworkAdvisor.Advise(new[] { Carte(eee: TriState.Unknown, economies: TriState.Unknown) })
            .Should().NotContain(g => g.MessageKey == "Finding.Network.LinkPowerSavingOn");
    }
}
