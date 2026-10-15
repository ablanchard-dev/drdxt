using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;
using DrDxt.TestSupport;
using FluentAssertions;
using NSec.Cryptography;

namespace DrDxt.Reconciler.Tests;

// -----------------------------------------------------------------------
// La cle de la machine et l'ecrivain du manifeste (ce que fait le helper eleve).
//
// Tout se joue dans un dossier temporaire, ACL NON appliquee (appliquerAcl: false) : la decision d'ACL
// d'un fichier prive est prouvee a part, en fonction pure (Evaluate), comme le reste de la racine de
// confiance. DPAPI portee machine est reel : il n'ecrit rien sur la machine, il chiffre avec une cle
// qu'elle possede deja. Aucune vraie cle n'est creee sous %ProgramData%.
//
// La relecture se fait comme le service et l'App la font : avec la seule cle publique posee a cote.
// -----------------------------------------------------------------------
public sealed class MachineManifestCommitTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "drdxt-cle-machine-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* dossier temporaire */ }
    }

    private string Manifeste => Path.Combine(_racine, "desired-state.manifest");
    private string Privee => Path.Combine(_racine, MachineManifestKey.PrivateFileName);
    private string Publique => Path.Combine(_racine, MachineManifestKey.PublicFileName);

    private Task<Result> Commit(DesiredState ajout, bool repartir = false)
        => new MachineManifestCommit(_racine, appliquerAcl: false, new SystemClock()).ExecuterAsync(ajout, repartir);

    private static AppliedTweak Correction(string origine = "\"0\"")
        => new("latency.reflex.lowLatencyMode", "2", origine, DateTimeOffset.UnixEpoch);

    private static AppliedTweak Garde(string id, string origine) => new(id, "\"cible\"", origine, DateTimeOffset.UnixEpoch);

    private static DesiredState Corrections(params AppliedTweak[] t) => new(t);
    private static DesiredState Gardes(params AppliedTweak[] t) => new(Array.Empty<AppliedTweak>(), t);

    private async Task<Result<DesiredState>> RelireAvecLaSeulePublique()
    {
        var verificateur = new Ed25519ManifestVerifier(await File.ReadAllBytesAsync(Publique));
        return await new SignedDesiredStateStore(new ProgramDataManifestStorage(_racine, applyAcl: false),
            signer: null, verificateur, new DefaultReconcilabilityPolicy(), new SystemClock()).LoadAsync();
    }

    private string[] MisDeCote(string nom) => Directory.GetFiles(_racine, nom + ".mis-de-cote-*");

    // ── la cle ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Le_premier_commit_cree_la_cle_signe_et_le_service_relit_avec_la_seule_publique()
    {
        (await Commit(new DesiredState(new[] { Correction() }, new[] { Garde("latency.reflex", "\"1\"") })))
            .IsOk.Should().BeTrue();

        File.Exists(Privee).Should().BeTrue();
        File.Exists(Publique).Should().BeTrue();
        var relu = await RelireAvecLaSeulePublique();
        relu.IsOk.Should().BeTrue();
        relu.Value!.Tweaks.Should().ContainSingle().Which.TweakId.Should().Be("latency.reflex.lowLatencyMode");
        relu.Value.RevertOnly.Should().ContainSingle().Which.TweakId.Should().Be("latency.reflex");
    }

    [Fact]
    public async Task La_cle_privee_au_repos_est_un_blob_DPAPI_machine_et_la_publique_en_derive()
    {
        (await Commit(Corrections(Correction()))).IsOk.Should().BeTrue();
        var blob = await File.ReadAllBytesAsync(Privee);

        blob.Length.Should().BeGreaterThan(32, "la graine n'est pas ecrite en clair");
        FluentActions.Invoking(() => ProtectedData.Unprotect(blob, null, DataProtectionScope.LocalMachine))
            .Should().Throw<CryptographicException>("sans l'entropie du produit, le blob ne se dechiffre pas");

        var graine = ProtectedData.Unprotect(blob, "DrDXT.manifest-signing.v1"u8.ToArray(), DataProtectionScope.LocalMachine);
        graine.Should().HaveCount(32);
        using var cle = Key.Import(SignatureAlgorithm.Ed25519, graine, KeyBlobFormat.RawPrivateKey);
        cle.PublicKey.Export(KeyBlobFormat.RawPublicKey).Should().Equal(await File.ReadAllBytesAsync(Publique),
            "la cle publique posee a cote est DERIVEE de la privee, la source de verite");
    }

    [Fact]
    public async Task Une_cle_publique_divergente_est_reparee_depuis_la_privee()
    {
        (await Commit(Corrections(Correction()))).IsOk.Should().BeTrue();
        var bonne = await File.ReadAllBytesAsync(Publique);
        await File.WriteAllBytesAsync(Publique, RandomNumberGenerator.GetBytes(32));

        (await Commit(Gardes(Garde("latency.reflex", "\"1\"")))).IsOk.Should().BeTrue();

        (await File.ReadAllBytesAsync(Publique)).Should().Equal(bonne);
    }

    // ── la fusion ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deux_commits_fusionnent_et_la_premiere_origine_gagne()
    {
        (await Commit(Gardes(Garde("latency.reflex", "\"origine-vraie\"")))).IsOk.Should().BeTrue();
        (await Commit(Gardes(Garde("latency.reflex", "\"cible-recapturee\""), Garde("fixit.power_plan", "\"balanced\""))))
            .IsOk.Should().BeTrue();

        var relu = await RelireAvecLaSeulePublique();
        relu.Value!.RevertOnly!.Select(t => (t.TweakId, t.OriginalValueJson)).Should().BeEquivalentTo(new[]
        {
            ("latency.reflex", "\"origine-vraie\""),
            ("fixit.power_plan", "\"balanced\""),
        }, "un levier retente recapture la cible comme « origine » : l'ecrire perdrait la vraie");
    }

    // ── l'illisible ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_manifeste_illisible_n_est_jamais_ecrase_le_refus_est_nomme()
    {
        (await Commit(Corrections(Correction()))).IsOk.Should().BeTrue();
        await File.WriteAllTextAsync(Manifeste, "{pas du json");
        var avant = await File.ReadAllBytesAsync(Manifeste);

        var r = await Commit(Gardes(Garde("latency.reflex", "\"1\"")));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ManifestUnreadable);
        r.Error.Value.Message.Should().Contain("Nothing was written");
        (await File.ReadAllBytesAsync(Manifeste)).Should().Equal(avant, "rien n'est ecrit par-dessus l'illisible");
    }

    [Fact]
    public async Task Un_manifeste_signe_par_la_cle_de_dev_n_est_jamais_accepte()
    {
        // Un manifeste parfaitement signe... par la cle de DEV, dont la graine est dans ce depot.
        var dev = new SignedDesiredStateStore(new ProgramDataManifestStorage(_racine, applyAcl: false),
            Ed25519ManifestSigner.FromSeed(ManifestDevTestKeys.DevPrivateSeed), ManifestDevKeys.CreateVerifier().Value!,
            new DefaultReconcilabilityPolicy(), new SystemClock());
        (await dev.CommitAsync(Corrections(Correction("\"forge\"")))).IsOk.Should().BeTrue();
        var avant = await File.ReadAllBytesAsync(Manifeste);

        var r = await Commit(Gardes(Garde("latency.reflex", "\"1\"")));

        r.Error!.Value.Code.Should().Be(ErrorCodes.ManifestUnreadable,
            "seule la cle de la machine fait foi : un manifeste de dev ne se fusionne pas, il se refuse");
        (await File.ReadAllBytesAsync(Manifeste)).Should().Equal(avant);
    }

    [Fact]
    public async Task Une_ancienne_cle_n_est_jamais_acceptee()
    {
        (await Commit(Corrections(Correction()))).IsOk.Should().BeTrue();
        // La cle privee disparait (restauration partielle, disque clone...) : une NOUVELLE est creee au
        // prochain commit, et le manifeste signe par l'ancienne ne fait plus foi.
        File.Move(Privee, Privee + ".ancienne");

        var r = await Commit(Gardes(Garde("latency.reflex", "\"1\"")));

        r.Error!.Value.Code.Should().Be(ErrorCodes.ManifestUnreadable);
    }

    // ── la reprise ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task La_reprise_met_l_illisible_de_cote_sans_l_effacer_et_commence_un_manifeste_neuf()
    {
        (await Commit(Corrections(Correction()))).IsOk.Should().BeTrue();
        await File.WriteAllTextAsync(Manifeste, "{pas du json");

        (await Commit(Gardes(Garde("latency.reflex", "\"1\"")), repartir: true)).IsOk.Should().BeTrue();

        var deCote = MisDeCote("desired-state.manifest");
        deCote.Should().ContainSingle("l'illisible est renomme, jamais efface");
        (await File.ReadAllTextAsync(deCote[0])).Should().Be("{pas du json");
        var relu = await RelireAvecLaSeulePublique();
        relu.Value!.Tweaks.Should().BeEmpty("les origines de l'ancien ne sont plus rendues : l'ecran le dit");
        relu.Value.RevertOnly.Should().ContainSingle().Which.TweakId.Should().Be("latency.reflex");
    }

    [Fact]
    public async Task La_reprise_est_refusee_sur_un_manifeste_lisible()
    {
        (await Commit(Corrections(Correction()))).IsOk.Should().BeTrue();

        var r = await Commit(Gardes(Garde("latency.reflex", "\"1\"")), repartir: true);

        r.Error!.Value.Code.Should().Be(ErrorCodes.OpNotAllowed);
        MisDeCote("desired-state.manifest").Should().BeEmpty();
        (await RelireAvecLaSeulePublique()).Value!.Tweaks.Should().ContainSingle(
            "on ne jette jamais des origines qu'on sait relire");
    }

    [Fact]
    public async Task Une_cle_privee_qui_est_la_cle_de_dev_est_refusee_puis_mise_de_cote_par_la_reprise()
    {
        Directory.CreateDirectory(_racine);
        var planquee = ProtectedData.Protect(ManifestDevTestKeys.DevPrivateSeed, "DrDXT.manifest-signing.v1"u8.ToArray(),
            DataProtectionScope.LocalMachine);
        await File.WriteAllBytesAsync(Privee, planquee);

        var r = await Commit(Corrections(Correction()));
        r.Error!.Value.Code.Should().Be(ErrorCodes.ManifestKeyUnusable);
        (await File.ReadAllBytesAsync(Privee)).Should().Equal(planquee, "une cle refusee n'est ni ecrasee ni remplacee en silence");

        (await Commit(Corrections(Correction()), repartir: true)).IsOk.Should().BeTrue();
        MisDeCote(MachineManifestKey.PrivateFileName).Should().ContainSingle();
        ManifestDevKeys.EstLaCleDeDev(await File.ReadAllBytesAsync(Publique)).Should().BeFalse();
    }

    [Fact]
    public async Task Une_cle_privee_illisible_est_refusee_nommee()
    {
        Directory.CreateDirectory(_racine);
        await File.WriteAllBytesAsync(Privee, new byte[] { 1, 2, 3 });

        var r = await Commit(Corrections(Correction()));

        r.Error!.Value.Code.Should().Be(ErrorCodes.ManifestKeyUnusable);
        File.Exists(Manifeste).Should().BeFalse();
    }

    // La clé privée perdue (corrompue), la publique et le manifeste intacts : la reprise mettait de côté un manifeste encore
    // lisible, et ses origines étaient perdues pour la désinstallation. Il est relu avec la publique et re-signé.
    [Fact]
    public async Task La_reprise_garde_les_origines_d_un_manifeste_encore_verifiable()
    {
        (await Commit(Gardes(Garde("latency.reflex", "\"1\"")))).IsOk.Should().BeTrue();
        await File.WriteAllBytesAsync(Privee, new byte[] { 1, 2, 3 });

        (await Commit(Gardes(Garde("latency.render_queue_cap", "\"0\"")), repartir: true)).IsOk.Should().BeTrue();

        var relu = await RelireAvecLaSeulePublique();
        relu.Value!.RevertOnly!.Select(t => t.TweakId).Should().BeEquivalentTo(["latency.reflex", "latency.render_queue_cap"]);
    }

    // Une lecture empêchée un instant (un antivirus tient le fichier) n'est pas une clé inutilisable : rien n'est mis de
    // côté, l'écriture est remise.
    [Fact]
    public async Task Une_cle_tenue_un_instant_n_est_pas_mise_de_cote()
    {
        (await Commit(Gardes(Garde("latency.reflex", "\"1\"")))).IsOk.Should().BeTrue();
        Result r;
        using (new FileStream(Privee, FileMode.Open, FileAccess.Read, FileShare.None))
            r = await Commit(Gardes(Garde("latency.render_queue_cap", "\"0\"")), repartir: true);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().NotBe(ErrorCodes.ManifestKeyUnusable);
        MisDeCote(MachineManifestKey.PrivateFileName).Should().BeEmpty();
        MisDeCote("desired-state.manifest").Should().BeEmpty();
    }

    // ── l'ACL d'un fichier prive (fonction pure) ────────────────────────────────────────────

    private static SecurityIdentifier Admins => new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static SecurityIdentifier Systeme => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier Utilisateurs => new(WellKnownSidType.AuthenticatedUserSid, null);

    private static TrustedManifestRoot.AceView Autorise(SecurityIdentifier sid, FileSystemRights droits)
        => new(sid, droits, AccessControlType.Allow);

    [Fact]
    public void Un_fichier_prive_lisible_par_les_utilisateurs_n_est_pas_fiable_la_ou_un_public_l_est()
    {
        var posturePublique = new[]
        {
            Autorise(Admins, FileSystemRights.FullControl), Autorise(Systeme, FileSystemRights.FullControl),
            Autorise(Utilisateurs, FileSystemRights.ReadAndExecute),
        };

        TrustedManifestRoot.Evaluate("pub", Admins, posturePublique).IsOk.Should().BeTrue("temoin : la posture des fichiers publics");
        TrustedManifestRoot.Evaluate("cle", Admins, posturePublique, refuserLectureNonAdmin: true).IsOk.Should().BeFalse(
            "le blob DPAPI machine se dechiffre depuis n'importe quel compte local : c'est l'ACL qui garde la cle");
        TrustedManifestRoot.Evaluate("cle", Admins,
                new[] { Autorise(Admins, FileSystemRights.FullControl), Autorise(Utilisateurs, (FileSystemRights)unchecked((int)0x80000000)) },
                refuserLectureNonAdmin: true)
            .IsOk.Should().BeFalse("GENERIC_READ brut sur un utilisateur");
        TrustedManifestRoot.Evaluate("cle", Admins,
                new[] { Autorise(Admins, FileSystemRights.FullControl), Autorise(Systeme, FileSystemRights.FullControl) },
                refuserLectureNonAdmin: true)
            .IsOk.Should().BeTrue("temoin : Administrateurs + SYSTEM seulement");
    }

    [Fact]
    public void Le_descripteur_prive_ne_donne_rien_aux_utilisateurs_et_n_herite_de_rien()
    {
        var s = TrustedManifestRoot.BuildPrivateFileSecurity();

        s.AreAccessRulesProtected.Should().BeTrue();
        s.GetOwner(typeof(SecurityIdentifier)).Should().Be(Admins);
        s.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference).Should().BeEquivalentTo(new[] { Admins, Systeme });
    }
}
