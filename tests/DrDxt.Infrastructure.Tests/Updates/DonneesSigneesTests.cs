using System.Net;
using System.Net.Http;
using DrDxt.Core;
using DrDxt.Infrastructure.Updates;
using FluentAssertions;
using NSec.Cryptography;
using DrDxt.TestSupport;

namespace DrDxt.Infrastructure.Tests.Updates;

/// <summary>
/// Les tables reçues sans changer de binaire : signées par la clé des mises à jour, versionnées dans la signature,
/// refusées en entier au moindre écart, et jamais plus anciennes que ce qui est en service.
/// </summary>
public sealed class DonneesSigneesTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-donnees-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Contenu =
        """{"publie":"2026-10-17","radar":[{"id":"exemple-1","kb":"KB5063878","fr":"Phrase.","en":"Sentence.","source":"https://learn.microsoft.com/x","releve":"2026-10-16"}]}""";

    private static DonneesSignees.Paquet Signer(int version, string contenu = Contenu, byte[]? graine = null)
    {
        using var cle = Key.Import(SignatureAlgorithm.Ed25519, graine ?? UpdateDevTestKeys.DevPrivateSeed, KeyBlobFormat.RawPrivateKey);
        var signature = SignatureAlgorithm.Ed25519.Sign(cle, DonneesSignees.OctetsSignes(version, contenu));
        return new DonneesSignees.Paquet(version, contenu, Convert.ToBase64String(signature));
    }

    private static DonneesSignees.Verificateur Dev() => DonneesSignees.Verificateur.Importer(UpdateDevKeys.UpdatePublicKey).Value!;

    [Fact]
    public void Un_paquet_signe_se_verifie_et_rend_ses_tables()
    {
        var tables = Dev().Verifier(Signer(4), plancher: 1);
        tables.IsOk.Should().BeTrue(tables.Error?.Message);
        tables.Value!.Version.Should().Be(4);
        tables.Value.Radar.Should().ContainSingle();
    }

    [Fact]
    public void Un_contenu_modifie_apres_signature_est_refuse()
    {
        var p = Signer(4);
        Dev().Verifier(p with { Contenu = p.Contenu.Replace("Phrase.", "Autre.") }, 1).IsOk.Should().BeFalse();
    }

    [Fact]
    public void La_version_est_dans_les_octets_signes()
    {
        // Changer le numéro sans resigner : c'est ainsi qu'on ferait passer un vieux paquet pour un neuf.
        var p = Signer(2);
        Dev().Verifier(p with { Version = 9 }, 1).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Un_paquet_plus_ancien_que_le_plancher_est_refuse()
    {
        var r = Dev().Verifier(Signer(2), plancher: 3);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(UpdateErrorCodes.Downgrade);
    }

    [Fact]
    public void Une_autre_cle_ne_signe_pas_des_donnees_acceptees()
    {
        using var autre = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        Dev().Verifier(Signer(4, graine: autre.Export(KeyBlobFormat.RawPrivateKey)), 1).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Une_signature_faite_hors_du_domaine_des_donnees_ne_vaut_pas_pour_des_donnees()
    {
        // Les deux canaux partagent la clé : sans le domaine en tête, une signature posée par un autre usage de la clé
        // sur « version, contenu » passerait pour un paquet de données.
        using var cle = Key.Import(SignatureAlgorithm.Ed25519, UpdateDevTestKeys.DevPrivateSeed, KeyBlobFormat.RawPrivateKey);
        var sansDomaine = System.Text.Encoding.UTF8.GetBytes("1\n" + Contenu);
        var signature = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(cle, sansDomaine));

        DonneesSignees.OctetsSignes(1, Contenu).Take(DonneesSignees.Domaine.Length).Should().Equal(DonneesSignees.Domaine.ToArray());
        Dev().Verifier(new DonneesSignees.Paquet(1, Contenu, signature), 1).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Un_paquet_signe_mais_hors_schema_est_refuse_en_entier()
    {
        // Signé par la bonne clé, il transporte quand même une clé « script » : le schéma fermé le refuse.
        var contenu = Contenu.Replace("\"radar\":", "\"script\":\"Start-Process cmd\",\"radar\":");
        Dev().Verifier(Signer(4, contenu), 1).IsOk.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"Version":1,"Contenu":"{}","Signature":"AA==","Executable":"TVqQ"}""")]
    [InlineData("""{"Version":1,"Contenu":"{}"}""")]
    [InlineData("""{"Version":"1","Contenu":"{}","Signature":"AA=="}""")]
    [InlineData("""{"Version":0,"Contenu":"{}","Signature":"AA=="}""")]
    [InlineData("pas du json")]
    public void Le_paquet_porte_exactement_ses_trois_champs(string json)
    {
        DonneesSignees.Lire(json).IsOk.Should().BeFalse();
    }

    [Fact]
    public void La_production_refuse_la_cle_de_dev_et_l_absence_de_cle()
    {
        DonneesSignees.Verificateur.Pour(UpdateDevKeys.UpdatePublicKey).IsOk.Should().BeFalse();
        DonneesSignees.Verificateur.Pour(null).IsOk.Should().BeFalse();
        DonneesSignees.Verificateur.DeProduction().IsOk.Should().BeTrue("la clé de release est embarquée");
    }

    [Fact]
    public void Au_demarrage_le_paquet_garde_est_repris_s_il_se_verifie()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Path.Combine(_dossier, DonneesSignees.Fichier), DonneesSignees.Ecrire(Signer(5)));
        DonneesSignees.Charger(_dossier, Dev()).Version.Should().Be(5);
    }

    [Fact]
    public void Un_paquet_garde_abime_cede_la_place_au_precedent_puis_aux_tables_embarquees()
    {
        Directory.CreateDirectory(_dossier);
        var abime = Signer(5) with { Contenu = Contenu.Replace("Phrase.", "Changée.") };
        File.WriteAllText(Path.Combine(_dossier, DonneesSignees.Fichier), DonneesSignees.Ecrire(abime));
        File.WriteAllText(Path.Combine(_dossier, DonneesSignees.FichierPrecedent), DonneesSignees.Ecrire(Signer(4)));
        DonneesSignees.Charger(_dossier, Dev()).Version.Should().Be(4);

        File.Delete(Path.Combine(_dossier, DonneesSignees.FichierPrecedent));
        DonneesSignees.Charger(_dossier, Dev()).Should().BeSameAs(TablesDeDonnees.Embarquees);
    }

    private sealed class Serveur(HttpStatusCode code, string corps) : HttpMessageHandler
    {
        public string? Demande;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Demande = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(corps) });
        }
    }

    private static readonly Uri Racine = new("https://ablanchard-dev.github.io/drdxt");

    [Fact]
    public async Task Un_paquet_plus_recent_est_garde_et_l_ancien_devient_le_precedent()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Path.Combine(_dossier, DonneesSignees.Fichier), DonneesSignees.Ecrire(Signer(4)));
        var serveur = new Serveur(HttpStatusCode.OK, DonneesSignees.Ecrire(Signer(6)));
        var courantes = DonneesSignees.Charger(_dossier, Dev());

        var (issue, tables) = await DonneesSignees.DemanderAsync(new HttpClient(serveur), Racine, _dossier, Dev(), courantes, CancellationToken.None);

        serveur.Demande.Should().Be("https://ablanchard-dev.github.io/drdxt/donnees/paquet.json", "la racine garde son chemin");
        issue.Should().Be(DonneesSignees.Issue.Installe);
        tables!.Version.Should().Be(6);
        DonneesSignees.Charger(_dossier, Dev()).Version.Should().Be(6);
        DonneesSignees.Lire(File.ReadAllText(Path.Combine(_dossier, DonneesSignees.FichierPrecedent))).Value!.Version.Should().Be(4);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lien, string cible, IntPtr securite);

    // L'ancien paquet passait au précédent par File.Copy : un nom posé d'avance en lien dur faisait écrire dans un autre
    // fichier. Le renommage de l'écriture atomique remplace le nom seul.
    [Fact]
    public async Task Le_precedent_ne_s_ecrit_jamais_a_travers_un_lien_dur()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Path.Combine(_dossier, DonneesSignees.Fichier), DonneesSignees.Ecrire(Signer(4)));
        var temoin = Path.Combine(Path.GetTempPath(), "drdxt-temoin-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(temoin, "intact");
        try
        {
            CreateHardLink(Path.Combine(_dossier, DonneesSignees.FichierPrecedent), temoin, IntPtr.Zero).Should().BeTrue();
            var courantes = DonneesSignees.Charger(_dossier, Dev());

            var (issue, _) = await DonneesSignees.DemanderAsync(new HttpClient(new Serveur(HttpStatusCode.OK, DonneesSignees.Ecrire(Signer(6)))),
                Racine, _dossier, Dev(), courantes, CancellationToken.None);

            issue.Should().Be(DonneesSignees.Issue.Installe);
            File.ReadAllText(temoin).Should().Be("intact");
            DonneesSignees.Lire(File.ReadAllText(Path.Combine(_dossier, DonneesSignees.FichierPrecedent))).Value!.Version.Should().Be(4);
        }
        finally { File.Delete(temoin); }
    }

    [Fact]
    public async Task Un_paquet_plus_ancien_que_celui_en_service_n_est_pas_garde()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Path.Combine(_dossier, DonneesSignees.Fichier), DonneesSignees.Ecrire(Signer(6)));
        var courantes = DonneesSignees.Charger(_dossier, Dev());

        var (issue, _) = await DonneesSignees.DemanderAsync(new HttpClient(new Serveur(HttpStatusCode.OK, DonneesSignees.Ecrire(Signer(5)))),
            Racine, _dossier, Dev(), courantes, CancellationToken.None);

        issue.Should().Be(DonneesSignees.Issue.Refuse);
        DonneesSignees.Charger(_dossier, Dev()).Version.Should().Be(6);
    }

    // Le plancher d'un paquet neuf est la version en service plus un : à int.MaxValue, il repassait sous zéro et tout
    // paquet signé plus ancien était repris.
    [Fact]
    public async Task Au_bout_des_versions_un_paquet_plus_ancien_reste_refuse()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Path.Combine(_dossier, DonneesSignees.Fichier), DonneesSignees.Ecrire(Signer(int.MaxValue)));
        var courantes = DonneesSignees.Charger(_dossier, Dev());
        courantes.Version.Should().Be(int.MaxValue);

        var (issue, _) = await DonneesSignees.DemanderAsync(new HttpClient(new Serveur(HttpStatusCode.OK, DonneesSignees.Ecrire(Signer(5)))),
            Racine, _dossier, Dev(), courantes, CancellationToken.None);

        issue.Should().Be(DonneesSignees.Issue.Refuse);
        DonneesSignees.Charger(_dossier, Dev()).Version.Should().Be(int.MaxValue);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, DonneesSignees.Issue.RienDePublie)]
    [InlineData(HttpStatusCode.InternalServerError, DonneesSignees.Issue.Injoignable)]
    public async Task Rien_de_publie_ou_un_serveur_en_panne_laissent_les_tables_en_service(HttpStatusCode code, DonneesSignees.Issue attendue)
    {
        var (issue, tables) = await DonneesSignees.DemanderAsync(new HttpClient(new Serveur(code, "")), Racine, _dossier, Dev(),
            TablesDeDonnees.Embarquees, CancellationToken.None);
        issue.Should().Be(attendue);
        tables.Should().BeNull();
        File.Exists(Path.Combine(_dossier, DonneesSignees.Fichier)).Should().BeFalse();
    }

    // Un corps sans longueur annoncée, qui ne finit jamais : la lecture s'arrête au plafond au lieu de tout garder en mémoire.
    private sealed class SansFin : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'a', offset, count); return count; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ServeurQuiLeve : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("imprévu");
    }

    // Une exception que la couture ne prévoit pas ne traverse pas la frontière : les tables en service restent.
    [Fact]
    public async Task Une_exception_imprevue_rend_un_echec_et_garde_les_tables()
    {
        var (issue, tables) = await DonneesSignees.DemanderAsync(new HttpClient(new ServeurQuiLeve()), Racine, _dossier, Dev(),
            TablesDeDonnees.Embarquees, CancellationToken.None);

        issue.Should().Be(DonneesSignees.Issue.Echec);
        tables.Should().BeNull();
    }

    private sealed class ServeurSansFin : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SansFin()) });
    }

    [Fact]
    public async Task Un_corps_sans_fin_ni_longueur_est_refuse_au_plafond()
    {
        using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (issue, tables) = await DonneesSignees.DemanderAsync(new HttpClient(new ServeurSansFin()), Racine, _dossier, Dev(),
            TablesDeDonnees.Embarquees, delai.Token);

        issue.Should().Be(DonneesSignees.Issue.Refuse, "au-delà de OctetsMax, rien n'est lu de plus");
        tables.Should().BeNull();
        delai.IsCancellationRequested.Should().BeFalse("le refus vient du plafond, pas du délai");
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas-du-base64")]
    public void Une_signature_absente_ou_illisible_est_refusee(string signature)
    {
        Dev().Verifier(Signer(3) with { Signature = signature }, 1).Error!.Value.Code.Should().Be(UpdateErrorCodes.SignatureInvalid);
    }
}
