using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Adapters;
using FluentAssertions;
using Microsoft.Win32;

namespace DrDxt.Integration.Tests;

// =======================================================================
// Réconciliation de bout en bout contre une vraie clé de registre (sans élévation).
//
// Toute la boucle de réconciliation est câblée avec les vraies pièces ; la seule chose
// court-circuitée est le transport élevé (pas d'IPC, pas d'UAC) :
//   * SurfaceSettingProbe (vrai) lit la vraie feuille de test HKCU via son lecteur
//     brut Win32 de production,
//   * DefaultReapplyOpMapper (vrai) résout le tweakId → la SetRegistryValueOp typée
//     existante depuis un catalogue de test,
//   * ElevatedReapplyClient (vrai) envoie cette op par un IElevatedClient en processus
//     qui joue directement la vraie feuille d'écriture du registre
//     (Win32RegistryActions.SetValue) : aucune écriture de registre à la main,
//   * JsonReconcilerStateStore (vrai) sur un fichier temporaire (ni ProgramData ni admin).
//
// HKCU est inscriptible par l'utilisateur, donc aucune élévation. La clé de test vit sous
// une sous-clé isolée et unique, supprimée dans un finally pour que les relances restent propres.
//
// Le catalogue et la politique de production sont une table fermée à 3 entrées ; on n'y
// ajoute pas d'identifiant de test : un catalogue et une politique de test portent le seul
// identifiant « integ.test.dword », pour ce test seulement (même canonisation à l'octet près que la sonde de production).
// =======================================================================
[Trait("Category", "Integration")]
[SupportedOSPlatform("windows")]
public sealed class ReconcileEndToEndRegistryTests
{
    private const string TweakId = "integ.test.dword";
    private const string Racine = @"Software\DrDxt\IntegrationTest";
    // Une clé par passage : deux suites lancées en même temps partageaient la clé, et le nettoyage de l'une effaçait toutes
    // ses valeurs, celle de l'autre comprise, au milieu de sa réconciliation.
    private readonly string KeyPath = Racine + @"\" + Guid.NewGuid().ToString("N");
    private const string ValueName = "Value";

