using System.IO;
using System.Net;
using System.Net.Http;
using DrDxt.App.Updates;
using DrDxt.Core;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>La mise a jour branchee dans l'App : ce que le pied de page a le droit de
/// dire, et le demarrage qu'elle n'a pas le droit de tenir.</summary>
public sealed class VerificationMiseAJourTests : IDisposable
{
    // Chaque cas laissait son dossier « drdxt-maj-<id> » dans %TEMP% ; 1 977 trouvés sur
    // le poste de développement, 738 rien que ce jour-là. Ce test range ce qu'il crée.
    private readonly List<string> _dossiers = new();

    public void Dispose()
    {
        foreach (var d in _dossiers)
            try { Directory.Delete(d, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Serveur(TimeSpan attente) : HttpMessageHandler
    {
        public int Requetes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requetes++;
            await Task.Delay(attente, ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);   // « rien a offrir »
        }
    }

    // La clef de DEV n'entre que par ici : en production, le montage exige la clef de release.
    private static async Task<EtatMiseAJour> Verifier(string? url, Serveur serveur, TimeSpan delai) =>
        (await VerificationMiseAJour.VerifierAsync(
            () => VerificationMiseAJour.Monter(url, new HttpClient(serveur), "1.0.0",
                UpdateDevKeys.CreateUpdateVerifier().Value!), delai, CancellationToken.None)).Etat;

    [Fact]
    public void Sans_variable_l_adresse_du_projet_est_lue_et_off_coupe_tout()
    {
        // Sans adresse par défaut, aucune copie installée ne pouvait se mettre à jour.
        VerificationMiseAJour.UrlEffective(null).Should().Be("https://ablanchard-dev.github.io/drdxt");
        VerificationMiseAJour.UrlEffective("off").Should().BeNull("« off » coupe toute requête");
        VerificationMiseAJour.UrlEffective(" OFF ").Should().BeNull();
        // Sous Windows, une variable posée vide n'existe pas : elle ne peut pas servir à couper.
        VerificationMiseAJour.UrlEffective("").Should().Be(VerificationMiseAJour.UrlParDefaut);
        VerificationMiseAJour.UrlEffective("https://exemple.test", adresseRemplacable: true).Should().Be("https://exemple.test");
        VerificationMiseAJour.UrlParDefaut.Should().StartWith("https://");
    }

    // Livrée, l'App ne se laisse pas détourner vers un autre serveur par une variable posée par un autre programme.
    [Fact]
    public void Livree_la_variable_coupe_mais_ne_remplace_pas_l_adresse()
    {
        VerificationMiseAJour.UrlEffective("https://exemple.test", adresseRemplacable: false).Should().Be(VerificationMiseAJour.UrlParDefaut);
        VerificationMiseAJour.UrlEffective("off", adresseRemplacable: false).Should().BeNull();
    }

    [Fact]
    public void Seule_une_version_verifiee_et_complete_est_installable()
    {
        Func<CancellationToken, Task<Result<string>>> telecharger = _ => Task.FromResult(Result<string>.Ok("p.pkg"));
        new ResultatMiseAJour(EtatMiseAJour.NouvelleVersionVerifiee, "abc", "1.3.0", telecharger).Installable.Should().BeTrue();
        new ResultatMiseAJour(EtatMiseAJour.NouvelleVersionVerifiee).Installable.Should().BeFalse();
        new ResultatMiseAJour(EtatMiseAJour.AJour, "abc", "1.3.0", telecharger).Installable.Should().BeFalse();
    }

    // ── L'installeur : l'empreinte est RE-VERIFIEE au clic ──────────────────────────────

    private (string Stage, string Sha, List<string> Lances) Installeur(string contenu)
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"drdxt-maj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        _dossiers.Add(dossier);
        var stage = Path.Combine(dossier, "payload.pkg");
        File.WriteAllText(stage, contenu);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(stage))).ToLowerInvariant();
        return (stage, sha, new List<string>());
    }

    // Depuis une App relancée en administrateur, le setup hériterait du jeton élevé et chargerait ce qui est posé à côté de
    // lui, dans un dossier que le compte peut écrire. Refusé : rien n'est copié, rien n'est lancé.
    [Fact]
    public void Une_App_elevee_ne_lance_pas_l_installeur()
    {
        var (stage, sha, lances) = Installeur("installeur signe 1.3.0");

        var r = InstallationMiseAJour.Lancer(stage, sha, "1.3.0", p => { lances.Add(p); return true; }, eleve: () => true);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(InstallationMiseAJour.CodeEleve);
        r.Error!.Value.Message.Should().Be(DrDxt.Core.Docteur.T("MAJ.InstalleurEleve"));
        lances.Should().BeEmpty();
        File.Exists(Path.Combine(Path.GetDirectoryName(stage)!, "DrDXT-Setup-1.3.0.exe")).Should().BeFalse("rien n'est copié à côté");
    }

    [Fact]
    public void Un_installeur_intact_est_lance_sous_un_nom_exe()
    {
        var (stage, sha, lances) = Installeur("installeur signe 1.3.0");

        InstallationMiseAJour.Lancer(stage, sha, "1.3.0", p => { lances.Add(p); return true; }, eleve: () => false).IsOk.Should().BeTrue();

        lances.Should().ContainSingle().Which.Should().EndWith("DrDXT-Setup-1.3.0.exe");
        File.ReadAllText(lances[0]).Should().Be("installeur signe 1.3.0");
    }

    [Fact]
    public void Un_installeur_remplace_apres_verification_n_est_JAMAIS_lance()
    {
        var (stage, sha, lances) = Installeur("installeur signe 1.3.0");
        File.WriteAllText(stage, "autre chose");   // remplace entre la verification et le clic

        InstallationMiseAJour.Lancer(stage, sha, "1.3.0", p => { lances.Add(p); return true; }, eleve: () => false).IsOk.Should().BeFalse();

        lances.Should().BeEmpty();
    }

    // La relecture de l'empreinte ne suffisait pas : entre elle et
    // Process.Start, le dossier de staging est sous %LOCALAPPDATA%, donc ecrivable par n'importe quel
    // code tournant sous le compte de l'utilisateur. Ce qui partait ensuite sous UAC n'etait plus ce
    // qui avait ete verifie. Le lanceur est appele a l'instant exact du lancement : c'est la que
    // l'attaquant frappe.
    [Fact]
    public void Un_installeur_ne_peut_pas_etre_remplace_entre_l_empreinte_et_le_lancement()
    {
        var (stage, sha, lances) = Installeur("installeur signe 1.3.0");
        Exception? ecriture = null;
        Exception? suppression = null;

        var lancement = InstallationMiseAJour.Lancer(stage, sha, "1.3.0", exe =>
        {
            lances.Add(exe);
            try { File.WriteAllText(exe, "charge de l'attaquant"); }
            catch (Exception ex) { ecriture = ex; }
            try { File.Delete(exe); }
            catch (Exception ex) { suppression = ex; }
            return true;
        }, eleve: () => false);

        lancement.IsOk.Should().BeTrue();
        ecriture.Should().NotBeNull("le binaire lance doit etre verrouille en ecriture pendant le lancement");
        suppression.Should().NotBeNull("un remplacement par suppression-puis-recreation doit echouer aussi");
        File.ReadAllText(lances[0]).Should().Be("installeur signe 1.3.0",
            "ce qui part sous UAC est exactement ce dont l'empreinte a ete relue");
    }

    [Fact]
    public void Un_installeur_disparu_est_refuse()
        => InstallationMiseAJour.Lancer(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pkg"), "abc", "1.3.0", _ => true, eleve: () => false)
            .IsOk.Should().BeFalse();

    /// <summary>La clef de release est embarquée ; avec une URL https, la vérification lit le
    /// manifeste de l'App avec cette clef et rien d'autre ne part. Un serveur qui n'offre rien ⇒ « à jour ».</summary>
    [Fact]
    public async Task Avec_la_clef_de_production_une_URL_https_lit_le_manifeste_et_rien_de_plus()
    {
        var serveur = new Serveur(TimeSpan.Zero);

        (await VerificationMiseAJour.VerifierAsync(
                () => VerificationMiseAJour.Monter("https://updates.example.invalid", new HttpClient(serveur), "1.0.0"),
                TimeSpan.FromSeconds(5), CancellationToken.None)).Etat
            .Should().Be(EtatMiseAJour.AJour);
        serveur.Requetes.Should().Be(1, "le manifeste de l'App, pas un octet de plus");
    }

    [Fact]
    public async Task Sans_serveur_configure_rien_ne_part_sur_le_reseau()
    {
        var serveur = new Serveur(TimeSpan.Zero);

        (await Verifier(null, serveur, TimeSpan.FromSeconds(5))).Should().Be(EtatMiseAJour.NonConfigure);
        serveur.Requetes.Should().Be(0);
    }

    [Fact]
    public async Task Un_serveur_joint_qui_n_offre_rien_dit_a_jour()
    {
        var serveur = new Serveur(TimeSpan.Zero);

        (await Verifier("https://updates.example.invalid", serveur, TimeSpan.FromSeconds(5)))
            .Should().Be(EtatMiseAJour.AJour);
        serveur.Requetes.Should().Be(1, "le canal de l'App est interroge");
    }

    [Fact]
    public async Task Un_serveur_trop_lent_ne_tient_pas_le_demarrage()
    {
        // Chronometre, ce test a encore echoue en CI (7 s pour une borne de 200 ms) : sur un runner
        // charge, le minuteur de la borne attend lui aussi un thread. Meme remede que pour le montage : un serveur
        // qui ne repondrait que dans dix minutes. Rendre la main avant la minute prouve la borne sans mesurer le temps.
        (await Verifier("https://updates.example.invalid", new Serveur(TimeSpan.FromMinutes(10)), TimeSpan.FromMilliseconds(200))
                .WaitAsync(TimeSpan.FromSeconds(60)))
            .Should().Be(EtatMiseAJour.Injoignable, "le delai est une borne, pas une suggestion");
    }

    [Fact]
    public async Task Un_montage_trop_lent_ne_tient_pas_le_demarrage_non_plus()
    {
        // Le montage tournait avant la borne ; la CI l'a vu durer 3 a 7 s. Pas de chronometre
        // ici : sur un runner charge, le minuteur de la borne attend lui aussi un thread (5 s mesurees pour
        // 200 ms). Un montage qui ne finit QUE si la verification a rendu la main prouve la borne sans
        // mesurer le temps : sans elle, la verification l'attend pour toujours.
        using var libere = new ManualResetEventSlim();
        try
        {
            // Dans une tache : un montage hors borne bloquerait sinon le thread du test, et la CI avec lui.
            var resultat = await Task.Run(() => VerificationMiseAJour.VerifierAsync(
                () => { libere.Wait(); return Result<AutoUpdater>.Fail(UpdateErrorCodes.NotConfigured, "lent"); },
                TimeSpan.FromMilliseconds(200), CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(60));

            resultat.Etat.Should().Be(EtatMiseAJour.Injoignable, "la borne couvre aussi le montage");
        }
        finally
        {
            libere.Set();
        }
    }

    private static Result<UpdateOutcome> Ok(UpdateOutcome o) => Result<UpdateOutcome>.Ok(o);
    private static Result<UpdateOutcome> Ko(string code) => Result<UpdateOutcome>.Fail(code, "x");

    [Fact]
    public void Classer_une_version_verifiee_passe_d_abord_un_refus_avant_le_hors_ligne()
    {
        VerificationMiseAJour.Classer(Ko(UpdateErrorCodes.ReplacerUnavailable))
            .Should().Be(EtatMiseAJour.NouvelleVersionVerifiee);
        VerificationMiseAJour.Classer(Ko(UpdateErrorCodes.SignatureInvalid))
            .Should().Be(EtatMiseAJour.Refusee, "un paquet rejete est une information de securite");
        VerificationMiseAJour.Classer(Ko(UpdateErrorCodes.Offline))
            .Should().Be(EtatMiseAJour.Injoignable);
        VerificationMiseAJour.Classer(Ok(UpdateOutcome.UpToDate))
            .Should().Be(EtatMiseAJour.AJour);
        VerificationMiseAJour.Classer(Ko(UpdateErrorCodes.StageFailed))
            .Should().Be(EtatMiseAJour.Echec);
    }

    [Fact]
    public void Chaque_etat_a_son_libelle_dans_les_deux_langues()
    {
        foreach (var etat in Enum.GetValues<EtatMiseAJour>())
        {
            var cle = VerificationMiseAJour.CleResx(etat);
            foreach (var resx in new[] { "Strings.resx", "Strings.fr.resx" })
                File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.Infrastructure", "Resources", resx))
                    .Should().Contain($"name=\"{cle}\"", $"{etat} dans {resx}");
        }
    }

    [Fact]
    public void L_App_verifie_la_mise_a_jour_au_demarrage()
    {
        // La faute que ce lot ferme : un moteur construit et teste que personne n'appelait.
        // L'APPEL au demarrage, pas seulement la methode : une methode jamais appelee est
        // exactement le defaut que ce lot ferme.
        var app = File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.App", "App.xaml.cs"));
        var demarrage = app[app.IndexOf("protected override void OnStartup", StringComparison.Ordinal)..];
        demarrage = demarrage[..demarrage.IndexOf("\n    }", StringComparison.Ordinal)];
        demarrage.Should().Contain("_ = VerifierMisesAJourAsync(scan);");
        app.Should().Contain("VerificationMiseAJour.VerifierAuDemarrageAsync(");
    }

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("racine du depot introuvable");
    }
}
