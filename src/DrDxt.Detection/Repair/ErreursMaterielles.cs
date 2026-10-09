using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// 04/10/2026 (plan 3.2) : les erreurs matérielles que Windows enregistre (WHEA : processeur, mémoire, PCIe). Après une
/// hausse de la limite de puissance, une erreur corrigée qui apparaît est le premier signe d'instabilité, avant l'arrêt.
///
/// Lues dans le journal Système (fournisseur Microsoft-Windows-WHEA-Logger) ; la gravité vient de l'en-tête de
/// l'enregistrement CPER (UEFI, annexe N.2.1 : signature « CPER » sur 4 octets, révision sur 2, fin de signature sur 4,
/// nombre de sections sur 2, puis la gravité sur 4 octets à l'octet 12 : 0 récupérable, 1 fatale, 2 corrigée, 3 information). La gravité CPER est la même
/// chez tous les fabricants, les numéros d'événement non. Machine de référence, 04/10 : un seul enregistrement en
/// 90 jours, le 17/09, gravité 3 (information) : aucune erreur.
/// </summary>
public static class ErreursMaterielles
{
    public sealed record Enregistrement(DateTimeOffset Quand, int Gravite);

    /// <summary>Revue du 04/10 (P1) : <c>null</c> quand le journal n'a pas pu être lu ; une liste vide veut dire
    /// « lu, rien trouvé ». Avaler l'échec rendait « aucune erreur en 90 jours » sur un journal refusé.</summary>
    public static IReadOnlyList<Enregistrement>? Lire(int jours = 90)
    {
        var lus = new List<Enregistrement>();
        try
        {
            var depuis = DateTime.UtcNow.AddDays(-jours).ToString("o", CultureInfo.InvariantCulture);
            var requete = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='{depuis}']]]");
            using var lecteur = new EventLogReader(requete);
            for (var e = lecteur.ReadEvent(); e is not null; e = lecteur.ReadEvent())
                using (e)
                {
                    var brut = e.Properties.Select(p => p.Value).OfType<byte[]>().FirstOrDefault()
                               ?? (e.Properties.Select(p => p.Value as string).FirstOrDefault(s => s?.StartsWith("43504552", StringComparison.Ordinal) == true) is { } hex
                                   ? Convert.FromHexString(hex) : null);
                    if (e.TimeCreated is { } t && Gravite(brut) is { } g) lus.Add(new(new DateTimeOffset(t), g));
                }
        }
        catch (Exception) { return null; }
        return lus;
    }

    /// <summary>La gravité CPER, ou <c>null</c> si l'en-tête n'en est pas un.</summary>
    public static int? Gravite(byte[]? cper)
        => cper is { Length: >= 16 } && cper[0] == (byte)'C' && cper[1] == (byte)'P' && cper[2] == (byte)'E' && cper[3] == (byte)'R'
            ? (int)BitConverter.ToUInt32(cper, 12) : null;

    /// <summary>Pur. Les informations ne comptent pas ; une erreur fatale ou récupérable pèse plus qu'une corrigée.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Enregistrement>? lus, int jours = 90)
    {
        if (lus is null)
        {
            yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)", null, null, RepairStatus.Unknown,
                "le journal des erreurs matérielles n'a pas pu être lu : « non lu » n'est pas « aucune erreur ».");
            yield break;
        }
        var erreurs = lus.Where(e => e.Gravite is 0 or 1 or 2).OrderByDescending(e => e.Quand).ToList();
        if (erreurs.Count == 0)
        {
            yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)", $"aucune en {jours} jours", "aucune",
                RepairStatus.ExpectedDefault,
                $"Windows n'a enregistré aucune erreur matérielle (processeur, mémoire, PCIe) en {jours} jours"
                + (lus.Count > 0 ? $" ({lus.Count} enregistrement(s) d'information, qui ne sont pas des erreurs)." : ".")
                + " Après un changement de puissance ou de mémoire au BIOS, c'est ici qu'une instabilité se voit en premier.");
            yield break;
        }
        var graves = erreurs.Count(e => e.Gravite is 0 or 1);
        var dates = string.Join(", ", erreurs.Take(5).Select(e => e.Quand.LocalDateTime.ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)));
        yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)",
            graves > 0 ? $"{graves} grave(s), {erreurs.Count - graves} corrigée(s)" : $"{erreurs.Count} corrigée(s)", "aucune",
            RepairStatus.Suspicious,
            (graves > 0
                ? $"Windows a enregistré {graves} erreur(s) matérielle(s) que le matériel n'a pas pu corriger, "
                : $"Windows a enregistré {erreurs.Count} erreur(s) matérielle(s) corrigée(s) par le matériel, ")
            + $"en {jours} jours (dernières : {dates}). Une erreur corrigée ne plante rien, mais elle annonce une marge qui "
            + "manque : tension, chaleur ou réglage trop poussé. Si un réglage de puissance ou de mémoire a changé au BIOS "
            + "juste avant, c'est une piste ; reviens au réglage d'avant et regarde si elles s'arrêtent.",
            RepairAction.Recommandation);
    }
}
