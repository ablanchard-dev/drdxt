using DrDxt.Measurement.Cpu;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Cpu;

// Le relevé de fréquence tourne à côté du banc. Une lecture qui rend aussitôt (compteurs absents, fréquence nominale
// non lue) ne doit pas faire tourner un cœur à plein : il fausserait le banc qu'il accompagne.
public sealed class LeReleveurNeTournePasAVideTests
{
    [Fact]
    public void Une_lecture_qui_rend_aussitot_n_occupe_pas_un_coeur()
    {
        var lectures = 0;
        using var premiere = new ManualResetEventSlim();
        var banc = new RealCpuSustainedBench(() => [0], frequenceMhz: _ => { Interlocked.Increment(ref lectures); premiere.Set(); return null; });
        int debut = 0, fin = 0;
        var fenetre = System.Diagnostics.Stopwatch.StartNew();

        // La fenêtre compte à partir de la première lecture : sur une machine chargée, le relevé peut démarrer en retard,
        // et une fenêtre fixe dès l'appel ne comptait alors aucune lecture. Attente bornée large.
        banc.PendantQue(() =>
        {
            premiere.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("le relevé démarre");
            debut = Volatile.Read(ref lectures);
            fenetre.Restart();
            Thread.Sleep(1000);
            fin = Volatile.Read(ref lectures);
            fenetre.Stop();
            return 0;
        }, [0], 0, out var frequence);

        frequence.Should().BeNull();
        debut.Should().BeGreaterThanOrEqualTo(1);
        (fin - debut).Should().BeLessThanOrEqualTo((int)(fenetre.ElapsedMilliseconds / 250) + 2,
            "une lecture toutes les 250 ms au plus, pas des millions");
    }
}
