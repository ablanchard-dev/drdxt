using DrDxt.AmdHarness.Reporting;
using FluentAssertions;

namespace DrDxt.AmdHarness.Tests;

// Le rapport chez un joueur ne s'arrête jamais : une étape qui plante ou qui pend est notée, la suite part.
public sealed class RapportLectureTests
{
    [Fact]
    public async Task Une_etape_qui_plante_ou_qui_pend_n_arrete_pas_les_suivantes()
    {
        var etapes = new RapportLecture.Etape[]
        {
            new("plante", _ => throw new InvalidOperationException("pilote absent")),
            new("pend", async ct => { await Task.Delay(Timeout.Infinite, CancellationToken.None); return "jamais"; }),
            new("répond", _ => Task.FromResult("carte AMD trouvée")),
        };

        var texte = await RapportLecture.DeroulerAsync(etapes, TimeSpan.FromMilliseconds(200));

        texte.Should().StartWith("Étapes réussies : 1 sur 3");
        texte.Should().Contain("=== plante — ERREUR").And.Contain("pilote absent");
        texte.Should().Contain("=== pend — DÉLAI DÉPASSÉ");
        texte.Should().Contain("=== répond — OK").And.Contain("carte AMD trouvée");
    }

    // Chaque bloc s'écrit dès que son étape finit.
    [Fact]
    public async Task Chaque_etape_s_ecrit_des_qu_elle_finit()
    {
        var ecrits = new List<string>();
        var etapes = new RapportLecture.Etape[]
        {
            new("un", _ => Task.FromResult("a")),
            new("deux", _ => { ecrits.Should().ContainSingle("le bloc d'avant est déjà écrit"); throw new InvalidOperationException("x"); }),
        };

        await RapportLecture.DeroulerAsync(etapes, TimeSpan.FromSeconds(5), ecrireBloc: ecrits.Add);

        ecrits.Should().HaveCount(2);
        ecrits[0].Should().StartWith("=== un — OK");
        ecrits[1].Should().StartWith("=== deux — ERREUR");
    }

    [Fact]
    public void Le_dossier_du_rapport_existe_toujours()
        => Directory.Exists(RapportLecture.Dossier()).Should().BeTrue();
}
