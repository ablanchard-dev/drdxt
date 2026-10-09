using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// 11/09/2026 — la reference de derive (build, pilote) ecrite au commit, et son echec.
//
// Constat du lot precedent : l'App (integrite moyenne) ecrivait ce fichier dans %ProgramData%\DrDxt, dont
// le DACL ne donne aux utilisateurs que la lecture. Le code : JsonReconcilerStateStore.SaveAsync rend Fail
// (IoFailure) — et ApplyReferenceSeedingStore JETAIT ce resultat (`await _sidecar.SaveAsync(...)` sans le
// lire) puis avalait toute exception. Le commit rendait Ok ; rien ne disait que la reference manquait.
//
// La preuve ici : un dossier dont l'ecriture est refusee a l'utilisateur courant (ACE de refus, qui tient
// meme sous un jeton administrateur), la meme situation d'acces refuse que l'App face a la racine admin.
// -----------------------------------------------------------------------
public sealed class ReferenceDeDeriveTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 11, 22, 0, 0, TimeSpan.Zero);

    private static DesiredState UneCorrection() =>
        new(new[] { new AppliedTweak("latency.reflex.lowLatencyMode", "2", "\"1\"", At) });

    [Fact]
    public async Task Un_fichier_de_reference_refuse_a_l_ecriture_ne_passe_plus_en_silence()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "drdxt-reference-refusee-" + Guid.NewGuid().ToString("N"));
        var di = Directory.CreateDirectory(dossier);
        var refus = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.AppendData,
            AccessControlType.Deny);
        var acl = di.GetAccessControl();
        acl.AddAccessRule(refus);
        di.SetAccessControl(acl);
        try
        {
            var sidecar = new JsonReconcilerStateStore(Path.Combine(dossier, "reconciler-state.json"));

            // Le constat : l'ecriture echoue, et le magasin du sidecar le DIT.
            (await sidecar.SaveAsync(ReconcilerState.Default, CancellationToken.None)).IsOk.Should().BeFalse();

            var magasin = new ApplyReferenceSeedingStore(new ManifesteEnMemoire(), sidecar,
                new FakeOsBuildProbe { Build = "22631.4317" }, new FakeGpuDriverProbe(), new DefaultReconcilabilityPolicy());

            var r = await magasin.CommitAsync(UneCorrection());

            r.IsOk.Should().BeFalse("le manifeste est ecrit mais la reference ne l'est pas : le taire ferait croire la derive armee");
            r.Error!.Value.Code.Should().Be(ErrorCodes.ManifestReferenceNotSeeded);
        }
        finally
        {
            acl.RemoveAccessRule(refus);
            di.SetAccessControl(acl);
            try { Directory.Delete(dossier, recursive: true); } catch { /* dossier temporaire */ }
        }
    }

    // ── le helper pose la reference dans le meme geste que le manifeste ─────────────────────────

    private static string Racine() => Path.Combine(Path.GetTempPath(), "drdxt-reference-helper-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Le_helper_pose_la_reference_dans_le_meme_commit_que_le_manifeste()
    {
        var racine = Racine();
        try
        {
            var sidecar = new JsonReconcilerStateStore(Path.Combine(racine, "reconciler-state.json"));
            var r = await new MachineManifestCommit(racine, appliquerAcl: false, new SystemClock(), sidecar,
                new FakeOsBuildProbe { Build = "22631.4317" }, new FakeGpuDriverProbe()).ExecuterAsync(UneCorrection(), repartir: false);

            r.IsOk.Should().BeTrue();
            (await sidecar.LoadAsync(CancellationToken.None)).Value!.Items
                .Single(i => i.TweakId == "latency.reflex.lowLatencyMode").LastSeenBuild.Should().Be("22631.4317");
        }
        finally
        {
            try { Directory.Delete(racine, recursive: true); } catch { /* dossier temporaire */ }
        }
    }

    [Fact]
    public async Task Une_reference_non_posee_laisse_le_manifeste_ecrit_la_cle_publiee_et_l_echec_nomme()
    {
        var racine = Racine();
        try
        {
            var r = await new MachineManifestCommit(racine, appliquerAcl: false, new SystemClock(),
                new InMemoryReconcilerStateStore { FailSave = true },
                new FakeOsBuildProbe { Build = "22631.4317" }, new FakeGpuDriverProbe()).ExecuterAsync(UneCorrection(), repartir: false);

            r.Error!.Value.Code.Should().Be(ErrorCodes.ManifestReferenceNotSeeded);
            File.Exists(Path.Combine(racine, "desired-state.manifest")).Should().BeTrue();
            File.Exists(Path.Combine(racine, DrDxt.Reconciler.Manifest.MachineManifestKey.PublicFileName)).Should().BeTrue(
                "le manifeste est ecrit : sans cle publique, ni l'App ni le service ne le reliraient");
        }
        finally
        {
            try { Directory.Delete(racine, recursive: true); } catch { /* dossier temporaire */ }
        }
    }

    private sealed class ManifesteEnMemoire : IDesiredStateStore
    {
        private DesiredState _etat = new(Array.Empty<AppliedTweak>());

        public Task<Result> CommitAsync(DesiredState state)
        {
            _etat = state;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<DesiredState>> LoadAsync() => Task.FromResult(Result<DesiredState>.Ok(_etat));
    }
}
