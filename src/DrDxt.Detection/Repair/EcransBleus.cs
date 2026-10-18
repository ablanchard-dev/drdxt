using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Les écrans bleus comptés par trois sources, toutes bornées à la même fenêtre : le code d'arrêt porté par
/// Kernel-Power 41, le rapport « BugCheck » que Windows écrit au redémarrage (WER-SystemErrorReporting 1001), et les
/// fichiers de vidage écrits dans la fenêtre. Une seule source peut manquer un écran bleu (vidage désactivé ou raté,
/// journal tourné, coupure avant l'écriture du code) : un vidage récent suffit à interdire « aucun écran bleu ».
/// Quand les sources se contredisent, le constat le dit au lieu de choisir.
/// Sources : Microsoft Learn, « Event ID 41 » (BugcheckCode) et « Read small memory dump files » (%SystemRoot%\Minidump).
/// </summary>
public static class EcransBleus
{
    /// <summary>La requête des rapports BugCheck, bornée par le journal à la fenêtre (même forme que les pannes du journal).</summary>
    internal static string RequeteWer(DateTimeOffset depuis)
        => "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001) and TimeCreated[@SystemTime>='"
           + depuis.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + "']]]";

    /// <summary>Les dates des rapports BugCheck depuis <paramref name="depuis"/> ; <c>null</c> = journal non lu.</summary>
    public static IReadOnlyList<DateTimeOffset>? LireRapports(DateTimeOffset depuis)
    {
        try
        {
            var dates = new List<DateTimeOffset>();
            using var lecteur = new EventLogReader(new EventLogQuery("System", PathType.LogName, RequeteWer(depuis)) { ReverseDirection = true });
            for (var e = lecteur.ReadEvent(); e is not null && dates.Count < 200; e = lecteur.ReadEvent())
                using (e)
                    if (e.TimeCreated is { } t && new DateTimeOffset(t) >= depuis) dates.Add(new DateTimeOffset(t));
            return dates;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur. <paramref name="arrets"/> : Kernel-Power 41 de la fenêtre ; <paramref name="rapports"/> : dates des
    /// rapports BugCheck ; <paramref name="vidages"/> : les vidages écrits dans la fenêtre. Chaque <c>null</c> = non lu.</summary>
    public static RepairFinding Juger(IReadOnlyList<ArretBrutal>? arrets, IReadOnlyList<DateTimeOffset>? rapports,
        EtatDesVidages? vidages, int jours)
    {
        int? parCode = arrets?.Count(a => a.EcranBleu);
        int? parRapport = rapports?.Count;
        int? parVidage = vidages is null ? null : vidages.PetitsVidages + (vidages.VidageComplet ? 1 : 0);
        string N(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? Docteur.T("EC.NonLu");
        var valeur = Docteur.T("EC.Valeur", N(parCode), N(parRapport), N(parVidage));
        var lus = new[] { parCode, parRapport, parVidage };
        var plus = lus.Max() ?? 0;

        if (plus == 0)
            return lus.All(n => n is not null)
                ? new RepairFinding("STABILITE", "ecrans bleus", valeur, Docteur.T("EC.Attendu"), RepairStatus.ExpectedDefault,
                    Docteur.T("EC.Aucun", jours))
                : new RepairFinding("STABILITE", "ecrans bleus", valeur, Docteur.T("EC.Attendu"), RepairStatus.Unknown,
                    Docteur.T("EC.AucunPartiel", jours));

        var dernier = new[]
        {
            arrets?.Where(a => a.EcranBleu).Select(a => (DateTimeOffset?)a.Quand).Max(),
            rapports?.Select(d => (DateTimeOffset?)d).Max(),
            vidages?.DernierVidage,
        }.Max();
        var accord = lus.Where(n => n is not null).Distinct().Count() == 1;
        return new RepairFinding("STABILITE", "ecrans bleus", valeur, Docteur.T("EC.Attendu"), RepairStatus.Suspicious,
            Docteur.T("EC.Vus", plus, jours, dernier?.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue) ?? "?")
            + (accord ? "" : Docteur.T("EC.Desaccord"))
            + (parVidage == 0 ? Docteur.T("EC.SansVidage") : "")
            + Docteur.T("EC.Geste"),
            RepairAction.Recommandation);
    }
}
