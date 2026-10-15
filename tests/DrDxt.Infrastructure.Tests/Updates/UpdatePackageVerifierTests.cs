using System.Security.Cryptography;
using System.Text;
using DrDxt.Core;
using DrDxt.Infrastructure.Updates;
using DrDxt.TestSupport;
using FluentAssertions;
using NSec.Cryptography;

namespace DrDxt.Infrastructure.Tests.Updates;

// -----------------------------------------------------------------------
// La porte de confiance des mises à jour. Échec propre, jamais ouverte.
//
// Matrice prouvée :
//   * manifeste signé par la vraie clé de test (dédiée aux mises à jour) → VerifyManifest Ok
//   * manifeste non signé / signature invalide / champ altéré         → Fail(UPDATE_SIG_INVALID)
//                                                                        (téléchargement refusé)
//   * SHA-256 de la charge ≠ sha256 du manifeste signé                 → Fail(UPDATE_HASH_MISMATCH)
//                                                                        (jamais exécutée)
//   * rétrogradation (version < installée), hors bêta                  → Fail(UPDATE_DOWNGRADE)
//   * toute incertitude                                                → garder la version courante
// Plus : ordre imposé (signature avant charge), forme canonique figée, la clé de mise à jour
// est dédiée (elle ne vérifie pas un manifeste signé par la clé de licence ou de manifeste).
// -----------------------------------------------------------------------
public sealed class UpdatePackageVerifierTests
{
    private const string InstalledVersion = "1.2.0";

