using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.Reconciler;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>Le joueur met le maintien en pause depuis l'App : le helper élevé est le seul à écrire le fichier de pause
/// sous %ProgramData%\DrDxt. L'op ne porte qu'un booléen : ni chemin, ni texte.</summary>
public sealed class PauseDuMaintienParLeHelperTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-pause-helper-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static PrivilegedOpExecutor Executeur(PauseDuMaintien? pause) => new(
        Substitute.For<IRegistryActions>(), Substitute.For<IPowerPlanActions>(), Substitute.For<IRestorePointActions>(),
        Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(), Substitute.For<INvApiTuningActions>(),
        Substitute.For<IADLXTuningActions>(), pauseDuMaintien: pause);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void L_allowlist_admet_la_pause_et_la_reprise_et_le_codec_les_rend_a_l_identique(bool enPause)
    {
        new OpAllowlist().Validate(new SetMaintenancePauseOp(enPause)).IsOk.Should().BeTrue();

        var fil = PrivilegedOpCodec.Serialize(new SetMaintenancePauseOp(enPause));
        PrivilegedOpCodec.Deserialize(fil.Value!).Value.Should().Be(new SetMaintenancePauseOp(enPause));
    }

    [Fact]
    public async Task Pause_puis_reprise_passent_par_le_fichier_que_lit_le_reconciliateur()
    {
        var pause = new PauseDuMaintien(_dossier, verifierConfiance: false);
        var executeur = Executeur(pause);

        (await executeur.ExecuteAsync(new SetMaintenancePauseOp(true), CancellationToken.None)).IsOk.Should().BeTrue();
        pause.Lire().Should().Be(EtatDeLaPause.EnPause);

        (await executeur.ExecuteAsync(new SetMaintenancePauseOp(false), CancellationToken.None)).IsOk.Should().BeTrue();
        pause.Lire().Should().Be(EtatDeLaPause.Actif);
    }

    [Fact]
    public async Task Sans_fichier_branche_l_ordre_echoue_honnetement()
    {
        var r = await Executeur(null).ExecuteAsync(new SetMaintenancePauseOp(true), CancellationToken.None);

        r.IsOk.Should().BeFalse("le service ne se met pas en pause lui-même : seul le helper branche le fichier");
    }
}
