using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.Gpu;

// Le décalage de fréquence de la carte, posé à la main en mode Expert, jamais par l'autopilote.
// Ordre attendu : journal (filet au démarrage), sonde, BAIL du helper, écriture, test de stabilité. Le bail est armé
// avant l'écriture : si DrDXT plante pendant le test, le helper rend la carte seul.
public sealed class ReglageCarteExpertTests : IDisposable
{
    private readonly INvApiGpuTuning _nvApi = Substitute.For<INvApiGpuTuning>();
    private readonly IAdlxGpuTuning _adlx = Substitute.For<IAdlxGpuTuning>();
    private readonly IStabilityTester _stabilite = Substitute.For<IStabilityTester>();
    private readonly IElevatedClient _helper = Substitute.For<IElevatedClient>();
    private readonly List<PrivilegedOp> _envoyees = [];
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-expert-" + Guid.NewGuid().ToString("N"));
    private readonly FichierRevertEnAttente _journal;
    private readonly GpuTuningTargetSource _cibles = new(new GpuTuningTarget(0, 0, 0), new GpuPowerLimitTarget(0, 90));

    public ReglageCarteExpertTests()
    {
        Directory.CreateDirectory(_dossier);
        _journal = new FichierRevertEnAttente(Path.Combine(_dossier, "pending-revert.json"));
        _nvApi.IsAvailable.Returns(true);
        _nvApi.GetCoreClockOffsetMhz(0).Returns(Result<int>.Ok(0));
        _nvApi.GetMemoryClockOffsetMhz(0).Returns(Result<int>.Ok(0));
        _adlx.IsAvailable.Returns(false);
        Stable(StabilityOutcome.Stable);
        _helper.ExecuteAsync(Arg.Do<PrivilegedOp>(_envoyees.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Ok()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* dossier temporaire */ }
    }

    private void Stable(StabilityOutcome verdict) =>
        _stabilite.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<StabilityOutcome>.Ok(verdict)));

    private ReglageCarteExpert Expert() =>
        new(new GpuUndervoltTweak(_nvApi, _adlx, _stabilite, _helper, _cibles), _cibles, _journal);

    // Le helper n'écrit que le cœur : une mémoire demandée serait affichée sans jamais être posée. Refusée avant tout.
    [Fact]
    public async Task Un_decalage_memoire_est_refuse_sans_rien_ecrire()
    {
        (await Expert().PoserAsync(-50, 300)).IsOk.Should().BeFalse();
        _envoyees.Should().BeEmpty();
    }

    [Fact]
    public async Task Le_bail_est_arme_avant_l_ecriture_et_le_journal_porte_de_quoi_revenir()
    {
        var r = await Expert().PoserAsync(coeurMhz: 75);
        r.IsOk.Should().BeTrue(r.Error?.Message);
        await using var essai = r.Value!;

        var ordre = _envoyees.Select(o => o.GetType().Name).ToList();
        ordre.IndexOf(nameof(EstablishLeaseOp)).Should().BeLessThan(
            _envoyees.FindIndex(o => o is ApplyNvApiClockOffsetOp { CoreOffsetMhz: 75 }), "le filet avant la carte");
        var bail = _envoyees.OfType<EstablishLeaseOp>().Single();
        PrivilegedOpCodec.Deserialize(bail.RevertOpJson).Value.Should().Be(new ApplyNvApiClockOffsetOp(0, 0, 0),
            "à l'expiration, le helper rend le décalage d'origine");
        _journal.Lire().Value!.TweakId.Should().Be("gpu.undervolt");
        _journal.Lire().Value!.Snapshot.Should().NotBeNull("le démarrage suivant saurait rendre la carte");
        _cibles.Tuning.CoreOffsetMhz.Should().Be(0, "la cible du joueur ne reste pas dans la source partagée");
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-201)]
    public async Task Hors_des_bornes_rien_n_est_envoye(int coeur)
    {
        var r = await Expert().PoserAsync(coeur);
        r.IsOk.Should().BeFalse();
        _envoyees.Should().BeEmpty();
        _journal.Lire().Value.Should().BeNull();
    }

    [Fact]
    public async Task Sans_bail_la_carte_n_est_pas_touchee()
    {
        _helper.ExecuteAsync(Arg.Any<EstablishLeaseOp>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refusé")));

        var r = await Expert().PoserAsync(75);

        r.IsOk.Should().BeFalse();
        _envoyees.Should().NotContain(new ApplyNvApiClockOffsetOp(0, 75, 0));
        _journal.Lire().Value.Should().BeNull("rien n'a été posé : le journal est levé");
    }

    [Fact]
    public async Task Une_carte_instable_est_rendue_et_le_journal_leve()
    {
        Stable(StabilityOutcome.Unstable);

        var r = await Expert().PoserAsync(75);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakUnstable);
        _envoyees.Last().Should().Be(new ApplyNvApiClockOffsetOp(0, 0, 0), "rendue tout de suite");
        _journal.Lire().Value.Should().BeNull();
    }

    [Fact]
    public async Task Instable_et_retour_rate_le_journal_reste()
    {
        Stable(StabilityOutcome.Unstable);
        var origine = 0;
        _helper.ExecuteAsync(Arg.Is<ApplyNvApiClockOffsetOp>(o => o.CoreOffsetMhz == 0), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(origine++ == 0 ? Result.Ok() : Result.Fail(ErrorCodes.HelperOpFailed, "refusé")));

        var r = await Expert().PoserAsync(75);

        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
        _journal.Lire().Value.Should().NotBeNull("la carte peut rester décalée : le démarrage suivant doit la rendre");
    }

    [Fact]
    public async Task Un_decalage_nul_ne_pose_rien()
    {
        (await Expert().PoserAsync(0)).IsOk.Should().BeFalse();
        _envoyees.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_autre_essai_en_cours_est_nomme_dans_le_refus()
    {
        _journal.Poser("latency.gameModeOn", null);
        var r = await Expert().PoserAsync(75);
        r.Error!.Value.Message.Should().Contain("latency.gameModeOn").And.Contain("encore en essai");
        _envoyees.Should().BeEmpty();
    }

    [Fact]
    public async Task La_cible_partagee_n_est_jamais_modifiee_pendant_l_essai()
    {
        GpuTuningTarget? vuePendant = null;
        Stable(StabilityOutcome.Stable);
        _stabilite.RunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            vuePendant = _cibles.Tuning;
            return Task.FromResult(Result<StabilityOutcome>.Ok(StabilityOutcome.Stable));
        });

        await using var essai = (await Expert().PoserAsync(75)).Value!;

        vuePendant!.CoreOffsetMhz.Should().Be(0, "un scan pendant l'essai lit la cible du profil, pas celle du curseur");
    }

    [Fact]
    public async Task Rendre_remet_l_origine_et_leve_le_journal()
    {
        var r = await Expert().PoserAsync(-100);
        await using var essai = r.Value!;
        _envoyees.Clear();

        (await essai.RendreAsync()).IsOk.Should().BeTrue();

        _envoyees.Should().Contain(new ApplyNvApiClockOffsetOp(0, 0, 0));
        _journal.Lire().Value.Should().BeNull();
    }
}
