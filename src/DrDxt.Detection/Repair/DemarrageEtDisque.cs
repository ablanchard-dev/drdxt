using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Ce qui ralentit le démarrage, mesuré par Windows lui-même, et la place libre du disque
/// système. Lecture seule.
///
/// Démarrage : journal Microsoft-Windows-Diagnostics-Performance/Operational, événement 101 (« le démarrage de cette
/// application a été plus long que d'habitude »), champs Name, FriendlyName et DegradationTime (millisecondes perdues
/// par le démarrage du système). Relevé le 04/10 sur la machine de référence : 81 événements, le plus récent du 31/07/2025 (Norton,
/// Discord, conteneur NVIDIA) ; rien sur les 90 derniers jours, donc rien n'est dit.
/// </summary>
public static class DemarrageEtDisque
{
    public sealed record Ralentisseur(DateTimeOffset Quand, string Nom, int DegradationMs);

    public static IReadOnlyList<Ralentisseur> LireRalentisseurs(int jours = 90)
    {
        var lus = new List<Ralentisseur>();
        try
        {
            var depuis = DateTime.UtcNow.AddDays(-jours).ToString("o", CultureInfo.InvariantCulture);
            var requete = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                $"*[System[EventID=101 and TimeCreated[@SystemTime>='{depuis}']]]");
            using var lecteur = new EventLogReader(requete);
            for (var e = lecteur.ReadEvent(); e is not null; e = lecteur.ReadEvent())
                using (e)
                {
                    var x = System.Xml.Linq.XElement.Parse(e.ToXml());
                    string? Champ(string nom) => x.Descendants().FirstOrDefault(d => d.Name.LocalName == "Data" && (string?)d.Attribute("Name") == nom)?.Value;
                    var nomLisible = Champ("FriendlyName") is { Length: > 0 } f ? f : Champ("Name");
                    if (e.TimeCreated is { } t && nomLisible is not null && int.TryParse(Champ("DegradationTime"), out var ms))
                        lus.Add(new(new DateTimeOffset(t), nomLisible, ms));
                }
        }
        catch (Exception) { }
        return lus;
    }

    /// <summary>Pur. Rien sur la période : rien n'est dit (une absence d'événement n'est pas un fait à afficher).</summary>
    public static IEnumerable<RepairFinding> JugerDemarrage(IReadOnlyList<Ralentisseur> lus, int jours = 90)
    {
        if (lus.Count == 0) yield break;
        var pires = lus.GroupBy(r => r.Nom, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Nom: g.Key, Fois: g.Count(), Max: g.Max(r => r.DegradationMs)))
            .OrderByDescending(r => r.Max).Take(5).ToList();
        yield return new RepairFinding("DEMARRAGE", "programmes qui ralentissent le démarrage",
            Docteur.T("DD.Programmes", pires.Count), null, RepairStatus.Observed,
            Docteur.T("DD.Lents", jours, string.Join(", ", pires.Select(p => Docteur.T("DD.Lent", p.Nom, p.Max / 1000.0, p.Fois)))));
    }

    /// <summary>**Non chiffré** : sous 5 % de place libre, les mises à jour de Windows, le fichier d'échange et les jeux
    /// (mises à jour de plusieurs dizaines de Go) en manquent. Aucune source ne fixe ce seuil ; il est bas exprès.</summary>
    public const double SeuilPlaceLibre = 0.05;

    public static IEnumerable<RepairFinding> JugerDisqueSysteme(string lettre, long libre, long total)
    {
        if (total <= 0 || libre < 0) yield break;
        var part = (double)libre / total;
        // En Go de 1 024³ octets, comme l'Explorateur (sinon « 1 999 Go » contre « 1,81 To » à l'écran).
        const double Go = 1024.0 * 1024 * 1024;
        var texte = Docteur.T("DD.Place", lettre.TrimEnd(':'), libre / Go, total / Go, part * 100);
        yield return part < SeuilPlaceLibre
            ? new RepairFinding("STOCKAGE", "place libre du disque système", texte, Docteur.T("DD.AuMoins5"), RepairStatus.Suspicious,
                Docteur.T("DD.PeuDePlace", texte),
                RepairAction.Recommandation)
            : new RepairFinding("STOCKAGE", "place libre du disque système", texte, Docteur.T("DD.AuMoins5"), RepairStatus.Observed,
                texte + ".");
    }

    /// <summary>Sous ce total, le cache ne vaut pas une ligne. **Non chiffré** : un choix de lisibilité.</summary>
    public const long SeuilCacheDeShaders = 10L << 30;

    /// <summary>Les dossiers de cache de shaders des pilotes (NVIDIA, AMD) et de Windows, sous le profil.</summary>
    private static readonly string[] DossiersDeShaders = [@"NVIDIA\DXCache", @"NVIDIA\GLCache", @"AMD\DxCache", @"AMD\DxcCache", "D3DSCache"];

    /// <summary>La taille totale des caches de shaders (10 ms pour 832 fichiers et 98,6 Go mesurés), ou <c>null</c> si
    /// aucun dossier n'est lisible.</summary>
    public static long? LireCacheDeShaders()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        long? total = null;
        foreach (var dossier in DossiersDeShaders.Select(d => Path.Combine(local, d)).Where(Directory.Exists))
        {
            try { total = (total ?? 0) + new DirectoryInfo(dossier).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch (Exception) { /* dossier illisible : les autres comptent */ }
        }
        return total;
    }

    /// <summary>98,6 Go de cache de shaders NVIDIA sur la machine de référence. Ce n'est pas une panne : un
    /// cache évite de recompiler les shaders, donc des à-coups. La place se dit, avec le prix d'un vidage.</summary>
    public static IEnumerable<RepairFinding> JugerCacheDeShaders(long? octets)
    {
        if (octets is not { } o || o < SeuilCacheDeShaders) yield break;
        var taille = Docteur.T("DD.Taille", o / (1024.0 * 1024 * 1024));
        yield return new RepairFinding("STOCKAGE", "cache de shaders de la carte graphique", taille, null, RepairStatus.Observed,
            Docteur.T("DD.Shaders", taille));
    }
}
