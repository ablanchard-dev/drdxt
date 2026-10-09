using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// X3dParkingHeuristic — conservative detection of "wrong CCD parking" on a
// dual-CCD AMD X3D CPU, from SettingsState.ParkedCoreIndexes (read in M1/P0).
//
// Background: on a dual-CCD X3D (7950X3D / 7900X3D / 9950X3D), one CCD has the
// stacked 3D V-Cache (the gaming CCD) and the other is the high-frequency CCD. The
// AMD 3D V-Cache Performance Optimizer service + Game Bar route games to the
// V-Cache CCD by PARKING the frequency CCD's cores during gaming. By convention the
// V-Cache CCD is CCD0 (the LOW core indexes); the frequency CCD is CCD1 (the HIGH
// indexes).
//
// Broken states this flags (CONSERVATIVE — a false positive only enables a
// supported, fully-reverted software route, and the real gain is PROVEN by the
// engine re-bench, so we still err toward not flagging healthy machines):
//   * nothing parked → the optimizer isn't routing at all (the classic broken
//     state: both CCDs active, the scheduler bounces the game off the cache CCD).
//   * A V-Cache-CCD core (a LOW index, < cores-per-CCD) is parked → the WRONG CCD
//     is parked (games would be pushed onto the frequency CCD = the regression).
//
// Healthy (not flagged): only frequency-CCD cores (HIGH indexes) are parked.
// -----------------------------------------------------------------------
public static class X3dParkingHeuristic
{
    /// <summary>True only on a dual-CCD X3D whose core parking looks wrong.</summary>
    public static bool LooksMisparked(CpuInfo cpu, SettingsState state)
    {
        if (!cpu.IsX3D || cpu.CcdCount < 2)
            return false;

        var parked = state.ParkedCoreIndexes;

        // Parking NON LU (null) n'est pas "rien n'est parque". Ce tweak ecrit
        // (AMD3DVCacheSvc + CppcEnable) : proposer une ecriture sur un signal non lu
        // est exactement ce qu'un lecteur bouchonne rendant toujours une liste vide a
        // fait faire ici, sur toute machine X3D bi-CCD, saines comprises.
        if (parked is null)
            return false;

        // Broken state #1: nothing parked → the optimizer isn't routing.
        if (parked.Count == 0)
            return true;

        // Broken state #2: a V-Cache-CCD (low-index) core is parked = wrong CCD.
        // Cores-per-CCD split evenly across the detected CCDs; the V-Cache CCD0 owns
        // the lowest block of indexes [0 .. coresPerCcd-1].
        var coresPerCcd = Math.Max(1, cpu.Cores / cpu.CcdCount);
        foreach (var index in parked)
        {
            if (index >= 0 && index < coresPerCcd)
                return true; // a cache-CCD core is parked → misparked.
        }

        // Otherwise only frequency-CCD cores are parked → healthy.
        return false;
    }
}
