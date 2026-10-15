using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// L'historique sur une seule ligne du temps, le plus récent d'abord.
public sealed class FriseDeLaMachineTests
{
    [Fact]
    public void Les_faits_se_rangent_du_plus_recent_au_plus_ancien_sur_trente_jours()
    {
        var t = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);
        var frise = FriseDeLaMachine.Construire(
            [new Changement(t.AddHours(-3), "BIOS"), new Changement(t.AddHours(-4), "pose", null, "pose:latency.hagsOn"),
             new Changement(t.AddDays(-40), "vieux")],
            [new PartieRelevee("cod.exe", t.AddHours(-2), t.AddHours(-1), 6, 140, 100, BottleneckState.CpuBoundOrAppBound)],
            [t.AddDays(-17)], [t.AddDays(-5)], t,
            c => c.Libelle, p => p.Jeu, "arrêt", "erreur");

        frise.Select(e => e.Texte).Should().Equal("cod.exe", "BIOS", "erreur", "arrêt");
        frise.Select(e => e.Genre).Should().Equal(GenreDEvenement.Partie, GenreDEvenement.Changement,
            GenreDEvenement.ErreurMaterielle, GenreDEvenement.ArretBrutal);
    }

    [Fact]
    public void La_frise_est_bornee_en_nombre_de_lignes()
    {
        var t = DateTimeOffset.Now;
        var arrets = Enumerable.Range(1, 40).Select(i => t.AddHours(-i)).ToList();
        FriseDeLaMachine.Construire([], [], arrets, [], t, c => "", p => "", "a", "e").Should().HaveCount(FriseDeLaMachine.Lignes);
    }
}
