using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests.Updates;

/// <summary>
/// Le manifeste que lisent les App installées est un fichier du dépôt, servi tel quel par le site (docs/updates/app/manifest).
/// Supprimé par une publication, toutes les App liraient « aucune mise à jour publiée » ; mal signé, « un paquet a été
/// refusé ». Ce test le relit comme l'App : présent, signé par la clé de production, et jamais en avance sur le code.
/// </summary>
public sealed class LeManifestePublieEstDansLeDepotTests
{
    private static readonly JsonSerializerOptions CommeLApp = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Le_manifeste_publie_est_present_signe_par_la_cle_de_production_et_pas_en_avance_sur_le_code()
    {
        var chemin = Path.Combine(Racine(), "docs", "updates", "app", "manifest");
        File.Exists(chemin).Should().BeTrue("le site sert ce fichier aux App installées : absent, plus aucune mise à jour n'arrive");

        var manifeste = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllBytes(chemin), CommeLApp)!;
        manifeste.Channel.Should().Be(UpdateChannel.App);
        UpdateTrust.CreateProductionVerifier().Value!.VerifyManifest(manifeste).IsOk
            .Should().BeTrue("une App installée refuse un manifeste que la clé de production ne vérifie pas");
        Uri.TryCreate(manifeste.Url, UriKind.Absolute, out var adresse).Should().BeTrue();
        adresse!.Scheme.Should().Be(Uri.UriSchemeHttps);
        manifeste.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");

        var duCode = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(Path.Combine(Racine(), "Directory.Build.props")), "<Version>([^<]+)</Version>").Groups[1].Value;
        duCode.Should().NotBeEmpty("témoin : la version du code se lit");
        UpdateVerifier.IsDowngrade(duCode, manifeste.Version).Should().BeFalse(
            "le manifeste annonce une version que ce code ne sait pas encore construire");
    }

    [Fact]
    public void Un_manifeste_retouche_ne_se_verifie_plus()
    {
        var manifeste = JsonSerializer.Deserialize<UpdateManifest>(
            File.ReadAllBytes(Path.Combine(Racine(), "docs", "updates", "app", "manifest")), CommeLApp)!;

        UpdateTrust.CreateProductionVerifier().Value!.VerifyManifest(manifeste with { Version = "99.0.0" }).IsOk
            .Should().BeFalse("témoin : la vérification ci-dessus refuse bien ce qu'elle doit refuser");
    }

    private static string Racine()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "DrDXT.sln"))) dossier = dossier.Parent;
        return dossier?.FullName ?? throw new InvalidOperationException("DrDXT.sln not found above the test folder.");
    }
}
