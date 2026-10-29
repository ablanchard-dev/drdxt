using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// X3dParkingHeuristic : détection prudente d'un « mauvais parcage des CCD » sur un processeur
// AMD X3D à deux CCD, d'après SettingsState.ParkedCoreIndexes (lu par le scan, jeu ouvert seulement : hors jeu,
// l'optimiseur ne parque rien, et « rien de parqué » ne distingue pas un routage sain d'un cassé).
//
// Sur un X3D à deux CCD (7950X3D / 7900X3D / 9950X3D), CCD0 (index bas) porte la 3D V-Cache, CCD1 (index hauts) la
// haute fréquence ; l'optimiseur AMD 3D V-Cache et la Game Bar parquent les cœurs de CCD1 pendant le jeu. Signalé
// prudemment (un faux positif n'active qu'une voie annulable, et le gain se prouve à la remesure) :
//   * rien de parqué → l'optimiseur ne route rien (le cas cassé classique : les deux CCD
//     actifs, l'ordonnanceur éjecte le jeu du CCD à cache).
//   * un cœur du CCD à V-Cache (index bas, < cœurs par CCD) est parqué → le mauvais CCD est
//     parqué (les jeux seraient poussés sur le CCD fréquence = la régression).
//
// Sain (non signalé) : seuls des cœurs du CCD fréquence (index hauts) sont parqués.
// -----------------------------------------------------------------------
public static class X3dParkingHeuristic
{
    /// <summary>Vrai seulement sur un X3D à deux CCD dont le parcage des cœurs semble faux.</summary>
    public static bool LooksMisparked(CpuInfo cpu, SettingsState state)
    {
        if (!cpu.IsX3D || cpu.CcdCount < 2)
            return false;

        var parked = state.ParkedCoreIndexes;

        // Non lu (null) n'est pas « rien de parque » : ce levier ecrit (AMD3DVCacheSvc, mode Jeu), et un lecteur bouchonne
        // le ferait proposer sur toute machine X3D bi-CCD, saines comprises.
        if (parked is null)
            return false;

        // État cassé n° 1 : rien de parqué → l'optimiseur ne route rien.
        if (parked.Count == 0)
            return true;

        // État cassé n° 2 : un cœur du CCD à V-Cache parqué. Windows numérote un cœur par son premier processeur logique
        // (0, 2, 4… avec l'hyperthreading) : CCD0 possède [0 .. logiques par CCD - 1].
        var logiquesParCcd = Math.Max(1, cpu.Threads / cpu.CcdCount);
        foreach (var index in parked)
        {
            if (index >= 0 && index < logiquesParCcd)
                return true; // un cœur du CCD à cache est parqué → mauvais parcage.
        }

        // Sinon, seuls des cœurs du CCD fréquence sont parqués → sain.
        return false;
    }
}
