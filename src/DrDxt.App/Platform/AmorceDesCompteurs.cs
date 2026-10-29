using System.Runtime.Versioning;
using DrDxt.Detection.Cpu;

namespace DrDxt.App.Platform;

// Amorce des compteurs de performance : payer l'attente pendant que personne n'attend. Mesure du 19/09/2026, en trois
// phases dans un processus neuf :
//     1. resoudre cinq noms de compteurs, tables froides      427 ms
//     2. premier `CpuFrequencyReader.Lire()`                4 543 ms
//     3. le suivant, tout chaud                               11,6 ms
// Les 4,5 s sont l'initialisation de perflib au premier `new PerformanceCounter` du processus, une fois quoi qu'on
// fasse : au lancement, sur un fil de fond pendant l'ecran d'accueil, plutot que pendant la barre du scan.
// Ne jette jamais : compteurs illisibles, le produit demarre comme sans elle (`CpuFrequencyReader` rend deja `null`).
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