    // Les DWORD voulu et d'origine, canonisés dans le JSON exact que la sonde relit
    // (décimal invariant) pour que la comparaison ordinale du réconciliateur tienne.
    private static readonly string DesiredJson = ReconcilableValueCanonicalizer.Dword(7);
    private static readonly string OriginalJson = ReconcilableValueCanonicalizer.Dword(0);
    private static readonly DateTimeOffset AppliedAt = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RealHkcuDrift_IsReconciledBackToDesired_ThenSecondPassIsNoOp()
    {
        // Se retirer proprement sur un runner hors Windows (la surface registre/HKCU est Windows seulement).
        if (!OperatingSystem.IsWindows())
            return;

        // Une feuille isolée unique par passage, pour que des passages concurrents ou répétés n'entrent jamais en collision.
        var uniqueValueName = ValueName + "_" + Guid.NewGuid().ToString("N");
        var catalog = new TestCatalog(KeyPath, uniqueValueName);
        var statePath = Path.Combine(
            Path.GetTempPath(), "drdxt-integ-" + Guid.NewGuid().ToString("N") + ".json");

        // Vraie sonde sur la vraie feuille HKCU ; vrai mappeur sur le même catalogue ; le client
        // élevé en processus joue la vraie feuille d'écriture du registre.
        var probe = new SurfaceSettingProbe(catalog);
        var mapper = new DefaultReapplyOpMapper(catalog);
        var elevated = new CountingInProcessElevatedClient(new Win32RegistryActions());
        var reapply = new ElevatedReapplyClient(mapper, elevated);
        var stateStore = new JsonReconcilerStateStore(statePath);
        var notifier = new RecordingNotifier();
        var diagnostics = new RecordingDiagnostics();
        var policy = new TestPolicy();
        var bootGuard = new BootLoopGuard(new HealthyBootStore());
        // Une sonde de build qui annonce un build différent du LastSeenBuild de l'élément, pour
        // qu'une dérive vers la valeur d'origine soit classée WindowsReset → réappliquer.
        var buildProbe = new FixedBuildProbe("22635.5000");
        var driverProbe = new NoDriverProbe();

        var reconciler = new DesiredStateReconciler(
            probe, buildProbe, driverProbe, stateStore, reapply, notifier, policy,
            diagnostics, bootGuard, new SystemClock(),
            new PauseDuMaintien(Path.Combine(Path.GetTempPath(), "drdxt-pause-" + Guid.NewGuid().ToString("N")), verifierConfiance: false));

        // Amorcer le fichier d'état pour que l'élément soit « connu appliqué » à un build plus
        // ancien (c'est le changement de build qui fait d'une dérive vers l'origine un WindowsReset, pas un choix du joueur).
        var seeded = ReconcilerState.Default with
        {
            Items = new[]
            {
                new ReconcilerItemState(TweakId, Paused: false, LastSeenBuild: "22631.3447",
                    LastSeenDriverVersion: string.Empty, LastReconciledAt: AppliedAt, LastResultCode: "OK"),
            },
        };
        (await stateStore.SaveAsync(seeded, default)).IsOk.Should().BeTrue();

        var desired = new DesiredState(new[]
        {
            new AppliedTweak(TweakId, DesiredJson, OriginalJson, AppliedAt),
        });

        try
        {
            // (1) Appliquer la valeur voulue : écrire le DWORD voulu dans la vraie feuille HKCU via
            //     la vraie feuille d'écriture, et confirmer que la sonde le relit à l'octet près.
            WriteDword(uniqueValueName, 7);
            (await probe.ReadCurrentValueAsync(TweakId, default)).Value
                .Should().Be(DesiredJson, "the seeded value is the canonical desired");

            // (2) Muter la clé « comme Windows » : une mise à jour de fonctionnalité l'a remise à l'origine (0).
            WriteDword(uniqueValueName, 0);

            // (3) Réconcilier : la dérive (0 != 7) est classée WindowsReset et réappliquée.
            var first = await reconciler.ReconcileWithReportAsync(desired, default);

            first.result.IsOk.Should().BeTrue();
            ReadDword(uniqueValueName).Should().Be(7, "the reconciler RESTORED the real key to desired");
            first.report.Items.Should().ContainSingle()
                .Which.Outcome.Should().Be(ItemOutcome.Reapplied);
            elevated.ExecuteCount.Should().Be(1, "exactly one real registry write was issued");
            diagnostics.Changes.Should().ContainSingle(c =>
                c.TweakId == TweakId && c.To == DesiredJson && c.Actor == "DrDXT");

            // (4) Second passage sur la clé désormais correcte → strictement rien : aucune nouvelle écriture.
            var second = await reconciler.ReconcileWithReportAsync(desired, default);

            second.result.IsOk.Should().BeTrue();
            second.report.Items.Should().ContainSingle()
                .Which.Outcome.Should().Be(ItemOutcome.NoOp);
            elevated.ExecuteCount.Should().Be(1,
                "idempotence on a REAL key: the second pass issued ZERO new writes");
            ReadDword(uniqueValueName).Should().Be(7, "the key is unchanged by the no-op pass");
        }
        finally
        {
            // Négatif et nettoyage : la valeur de test isolée et (si vide) la clé sont retirées.
            DeleteTestArtifacts();
            if (File.Exists(statePath))
                File.Delete(statePath);
        }

        // Assertion de nettoyage : rien de nous ne reste sous HKCU.
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        key.Should().BeNull("the test key was deleted on cleanup");
    }

    // ---- aides pour le vrai registre (HKCU est inscriptible par l'utilisateur ; aucune élévation) ----

    private void WriteDword(string valueName, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private int ReadDword(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        key.Should().NotBeNull();
        return (int)key!.GetValue(valueName)!;
    }

    private void DeleteTestArtifacts()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);