    private static UpdateManifest UnsignedManifest(
        UpdateChannel channel = UpdateChannel.App,
        string version = "1.3.0",
        string url = "https://updates.drdxt.example/app/1.3.0.exe",
        string? sha256 = null,
        byte[]? payload = null)
    {
        payload ??= Encoding.UTF8.GetBytes("the-update-payload-bytes");
        sha256 ??= Sha256Hex(payload);
        return new UpdateManifest(channel, version, url, sha256, Signature: string.Empty);
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Signe les octets canoniques d'un manifeste avec une graine privée Ed25519 brute (le test
    // joue le « pipeline de publication »). Même canonisation que le vérificateur, et
    // rend le manifeste avec la signature base64 attachée.
    private static UpdateManifest Signed(UpdateManifest manifest, byte[] seed)
    {
        using var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        var canonical = UpdateVerifier.CanonicalBytes(manifest);
        var sig = SignatureAlgorithm.Ed25519.Sign(key, canonical);
        return manifest with { Signature = Convert.ToBase64String(sig) };
    }

    private static IUpdateVerifier EmbeddedVerifier()
    {
        var created = UpdateVerifier.Create(UpdateDevKeys.UpdatePublicKey);
        created.IsOk.Should().BeTrue("the embedded dev update public key must import cleanly");
        return created.Value!;
    }

    // ── VerifyManifest : porte de signature ───────────────────────────────────────

    [Fact]
    public void VerifyManifest_RealKeySignature_IsOk()
    {
        var manifest = Signed(UnsignedManifest(), UpdateDevKeys.DevPrivateSeed);

        EmbeddedVerifier().VerifyManifest(manifest).IsOk.Should().BeTrue();
    }

    [Fact]
    public void VerifyManifest_AbsentSignature_RefusesDownload_NotOkByDefault()
    {
        // Un manifeste non signé n'est jamais cru : le téléchargement est refusé sur-le-champ.
        var result = EmbeddedVerifier().VerifyManifest(UnsignedManifest());

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void VerifyManifest_NonBase64Signature_Fails()
    {
        var manifest = UnsignedManifest() with { Signature = "not-base64!!!" };

        var result = EmbeddedVerifier().VerifyManifest(manifest);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void VerifyManifest_SignatureFlippedByOneByte_Fails()
    {
        var manifest = Signed(UnsignedManifest(), UpdateDevKeys.DevPrivateSeed);
        var sig = Convert.FromBase64String(manifest.Signature);
        sig[0] ^= 0x01;
        manifest = manifest with { Signature = Convert.ToBase64String(sig) };

        var result = EmbeddedVerifier().VerifyManifest(manifest);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void VerifyManifest_TamperedUrlAfterSigning_Fails()
    {
        var signed = Signed(UnsignedManifest(), UpdateDevKeys.DevPrivateSeed);

        // L'attaquant remplace l'URL de téléchargement mais réutilise l'ancienne signature.
        var tampered = signed with { Url = "https://evil.example/malware.zip" };

        var result = EmbeddedVerifier().VerifyManifest(tampered);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void VerifyManifest_TamperedSha256AfterSigning_Fails()
    {
        var signed = Signed(UnsignedManifest(), UpdateDevKeys.DevPrivateSeed);

        // L'attaquant remplace l'empreinte attendue (pour coller à sa charge) mais réutilise la signature.
        var tampered = signed with { Sha256 = Sha256Hex(Encoding.UTF8.GetBytes("attacker-payload")) };

        EmbeddedVerifier().VerifyManifest(tampered).Error!.Value.Code
            .Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    // Audit securite 26/08 : la signature couvre les 4 champs. Url + Sha256 sont deja
    // testes ci-dessus ; Version et Channel manquaient. Un Version non signe defeat le
    // no-downgrade (rejouer un vieux binaire signe en mentant sur la version) ; un Channel
    // non signe ferait passer un autre canal pour l'App. On tord chacun -> refus.
    [Fact]
    public void VerifyManifest_TamperedVersionAfterSigning_Fails()
    {
        var signed = Signed(UnsignedManifest(version: "1.3.0"), UpdateDevKeys.DevPrivateSeed);
        var tampered = signed with { Version = "9.9.9" };

        EmbeddedVerifier().VerifyManifest(tampered).Error!.Value.Code
            .Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void VerifyManifest_TamperedChannelAfterSigning_Fails()
    {
        var signed = Signed(UnsignedManifest(channel: (UpdateChannel)7), UpdateDevKeys.DevPrivateSeed);
        var tampered = signed with { Channel = UpdateChannel.App };

        EmbeddedVerifier().VerifyManifest(tampered).Error!.Value.Code
            .Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void VerifyManifest_SignedByAnotherKey_Fails()
    {
        using var foreignKey = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var foreignSeed = foreignKey.Export(KeyBlobFormat.RawPrivateKey);

        var manifest = Signed(UnsignedManifest(), foreignSeed);

        EmbeddedVerifier().VerifyManifest(manifest).Error!.Value.Code
            .Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void VerifyManifest_SignedByManifestKey_Fails_UpdateKeyIsDedicated()
    {
        // La clé de signature du manifeste machine ne doit pas pouvoir cautionner un manifeste de
        // mise à jour : le canal de mise à jour est cloîtré derrière sa propre clé.
        var manifest = Signed(UnsignedManifest(), ManifestDevTestKeys.DevPrivateSeed);

        EmbeddedVerifier().VerifyManifest(manifest).Error!.Value.Code
            .Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void Create_CorruptPublicKey_FailsCleanly_NoThrow()
    {
        UpdateVerifier.Create(new byte[16]).Error!.Value.Code
            .Should().Be(UpdateErrorCodes.SignatureInvalid);
        UpdateVerifier.Create(Array.Empty<byte>()).Error!.Value.Code
            .Should().Be(UpdateErrorCodes.SignatureInvalid);
    }

    [Fact]
    public void EmbeddedUpdateVerifier_BuildsAndVerifies()
    {
        var created = UpdateDevKeys.CreateUpdateVerifier();
        created.IsOk.Should().BeTrue();

        var manifest = Signed(UnsignedManifest(), UpdateDevKeys.DevPrivateSeed);
        created.Value!.VerifyManifest(manifest).IsOk.Should().BeTrue();
    }

    // ── VerifyPayload : empreinte + rétrogradation, après que la signature a passé ─────────

    [Fact]
    public void VerifyPayload_MatchingHash_IsOk()
    {
        var payload = Encoding.UTF8.GetBytes("the-update-payload-bytes");
        var manifest = Signed(UnsignedManifest(payload: payload), UpdateDevKeys.DevPrivateSeed);

        EmbeddedVerifier()
            .VerifyPayload(manifest, payload, InstalledVersion, allowDowngrade: false)
            .IsOk.Should().BeTrue();
    }

    [Fact]
    public void VerifyPayload_HashMismatch_Fails_NeverExecuted()
    {
        // Le manifeste est honnêtement signé pour une charge ; une autre charge est téléchargée
        // (remplacée sur le CDN, par exemple). Le sha256 signé ne correspond plus → refuser.
        var manifest = Signed(
            UnsignedManifest(payload: Encoding.UTF8.GetBytes("the-real-payload")),
            UpdateDevKeys.DevPrivateSeed);
        var attackerPayload = Encoding.UTF8.GetBytes("a-tampered-payload");

        var result = EmbeddedVerifier()
            .VerifyPayload(manifest, attackerPayload, InstalledVersion, allowDowngrade: false);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.HashMismatch);
    }

    [Fact]
    public void VerifyPayload_DowngradeOutsideBeta_Fails()
    {
        var payload = Encoding.UTF8.GetBytes("older-version-payload");
        var manifest = Signed(
            UnsignedManifest(version: "1.1.0", payload: payload), UpdateDevKeys.DevPrivateSeed);

        var result = EmbeddedVerifier()
            .VerifyPayload(manifest, payload, InstalledVersion, allowDowngrade: false);

        result.IsOk.Should().BeFalse();
        result.Error!.Value.Code.Should().Be(UpdateErrorCodes.Downgrade);
    }

    [Fact]
    public void VerifyPayload_DowngradeAllowedOnBeta_IsOk()
    {
        var payload = Encoding.UTF8.GetBytes("beta-rollback-payload");
        var manifest = Signed(
            UnsignedManifest(version: "1.1.0", payload: payload), UpdateDevKeys.DevPrivateSeed);

        EmbeddedVerifier()
            .VerifyPayload(manifest, payload, InstalledVersion, allowDowngrade: true)
            .IsOk.Should().BeTrue();
    }

    [Fact]
    public void VerifyPayload_SameVersion_IsNotADowngrade()
    {
        var payload = Encoding.UTF8.GetBytes("same-version-payload");
        var manifest = Signed(
            UnsignedManifest(version: InstalledVersion, payload: payload), UpdateDevKeys.DevPrivateSeed);

        EmbeddedVerifier()
            .VerifyPayload(manifest, payload, InstalledVersion, allowDowngrade: false)
            .IsOk.Should().BeTrue();
    }

    [Fact]
    public void IsDowngrade_NumericComponents_NotLexical()
    {
        // 1.2.10 est plus récent que 1.2.9 (comparaison numérique, pas de chaînes).
        UpdateVerifier.IsDowngrade("1.2.10", "1.2.9").Should().BeFalse();
        UpdateVerifier.IsDowngrade("1.2.9", "1.2.10").Should().BeTrue();
        UpdateVerifier.IsDowngrade("2.0.0", "1.9.9").Should().BeFalse();
        UpdateVerifier.IsDowngrade("1.0.0", "1.0.0").Should().BeFalse();
    }

    // ── Forme canonique : déterminisme et stabilité ───────────────────────────────

    [Fact]
    public void Canonicalize_IsDeterministic_AcrossCalls()
    {
        var m = UnsignedManifest();
        UpdateVerifier.Canonicalize(m).Should().Be(UpdateVerifier.Canonicalize(m));
    }

    [Fact]
    public void Canonicalize_HasPinnedFieldOrderAndShape_ExcludesSignature()
    {
        var m = new UpdateManifest(
            UpdateChannel.App, "1.3.0",
            "https://updates.drdxt.example/app/1.3.0.exe",
            "abc123", Signature: "SHOULD-NOT-APPEAR");

        var canonical = UpdateVerifier.Canonicalize(m);

        canonical.Should().Be(
            "{\"channel\":\"App\",\"version\":\"1.3.0\","
            + "\"url\":\"https://updates.drdxt.example/app/1.3.0.exe\",\"sha256\":\"abc123\"}");
        canonical.Should().NotContain("SHOULD-NOT-APPEAR",
            "the signature field is the signature — it is never part of the signed bytes");
    }
}
