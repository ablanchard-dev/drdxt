using DrDxt.Core;

namespace DrDxt.Infrastructure.Updates;

/// <summary>
/// Le paquet de confiance de production des mises a jour. <see cref="UpdateDevKeys.UpdatePublicKey"/> a sa graine privee
/// dans le depot : verifier avec elle laisserait quiconque a lu le depot signer un manifeste accepte. Sans clef de
/// production, aucun montage ; la clef de dev est refusee meme posee ici par erreur. L'URL du serveur, cette clef et les
/// empreintes SPKI changent ensemble, dans un seul commit, le jour du deploiement.
/// </summary>
public static class UpdateTrust
{
    /// <summary>La clef publique Ed25519 de release (SEC5, tools/publication/signer.cs). Elle ne peut que vérifier.
    /// Créée par <c>cle --creer</c> : la graine privée est chiffrée pour le compte Windows de qui publie ET
    /// par une phrase que lui seul connaît ; elle n'entre jamais dans le dépôt. Changer ces octets, c'est changer de clé :
    /// les App déjà installées refuseraient alors toute mise à jour signée par la nouvelle.
    /// La clef vit ici ; l'URL du serveur est VerificationMiseAJour.UrlParDefaut (la variable <c>DRDXT_UPDATE_URL</c> la coupe, et ne la remplace qu'en Debug).</summary>
    public static byte[]? ProductionPublicKey =>
    [
        0x36, 0xD6, 0x22, 0x1A, 0x49, 0x2F, 0x6C, 0x6F, 0x4F, 0x63, 0xC2, 0xA6, 0x88, 0xB1, 0xAB, 0x9B,
        0x61, 0x5B, 0x68, 0x44, 0xAA, 0x40, 0xFC, 0xB8, 0x87, 0x91, 0x0E, 0x66, 0x48, 0x5C, 0xC4, 0x20,
    ];

    public static Result<IUpdateVerifier> CreateProductionVerifier() => CreateVerifier(ProductionPublicKey);

    /// <summary>Le verificateur pour une clef donnee : refuse l'absence et la clef de dev.</summary>
    public static Result<IUpdateVerifier> CreateVerifier(byte[]? clePublique)
    {
        var refus = Refuser(clePublique);
        return refus.IsOk ? UpdateVerifier.Create(clePublique) : Result<IUpdateVerifier>.Fail(refus.Error!.Value.Code, refus.Error.Value.Message);
    }

    /// <summary>Les refus communs aux deux canaux signés par cette clé (l'App et ses données) : absente, ou de dev.</summary>
    public static Result Refuser(byte[]? clePublique)
    {
        if (clePublique is null || clePublique.Length == 0)
            return Result.Fail(UpdateErrorCodes.NotConfigured,
                "No production update key is embedded; the app stays on its installed version.");

        if (clePublique.AsSpan().SequenceEqual(UpdateDevKeys.UpdatePublicKey)
            || UpdateDevKeys.ClesDevRetirees.Any(r => clePublique.AsSpan().SequenceEqual(r)))
            return Result.Fail(UpdateErrorCodes.NotConfigured,
                "The DEV update key cannot verify production updates: its private seed is public.");

        return Result.Ok();
    }
}
