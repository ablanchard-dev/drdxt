using System.Diagnostics;
using System.Runtime.InteropServices;
using DrDxt.Core;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// L'horloge haute résolution de Windows (QueryPerformanceCounter), mesurée au lieu d'être supposée : ce que coûte une
/// lecture, et si elle recule quand le fil change de cœur. Une horloge de plateforme forcée (HPET ou minuterie ACPI)
/// rend chaque lecture coûteuse, et un jeu en fait des milliers par image. Lecture seule.
/// </summary>
/// <remarks>
/// Microsoft Learn, « Acquiring high-resolution time stamps » : sur le TSC, une lecture coûte « 10s of CPU cycles to
/// several hundred CPU cycles », sans appel système ; sur une minuterie de la carte, « frequently in the vicinity of 0.8 -
/// 1.0 microseconds » et un appel système. La même page dit de ne pas déduire la source de QueryPerformanceFrequency
/// (souvent fixée à 10 MHz) : seul le coût mesuré tranche. QPC ne recule pas ; entre deux fils, ±1 tic est ambigu.
/// Mesuré sur la machine de référence le 16/10/2026 : 11,6 ns par lecture (cinq séries de deux millions).
/// </remarks>
public static class HorlogeQpc
{
    /// <summary>Au-dessus, la lecture passe par une minuterie de la carte. **Choix** entre les deux ordres de grandeur de
    /// la page Microsoft : plusieurs centaines de cycles restent sous 0,2 µs à 3 GHz, une minuterie de carte est vers
    /// 0,8 µs.</summary>
    public const double SeuilNs = 500;

    public sealed record Mesure(double NsParLecture, int Reculs, int CoeursVisites);

    /// <summary>Sur un fil à part, rendu à la fin : la médiane de cinq séries, puis un tour des cœurs du groupe.</summary>
    public static Mesure? Mesurer()
    {
        Mesure? m = null;
        var fil = new Thread(() =>
        {
            try
            {
                Thread.BeginThreadAffinity();
                const int n = 1_000_000;
                var series = new double[5];
                long puits = 0;
                for (var s = 0; s < series.Length; s++)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < n; i++) puits += Stopwatch.GetTimestamp();
                    series[s] = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / n;
                }
                if (puits == 0) return;
                Array.Sort(series);
                // Le tour des cœurs : le même fil, posé sur chaque processeur logique de son groupe à tour de rôle.
                var coeurs = Math.Min(Environment.ProcessorCount, 64);
                var precedent = Stopwatch.GetTimestamp();
                var reculs = 0;
                var visites = 0;
                for (var c = 0; c < coeurs; c++)
                {
                    if (SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << c) == 0) continue;
                    visites++;
                    var t = Stopwatch.GetTimestamp();
                    if (t < precedent) reculs++;
                    precedent = t;
                }
                m = new Mesure(series[series.Length / 2], reculs, visites);
            }
            catch (Exception) { /* mesure ratée : m reste null, l'horloge est dite non mesurée */ }
            finally { Thread.EndThreadAffinity(); }
        }) { IsBackground = true };
        fil.Start();
        return fil.Join(TimeSpan.FromSeconds(10)) ? m : null;
    }

    /// <summary>Pur. Le coût affiché est celui qui est jugé ; la fréquence de QPC n'entre jamais dans le verdict.</summary>
    public static RepairFinding Juger(Mesure? m)
    {
        if (m is null || m.CoeursVisites == 0)
            return new RepairFinding("CPU", "horloge haute resolution", null, null, RepairStatus.Unknown, Docteur.T("QP.NonLu"));
        var valeur = Docteur.T("QP.Valeur", m.NsParLecture);
        if (m.Reculs > 0)
            return new RepairFinding("CPU", "horloge haute resolution", valeur, null, RepairStatus.Suspicious,
                Docteur.T("QP.Recul", m.Reculs, m.CoeursVisites), RepairAction.Recommandation);
        return m.NsParLecture >= SeuilNs
            ? new RepairFinding("CPU", "horloge haute resolution", valeur, Docteur.T("QP.Attendu", SeuilNs), RepairStatus.Suspicious,
                Docteur.T("QP.Plateforme", m.NsParLecture), RepairAction.Recommandation)
            : new RepairFinding("CPU", "horloge haute resolution", valeur, Docteur.T("QP.Attendu", SeuilNs), RepairStatus.ExpectedDefault,
                Docteur.T("QP.Tsc", m.NsParLecture, m.CoeursVisites));
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);
}
