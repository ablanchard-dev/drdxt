using System.Globalization;
using System.Resources;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 14/09/2026 (plan 2.4) — l'ecran de fin de session disait « RejectedUnresolved » ou « Drifted » : le nom
// d'une enum. Chaque etat se dit maintenant par une cle de convention ; ce test exige la cle de chaque
// valeur, en anglais et en francais, pour qu'une valeur ajoutee demain ne reparte pas brute a l'ecran.
// Lu sans remontee aux parents (piege note dans DashboardLocalizationTests, 21/08/2026) : une cle
// absente du resx FR rendrait sinon la phrase anglaise, et le test passerait. Vu ici par mutation.
public sealed class EverySessionStateIsSpokenTests
{
    private static readonly ResourceManager Resources =
        new("DrDxt.Infrastructure.Resources.Strings", typeof(ResxLocalizer).Assembly);

    private static bool ExistsIn(string culture, string key) =>
        !string.IsNullOrEmpty(Resources.GetResourceSet(new CultureInfo(culture), createIfNotExists: true,
            tryParents: false)?.GetString(key));

    private static IEnumerable<string> Cles() =>
        Enum.GetNames<CandidateOutcome>().Select(v => $"Autopilot.Outcome.{v}")
            .Concat(Enum.GetNames<EvidenceOutcome>().Select(v => $"Autopilot.Evidence.{v}"))
            .Concat(Enum.GetNames<EtatApplique>().Select(v => $"Autopilot.ReadBack.{v}"))
            .Append("Apply.Session.ReadBack");

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void Every_state_the_session_can_show_has_words(string langue)
    {
        // Temoin de lecture (18/09/2026) : sans lui, une liste d'etats devenue vide rendrait ce
        // test vert en n'ayant regarde aucun etat.
        Cles().Should().NotBeEmpty("le crible doit avoir de vrais etats a examiner");

        Cles().Where(cle => !ExistsIn(langue, cle)).Should().BeEmpty();
    }
}
