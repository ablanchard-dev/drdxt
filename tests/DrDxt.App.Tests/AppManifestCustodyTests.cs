using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.App;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Manifest;
using DrDxt.TestSupport;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Garde de la clé du manifeste, côté App. L'hôte a déjà son pendant Release
// (Host_InRelease_FailsClosed_EvenOnAnEmbeddedDevKeySignedManifest) ;
// l'App n'en avait pas, donc retirer sa barrière #if DEBUG ouvrait la porte sans qu'un test rougisse.
// Ces tests reflètent ceux de l'hôte pour AppManifestPublicKey / AppReloadingManifestVerifier :
//   * l'App n'a plus de signataire (le helper élevé signe avec la clé propre à la
//     machine) ; un fichier de clé provisionné portant la clé de dev est refusé dans toute configuration ;
//   * la garde du vérificateur dépend de la configuration : en Release, sans clé provisionnée,
//     le chargeur se ferme et le vérificateur câblé rejette un manifeste signé par la clé de dev ;
//     en Debug la version d'équipe se replie sur la clé embarquée et le vérifie.
// -----------------------------------------------------------------------
public sealed class AppManifestCustodyTests
{
    private static readonly IReconcilabilityPolicy Policy = new DefaultReconcilabilityPolicy();

    // ── Un fichier de cle publique provisionne qui contient la cle de DEV (config-INDEPENDENT) ──
    //
    // L'App ne signe plus (le signataire a graine d'environnement est supprime du produit : c'est le helper
    // eleve qui signe, avec la cle de la machine). Reste la moitie verification : la cle publique lue sous
    // %ProgramData%\DrDxt ne doit jamais etre la cle de dev, dont la graine privee est dans ce depot.

    [Fact]
    public async Task Un_fichier_de_cle_qui_contient_la_cle_de_dev_est_refuse_et_son_manifeste_ne_se_relit_pas()
    {
        var chemin = NoKeyPath();
        await File.WriteAllBytesAsync(chemin, ManifestDevKeys.PublicKey);
        try
        {
            var charge = AppManifestPublicKey.LoadVerifier(chemin, enforceTrust: false);
            charge.IsOk.Should().BeFalse("une cle publique provisionnee egale a la cle de dev n'est jamais crue");
            charge.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);

            // Le verificateur que l'App injecte relit ce fichier et refuse tout, donc un manifeste signe
            // par la cle de dev ne se relit pas.
            var verificateur = new AppReloadingManifestVerifier(chemin, enforceTrust: false);
            (await VerifyOnly(await DevSignedManifestStorageAsync(), verificateur).LoadAsync())
                .IsOk.Should().BeFalse();
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    [Fact]
    public async Task Le_verificateur_de_l_App_voit_la_cle_nee_apres_son_demarrage()
    {
        // La cle de la machine nait au premier commit du helper, alors que l'App tourne deja : la meme
        // instance de verificateur (un singleton de l'injection) doit la voir sans redemarrer.
        var chemin = NoKeyPath();
        var verificateur = new AppReloadingManifestVerifier(chemin, enforceTrust: false);
        using var cle = NSec.Cryptography.Key.Create(NSec.Cryptography.SignatureAlgorithm.Ed25519);
        var octets = Encoding.UTF8.GetBytes("les octets canoniques");
        var signature = NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(cle, octets);

        verificateur.Verify(octets, signature).Should().BeFalse("la cle de la machine n'existe pas encore");

        await File.WriteAllBytesAsync(chemin, cle.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));
        try
        {
            verificateur.Verify(octets, signature).Should().BeTrue("la meme instance voit la cle nee apres elle");
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    [Fact]
    public async Task Temoin_un_fichier_de_cle_de_machine_est_accepte()
    {
        using var cle = NSec.Cryptography.Key.Create(NSec.Cryptography.SignatureAlgorithm.Ed25519);
        var chemin = NoKeyPath();
        await File.WriteAllBytesAsync(chemin, cle.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));
        try
        {
            AppManifestPublicKey.LoadVerifier(chemin, enforceTrust: false).IsOk.Should().BeTrue(
                "sans ce temoin, le refus ci-dessus pourrait etre un chargeur qui refuse tout");
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    // ── garde du vérificateur (dépend de la configuration) ─────────────────────────

#if DEBUG
    [Fact]
    public async Task InDebug_NoProvisionedKey_FallsBackToEmbedded_AndLoadsADevSignedManifest()
    {
        AppManifestPublicKey.LoadVerifier(NoKeyPath()).IsOk.Should().BeTrue(
            "a DEBUG team build falls back to the embedded public key when none is provisioned");

        var storage = await DevSignedManifestStorageAsync();
        var loaded = await VerifyOnly(storage, WiredVerifierWithNoProvisionedKey()).LoadAsync();

        loaded.IsOk.Should().BeTrue("the DEBUG build verifies a dev-key-signed manifest");
        loaded.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("fixit.sanePowerPlan");
    }
#else
    [Fact]
    public void InRelease_NoProvisionedKey_LoadVerifier_FailsClosed()
    {
        var loaded = AppManifestPublicKey.LoadVerifier(NoKeyPath());

        loaded.IsOk.Should().BeFalse("a RELEASE App never trusts the embedded dev key (fail-closed)");
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.SignatureInvalid);
    }

    [Fact]
    public async Task InRelease_WiredVerifier_RejectsADevKeySignedManifest()
    {
        var verifier = WiredVerifierWithNoProvisionedKey();

        var storage = await DevSignedManifestStorageAsync();
        var loaded = await VerifyOnly(storage, verifier).LoadAsync();

        loaded.IsOk.Should().BeFalse("a RELEASE App rejects a dev-key-signed manifest — nothing is reapplied");
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.ManifestTampered);
    }
#endif

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static string NoKeyPath()
        => Path.Combine(Path.GetTempPath(), "DrDxtNoKey_" + Guid.NewGuid().ToString("N") + ".key");

    // Exactement comme AppServiceCollectionExtensions câble le vérificateur de manifeste de l'App.
    private static IManifestVerifier WiredVerifierWithNoProvisionedKey() => new AppReloadingManifestVerifier(NoKeyPath());

    private static async Task<InMemoryManifestStorage> DevSignedManifestStorageAsync()
    {
        var storage = new InMemoryManifestStorage();
        var committing = new SignedDesiredStateStore(
            storage, Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed),
            ManifestDevKeys.CreateVerifier().Value!, Policy, new SystemClock());
        (await committing.CommitAsync(new DesiredState(new[]
        {
            new AppliedTweak("fixit.sanePowerPlan", "high", "balanced", DateTimeOffset.UtcNow),
        }))).IsOk.Should().BeTrue("the dev signer commits a real signed manifest into storage");
        return storage;
    }

    private static SignedDesiredStateStore VerifyOnly(IManifestStorage storage, IManifestVerifier verifier)
        => new(storage, signer: null, verifier, Policy, new SystemClock());

    private sealed class InMemoryManifestStorage : IManifestStorage
    {
        public SignedManifestEnvelope? Slot { get; set; }

        public Task<Result> WriteAsync(SignedManifestEnvelope envelope, CancellationToken ct)
        {
            Slot = envelope;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<SignedManifestEnvelope?>> ReadAsync(CancellationToken ct)
            => Task.FromResult(Result<SignedManifestEnvelope?>.Ok(Slot));
    }
}
