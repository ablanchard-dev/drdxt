using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// X3dParkingHeuristic : détection prudente d'un « mauvais parcage des CCD » sur un processeur
// AMD X3D à deux CCD, d'après SettingsState.ParkedCoreIndexes (lu par le scan).
//
// Contexte : sur un X3D à deux CCD (7950X3D / 7900X3D / 9950X3D), un CCD porte la 3D V-Cache
// empilée (le CCD de jeu) et l'autre est le CCD haute fréquence. Le service AMD 3D V-Cache
// Performance Optimizer + la Game Bar envoient les jeux sur le CCD à V-Cache en parquant les
// cœurs du CCD fréquence pendant le jeu. Par convention, le CCD à V-Cache est CCD0 (les index
// de cœur bas) ; le CCD fréquence est CCD1 (les index hauts).
//
// États cassés signalés (prudemment : un faux positif ne fait qu'activer une voie logicielle
// prise en charge et entièrement annulable, et le vrai gain est prouvé par la remesure du
// moteur ; on préfère quand même ne pas signaler une machine saine) :
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

        // Parcage non lu (null) n'est pas « rien n'est parque ». Ce levier ecrit
        // (AMD3DVCacheSvc + CppcEnable) : proposer une ecriture sur un signal non lu
        // est exactement ce qu'un lecteur bouchonne rendant toujours une liste vide
        // ferait faire ici, sur toute machine X3D bi-CCD, saines comprises.
        if (parked is null)
            return false;

        // État cassé n° 1 : rien de parqué → l'optimiseur ne route rien.
        if (parked.Count == 0)
            return true;

        // État cassé n° 2 : un cœur du CCD à V-Cache (index bas) est parqué = mauvais CCD.
        // Les cœurs sont répartis également entre les CCD détectés ; le CCD0 à V-Cache possède
        // le bloc d'index le plus bas [0 .. coresPerCcd-1].
        var coresPerCcd = Math.Max(1, cpu.Cores / cpu.CcdCount);
        foreach (var index in parked)
        {
            if (index >= 0 && index < coresPerCcd)
                return true; // un cœur du CCD à cache est parqué → mauvais parcage.
        }

        // Sinon, seuls des cœurs du CCD fréquence sont parqués → sain.
        return false;
    }
}
