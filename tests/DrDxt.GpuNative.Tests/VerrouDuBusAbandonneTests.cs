using DrDxt.GpuNative.PawnIo;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// Un outil (HWiNFO...) qui meurt en tenant le verrou du bus ISA le laisse « abandonné » : WaitOne lève
// AbandonedMutexException EN AYANT pris le verrou. L'exception sortait avant le ReleaseMutex, et le fil du service SYSTEM
// gardait le bus indéfiniment : plus aucun outil de ventilateurs ne le prenait. Abandonné = pris, puis relâché normalement.
public sealed class VerrouDuBusAbandonneTests
{
    [Fact]
    public void Un_verrou_abandonne_est_pris_puis_se_relache()
    {
        var nom = $@"Local\drdxt-test-{Guid.NewGuid():N}";
        var fil = new Thread(() => new Mutex(false, nom).WaitOne()); // pris, jamais relâché : abandonné à la fin du fil
        fil.Start();
        fil.Join();

        using var verrou = new Mutex(false, nom);
        PawnIoSuperIo.Prendre(verrou, TimeSpan.FromMilliseconds(500)).Should().BeTrue();
        verrou.ReleaseMutex();

        var autre = false;
        var t = new Thread(() => { using var m = new Mutex(false, nom); autre = m.WaitOne(500); if (autre) m.ReleaseMutex(); });
        t.Start();
        t.Join();
        autre.Should().BeTrue("relâché, un autre outil le reprend");
    }
}
