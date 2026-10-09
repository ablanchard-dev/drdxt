using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Pure, testable derivation of all human-facing narrative from real scores.
/// Bands follow the product spec (≥90 / 75–89 / 60–74 / &lt;60). Every string is a
/// resx key passed through the injected localizer — zero hardcoded copy.
/// </summary>
// ══ ce qui reste de ce fichier, et pourquoi (05/09/2026) ══
//
// Ce type derivait tout le langage humain du score /100 : le verdict de l'entete, le mot de bande,
// la ligne « globalement en bonne sante » du resume. Mesure du 05/09 : doubler les penalites du
// grand livre — sans qu'un seul levier change d'etat — faisait basculer ces trois lignes. Une
// estimation decidait ce que le client lit.
//
// SUPPRIMES, sans appelant restant : `VerdictKey`, `BandWordKey`, et les trois convertisseurs
// d'affichage (bande, arc, etoiles) qui n'etaient references nulle part — ni code, ni XAML.
//
// GARDES, et la raison : `BandOf` + `QualKey` servent encore aux sous-SCORES qualitatifs
// (performance / latence / stabilite), qui sont des grandeurs par dimension et non le verdict
// global ; `CriticalCount` compte des constats, pas des points. Ce qui a ete retire, c'est le
// score comme juge de l'etat de la machine — pas l'arithmetique de bande partout ou elle qualifie
// encore une dimension.
public static class ScoreNarrative
{
    public const int BandExcellentMin = 90;
    public const int BandGoodMin = 75;
    public const int BandFairMin = 60;

    public enum Band { Excellent, Good, Fair, Poor }

    public static Band BandOf(int score) =>
        score >= BandExcellentMin ? Band.Excellent
        : score >= BandGoodMin ? Band.Good
        : score >= BandFairMin ? Band.Fair
        : Band.Poor;



    /// <summary>Resx key for the qualitative label on a sub-score card.</summary>
    public static string QualKey(int score) => BandOf(score) switch
    {
        Band.Excellent => "UI.Qual.Excellent",
        Band.Good => "UI.Qual.Good",
        Band.Fair => "UI.Qual.Fair",
        _ => "UI.Qual.Poor",
    };

    /// <summary>Count of findings that represent a confirmed problem (Major severity).</summary>
    public static int CriticalCount(IEnumerable<FindingViewModel> findings) =>
        findings.Count(f => f.Severity == Severity.Major);
}
