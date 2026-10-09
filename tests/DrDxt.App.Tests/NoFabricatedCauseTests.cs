using System.Globalization;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Loi anti-scareware : le produit ne nomme jamais une cause qu'il n'a pas prouvée.
///
/// Reproduit le 27/08/2026 : une capture lancée sur <c>explorer.exe</c> — qui n'a aucun
/// anti-triche — renvoyait MEAS.NO_PRESENTING_FRAMES, et l'utilisateur lisait « La mesure est
/// bloquée par l'anti-cheat de ce jeu ». La seule preuve détenue était « processus vivant,
/// zéro image », ce qui arrive aussi devant un menu ou une fenêtre minimisée.
///
/// Le code d'erreur reste stable (il sert d'identifiant dans les logs) ; c'est la PHRASE
/// montrée à l'utilisateur qui doit cesser d'affirmer. Elle doit énoncer le fait constaté
/// et proposer les causes possibles sans en désigner une.
/// </summary>
public class NoFabricatedCauseTests
{
    private static readonly ILocalizer Localizer = new ResxLocalizer();

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Zero_frames_message_never_asserts_anticheat_as_the_cause(string culture)
    {
        var msg = Localizer.Get("Measure.NoPresentingFrames", new CultureInfo(culture));

        // L'anti-triche reste mentionné — c'est une cause plausible…
        msg.ToLowerInvariant().Should().MatchRegex("anti.?(cheat|trich)");

        // …mais jamais seul : une alternative doit être offerte au lecteur.
        var alternative = culture == "fr" ? " ou " : " or ";
        msg.Should().Contain(alternative,
            "une seule cause proposée se lit comme un diagnostic, or nous n'avons prouvé que « zéro image »");
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Zero_frames_message_states_what_was_actually_observed(string culture)
    {
        var msg = Localizer.Get("Measure.NoPresentingFrames", new CultureInfo(culture)).ToLowerInvariant();

        // Le fait réellement constaté — aucune image captée — doit apparaître.
        var observed = culture == "fr" ? "image" : "frame";
        msg.Should().Contain(observed,
            "le message doit d'abord dire ce qui a été mesuré, avant toute hypothèse");
    }
}
