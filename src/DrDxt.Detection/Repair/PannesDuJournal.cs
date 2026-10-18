using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Les pannes que Windows note dans le journal Système en dehors des arrêts brutaux et des erreurs WHEA : carte graphique
/// relancée par Windows, disque ou contrôleur qui ne répond plus, système de fichiers abîmé, pilote qui ne se charge pas,
/// mémoire virtuelle épuisée. Chaque famille se lit par fournisseur ET numéro d'événement : un numéro seul attrape les
/// événements d'information d'autres fournisseurs et gonfle le compte.
///
/// Le journal Système est circulaire (20 Mio par défaut) : sur une machine bavarde il ne couvre que quelques jours. Sa
/// date la plus ancienne est lue et dite ; une fenêtre qu'il ne couvre pas donne « non lu sur la fenêtre », jamais « aucun ».
/// Mesuré le 16/10/2026 sur la machine de référence : premier événement du journal Système le 11/10, soit 5 jours.
/// </summary>
public static class PannesDuJournal
{
    /// <summary>Une famille de pannes : fournisseurs (un seul nom suffit à les reconnaître) et numéros comptés.
    /// <paramref name="Grave"/> : une occurrence suffit à mériter le regard ; sinon elle est seulement dite.</summary>
    public sealed record Famille(string Nom, string[] Fournisseurs, int[] Ids, bool Grave);

    // Microsoft Learn : « Timeout Detection and Recovery (TDR) » (Display 4101 : le pilote d'affichage a cessé de répondre
    // et a été relancé) ; « Troubleshoot disk events » (disk 7 bloc défectueux, 11 erreur du contrôleur, 51 erreur pendant
    // une pagination, 153 entrée-sortie relancée) ; « Event ID 129 » des pilotes de port Storport (stornvme, storahci :
    // le périphérique a été réinitialisé) ; Ntfs 55 (structure du système de fichiers corrompue) ; Kernel-PnP 219 (un
    // pilote n'a pas pu être chargé) ; Resource-Exhaustion-Detector 2004 (mémoire virtuelle presque épuisée, Windows a
    // nommé les programmes qui en tenaient le plus).
    public static readonly Famille[] Familles =
    [
        new("pilote graphique relance", ["Display"], [4101], true),
        new("erreurs du disque", ["disk"], [7, 11, 51, 153], true),
        new("controleur de stockage reinitialise", ["stornvme", "storahci"], [129], true),
        new("systeme de fichiers abime", ["Ntfs", "Microsoft-Windows-Ntfs"], [55], true),
        new("memoire virtuelle epuisee", ["Microsoft-Windows-Resource-Exhaustion-Detector"], [2004], true),
        // Fréquent et souvent sans suite (un pilote de périphérique absent au démarrage) : dit, pas jugé.
        new("pilote non charge au demarrage", ["Microsoft-Windows-Kernel-PnP"], [219], false),
    ];

    /// <summary>Ce qu'une famille a rendu : <c>null</c> = non lue.</summary>
    public sealed record Releve(Famille Famille, int? Nombre, DateTimeOffset? Dernier);

    private const int Plafond = 500;

    /// <summary>La requête d'une famille, filtrée par le journal (fournisseur, numéros, date).</summary>
    internal static string Requete(Famille f, DateTimeOffset depuis)
        => "*[System[(" + string.Join(" or ", f.Fournisseurs.Select(p => $"Provider[@Name='{p}']")) + ") and ("
           + string.Join(" or ", f.Ids.Select(i => $"EventID={i}")) + ") and TimeCreated[@SystemTime>='"
           + depuis.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + "']]]";

    public static IReadOnlyList<Releve> Lire(DateTimeOffset depuis) => Familles.Select(f =>
    {
        try
        {
            var n = 0;
            DateTimeOffset? dernier = null;
            using var lecteur = new EventLogReader(new EventLogQuery("System", PathType.LogName, Requete(f, depuis)) { ReverseDirection = true });
            for (var e = lecteur.ReadEvent(); e is not null && n < Plafond; e = lecteur.ReadEvent())
                using (e)
                {
                    n++;
                    if (dernier is null && e.TimeCreated is { } t) dernier = new DateTimeOffset(t);
                }
            return new Releve(f, n, dernier);
        }
        catch (Exception) { return new Releve(f, null, null); }
    }).ToList();

    /// <summary>La date du plus ancien événement du journal Système ; <c>null</c> si non lue.</summary>
    public static DateTimeOffset? DebutDuJournal()
    {
        try
        {
            using var lecteur = new EventLogReader(new EventLogQuery("System", PathType.LogName, "*"));
            using var e = lecteur.ReadEvent();
            return e?.TimeCreated is { } t ? new DateTimeOffset(t) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur. Une ligne par famille, et la profondeur du journal. Un journal qui commence après le début de la
    /// fenêtre ne prouve pas « aucune panne » : chaque famille sans panne devient « non lu sur la fenêtre ».</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Releve> releves, DateTimeOffset? debutDuJournal,
        DateTimeOffset depuis, DateTimeOffset maintenant)
    {
        var jours = (int)Math.Round((maintenant - depuis).TotalDays);
        var couvert = debutDuJournal is { } d && d <= depuis;
        yield return debutDuJournal is { } debut
            ? new RepairFinding("STABILITE", "profondeur du journal", Docteur.T("JP.Profondeur",
                    debut.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue), Math.Max(0, (int)(maintenant - debut).TotalDays)),
                Docteur.T("JP.ProfondeurAttendue", jours), couvert ? RepairStatus.Observed : RepairStatus.Unknown,
                Docteur.T(couvert ? "JP.Couvert" : "JP.Court", jours))
            : new RepairFinding("STABILITE", "profondeur du journal", null, null, RepairStatus.Unknown, Docteur.T("JP.ProfondeurNonLue"));

        foreach (var r in releves)
        {
            var nom = Docteur.T("JP.Nom." + r.Famille.Nom);
            if (r.Nombre is not { } n)
                yield return new RepairFinding("STABILITE", r.Famille.Nom, null, null, RepairStatus.Unknown, Docteur.T("JP.NonLu", nom));
            else if (n == 0)
                yield return couvert
                    ? new RepairFinding("STABILITE", r.Famille.Nom, Docteur.T("JP.Aucun"), Docteur.T("JP.Aucun"), RepairStatus.ExpectedDefault,
                        Docteur.T("JP.AucunEn", nom, jours))
                    : new RepairFinding("STABILITE", r.Famille.Nom, null, Docteur.T("JP.Aucun"), RepairStatus.Unknown,
                        Docteur.T("JP.AucunMaisCourt", nom, jours));
            else
                yield return new RepairFinding("STABILITE", r.Famille.Nom,
                    Docteur.T("JP.Compte", n, r.Dernier?.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue) ?? "?"),
                    Docteur.T("JP.Aucun"), r.Famille.Grave ? RepairStatus.Suspicious : RepairStatus.Observed,
                    Docteur.T("JP.Explication." + r.Famille.Nom, n, jours),
                    r.Famille.Grave ? RepairAction.Recommandation : RepairAction.Aucune);
        }
    }
}
