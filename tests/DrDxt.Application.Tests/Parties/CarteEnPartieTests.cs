using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// La carte qui a porté la partie, et la vraie carte laissée de côté.
public sealed class CarteEnPartieTests
{
    private static CaptureEnPartie C(string? carte, string? ignoree)
        => new(DateTimeOffset.Now, 140, 90, BottleneckState.GpuBound, CarteDuJeu: carte, CarteDedieeIgnoree: ignoree);

    [Fact]
    public void La_partie_garde_la_carte_la_plus_vue_et_la_vraie_carte_ignoree_des_qu_une_capture_la_voit()
    {
        var p = ReleveDesParties.Resumer("jeu.exe", [C("UHD", "RTX"), C("RTX", null), C("UHD", "RTX")])!;
        p.CarteDuJeu.Should().Be("UHD");
        p.CarteDedieeIgnoree.Should().Be("RTX");
        ReleveDesParties.Resumer("jeu.exe", [C("RTX", null), C("RTX", null)])!.CarteDedieeIgnoree.Should().BeNull();
    }

    [Fact]
    public async Task La_veille_lit_la_carte_du_jeu_a_chaque_capture()
    {
        var etats = new Queue<(string, int)?>([("jeu.exe", 42), ("jeu.exe", 42), null]);
        var enregistrees = new List<PartieRelevee>();
        using var fin = new CancellationTokenSource();
        var veille = new VeilleDesParties(
            jeu: () => { if (etats.Count == 0) fin.Cancel(); return etats.Count > 0 ? etats.Dequeue() : null; },
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(new BenchmarkResult(140, 98, 70, [], null, 1, 1)),
            classer: _ => BottleneckState.GpuBound,
            enregistrer: enregistrees.Add,
            patienter: (_, _) => Task.CompletedTask,
            carteDuJeu: pid => pid == 42 ? ("UHD", "RTX") : null);

        await veille.VeillerAsync(fin.Token);

        enregistrees.Single().CarteDedieeIgnoree.Should().Be("RTX");
    }
}
