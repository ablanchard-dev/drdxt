using System.Runtime.InteropServices;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// Ce lecteur (OpenThread + GetThreadTimes) n'était exercé par aucun test sur le vrai
// système. Un thread qui tourne à plein dans ce processus doit sortir le plus chaud, avec une large part
// d'un thread logique ; un processus inexistant rend null, jamais des zéros.
[Collection(ThreadCpuReaderSurLeVraiSystemeTests.Seul)]
public sealed class ThreadCpuReaderSurLeVraiSystemeTests
{
    // Seul pendant sa mesure : un autre test qui chauffe un thread du même processus prendrait la tête.
    public const string Seul = "Lecteur de threads, sans voisin qui chauffe";

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [Fact]
    public void Un_thread_qui_tourne_a_plein_sort_le_plus_chaud()
    {
        var idNatif = 0;
        using var fin = new CancellationTokenSource();
        var pret = new ManualResetEventSlim();
        var chaud = new Thread(() =>
        {
            idNatif = (int)GetCurrentThreadId();
            pret.Set();
            var x = 0UL;
            while (!fin.IsCancellationRequested) x++;
            GC.KeepAlive(x);
        }) { IsBackground = true };
        chaud.Start();
        pret.Wait();

        try
        {
            var lu = new ThreadCpuReader().Observer(Environment.ProcessId, TimeSpan.FromSeconds(1));

            lu.Should().NotBeNull();
            lu!.ThreadsLogiques.Should().Be(Environment.ProcessorCount);
            lu.ThreadsChauds.Should().NotBeEmpty();
            lu.ThreadsChauds[0].ThreadId.Should().Be(idNatif, "le thread qui boucle est le plus chaud du processus");
            // « plus de 0,3 » mesurait la charge de la machine, pas le lecteur (CI 37738806287 : un serveur
            // partagé ne lui a donné que 0,157). Ce qui se vérifie partout : une part non nulle, dans l'unité d'un thread
            // logique (une erreur d'unité, 100 ns contre ms, la ferait sortir de [0 ; 1,05]).
            lu.ThreadsChauds[0].PartDUnThreadLogique.Should().BeInRange(0.01, 1.05, "une part d'un thread logique, non nulle");
        }
        finally
        {
            fin.Cancel();
            chaud.Join();
        }
    }

    [Fact]
    public void Un_processus_inexistant_rend_null()
        => new ThreadCpuReader().Observer(int.MaxValue - 1, TimeSpan.FromMilliseconds(50)).Should().BeNull();
}

[CollectionDefinition(ThreadCpuReaderSurLeVraiSystemeTests.Seul, DisableParallelization = true)]
public sealed class CollectionThreadCpuReaderSeul { }
