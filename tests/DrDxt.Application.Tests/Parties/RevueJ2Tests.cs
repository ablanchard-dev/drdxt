using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Les gains affichés doivent tenir dans la vraie vie, pas seulement sur
// l'exemple du test.
public sealed class RevueJ2Tests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-j2-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private static PartieRelevee P(int jour, double low1) =>
        new("cod.exe", new DateTimeOffset(2026, 10, jour, 20, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, jour, 20, 30, 0, TimeSpan.Zero),
            4, 130, low1, BottleneckState.CpuBoundOrAppBound);

    // L'écart des bornes (pire cas) accompagne un verdict tranché ; il est plus prudent que l'écart des médianes.
    [Fact]
    public void Un_verdict_tranche_porte_l_ecart_de_ses_bornes()
    {
        var parties = new[] { P(1, 70), P(2, 72), P(3, 74), P(5, 80), P(6, 82), P(7, 84) };

        var b = ComparaisonDesParties.Bilan(parties, new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));

        b.Verdict.Should().Be(VerdictEnJeu.Mieux);
        b.EcartBornesPercent.Should().BeApproximately((80.0 / 74 - 1) * 100, 1e-9);
        b.EcartBornesPercent.Should().BeLessThan((b.Low1Apres!.Value / b.Low1Avant!.Value - 1) * 100);
    }

    // Figer écrit le bilan tranché une fois ; un bilan déjà figé n'est jamais réécrit ; un « pas encore jugé » non plus.
    [Fact]
    public void Figer_garde_le_premier_bilan_tranche()
    {
        var journal = new JournalDesChangements(Path.Combine(_dossier, "changements.json"));
        var c = new Changement(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), "HAGS");
        journal.Ajouter(c);
        var premier = new BilanDuChangement(VerdictEnJeu.Mieux, "cod.exe", 3, 3, 70, 80, 130, 135, 5);

        journal.Figer(c, new BilanDuChangement(VerdictEnJeu.PasEncoreJuge, "cod.exe", 3, 1, 70, 80, 130, 135));
        journal.Lire().Single().Fige.Should().BeNull();
        journal.Figer(c, premier);
        journal.Figer(c, premier with { Verdict = VerdictEnJeu.Pire });

        journal.Lire().Single().Fige.Should().Be(premier);
    }

    // Une seule capture de 20 s (une vidéo plein écran de passage) n'est pas une partie.
    [Fact]
    public void Une_seule_capture_ne_fait_pas_une_partie()
    {
        CaptureEnPartie Cap(int minute) => new(new DateTimeOffset(2026, 10, 6, 20, minute, 0, TimeSpan.Zero), 120, 80,
            BottleneckState.CpuBoundOrAppBound, null, null, null, null, null);

        ReleveDesParties.Resumer("cod.exe", [Cap(0)]).Should().BeNull();
        ReleveDesParties.Resumer("cod.exe", [Cap(0), Cap(3)]).Should().NotBeNull();
    }
}
