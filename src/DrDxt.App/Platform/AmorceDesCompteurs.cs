using System.Runtime.Versioning;
using DrDxt.Detection.Cpu;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Amorce des compteurs de performance : payer l'attente pendant que personne n'attend.
//
// Mesure du 19/09/2026, chronometre en trois phases dans un processus neuf :
//
//     1. resoudre cinq noms de compteurs, tables froides      427 ms
//     2. premier `CpuFrequencyReader.Lire()`                4 543 ms
//     3. le suivant, tout chaud                               11,6 ms
//
// Les 4,5 secondes ne sont pas notre code : c'est l'initialisation de perflib au premier
// `new PerformanceCounter` du processus. Elle se paie une fois, quoi qu'on fasse : la
// seule question est quand. Pendant le scan, ce serait pendant que quelqu'un regarde
// une barre de progression.
//
// Ici, elle se paie au lancement, sur un fil de fond, pendant que l'ecran d'accueil
// s'affiche et que personne n'attend rien. Le scan qui suit trouve les compteurs chauds.
//
// Ne jette jamais. C'est un confort, pas une fonction : si les compteurs sont
// illisibles sur cette machine, le produit doit demarrer exactement comme sans elle, et
// `CpuFrequencyReader` rend deja `null` plutot que de lever. Une amorce qui ferait
// tomber le lancement couterait infiniment plus cher que les 4,5 secondes qu'elle evite.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class AmorceDesCompteurs
{
    /// <summary>Ce qu'on amorce. Couture : le test compte les appels au lieu de chronometrer
    /// une duree, qui dependrait de la machine et de la charge.</summary>
    internal static Action Amorcer { get; set; } = () => new CpuFrequencyReader().Lire();

    /// <summary>Chauffe les compteurs hors du fil d'interface. Rend une tache qu'on peut
    /// attendre dans un test ; au lancement, elle part et personne ne l'attend.</summary>
    public static Task AuLancementAsync() => Task.Run(() =>
    {
        try { Amorcer(); }
        catch { /* un demarrage ne tombe pas pour un confort */ }
    });
}
