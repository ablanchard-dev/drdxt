using System.Runtime.InteropServices;
using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

// Notre règle : la mesure tourne sur un seul cœur, puis le fil retrouve l'affinité qu'il avait, pas « tous les processeurs ».
public sealed class SurUnSeulCoeurTests
{
    [Fact]
    public void Pendant_la_mesure_un_seul_coeur_puis_l_affinite_d_origine_est_rendue()
    {
        if (Environment.ProcessorCount < 3) return;
        (nuint pendant, nuint apres) resultat = default;
        var fil = new Thread(() =>
        {
            Thread.BeginThreadAffinity();
            try
            {
                const nuint origine = 0b110;
                var initiale = SetThreadAffinityMask(GetCurrentThread(), origine);
                var pendant = SurUnSeulCoeur.Executer(() =>
                {
                    var m = SetThreadAffinityMask(GetCurrentThread(), origine);
                    SetThreadAffinityMask(GetCurrentThread(), m);
                    return m;
                });
                var apres = SetThreadAffinityMask(GetCurrentThread(), initiale);
                resultat = (pendant, apres);
            }
            finally { Thread.EndThreadAffinity(); }
        });
        fil.Start();
        fil.Join();

        System.Numerics.BitOperations.PopCount((ulong)resultat.pendant).Should().Be(1);
        resultat.apres.Should().Be((nuint)0b110);
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
}
