using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

/// <summary>La pause du maintien : un fichier à part sous %ProgramData%\DrDxt, écrit par le helper élevé à la
/// demande du joueur, lu par le réconciliateur seul. Le service ne l'écrit jamais : une passe en cours ne
/// peut pas l'écraser.</summary>
public sealed class PauseDuMaintienTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 6, 23, 10, 0, 0, TimeSpan.Zero);
    private readonly ReconcilerHarness _h;

    public PauseDuMaintienTests()
    {
        var items = new[] { "a", "b" }.Select(id => new ReconcilerItemState(id, false, "22631.3447", "555.99", At, "OK")).ToArray();
        _h = new ReconcilerHarness(ReconcilerState.Default with { Items = items });
        foreach (var id in new[] { "a", "b" }) _h.Policy.Map(id, ReconcileSurface.Registry);
        _h.BuildProbe.Build = "22635.5000"; // build changé : une valeur revenue à l'origine se réapplique
        _h.Probe.Set("a", "0").Set("b", "0");
    }

    public void Dispose()
    {
        try { Directory.Delete(_h.DossierPause, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static DesiredState Voulu()
        => new(new[] { new AppliedTweak("a", "1", "0", At), new AppliedTweak("b", "2", "0", At) });

    [Fact]
    public async Task Une_pause_posee_ne_reapplique_rien_et_n_ecrit_pas_le_sidecar()
    {
        _h.Pause.Poser(true).IsOk.Should().BeTrue();
        var avant = _h.StateStore.Saved;

        var (result, report) = await _h.Build().ReconcileWithReportAsync(Voulu(), default);

        result.IsOk.Should().BeTrue();
        report.GloballyPaused.Should().BeTrue();
        _h.Reapply.Reapplies.Should().BeEmpty();
        _h.StateStore.Saved.Should().Be(avant);
        _h.StateStore.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task La_reprise_reapplique_de_nouveau()
    {
        _h.Pause.Poser(true);
        _h.Pause.Poser(false).IsOk.Should().BeTrue();

        var (_, report) = await _h.Build().ReconcileWithReportAsync(Voulu(), default);

        report.GloballyPaused.Should().BeFalse();
        _h.Reapply.Reapplies.Select(x => x.tweakId).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    // La course que le sidecar aurait eue : le service charge, compare, sauve ; une pause posée entre son
    // chargement et sa sauvegarde serait écrasée par l'ancien état. Le fichier à part n'est jamais réécrit
    // par le service.
    [Fact]
    public async Task Une_pause_posee_pendant_une_passe_survit_a_la_sauvegarde_de_la_passe()
    {
        _h.StoreOverride = new PoseLaPauseALaSauvegarde(_h.StateStore, _h.Pause);

        var (_, pendant) = await _h.Build().ReconcileWithReportAsync(Voulu(), default);
        pendant.GloballyPaused.Should().BeFalse("la pause est arrivée après le début de la passe");

        _h.Pause.Lire().Should().Be(EtatDeLaPause.EnPause);
        var (_, suivante) = await _h.Build().ReconcileWithReportAsync(Voulu(), default);
        suivante.GloballyPaused.Should().BeTrue();
    }

    // La racine est aux administrateurs : un programme du compte ne peut pas y poser ce fichier. Non fiable, il vient d'un
    // fichier abîmé ou d'un propriétaire changé. Dans ce doute, l'échec sûr est de ne rien écrire : une pause à tort
    // n'abîme rien.
    [Fact]
    public async Task Un_fichier_de_pause_non_fiable_vaut_pause_et_c_est_dit_au_journal()
    {
        _h.Pause.Poser(true);
        _h.VerifierConfiancePause = true;

        var (result, report) = await _h.Build().ReconcileWithReportAsync(Voulu(), default);

        result.IsOk.Should().BeTrue();
        report.GloballyPaused.Should().BeTrue();
        _h.Reapply.Reapplies.Should().BeEmpty();
        _h.StateStore.SaveCount.Should().Be(0);
        _h.Diagnostics.Entries.Should().Contain(e => e.tweakId == PauseDuMaintien.NomFichier && e.actor.Contains("untrusted"));
    }

    [Fact]
    public void Sans_fichier_le_maintien_est_actif()
        => _h.Pause.Lire().Should().Be(EtatDeLaPause.Actif);

    private sealed class PoseLaPauseALaSauvegarde(IReconcilerStateStore interne, PauseDuMaintien pause) : IReconcilerStateStore
    {
        public Task<Result<ReconcilerState>> LoadAsync(CancellationToken ct) => interne.LoadAsync(ct);

        public Task<Result> SaveAsync(ReconcilerState state, CancellationToken ct)
        {
            pause.Poser(true);
            return interne.SaveAsync(state, ct);
        }
    }
}
