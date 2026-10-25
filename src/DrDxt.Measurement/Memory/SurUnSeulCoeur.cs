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
        var avant = SetThreadAffinityMask(GetCurrentThread(), Masque(ProcessusPermis(), GetCurrentProcessorNumber()));
        try { return mesure(); }
        finally
        {
            if (avant != 0) SetThreadAffinityMask(GetCurrentThread(), avant);
            Thread.EndThreadAffinity();
        }
    }

    /// <summary>Le même processeur à chaque mesure : le plus bas que le processus a le droit d'utiliser. Sur un Intel hybride,
    /// les P-cores sont numérotés d'abord : le cœur courant pouvait être un P-core pour A et un E-core pour B, et l'écart venait
    /// du cœur. Sans masque du processus lisible, le cœur courant, dans le groupe du fil.</summary>
    internal static nuint Masque(nuint permis, uint courant) =>
        permis != 0 ? permis & (nuint)(-(nint)permis) : (nuint)1 << (int)courant;

    private static nuint ProcessusPermis() =>
        GetProcessAffinityMask(GetCurrentProcess(), out var processus, out _) ? processus : 0;

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(nint hProcess, out nuint lpProcessAffinityMask, out nuint lpSystemAffinityMask);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
    [DllImport("kernel32.dll")] private static extern nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);
}
