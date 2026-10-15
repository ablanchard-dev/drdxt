using DrDxt.Infrastructure.Updates;
using DrDxt.Server.Manifest;
using FluentAssertions;
using Xunit;

namespace DrDxt.Infrastructure.Tests.Updates;

/// <summary>
/// Les deux moities de la mise a jour se parlent — prouve, pas suppose.
///
/// Le serveur signe une suite d'octets ; le client en recompose une et verifie la signature dessus.
/// Si les deux formes canoniques different d'un seul caractere — un nom de champ, leur ordre, une
/// indentation, l'encodage d'un caractere dans une url — **toutes** les mises a jour sont refusees,
/// en silence et chez le client. C'est la panne la plus chere du produit : elle ne se voit pas
/// depuis le serveur, elle ne casse aucun test unitaire de chaque cote, et personne ne la remarque
/// avant la premiere version publiee.
///
/// Les deux formes ne peuvent pas partager de code : le client cible `net8.0-windows`, le serveur
/// `net8.0`. La duplication est donc SUBIE — ce test la rend gardee. Il fait signer par le vrai
/// signeur du serveur, avec la graine de test, et verifier par le vrai verificateur du client, avec
/// la clef publique reellement embarquee dans l'application.
///
/// C'est le meme motif que `NvApiTuningPolicyBoundSyncTests` pour les bornes GPU : quand une copie
/// est inevitable, ce qui la tient est un test qui compare les deux originaux, jamais une relecture.
/// </summary>
public sealed class TheServerSignsWhatTheClientVerifiesTests
{
    private const string Version = "1.4.0";
    private const string Url = "https://updates.example.invalid/drdxt/1.4.0/DrDXT-Setup.exe";
    private const string Sha256 = "9f2c1b0d4e5a6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8";

    private static string SignLikeTheServer(string channel, string version, string url, string sha256)
    {
        // Le signeur du serveur, tel quel, alimente par la graine de test appariee a la clef
        // publique embarquee. Aucun raccourci : si le serveur change sa facon de signer, ce test
        // change de couleur.
        var signer = new Ed25519ManifestSigningService(new ManifestSigningOptions
        {
            Ed25519PrivateSeedBase64 = Convert.ToBase64String(UpdateDevKeys.DevPrivateSeed),
        });

        var signature = signer.Sign(UpdateManifestEndpoint.CanonicalBytes(channel, version, url, sha256));
        signature.IsOk.Should().BeTrue("la graine de test doit etre acceptee par le signeur");
        return signature.Value!;
    }

    [Fact]
    public void A_manifest_signed_by_the_server_verifies_under_the_key_embedded_in_the_app()
    {
        var signature = SignLikeTheServer("App", Version, Url, Sha256);
        var verifier = UpdateDevKeys.CreateUpdateVerifier();
        verifier.IsOk.Should().BeTrue("la clef publique embarquee doit etre importable");

        var verdict = verifier.Value!.VerifyManifest(
            new UpdateManifest(UpdateChannel.App, Version, Url, Sha256, signature));

        verdict.IsOk.Should().BeTrue(
            "les deux formes canoniques doivent produire les MEMES octets ; sinon toutes les mises "
            + "a jour sont refusees en silence chez le client, et rien cote serveur ne le montre");
    }

    [Fact]
    public void One_field_changed_after_signing_is_refused()
    {
        // Controle POSITIF : sans lui, un verificateur qui dirait toujours oui (clef nulle, chemin
        // d'exception avale) rendrait les deux tests ci-dessus verts sans rien prouver.
        var signature = SignLikeTheServer("App", Version, Url, Sha256);

        UpdateDevKeys.CreateUpdateVerifier().Value!
            .VerifyManifest(new UpdateManifest(UpdateChannel.App, "1.4.1", Url, Sha256, signature))
            .IsOk.Should().BeFalse("une version changee apres coup n'est plus celle qui a ete signee");
    }

    [Fact]
    public void The_channel_itself_is_covered_by_the_signature()
    {
        // Un manifeste signe pour un autre canal ne doit pas pouvoir etre presente comme un manifeste
        // d'application.
        var signature = SignLikeTheServer("Other", Version, Url, Sha256);

        UpdateDevKeys.CreateUpdateVerifier().Value!
            .VerifyManifest(new UpdateManifest(UpdateChannel.App, Version, Url, Sha256, signature))
            .IsOk.Should().BeFalse();
    }
}
