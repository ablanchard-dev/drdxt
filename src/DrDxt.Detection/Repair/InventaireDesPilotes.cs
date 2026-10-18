using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// L'inventaire des pilotes de périphériques (Win32_PnPSignedDriver) : combien, de quelles classes, lesquels Windows lit comme
/// non signés et lesquels sont vraiment anciens. Une seule règle pour « ancien », testée : un paquet tiers (publié
/// oemNN.inf dans le magasin de pilotes), d'un autre fournisseur que Microsoft, daté de plus de <see cref="AnsAncien"/> ans,
/// hors dates jeton. Les pilotes de la boîte de Windows portent la date 21/06/2006 et l'INF du chipset Intel 18/07/1968,
/// exprès pour ne jamais passer devant un pilote plus précis (relevé le 16/10/2026 sur la machine de référence : 245
/// pilotes, 9 sans INF, 7 sans signature lue, 1 non signé ; 8 paquets « oem » de plus de cinq ans dont 7 datés 1968).
/// Un champ de signature vide est « non lu », jamais « non signé ».
/// Sources : Microsoft Learn, « Win32_PnPSignedDriver class » (IsSigned, DriverDate, InfName) et « INF DriverVer
/// directive » (la date sert au classement des pilotes).
/// </summary>
public static class InventaireDesPilotes
{
    public sealed record Pilote(string Classe, string Nom, string? Fournisseur, DateTime? Date, bool? Signe, string? Inf);

    /// <summary>Choix DrDXT, pas une norme : cinq ans couvrent plusieurs versions majeures de Windows 10 et 11 ; un pilote
    /// plus vieux n'a souvent jamais été testé par son fabricant sur le Windows en service.</summary>
    public const int AnsAncien = 5;

    private static readonly DateTime[] DatesJeton = [new(2006, 6, 21), new(1968, 7, 18)];

    /// <summary>Pur : la date d'un DriverDate DMTF (« 19680718000000.******+000 »), jour seul. La convertir en heure locale
    /// la ferait reculer d'un jour à l'ouest de Greenwich, et une date jeton ne serait plus reconnue.</summary>
    public static DateTime? DateDmtf(string? dmtf)
        => dmtf is { Length: >= 8 } && DateTime.TryParseExact(dmtf[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    /// <summary>Pur : la règle « vraiment ancien ».</summary>
    public static bool Ancien(Pilote p, DateTime maintenant)
        => p.Inf?.StartsWith("oem", StringComparison.OrdinalIgnoreCase) == true
           && !string.Equals(p.Fournisseur, "Microsoft", StringComparison.OrdinalIgnoreCase)
           && p.Date is { } d && !DatesJeton.Contains(d.Date) && d < maintenant.AddYears(-AnsAncien);

    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Pilote>? pilotes, DateTime maintenant)
    {
        if (pilotes is null)
        {
            yield return new RepairFinding("PERIPHERIQUES", "inventaire des pilotes", null, null, RepairStatus.Unknown, Docteur.T("IV.NonLu"));
            yield break;
        }
        var avecInf = pilotes.Where(p => p.Inf is not null).ToList();
        var nonSignes = avecInf.Where(p => p.Signe == false).ToList();
        var nonLus = avecInf.Count(p => p.Signe is null);
        var anciens = avecInf.Where(p => Ancien(p, maintenant)).ToList();
        var classes = string.Join(", ", avecInf.GroupBy(p => p.Classe).OrderByDescending(g => g.Count()).Take(5)
            .Select(g => $"{g.Key} {g.Count().ToString(CultureInfo.InvariantCulture)}"));
        yield return new RepairFinding("PERIPHERIQUES", "inventaire des pilotes",
            Docteur.T("IV.Valeur", avecInf.Count, nonSignes.Count, anciens.Count, nonLus), null, RepairStatus.Observed,
            Docteur.T("IV.Explication", classes, AnsAncien)
            + (anciens.Count > 0 ? Docteur.T("IV.Anciens", string.Join(", ", anciens.Select(p =>
                $"{p.Nom} ({p.Fournisseur}, {p.Date:yyyy})").Distinct().Take(8))) : ""));
        if (nonSignes.Count > 0)
            yield return new RepairFinding("PERIPHERIQUES", "pilotes non signes", string.Join(", ", nonSignes.Select(p => p.Nom).Distinct()), "0",
                RepairStatus.Suspicious, Docteur.T("IV.NonSignes", nonSignes.Count), RepairAction.Recommandation, Remede: Docteur.T("IV.NonSignesRemede"));
    }
}
