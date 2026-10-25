using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Journal;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

// « Corriger les droits » : un service SYSTEM dont le fichier ou le dossier est modifiable par le compte revient aux
// administrateurs, l'origine gardée par le helper, et Annuler la rend. De vraies ACL sur des fichiers temporaires ; le
// registre des services est une table du test, jamais un vrai service.
public sealed class DroitsDesServicesTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "drdxt-droits-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, DroitsDesServices.ServiceLu> _services = new(StringComparer.OrdinalIgnoreCase);
    private string Origines => Path.Combine(_racine, "origines");
    private string DrDxt => Path.Combine(_racine, "drdxt");
    private string Partage => Path.Combine(_racine, "partage");
    private readonly List<string> _ecrits = [];
    private Func<string, Result> _fiable = _ => Result.Ok();
    private Func<string, byte[], Result>? _ecrire;

    private static readonly string Compte = WindowsIdentity.GetCurrent().User!.Value;
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // Poser un propriétaire Administrateurs demande l'élévation : sans elle, ces tests n'ont rien à prouver.
    private static bool Eleve => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public DroitsDesServicesTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try
        {
            // Rendus au compte pour que le dossier temporaire se supprime.
            foreach (var d in Directory.GetDirectories(_racine, "*", SearchOption.AllDirectories).Prepend(_racine))
                new DirectoryInfo(d).SetAccessControl(Sddl<DirectorySecurity>($"O:{Compte}D:(A;OICI;FA;;;{Compte})(A;OICI;FA;;;BA)"));
            Directory.Delete(_racine, recursive: true);
        }
        catch { /* dossier temporaire du test */ }
    }

    private DroitsDesServices Droits() => new(
        nom => _services.TryGetValue(nom, out var s) ? s : null,
        Origines,
        [Environment.GetFolderPath(Environment.SpecialFolder.Windows), DrDxt],
        [Partage],
        (chemin, octets) =>
        {
            if (_ecrire is not null && _ecrire(chemin, octets) is { IsOk: false } refus) return refus;
            _ecrits.Add(chemin);
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllBytes(chemin, octets);
            return Result.Ok();
        },
        chemin => _fiable(chemin),
        borne: _racine);

    private static T Sddl<T>(string sddl) where T : FileSystemSecurity, new()
    {
        var s = new T();
        s.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Owner | AccessControlSections.Access);
        return s;
    }

    private static string SddlDe(FileSystemInfo f) => (f is DirectoryInfo d ? (FileSystemSecurity)d.GetAccessControl() : ((FileInfo)f).GetAccessControl())
        .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);

    /// <summary>Un service SYSTEM dont le dossier appartient au compte, qui y a le contrôle total : le cas du service de
    /// Call of Duty sous ProgramData. Le fichier hérite de son dossier.</summary>
    private string ServiceRemplacable(string nom = "Broker", string sousDossier = "service", string? dossierSddl = null)
    {
        var dossier = Directory.CreateDirectory(Path.Combine(_racine, sousDossier));
        dossier.SetAccessControl(Sddl<DirectorySecurity>(dossierSddl
            ?? $"O:{Compte}D:P(A;OICI;FA;;;{Compte})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"));
        var exe = Path.Combine(dossier.FullName, "svc.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var proprio = new FileSecurity();
        proprio.SetOwner(new SecurityIdentifier(Compte));
        new FileInfo(exe).SetAccessControl(proprio);
        _services[nom] = new DroitsDesServices.ServiceLu($"\"{exe}\" --run", "LocalSystem");
        return exe;
    }

    private (string Fichier, string Dossier) Etat(string exe) => (SddlDe(new FileInfo(exe)), SddlDe(new DirectoryInfo(Path.GetDirectoryName(exe)!)));

    // ── Allowlist et codec ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("JeuServiceA", true)]
    [InlineData("Steam Client Service", true)]
    [InlineData("EpicOnlineServices", true)]
    [InlineData("svc_1.2-x", true)]
    [InlineData("", false)]
    [InlineData(" Broker", false)]
    [InlineData("Broker ", false)]
    [InlineData(@"..\..\Control\Lsa", false)]
    [InlineData(@"Services\Broker", false)]
    [InlineData("Broker/x", false)]
    [InlineData("Bro\"ker", false)]
    [InlineData("Bro%ker", false)]
    [InlineData("Bro\nker", false)]
    [InlineData("Broker\n", false)]
    public void Seul_un_nom_de_service_simple_passe_l_allowlist(string nom, bool permis)
    {
        new OpAllowlist().Validate(new CorrigerDroitsDuServiceOp(nom)).IsOk.Should().Be(permis);
        new OpAllowlist().Validate(new RendreDroitsDuServiceOp(nom)).IsOk.Should().Be(permis);
    }

    [Fact]
    public void Un_nom_de_service_trop_long_est_refuse()
    {
        new OpAllowlist().Validate(new CorrigerDroitsDuServiceOp(new string('a', 256))).IsOk.Should().BeTrue();
        new OpAllowlist().Validate(new CorrigerDroitsDuServiceOp(new string('a', 257))).IsOk.Should().BeFalse();
        new OpAllowlist().Validate(new RendreDroitsDuServiceOp(new string('a', 257))).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Les_deux_ops_traversent_le_codec()
    {
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(new CorrigerDroitsDuServiceOp("JeuServiceA")).Value!).Value
            .Should().Be(new CorrigerDroitsDuServiceOp("JeuServiceA"));
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(new RendreDroitsDuServiceOp("Steam Client Service")).Value!).Value
            .Should().Be(new RendreDroitsDuServiceOp("Steam Client Service"));
    }

    // L'App n'envoie que le nom : un chemin ou un SDDL sur le fil serait de quoi donner l'écriture à tout le monde sur un
    // binaire SYSTEM.
    [Fact]
    public void Le_fil_ne_porte_aucun_chemin_ni_sddl()
        => PrivilegedOpCodec.Deserialize("{\"op\":48,\"NomDuService\":\"Broker\",\"Sddl\":\"D:(A;;FA;;;WD)\"}").IsOk.Should().BeFalse();

    [Fact]
    public void Les_deux_ops_sont_sans_etat_au_journal()
    {
        // L'origine vit dans le fichier de confiance du helper, pas dans le journal d'écriture anticipée.
        ClassesDuJournal.De(new CorrigerDroitsDuServiceOp("Broker")).Should().Be(ClasseDuJournal.SansEtat);
        ClassesDuJournal.De(new RendreDroitsDuServiceOp("Broker")).Should().Be(ClasseDuJournal.SansEtat);
    }

    [Fact]
    public async Task Sans_droits_branches_l_executeur_echoue_honnetement()
    {
        var executeur = new PrivilegedOpExecutor(
            Substitute.For<IRegistryActions>(), Substitute.For<IPowerPlanActions>(), Substitute.For<IRestorePointActions>(),
            Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(), Substitute.For<INvApiTuningActions>(),
            Substitute.For<DrDxt.ElevatedHelper.Execution.Adlx.IADLXTuningActions>());
        (await executeur.ExecuteAsync(new CorrigerDroitsDuServiceOp("Broker"), CancellationToken.None)).IsOk.Should().BeFalse();
        (await executeur.ExecuteAsync(new RendreDroitsDuServiceOp("Broker"), CancellationToken.None)).IsOk.Should().BeFalse();
    }

    // ── Refus : rien n'est écrit ───────────────────────────────────────────────────────

    [Fact]
    public void Un_service_absent_est_refuse()
    {
        Droits().Corriger("Absent").IsOk.Should().BeFalse();
        _ecrits.Should().BeEmpty();
    }

    [Theory]
    [InlineData("NT AUTHORITY\\LocalService")]
    [InlineData(".\\joueur")]
    [InlineData(null)]
    public void Un_service_qui_ne_tourne_pas_en_SYSTEM_est_refuse(string? compte)
    {
        var exe = ServiceRemplacable();
        _services["Broker"] = _services["Broker"] with { ObjectName = compte };
        var avant = Etat(exe);

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
        _ecrits.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\pilote.sys")]
    [InlineData(@"System32\drivers\pilote.sys")]
    [InlineData(@"system32\svchost.exe -k x")]
    [InlineData(@"%ProgramData%\Editeur\svc.exe")]
    [InlineData(@"\??\C:\Editeur\svc.exe")]
    [InlineData("")]
    public void Un_pilote_ou_un_chemin_qui_n_est_pas_un_exe_complet_est_refuse(string imagePath)
    {
        _services["Broker"] = new DroitsDesServices.ServiceLu(imagePath, "LocalSystem");
        Droits().Corriger("Broker").IsOk.Should().BeFalse();
        _ecrits.Should().BeEmpty();
    }

    [Fact]
    public void Un_binaire_absent_est_refuse()
    {
        _services["Broker"] = new DroitsDesServices.ServiceLu($"\"{Path.Combine(_racine, "absent.exe")}\"", "LocalSystem");
        Droits().Corriger("Broker").IsOk.Should().BeFalse();
        _ecrits.Should().BeEmpty();
    }

    [Fact]
    public void Un_binaire_de_Windows_n_est_jamais_touche()
    {
        var svchost = Path.Combine(Environment.SystemDirectory, "svchost.exe");
        var avant = SddlDe(new FileInfo(svchost));
        _services["Broker"] = new DroitsDesServices.ServiceLu($"{svchost} -k netsvcs -p", "LocalSystem");

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        SddlDe(new FileInfo(svchost)).Should().Be(avant);
        _ecrits.Should().BeEmpty();
    }

    [Fact]
    public void Un_binaire_de_DrDXT_n_est_jamais_touche()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable(sousDossier: "drdxt");
        var avant = Etat(exe);

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
        _ecrits.Should().BeEmpty();
    }

    // Réécrire la DACL de ProgramData ou de Program Files la propagerait à tout ce qu'ils contiennent.
    [Fact]
    public void Un_binaire_pose_a_meme_un_dossier_partage_est_refuse()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable(sousDossier: "partage");
        var avant = Etat(exe);

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
        _ecrits.Should().BeEmpty();
    }

    [Fact]
    public void Un_service_deja_protege_reussit_sans_rien_ecrire()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable(dossierSddl: "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");
        var proprio = new FileSecurity();
        proprio.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new FileInfo(exe).SetAccessControl(proprio);
        CheminProtege.Verifier(exe, _racine).Should().BeNull("témoin : rien à corriger");
        var avant = Etat(exe);

        Droits().Corriger("Broker").IsOk.Should().BeTrue();

        Etat(exe).Should().Be(avant);
        _ecrits.Should().BeEmpty();
    }

    // Un lien (jonction, lien symbolique) ferait réécrire la cible : le chemin final du handle doit être celui du registre.
    [Fact]
    public void Un_chemin_qui_passe_par_un_lien_est_refuse()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var lien = Path.Combine(_racine, "lien");
        Directory.CreateSymbolicLink(lien, Path.GetDirectoryName(exe)!);
        _services["Broker"] = new DroitsDesServices.ServiceLu($"\"{Path.Combine(lien, "svc.exe")}\"", "LocalSystem");
        var avant = Etat(exe);

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
        _ecrits.Should().BeEmpty();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string nouveau, string existant, IntPtr securite);

    // Un lien dur ferait réécrire un autre fichier, que le compte ne pourrait pas remplacer lui-même.
    [Fact]
    public void Un_binaire_a_plusieurs_liens_durs_est_refuse()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        CreateHardLinkW(Path.Combine(_racine, "autre.exe"), exe, IntPtr.Zero).Should().BeTrue();
        var avant = Etat(exe);

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
        _ecrits.Should().BeEmpty();
    }

    // ── La correction ───────────────────────────────────────────────────────────────────

    [Fact]
    public void La_correction_rend_le_fichier_et_son_dossier_aux_administrateurs()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        CheminProtege.Verifier(exe, _racine).Should().NotBeNull("témoin : le compte peut remplacer ce fichier");

        Droits().Corriger("Broker").IsOk.Should().BeTrue();

        CheminProtege.Verifier(exe, _racine).Should().BeNull();
        foreach (var acl in new FileSystemSecurity[] { new FileInfo(exe).GetAccessControl(), new DirectoryInfo(Path.GetDirectoryName(exe)!).GetAccessControl() })
        {
            acl.GetOwner(typeof(SecurityIdentifier))!.Value.Should().Be("S-1-5-32-544");
            acl.AreAccessRulesProtected.Should().BeTrue();
            var regles = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            regles.Select(r => r.IdentityReference.Value).Should().BeEquivalentTo(["S-1-5-18", "S-1-5-32-544", "S-1-5-32-545"]);
            regles.Single(r => r.IdentityReference.Value == "S-1-5-32-545").FileSystemRights
                .Should().Be(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
        }
        new DirectoryInfo(Path.GetDirectoryName(exe)!).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Should().OnlyContain(r => r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
    }

    [Fact]
    public void TrustedInstaller_est_garde_s_il_y_etait()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable(dossierSddl: $"O:{Compte}D:P(A;OICI;FA;;;{Compte})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{TrustedInstaller})");

        Droits().Corriger("Broker").IsOk.Should().BeTrue();

        new DirectoryInfo(Path.GetDirectoryName(exe)!).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Should().Contain(TrustedInstaller);
        CheminProtege.Verifier(exe, _racine).Should().BeNull();
    }

    [Fact]
    public void L_origine_est_gardee_avant_toute_ecriture()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var avant = Etat(exe);
        (string, string)? auMomentDeGarder = null;
        _ecrire = (_, _) => { auMomentDeGarder = Etat(exe); return Result.Ok(); };

        Droits().Corriger("Broker").IsOk.Should().BeTrue();

        auMomentDeGarder.Should().Be(avant, "l'origine s'écrit quand rien n'a encore changé");
        var garde = File.ReadAllText(Droits().CheminDeLOrigine("Broker"));
        garde.Should().Contain(avant.Fichier.Replace("\\", "\\\\")).And.Contain(avant.Dossier.Replace("\\", "\\\\"));
    }

    [Fact]
    public void Sans_origine_gardee_rien_n_est_corrige()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var avant = Etat(exe);
        _ecrire = (_, _) => Result.Fail(ErrorCodes.IoFailure, "disque plein");

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
    }

    // Une mise à jour du jeu rouvre le dossier ; corrigé une seconde fois, l'origine gardée reste la vraie, celle d'avant
    // la première correction.
    [Fact]
    public void Une_seconde_correction_n_ecrase_jamais_l_origine_gardee()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var origine = Etat(exe);
        Droits().Corriger("Broker").IsOk.Should().BeTrue();
        var gardee = File.ReadAllBytes(Droits().CheminDeLOrigine("Broker"));
        var dossier = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        dossier.SetAccessControl(Sddl<DirectorySecurity>($"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{Compte})"));
        _ecrits.Clear();

        Droits().Corriger("Broker").IsOk.Should().BeTrue();

        _ecrits.Should().BeEmpty();
        File.ReadAllBytes(Droits().CheminDeLOrigine("Broker")).Should().Equal(gardee);
        Droits().Rendre("Broker").IsOk.Should().BeTrue();
        Etat(exe).Should().Be(origine);
    }

    // Un dossier au-dessus que le compte peut modifier laisse renommer le dossier corrigé et en poser un autre : la
    // correction ne ferme rien. L'origine revient tout de suite, et la raison est dite.
    [Fact]
    public void Un_ancetre_modifiable_rend_l_origine_et_echoue_en_le_disant()
    {
        if (!Eleve) return;
        Directory.CreateDirectory(Path.Combine(_racine, "Jeu"));
        var exe = ServiceRemplacable(sousDossier: Path.Combine("Jeu", "service"));
        var avant = Etat(exe);
        var jeu = SddlDe(new DirectoryInfo(Path.Combine(_racine, "Jeu")));

        var r = Droits().Corriger("Broker");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain(Path.Combine(_racine, "Jeu"));
        Etat(exe).Should().Be(avant);
        File.Exists(Droits().CheminDeLOrigine("Broker")).Should().BeFalse("rendue, l'origine n'a plus rien à garder");
        SddlDe(new DirectoryInfo(Path.Combine(_racine, "Jeu"))).Should().Be(jeu, "un dossier au-dessus n'est jamais touché");
    }

    // ── L'inverse ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Annuler_rend_exactement_l_origine_et_retire_ce_qui_la_gardait()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable(dossierSddl: $"O:{Compte}D:P(A;OICI;FA;;;{Compte})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{TrustedInstaller})");
        var avant = Etat(exe);
        Droits().Corriger("Broker").IsOk.Should().BeTrue();
        Etat(exe).Should().NotBe(avant);

        Droits().Rendre("Broker").IsOk.Should().BeTrue();

        Etat(exe).Should().Be(avant);
        File.Exists(Droits().CheminDeLOrigine("Broker")).Should().BeFalse();
    }

    [Fact]
    public void Annuler_sans_origine_gardee_echoue_sans_rien_ecrire()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var avant = Etat(exe);

        Droits().Rendre("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
    }

    [Fact]
    public void Une_origine_gardee_qui_n_est_pas_de_confiance_est_refusee()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        Droits().Corriger("Broker").IsOk.Should().BeTrue();
        var corrige = Etat(exe);
        _fiable = _ => Result.Fail(ErrorCodes.SignatureInvalid, "non fiable");

        Droits().Rendre("Broker").IsOk.Should().BeFalse();
        Etat(exe).Should().Be(corrige);

        // Une origine non fiable n'est pas non plus prise pour la vraie à la correction suivante.
        Droits().Corriger("Broker").IsOk.Should().BeTrue("déjà protégé, rien à écrire");
    }

    [Fact]
    public void Une_origine_non_fiable_bloque_une_nouvelle_correction()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        Directory.CreateDirectory(Origines);
        File.WriteAllText(Droits().CheminDeLOrigine("Broker"), "{}");
        _fiable = _ => Result.Fail(ErrorCodes.SignatureInvalid, "non fiable");
        var avant = Etat(exe);

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
    }

    [Fact]
    public void Annuler_refuse_un_service_dont_le_chemin_a_change()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        Droits().Corriger("Broker").IsOk.Should().BeTrue();
        var corrige = Etat(exe);
        var ailleurs = Path.Combine(Path.GetDirectoryName(exe)!, "autre.exe");
        File.Copy(exe, ailleurs);
        _services["Broker"] = new DroitsDesServices.ServiceLu($"\"{ailleurs}\"", "LocalSystem");

        Droits().Rendre("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(corrige);
        File.Exists(Droits().CheminDeLOrigine("Broker")).Should().BeTrue("l'origine reste pour un Annuler qui pourra la rendre");
    }

    // Le fichier remplacé depuis la correction n'est plus celui dont l'origine a été lue : la rendre lui donnerait les droits d'un autre.
    [Fact]
    public void Annuler_refuse_un_fichier_remplace_depuis_la_correction()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        Droits().Corriger("Broker").IsOk.Should().BeTrue();
        File.Delete(exe);
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var avant = Etat(exe);

        Droits().Rendre("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant);
    }

    // ── La propagation aux enfants ──────────────────────────────────────────────────────

    /// <summary>Un dossier « victime » hors du dossier du service, avec une ACL connue et un fichier : ce qu'un lien planté par le
    /// compte ferait réécrire si la descente le suivait.</summary>
    private (string Dossier, string Fichier) Victime()
    {
        var dossier = Directory.CreateDirectory(Path.Combine(_racine, "victime"));
        dossier.SetAccessControl(Sddl<DirectorySecurity>($"O:{Compte}D:P(A;OICI;FA;;;{Compte})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)"));
        var fichier = Path.Combine(dossier.FullName, "systeme.dll");
        File.WriteAllBytes(fichier, [0x4D, 0x5A]);
        return (dossier.FullName, fichier);
    }

    private static byte[] Octets(FileSystemInfo f)
        => (f is DirectoryInfo d ? (FileSystemSecurity)d.GetAccessControl() : ((FileInfo)f).GetAccessControl()).GetSecurityDescriptorBinaryForm();

    private static (byte[], byte[]) EtatDeLaVictime((string Dossier, string Fichier) v)
        => (Octets(new DirectoryInfo(v.Dossier)), Octets(new FileInfo(v.Fichier)));

    private static void Jonction(string lien, string cible)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{lien}\" \"{cible}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Directory.Exists(lien).Should().BeTrue("témoin : la jonction est posée");
        new DirectoryInfo(lien).Attributes.Should().HaveFlag(FileAttributes.ReparsePoint);
    }

    // Le dossier du service appartenait au compte : il a pu y planter une jonction vers n'importe quel arbre. La correction la
    // trouve, ne la suit pas, rend l'origine et le dit.
    [Fact]
    public void Une_jonction_dans_le_dossier_du_service_n_est_jamais_suivie_et_la_correction_est_refusee()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var victime = Victime();
        Jonction(Path.Combine(Path.GetDirectoryName(exe)!, "donnees"), victime.Dossier);
        var avant = (Etat(exe), EtatDeLaVictime(victime));

        var r = Droits().Corriger("Broker");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("donnees");
        Etat(exe).Should().Be(avant.Item1, "l'origine est rendue");
        EtatDeLaVictime(victime).Item1.Should().Equal(avant.Item2.Item1);
        EtatDeLaVictime(victime).Item2.Should().Equal(avant.Item2.Item2);
        File.Exists(Droits().CheminDeLOrigine("Broker")).Should().BeFalse();
    }

    [Fact]
    public void Un_lien_symbolique_de_fichier_dans_le_dossier_du_service_n_est_jamais_suivi()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var victime = Victime();
        File.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(exe)!, "moteur.dll"), victime.Fichier);
        var avant = (Etat(exe), EtatDeLaVictime(victime));

        Droits().Corriger("Broker").IsOk.Should().BeFalse();

        Etat(exe).Should().Be(avant.Item1);
        EtatDeLaVictime(victime).Item1.Should().Equal(avant.Item2.Item1);
        EtatDeLaVictime(victime).Item2.Should().Equal(avant.Item2.Item2);
    }

    // Un lien posé après la correction (par un administrateur, le compte n'y écrit plus) : Annuler rend l'origine sans le
    // suivre, et la victime reste telle quelle.
    [Fact]
    public void Annuler_ne_suit_jamais_un_lien_du_dossier_du_service()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var origine = Etat(exe);
        Droits().Corriger("Broker").IsOk.Should().BeTrue();
        var victime = Victime();
        Jonction(Path.Combine(Path.GetDirectoryName(exe)!, "donnees"), victime.Dossier);
        File.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(exe)!, "moteur.dll"), victime.Fichier);
        var avant = EtatDeLaVictime(victime);

        Droits().Rendre("Broker").IsOk.Should().BeTrue();

        Etat(exe).Should().Be(origine);
        EtatDeLaVictime(victime).Item1.Should().Equal(avant.Item1);
        EtatDeLaVictime(victime).Item2.Should().Equal(avant.Item2);
    }

    // La pose sur le dossier ne propage rien d'elle-même : c'est la descente, qui ne suit aucun lien, qui rend l'héritage.
    [Fact]
    public void Poser_la_DACL_du_dossier_ne_propage_rien_aux_enfants()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var voisin = Path.Combine(Path.GetDirectoryName(exe)!, "voisin.dll");
        File.WriteAllBytes(voisin, [0]);
        var avant = SddlDe(new FileInfo(voisin));

        DroitsDesServices.PoserSurLeDossierSeul(Path.GetDirectoryName(exe)!, "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");

        SddlDe(new FileInfo(voisin)).Should().Be(avant);
    }

    // Une DLL voisine qui héritait l'écriture du compte la perd avec la correction (le service la chargerait), et la retrouve
    // avec Annuler ; un sous-dossier aussi.
    [Fact]
    public void Les_enfants_ordinaires_perdent_l_ecriture_heritee_du_compte_et_la_retrouvent_a_l_annulation()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable();
        var voisin = Path.Combine(Path.GetDirectoryName(exe)!, "voisin.dll");
        File.WriteAllBytes(voisin, [0]);
        var sous = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "plugins"));
        var profond = Path.Combine(sous.FullName, "plugin.dll");
        File.WriteAllBytes(profond, [0]);
        var avant = (SddlDe(new FileInfo(voisin)), SddlDe(sous), SddlDe(new FileInfo(profond)));
        bool EcritureDuCompte(FileSystemSecurity acl) => acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Value == Compte && (r.FileSystemRights & FileSystemRights.WriteData) != 0);
        EcritureDuCompte(new FileInfo(voisin).GetAccessControl()).Should().BeTrue("témoin");

        Droits().Corriger("Broker").IsOk.Should().BeTrue();

        EcritureDuCompte(new FileInfo(voisin).GetAccessControl()).Should().BeFalse();
        EcritureDuCompte(sous.GetAccessControl()).Should().BeFalse();
        EcritureDuCompte(new FileInfo(profond).GetAccessControl()).Should().BeFalse();

        Droits().Rendre("Broker").IsOk.Should().BeTrue();

        (SddlDe(new FileInfo(voisin)), SddlDe(sous), SddlDe(new FileInfo(profond))).Should().Be(avant);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Privileges
    {
        public uint Nombre;
        public uint LuidBas;
        public int LuidHaut;
        public uint Attributs;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processus, uint acces, out IntPtr jeton);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systeme, string nom, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr jeton, bool tout, ref Privileges etat, uint taille, IntPtr avant, IntPtr tailleAvant);

    /// <summary>Le privilège de restauration, le temps d'une préparation du test : il permet de donner un dossier à un autre
    /// compte. Le helper, lui, ne l'active jamais.</summary>
    private static void AvecLaRestauration(Action faire)
    {
        OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x28, out var jeton).Should().BeTrue();
        LookupPrivilegeValueW(null, "SeRestorePrivilege", out var luid).Should().BeTrue();
        var p = new Privileges { Nombre = 1, LuidBas = (uint)luid, LuidHaut = (int)(luid >> 32), Attributs = 2 };
        AdjustTokenPrivileges(jeton, false, ref p, 16, IntPtr.Zero, IntPtr.Zero).Should().BeTrue();
        try { faire(); }
        finally
        {
            p.Attributs = 0;
            AdjustTokenPrivileges(jeton, false, ref p, 16, IntPtr.Zero, IntPtr.Zero);
            Natif.CloseHandle(jeton);
        }
    }

    // Le dossier appartenait à un autre compte : le rendre demanderait un privilège que le helper n'active pas. L'échec dit que
    // l'origine n'est pas revenue, et elle reste gardée pour un Annuler.
    [Fact]
    public void Une_origine_qui_ne_se_remet_pas_se_dit_a_moitie_et_reste_gardee()
    {
        if (!Eleve) return;
        Directory.CreateDirectory(Path.Combine(_racine, "Jeu"));
        var exe = ServiceRemplacable(sousDossier: Path.Combine("Jeu", "service"));
        AvecLaRestauration(() =>
        {
            var proprio = new DirectorySecurity();
            proprio.SetOwner(new SecurityIdentifier("S-1-5-19"));
            new DirectoryInfo(Path.GetDirectoryName(exe)!).SetAccessControl(proprio);
        });

        var r = Droits().Corriger("Broker");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
        File.Exists(Droits().CheminDeLOrigine("Broker")).Should().BeTrue("Annuler pourra la rendre");
    }

    // Un nom court 8.3 n'est pas le chemin final : refusé comme un lien, et le message le dit.
    [Fact]
    public void Un_chemin_en_nom_court_est_refuse_et_le_message_le_dit()
    {
        if (!Eleve) return;
        var exe = ServiceRemplacable(sousDossier: "dossier au nom long");
        var court = new System.Text.StringBuilder(512);
        if (GetShortPathNameW(exe, court, 512) == 0 || string.Equals(court.ToString(), exe, StringComparison.OrdinalIgnoreCase)) return; // pas de noms courts sur ce volume
        _services["Broker"] = new DroitsDesServices.ServiceLu($"\"{court}\"", "LocalSystem");

        var r = Droits().Corriger("Broker");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("8.3");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longue, System.Text.StringBuilder courte, uint taille);

    [Fact]
    public void Les_services_corriges_se_lisent_par_leur_origine_gardee()
    {
        if (!Eleve) return;
        ServiceRemplacable("Broker");
        Droits().Corriger("Broker").IsOk.Should().BeTrue();

        DroitsDesServices.ServicesCorriges(Origines).Should().Equal("Broker");
        DroitsDesServices.ServicesCorriges(Path.Combine(_racine, "rien")).Should().BeEmpty();
    }
}
