using System.Diagnostics.Eventing.Reader;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// La dernière sortie de veille ou de veille prolongée : Windows la note dans le journal Système (Microsoft-Windows-Power-
/// Troubleshooter, événement 1, « Le système est sorti d'un état de faible alimentation » ; relevé le 23/10/2026 sur la
/// machine de référence, une par matin, veille S3). Null si le journal ne se lit pas ou n'en porte aucune.
/// </summary>
public static class SortieDeVeille
{
    public static DateTimeOffset? Derniere()
    {
        try
        {
            var requete = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=1)]]") { ReverseDirection = true };
            using var lecteur = new EventLogReader(requete);
            using var e = lecteur.ReadEvent();
            return e?.TimeCreated is { } t ? new DateTimeOffset(t) : null;
        }
        catch (Exception) { return null; /* journal illisible : aucune sortie de veille connue */ }
    }
}