        // Les parents, créés en passant par CreateSubKey, restaient vides dans le registre de la machine de test. Le produit
        // n'utilise pas ces clés : vides, elles partent. Le système refuse d'effacer une clé qui a des sous-clés : celle d'un
        // passage concurrent la garde.
        foreach (var parent in new[] { Racine, @"Software\DrDxt" })
        {
            using (var cle = Registry.CurrentUser.OpenSubKey(parent))
                if (cle is not { ValueCount: 0, SubKeyCount: 0 }) return;
            try { Registry.CurrentUser.DeleteSubKey(parent, throwOnMissingSubKey: false); }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException) { return; }
        }
    }

    // ---- catalogue et politique de test portant le seul identifiant integ ----

    private sealed class TestCatalog : IReconcilableTweakCatalog
    {
        private readonly ReconcilableDescriptor _descriptor;

        public TestCatalog(string keyPath, string valueName)
            => _descriptor = new ReconcilableDescriptor(
                TweakId,
                ReconcileSurface.Registry,
                Registry: new RegistryDescriptor(
                    RegistryHiveKind.CurrentUser, keyPath, valueName, RegistryValueType.DWord));

        public ReconcilableDescriptor? DescriptorFor(string tweakId)
            => string.Equals(tweakId, TweakId, StringComparison.Ordinal) ? _descriptor : null;
    }

    private sealed class TestPolicy : IReconcilabilityPolicy
    {
        public ReconcileSurface? SurfaceFor(string tweakId)
            => string.Equals(tweakId, TweakId, StringComparison.Ordinal)
                ? ReconcileSurface.Registry
                : null;

        public bool IsProtected(string tweakId) => false;
    }

    // ---- le client élevé en processus : vraie feuille d'écriture, pas d'IPC, qui compte ----

    private sealed class CountingInProcessElevatedClient : IElevatedClient
    {
        private readonly IRegistryActions _registry;

        public CountingInProcessElevatedClient(IRegistryActions registry) => _registry = registry;

        public int ExecuteCount { get; private set; }

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            ExecuteCount++;
            // Exercer le vrai chemin op → registre (la même feuille que le produit livre) ; jamais l'IPC.
            return Task.FromResult(op switch
            {
                SetRegistryValueOp r => _registry.SetValue(r.Hive, r.Path, r.Name, r.Kind, r.Value),
                _ => Result.Fail(ErrorCodes.HelperOpFailed, $"Unexpected op {op.GetType().Name} in T35."),
            });
        }
    }

    // ---- coutures enregistreuses / fixes ----

    private sealed record DiagChange(string TweakId, string From, string To, string Actor);

    private sealed class RecordingDiagnostics : IDiagnosticsLog
    {
        public List<DiagChange> Changes { get; } = new();

        public void Change(string tweakId, string from, string to, string actor)
            => Changes.Add(new DiagChange(tweakId, from, to, actor));

        public Task<Result<string>> ExportAsync()
            => Task.FromResult(Result<string>.Ok(string.Empty));
    }

    private sealed class RecordingNotifier : IReconcilerNotifier
    {
        public int Count { get; private set; }

        public Task NotifyUserIntentionalDriftAsync(AppliedTweak tweak, string currentValueJson, CancellationToken ct)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedBuildProbe : IOsBuildProbe
    {
        private readonly string _build;
        public FixedBuildProbe(string build) => _build = build;
        public Result<string> ReadCurrentBuild() => Result<string>.Ok(_build);
    }

    private sealed class NoDriverProbe : IGpuDriverProbe
    {
        public Result<string?> ReadDriverVersion(string vendor) => Result<string?>.Ok(null);
    }

    private sealed class HealthyBootStore : IBootOutcomeStore
    {
        public Task<Result<int>> GetUncleanStreakAsync(CancellationToken ct)
            => Task.FromResult(Result<int>.Ok(0));
        public Task<Result> MarkBootStartedAsync(CancellationToken ct) => Task.FromResult(Result.Ok());
        public Task<Result> MarkSessionStableAsync(CancellationToken ct) => Task.FromResult(Result.Ok());
    }
}
