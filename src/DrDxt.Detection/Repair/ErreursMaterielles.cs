using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Les erreurs matérielles que Windows enregistre (WHEA : processeur, mémoire, PCIe). Après une
/// hausse de la limite de puissance, une erreur corrigée qui apparaît est le premier signe d'instabilité, avant l'arrêt.
///
/// Lues dans le journal Système (fournisseur Microsoft-Windows-WHEA-Logger) ; la gravité vient de l'en-tête de
/// l'enregistrement CPER (UEFI, annexe N.2.1 : signature « CPER » sur 4 octets, révision sur 2, fin de signature sur 4,
/// nombre de sections sur 2, puis la gravité sur 4 octets à l'octet 12 : 0 récupérable, 1 fatale, 2 corrigée, 3 information). La gravité CPER est la même
/// chez tous les fabricants, les numéros d'événement non. Relevé le 04/10 sur la machine de référence : un seul
/// enregistrement en 90 jours, gravité 3 (information) : aucune erreur.
/// </summary>
public static class ErreursMaterielles
{
    /// <summary><paramref name="DuProcesseur"/> : une section CPER du processeur (générique ou IA32/X64), pas une erreur PCIe,
    /// mémoire ou de plateforme.</summary>
    public sealed record Enregistrement(DateTimeOffset Quand, int Gravite, bool DuProcesseur = false);

    /// <summary><c>null</c> quand le journal n'a pas pu être lu ; une liste vide veut dire
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
                    if (e.TimeCreated is { } t && Gravite(brut) is { } g) lus.Add(new(new DateTimeOffset(t), g, DuProcesseur(brut)));
                }
        }
        catch (Exception) { return null; }
        return lus;
    }

    /// <summary>La gravité CPER, ou <c>null</c> si l'en-tête n'en est pas un.</summary>
    public static int? Gravite(byte[]? cper)
        => cper is { Length: >= 16 } && cper[0] == (byte)'C' && cper[1] == (byte)'P' && cper[2] == (byte)'E' && cper[3] == (byte)'R'
            ? (int)BitConverter.ToUInt32(cper, 12) : null;

    /// <summary>Pur : l'enregistrement porte-t-il une section du processeur ? UEFI, annexe N (Common Platform Error Record) : le
    /// nombre de sections en octet 10, les descripteurs de section à partir de l'octet 128, 72 octets chacun, le type de section
    /// (GUID) à l'octet 16 du descripteur ; processeur = « Processor Generic » ou « IA32/X64 ».</summary>
    public static bool DuProcesseur(byte[]? cper)
    {
        if (Gravite(cper) is null || cper!.Length < 130) return false;
        var sections = BitConverter.ToUInt16(cper, 10);
        for (var i = 0; i < sections; i++)
        {
            var o = 128 + 72 * i;
            if (o + 32 > cper.Length) return false;
            var type = new Guid(cper.AsSpan(o + 16, 16));
            if (type == ProcesseurGenerique || type == ProcesseurX64) return true;
        }
        return false;
    }

    private static readonly Guid ProcesseurGenerique = new("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB");
    private static readonly Guid ProcesseurX64 = new("DC3EA0B0-A144-4797-B95B-53FA242B6E1D");

    /// <summary>Pur. Les informations ne comptent pas ; une erreur fatale ou récupérable pèse plus qu'une corrigée.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Enregistrement>? lus, int jours = 90)
    {
        if (lus is null)
        {
            yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)", null, null, RepairStatus.Unknown,
                Docteur.T("EM.NonLu"));
            yield break;
        }
        var erreurs = lus.Where(e => e.Gravite is 0 or 1 or 2).OrderByDescending(e => e.Quand).ToList();
        if (erreurs.Count == 0)
        {
            yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)", Docteur.T("EM.AucuneEn", jours), Docteur.T("EM.Aucune"),
                RepairStatus.ExpectedDefault,
                Docteur.T("EM.AucuneExplication", jours)
                + (lus.Count > 0 ? Docteur.T("EM.Information", lus.Count) : Docteur.T("EM.Point"))
                + Docteur.T("EM.ApresBios"));
            yield break;
        }
        var graves = erreurs.Count(e => e.Gravite is 0 or 1);
        var dates = string.Join(", ", erreurs.Take(5).Select(e => e.Quand.LocalDateTime.ToString(Docteur.T("EM.FormatDate"), CultureInfo.InvariantCulture)));
        yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)",
            graves > 0 ? Docteur.T("EM.GravesValeur", graves, erreurs.Count - graves) : Docteur.T("EM.CorrigeesValeur", erreurs.Count),
            Docteur.T("EM.Aucune"),
            RepairStatus.Suspicious,
            (graves > 0 ? Docteur.T("EM.Graves", graves) : Docteur.T("EM.Corrigees", erreurs.Count))
            + Docteur.T("EM.Suite", jours, dates),
            RepairAction.Recommandation);
    }
}
