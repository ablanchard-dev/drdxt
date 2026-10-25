using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Le journal d'écriture anticipée garde, avant chaque écriture privilégiée, ce qu'il faut pour la rendre si le processus
// meurt avant d'avoir noté l'origine. Chaque instance relit le fichier : c'est le « redémarrage » d'après un plantage.
public sealed class JournalDEcritureAnticipeeTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"drdxt-jea-{Guid.NewGuid():N}");
    private string Fichier => Path.Combine(_dossier, "ecritures-en-attente.json");

    private readonly string _cible = Path.Combine(Path.GetTempPath(), $"drdxt-jea-cible-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var d in new[] { _dossier, _cible })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
    }

    private static readonly SetRegistryValueOp Hags =
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "2");
    private static readonly AppelantDuJournal App = new("S-1-5-21-1-2-3-1001", 4242, new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
    private static readonly AppelantDuJournal Autre = new("S-1-5-21-1-2-3-1002", 5151, new DateTimeOffset(2026, 10, 18, 9, 5, 0, TimeSpan.Zero));
    private static readonly DateTimeOffset Le = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Une_entree_ajoutee_survit_au_plantage()
    {
        new JournalDEcritureAnticipee(Fichier).Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();

        var relu = new JournalDEcritureAnticipee(Fichier).EnAttente();

        relu.IsOk.Should().BeTrue();
        relu.Value.Should().ContainSingle();
        var e = relu.Value![0];
        e.Op.Should().Be(Hags);
        e.ImageAvant.Should().Be("1");
        e.Appelant.Should().Be(App);
        e.Le.Should().Be(Le);
    }

    [Fact]
    public void Une_valeur_absente_avant_reste_absente_pas_vide()
    {
        new JournalDEcritureAnticipee(Fichier).Ajouter(Hags, null, App, Le);

        new JournalDEcritureAnticipee(Fichier).EnAttente().Value![0].ImageAvant.Should().BeNull();
    }

    [Fact]
    public void Acquitter_retire_la_plus_ancienne_entree_de_cet_appelant_pour_cette_op()
    {
        var j = new JournalDEcritureAnticipee(Fichier);
        j.Ajouter(Hags, "1", App, Le);
        j.Ajouter(Hags, "1", Autre, Le.AddSeconds(1));
        j.Ajouter(Hags, "2", App, Le.AddSeconds(2));

        new JournalDEcritureAnticipee(Fichier).Acquitter(Hags, App).IsOk.Should().BeTrue();

        new JournalDEcritureAnticipee(Fichier).EnAttente().Value!.Select(e => (e.Appelant, e.ImageAvant))
            .Should().Equal((Autre, "1"), (App, "2"));
    }

    [Fact]
    public void Acquitter_une_ecriture_inconnue_echoue_sans_rien_retirer()
    {
        var j = new JournalDEcritureAnticipee(Fichier);
        j.Ajouter(Hags, "1", App, Le);

        j.Acquitter(Hags with { Value = "1" }, App).Error!.Value.Code.Should().Be(JournalDEcritureAnticipee.CodeSansEntree);
        j.Acquitter(Hags, Autre).Error!.Value.Code.Should().Be(JournalDEcritureAnticipee.CodeSansEntree);

        j.EnAttente().Value.Should().ContainSingle();
    }

    [Fact]
    public void Une_ecriture_qui_a_echoue_sort_du_journal()
    {
        var j = new JournalDEcritureAnticipee(Fichier);
        var garde = j.Ajouter(Hags, "1", App, Le).Value!;
        j.Ajouter(Hags, "1", Autre, Le);

        j.Retirer(garde.Id).IsOk.Should().BeTrue();

        j.EnAttente().Value!.Select(e => e.Appelant).Should().Equal(Autre);
    }

    // Un journal illisible n'est pas un journal vide : le croire vide perdrait des origines, l'écraser aussi.
    [Fact]
    public void Un_journal_illisible_n_est_ni_lu_vide_ni_ecrase()
    {
        // Un journal de DrDXT (fichier de confiance) devenu illisible ; un fichier qui n'est pas de confiance est mis de côté.
        DrDxt.Reconciler.Manifest.TrustedManifestRoot.WriteTrustedFile(Fichier, System.Text.Encoding.UTF8.GetBytes("{ pas du json"))
            .IsOk.Should().BeTrue();
        var j = new JournalDEcritureAnticipee(Fichier);

        j.EnAttente().Error!.Value.Code.Should().Be(JournalDEcritureAnticipee.CodeIllisible);
        j.Ajouter(Hags, "1", App, Le).Error!.Value.Code.Should().Be(JournalDEcritureAnticipee.CodeIllisible,
            "pas un refus passager : le joueur ne lit pas « réessaie dans un instant »");
        File.ReadAllText(Fichier).Should().Be("{ pas du json");
    }

    // Le journal est rejoué en administrateur : seuls SYSTEM et les administrateurs peuvent ouvrir son dossier. Un
    // processus du compte ne peut ni le lire, ni y tenir le verrou, ni y poser un fichier.
    [Fact]
    public void Le_dossier_du_journal_n_est_ouvert_qu_a_SYSTEM_et_aux_administrateurs()
    {
        new JournalDEcritureAnticipee(Fichier).Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();

        var acl = new DirectoryInfo(_dossier).GetAccessControl();
        acl.AreAccessRulesProtected.Should().BeTrue("aucun droit hérité du dossier parent");
        var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference).Value).Distinct().ToList();
        sids.Should().BeEquivalentTo(["S-1-5-18", "S-1-5-32-544"]);
    }

    // Le helper élevé et le service SYSTEM écrivent le même fichier : sans verrou, deux ajouts simultanés relisent la même
    // liste et l'un efface l'autre. Le verrou attend largement : le test éprouve la sérialisation, pas la vitesse du disque.
    [Fact]
    public async Task Deux_ecrivains_en_parallele_ne_perdent_aucune_entree()
    {
        var a = new JournalDEcritureAnticipee(Fichier, attente: TimeSpan.FromMinutes(1));
        var b = new JournalDEcritureAnticipee(Fichier, attente: TimeSpan.FromMinutes(1));

        var poses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => (i % 2 == 0 ? a : b).Ajouter(Hags, $"{i}", App, Le))));

        poses.Where(p => !p.IsOk).Select(p => p.Error!.Value.Message).Should().BeEmpty();
        new JournalDEcritureAnticipee(Fichier).EnAttente().Value.Should().HaveCount(40);
    }

    [Fact]
    public void Un_verrou_tenu_ailleurs_rend_un_echec_jamais_une_exception()
    {
        new JournalDEcritureAnticipee(Fichier).Ajouter(Hags, "1", App, Le);
        using var tenu = new FileStream(Fichier + ".verrou", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var r = new JournalDEcritureAnticipee(Fichier, attente: TimeSpan.FromMilliseconds(200)).Ajouter(Hags, "2", App, Le);

        r.IsOk.Should().BeFalse();
        new JournalDEcritureAnticipee(Fichier).EnAttente().Value.Should().ContainSingle();
    }

    // %ProgramData% laisse tout compte créer un dossier : le journal ne croit pas un dossier qui l'attendait. Un lien vers
    // ailleurs ferait réécrire l'accès de la cible par un processus élevé, et y poser le journal.
    [Fact]
    public void Un_dossier_qui_est_un_lien_est_refuse_et_rien_n_est_ecrit_dans_sa_cible()
    {
        Directory.CreateDirectory(_cible);
        Directory.CreateSymbolicLink(_dossier, _cible);

        new JournalDEcritureAnticipee(Fichier).Ajouter(Hags, "1", App, Le).IsOk.Should().BeFalse();

        Directory.EnumerateFileSystemEntries(_cible).Should().BeEmpty();
    }

    // Le propriétaire d'un dossier garde le droit de rendre l'accès : créé d'avance par un compte, il resterait à lui.
    [Fact]
    public void Un_dossier_cree_d_avance_passe_aux_administrateurs()
    {
        var dossier = Directory.CreateDirectory(_dossier);
        var acces = dossier.GetAccessControl();
        acces.SetOwner(WindowsIdentity.GetCurrent().User!);
        dossier.SetAccessControl(acces);

        new JournalDEcritureAnticipee(Fichier).Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();

        new DirectoryInfo(_dossier).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!.Value.Should().Be("S-1-5-32-544");
    }

    // %ProgramData% laisse tout compte créer un dossier, et la réparation de la racine ne descend pas : un journal posé
    // d'avance par un compte y survivait. Rejoué par le helper ou SYSTEM, il faisait écrire ses ops sans UAC, et la
    // première écriture le recopiait dans le fichier de confiance. Un journal qui n'est pas de confiance (propriétaire,
    // accès) n'est pas de DrDXT : mis de côté, lu vide.
    [Fact]
    public void Un_journal_pose_d_avance_par_un_compte_n_est_ni_rejoue_ni_recopie()
    {
        new JournalDEcritureAnticipee(Path.Combine(_cible, "modele.json")).Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();
        Directory.CreateDirectory(_dossier);   // un dossier ordinaire : il hérite des accès du compte
        File.WriteAllText(Fichier, File.ReadAllText(Path.Combine(_cible, "modele.json")));

        var relu = new JournalDEcritureAnticipee(Fichier).EnAttente();

        relu.IsOk.Should().BeTrue();
        relu.Value.Should().BeEmpty();
        File.Exists(Fichier).Should().BeFalse("mis de côté");
        Directory.GetFiles(_dossier, "ecritures-en-attente.json.mis-de-cote-*").Should().HaveCount(1);
    }

    // Créé par le helper élevé, le journal appartenait au compte (le créateur en est le propriétaire, par défaut) : un
    // programme du même compte, sans élévation, pouvait en réécrire l'accès puis y forger des écritures que SYSTEM
    // rejouerait. Le journal appartient aux Administrateurs, sous un accès que seuls eux et SYSTEM écrivent.
    [Fact]
    public void Le_journal_ecrit_est_un_fichier_de_confiance()
    {
        new JournalDEcritureAnticipee(Fichier).Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();

        DrDxt.Reconciler.Manifest.TrustedManifestRoot.EnsureTrustedFile(Fichier).IsOk.Should().BeTrue();
    }

    private static bool ProprietaireDeConfiance(string fichier)
    {
        var p = (SecurityIdentifier)new FileInfo(fichier).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        return p.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || p.IsWellKnown(WellKnownSidType.LocalSystemSid);
    }

    // Créé par le helper élevé, le verrou appartenait au compte : sans élévation, le compte pouvait s'en rendre l'accès et le
    // tenir ouvert, et toute écriture journalisée était refusée (« occupé »). Le verrou appartient aux Administrateurs ; un
    // verrou laissé par l'ancien code, au propriétaire non fiable, est remplacé.
    [Fact]
    public void Le_verrou_appartient_aux_administrateurs_meme_s_il_existait_deja()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Fichier + ".verrou", "");   // un verrou d'avant : propriétaire, le compte
        // Élevé (le runner de la CI, une session admin), un fichier créé appartient d'office aux Administrateurs : le compte
        // est posé propriétaire à la main, comme l'ancien code le laissait.
        var ancien = new FileInfo(Fichier + ".verrou");
        var acl = ancien.GetAccessControl();
        acl.SetOwner(WindowsIdentity.GetCurrent().User!);
        ancien.SetAccessControl(acl);
        ProprietaireDeConfiance(Fichier + ".verrou").Should().BeFalse("témoin : un fichier créé ici appartient au compte");

        new JournalDEcritureAnticipee(Fichier).Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();

        ProprietaireDeConfiance(Fichier + ".verrou").Should().BeTrue();
    }

    // Un appelant qui empilerait des poses sans jamais les acquitter ferait grossir le journal sans fin, relu et réécrit à
    // chaque écriture : au-delà de la borne, sa pose est refusée (fermé par défaut), celles des autres passent.
    [Fact]
    public void Au_dela_de_la_borne_par_appelant_la_pose_est_refusee()
    {
        var j = new JournalDEcritureAnticipee(Fichier, maxParAppelant: 2);
        j.Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();
        j.Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();

        j.Ajouter(Hags, "1", App, Le).IsOk.Should().BeFalse();

        j.Ajouter(Hags, "1", Autre, Le).IsOk.Should().BeTrue("la borne est par appelant");
        j.EnAttente().Value.Should().HaveCount(3);
    }

    [Fact]
    public void La_borne_par_defaut_est_512()
        => JournalDEcritureAnticipee.MaxParAppelantParDefaut.Should().Be(512);

    // ── le journal du compte : les valeurs de la session (HKCU), que l'App écrit elle-même ──────────────────────────────

    private static readonly SetRegistryValueOp ModeJeu =
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueType.DWord, "1");

    [Fact]
    public void Le_journal_du_compte_garde_une_valeur_de_la_session_jusqu_a_son_acquit()
    {
        JournalDEcritureAnticipee.DuCompte(Fichier).Ajouter(ModeJeu, "0", App, Le).IsOk.Should().BeTrue();

        // Le fichier appartient au compte, pas aux Administrateurs : le journal du compte le lit quand même.
        var relu = JournalDEcritureAnticipee.DuCompte(Fichier).EnAttente();
        relu.Value.Should().ContainSingle().Which.ImageAvant.Should().Be("0");

        JournalDEcritureAnticipee.DuCompte(Fichier).Acquitter(ModeJeu, App).IsOk.Should().BeTrue();
        JournalDEcritureAnticipee.DuCompte(Fichier).EnAttente().Value.Should().BeEmpty();
    }

    // Le compte peut réécrire son journal : une op hors de sa session n'y entre pas, et une op forgée n'en sort jamais pour
    // être rendue (elle partirait au helper).
    [Fact]
    public void Le_journal_du_compte_ne_porte_que_la_session()
    {
        var compte = JournalDEcritureAnticipee.DuCompte(Fichier);
        compte.Ajouter(Hags, "1", App, Le).IsOk.Should().BeFalse();

        new JournalDEcritureAnticipee(Path.Combine(_cible, "admin.json")).Ajouter(Hags, "1", App, Le).IsOk.Should().BeTrue();
        Directory.CreateDirectory(_dossier);
        File.Copy(Path.Combine(_cible, "admin.json"), Fichier, overwrite: true);
        compte.Ajouter(ModeJeu, "0", App, Le).IsOk.Should().BeTrue();

        compte.EnAttente().Value!.Select(e => e.Op).Should().Equal(ModeJeu);
    }
}
