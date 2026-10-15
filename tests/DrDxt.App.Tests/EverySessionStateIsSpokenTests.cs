using System.Globalization;
using System.Resources;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Chaque etat de fin de session se dit par une cle de convention, jamais par le nom de l'enum
// (« RejectedUnresolved », « Drifted »). Ce test exige la cle de chaque valeur, en anglais et en
// francais, pour qu'une valeur ajoutee demain ne parte pas brute a l'ecran.
// Lu sans remontee aux parents : sinon une cle absente du resx FR rendrait la phrase anglaise, et
// le test passerait (verifie par mutation).
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
        // Temoin de lecture : sans lui, une liste d'etats devenue vide rendrait ce
        // test vert en n'ayant regarde aucun etat.
        Cles().Should().NotBeEmpty("le crible doit avoir de vrais etats a examiner");

        Cles().Where(cle => !ExistsIn(langue, cle)).Should().BeEmpty();
    }
}
