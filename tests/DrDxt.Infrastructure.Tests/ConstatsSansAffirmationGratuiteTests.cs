using System.Globalization;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests;

/// <summary>
/// 16/09/2026 : deux constats affiches affirmaient ce que rien ne prouve. « Plus frequente
/// qu'on ne le croit » n'a aucune source. « DrDXT sait desormais le mesurer » etait faux : le scan
/// mesure la stabilite du reseau (passerelle contre internet), il ne compare pas la carte avec et sans
/// moderation d'interruption.
/// </summary>
public sealed class ConstatsSansAffirmationGratuiteTests
{
    [Theory]
    [InlineData("fr", "Finding.Display.MixedRefreshIdlePower", "plus fréquente")]
    [InlineData("en", "Finding.Display.MixedRefreshIdlePower", "more common than")]
    // La reponse « voulu » d'un support NVIDIA portait sur trois ecrans a haute frequence d'une
    // GTX 1080, relayee sur un forum tiers ; la generaliser a toute carte NVIDIA etait abusif.
    [InlineData("fr", "Finding.Display.MixedRefreshIdlePower", "NVIDIA décrit ce comportement comme voulu")]
    [InlineData("en", "Finding.Display.MixedRefreshIdlePower", "NVIDIA describes this as by design")]
    [InlineData("fr", "Finding.Display.MixedRefreshIdlePower", "ça empêche souvent")]
    [InlineData("en", "Finding.Display.MixedRefreshIdlePower", "this often stops")]
    // La source (Microsoft, reglage des cartes reseau sous Windows Server) decrit un arbitrage
    // latence / processeur ; elle ne dit ni « par defaut » ni quoi que ce soit des jeux.
    [InlineData("fr", "Finding.Network.InterruptModerationTradeoff", "généralement faible en jeu")]
    [InlineData("en", "Finding.Network.InterruptModerationTradeoff", "usually small in games")]
    [InlineData("fr", "Finding.Network.InterruptModerationTradeoff", "c'est le réglage par défaut")]
    [InlineData("en", "Finding.Network.InterruptModerationTradeoff", "the default")]
    [InlineData("fr", "Finding.Network.InterruptModerationTradeoff", "sait désormais le mesurer")]
    [InlineData("en", "Finding.Network.InterruptModerationTradeoff", "can now measure it")]
    public void Le_constat_n_affirme_pas_ce_que_rien_ne_prouve(string langue, string cle, string affirmation)
        => new ResxLocalizer().Get(cle, new CultureInfo(langue)).Should().NotContain(affirmation);

    [Theory]
    [InlineData("fr", "ne compare pas")]
    [InlineData("en", "does not compare")]
    public void La_moderation_dit_ce_que_le_scan_mesure_vraiment(string langue, string aveu)
        => new ResxLocalizer().Get("Finding.Network.InterruptModerationTradeoff", new CultureInfo(langue))
            .Should().Contain(aveu);
}
