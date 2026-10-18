using System.Runtime.InteropServices;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// Faire tourner une mesure sur un seul processeur logique, puis rendre au fil l'affinité qu'il avait avant, telle quelle.
/// Un parcours de latence qui change de cœur en route paie un cache froid et un autre chemin vers la mémoire : la
/// dispersion monte sans que la mémoire y soit pour rien. Microsoft Learn, SetThreadAffinityMask : la fonction rend
/// l'ancien masque (0 en cas d'échec), c'est lui qui est rendu, jamais « tous les processeurs ».
/// </summary>
internal static class SurUnSeulCoeur
{
    public static T Executer<T>(Func<T> mesure)
    {
        Thread.BeginThreadAffinity();
        // GetCurrentProcessorNumber rend le numéro dans le groupe du fil : le masque reste dans ce groupe.
        var avant = SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << (int)GetCurrentProcessorNumber());
        try { return mesure(); }
        finally
        {
            if (avant != 0) SetThreadAffinityMask(GetCurrentThread(), avant);
            Thread.EndThreadAffinity();
        }
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
    [DllImport("kernel32.dll")] private static extern nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);
}
