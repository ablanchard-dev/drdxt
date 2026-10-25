using DrDxt.GpuNative.PawnIo;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

/// <summary>Lire les ventilateurs par la puce Super I/O coûte ~79 ms de processeur (mesuré sur la machine de référence) :
/// chaque seconde en partie, c'était ~8 % d'un cœur et le bus ISA tenu. La dernière lecture sert pendant 5 s.</summary>
public class DerniereLectureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Une_lecture_sert_pendant_sa_validite_puis_se_refait()
    {
        var lectures = 0;
        var cache = new DerniereLecture<string>(TimeSpan.FromSeconds(5));
        string Lire() => $"l{++lectures}";

        cache.Obtenir(Lire, T0).Should().Be("l1");
        cache.Obtenir(Lire, T0.AddSeconds(1)).Should().Be("l1");
        cache.Obtenir(Lire, T0.AddSeconds(4.9)).Should().Be("l1");
        cache.Obtenir(Lire, T0.AddSeconds(5)).Should().Be("l2");

        lectures.Should().Be(2);
    }

    [Fact]
    public void Une_horloge_qui_recule_relit()
    {
        var lectures = 0;
        var cache = new DerniereLecture<string>(TimeSpan.FromSeconds(5));
        cache.Obtenir(() => $"l{++lectures}", T0);
        cache.Obtenir(() => $"l{++lectures}", T0.AddSeconds(-1)).Should().Be("l2");
    }

    // ~79 ms de processeur par lecture : toutes les 15 s en partie. L'arrêt d'un ventilateur se dit sur deux lectures
    // (VentilateursEnPartie.SecondesArretMinimum, 30 s, figé par VentilateursEnPartieTests).
    [Fact]
    public void Les_ventilateurs_se_lisent_toutes_les_quinze_secondes()
        => PawnIoSuperIo.IntervalleLecture.Should().Be(TimeSpan.FromSeconds(15));
}
