using System.Diagnostics.Eventing.Reader;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// L'heure du dernier démarrage du PC. « Maintenant moins le temps
/// depuis le démarrage » ne suffit pas : avec le démarrage rapide de Windows (HiberbootEnabled à 1, le cas sur la
/// machine de référence), un arrêt puis un allumage reprennent le noyau, et ce compteur peut ne pas repartir de zéro. Windows note
/// chaque démarrage, rapide ou complet, dans le journal Système (Microsoft-Windows-Kernel-Boot, événement 27, BootType 0
/// complet, 1 rapide ; relevé le 07/10 : rapides les 22/09, 23/09 et 02/10, complets ensuite). Le plus récent des deux
/// repères fait foi. Lu une fois par lancement : un démarrage ne change pas tant que le programme tourne.
/// </summary>
public static class DemarrageDuSysteme
{
    private static readonly Lazy<DateTimeOffset> Lu = new(() => Choisir(DernierEvenement(),
        DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64)));

    public static DateTimeOffset Valeur => Lu.Value;

    /// <summary>Le plus récent des deux repères ; sans événement lu, le compteur seul.</summary>
    internal static DateTimeOffset Choisir(DateTimeOffset? evenement, DateTimeOffset parLeCompteur)
        => evenement is { } e && e > parLeCompteur ? e : parLeCompteur;

    internal static DateTimeOffset? DernierEvenement()
    {
        try
        {
            var requete = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=27)]]") { ReverseDirection = true };
            using var lecteur = new EventLogReader(requete);
            using var e = lecteur.ReadEvent();
            return e?.TimeCreated is { } t ? new DateTimeOffset(t) : null;
        }
        catch (Exception) { return null; /* journal illisible : le compteur seul */ }
    }
}
