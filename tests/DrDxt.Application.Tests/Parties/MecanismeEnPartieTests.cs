using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

/// <summary>La veille lit la carte pendant chaque capture, et la partie résumée le porte.</summary>
public sealed class MecanismeEnPartieTests
{
    [Fact]
    public async Task La_carte_lue_pendant_les_captures_arrive_dans_la_partie()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 7), ("cod.exe", 7), null]);
        var parties = new List<PartieRelevee>();
        using var fin = new CancellationTokenSource();
        var lectures = 0;

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: async (_, _, ct) =>
            {
                await Task.Delay(1500, ct); // assez pour au moins une lecture de la carte
                return new BenchmarkResult(140, 98, 70, [], null, 1, 1);
            },
            classer: _ => BottleneckState.GpuBound,
            enregistrer: p => { parties.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            lireCarte: () =>
            {
                lectures++;
                return new HardwareReading(100, 60, 2715, ClocksEventReason.PlafondPuissanceLogiciel);
            });

        await veille.VeillerAsync(fin.Token);

        lectures.Should().BeGreaterThan(0);
        parties.Single().MecanismeCarte.Should().NotBeNull();
        parties.Single().MecanismeCarte!.BridesPuissance.Should().Be(parties.Single().MecanismeCarte!.Echantillons);
    }
}
