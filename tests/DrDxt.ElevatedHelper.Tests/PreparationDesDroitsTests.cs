using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Lanceur;
using FluentAssertions;
using DrDxt.Tests.Shared;

namespace DrDxt.ElevatedHelper.Tests;

// L'installeur, élevé, préparait ses dossiers avec icacls : /setowner et /reset récursifs suivaient une jonction posée par
// le compte dans un dossier qu'il possédait (celui d'une 0.9.1), et menaient le changement de droits sur n'importe quel
// arbre. Le lanceur natif ne suit jamais un lien et n'écrit rien sur ce que le compte contrôle : il le renomme de côté.
// On appelle ici le module tel que le lanceur l'exécute en mode « droits ».
public sealed class PreparationDesDroitsTests : IDisposable
{
    private const string SddlApp = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;OW)";
    private const string SddlDonnees = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;AU)";

    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"droits-{Guid.NewGuid():N}");

    public PreparationDesDroitsTests() => Directory.CreateDirectory(_dossier);

    public void Dispose() => Effacer(_dossier);

    // Un lien s'efface lui-même ; la suppression ne descend jamais dans sa cible.
    private static void Effacer(string chemin)
    {
        var attributs = File.GetAttributes(chemin);
        if (attributs.HasFlag(FileAttributes.Directory) && !attributs.HasFlag(FileAttributes.ReparsePoint))
            foreach (var e in Directory.GetFileSystemEntries(chemin)) Effacer(e);
        if (attributs.HasFlag(FileAttributes.Directory)) Directory.Delete(chemin);
        else File.Delete(chemin);
    }

    private static bool Eleve() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    // Le module tel que le lanceur l'exécute, en mémoire : les lignes « ecarte … » sont captées au lieu d'aller à la console.
    private static (int Code, string Sortie) Lancer(string mode, string chemin, string sddl)
    {
        var lignes = new List<string>();
        var code = PreparationDesDroits.Preparer(mode, chemin, sddl, lignes.Add);
        return (code, string.Join("\n", lignes) + "\n");
    }

    private static void Jonction(string lien, string cible)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{lien}\" \"{cible}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        File.GetAttributes(lien).HasFlag(FileAttributes.ReparsePoint).Should().BeTrue("témoin : la jonction existe");
    }

    // Le propriétaire par défaut d'un objet créé élevé dépend du jeton (le compte, ou les Administrateurs) : celui des
    // fichiers que [Files] pose est fixé ici au compte, le cas qui n'est pas fiable.
    private static void AuCompte(string chemin)
    {
        var moi = WindowsIdentity.GetCurrent().User!;
        if (Directory.Exists(chemin))
        {
            var d = new DirectoryInfo(chemin).GetAccessControl();
            d.SetOwner(moi);
            new DirectoryInfo(chemin).SetAccessControl(d);
        }
        else
        {
            var f = new FileInfo(chemin).GetAccessControl();
            f.SetOwner(moi);
            new FileInfo(chemin).SetAccessControl(f);
        }
    }

    private static string Canonique(string sddl)
    {
        var s = new DirectorySecurity();
        s.SetSecurityDescriptorSddlForm(sddl);
        return s.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
    }

    private static string Sddl(string dossier) => new DirectoryInfo(dossier).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);

    private string CibleProtegee()
    {
        var cible = Path.Combine(_dossier, "cible");
        Directory.CreateDirectory(cible);
        File.WriteAllText(Path.Combine(cible, "secret.txt"), "s");
        var acl = new DirectoryInfo(cible).GetAccessControl();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(cible).SetAccessControl(acl);
        return cible;
    }

    // Un dossier de l'App au compte, une jonction dedans : il part de côté en entier, la cible ne change pas. Élevé, le
    // dossier neuf naît verrouillé ; non élevé, il ne peut pas naître aux Administrateurs et le script le dit.
    [Fact]
    public void Un_dossier_du_compte_part_de_cote_sans_que_sa_jonction_soit_suivie()
    {
        var cible = CibleProtegee();
        var avant = Sddl(cible);
        var app = Path.Combine(_dossier, "DrDXT");
        Directory.CreateDirectory(Path.Combine(app, "service"));
        File.WriteAllText(Path.Combine(app, "DrDxt.App.exe"), "a");
        Jonction(Path.Combine(app, "lien"), cible);

        var (code, sortie) = Lancer("app", app, SddlApp);

        Sddl(cible).Should().Be(avant, "la cible de la jonction n'est jamais touchée");
        sortie.Should().Contain("ecarte " + app + ".ecarte-");
        Directory.GetDirectories(_dossier, "DrDXT.ecarte-*").Should().ContainSingle()
            .Which.Should().Match(d => File.Exists(Path.Combine(d, "DrDxt.App.exe")), "l'ancien dossier est renommé, pas vidé");
        if (Eleve())
        {
            code.Should().Be(0);
            Sddl(app).Should().Be(Canonique(SddlApp), "le dossier neuf naît avec son descripteur");
        }
        else code.Should().NotBe(0, "sans élévation, rien ne naît aux Administrateurs");
    }

    // La racine elle-même en jonction : c'est la jonction qui part de côté, pas sa cible.
    [Fact]
    public void Une_racine_en_jonction_part_de_cote_sans_etre_suivie()
    {
        var cible = CibleProtegee();
        var avant = Sddl(cible);
        var app = Path.Combine(_dossier, "DrDXT");
        Jonction(app, cible);

        Lancer("app", app, SddlApp);

        Sddl(cible).Should().Be(avant);
        File.Exists(Path.Combine(cible, "secret.txt")).Should().BeTrue();
        Directory.GetDirectories(_dossier, "DrDXT.ecarte-*").Should().ContainSingle()
            .Which.Should().Match(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint));
    }

    // Une cible qui a l'air fiable (le descripteur même du dossier de l'App) : seul le refus des points d'analyse écarte la
    // jonction qui y mène. Élevé seulement : seul un administrateur la donne aux Administrateurs.
    private string CibleFiable()
    {
        var cible = Path.Combine(_dossier, "cible");
        Directory.CreateDirectory(cible);
        var protegee = new DirectorySecurity();
        protegee.SetSecurityDescriptorSddlForm(SddlApp);
        new DirectoryInfo(cible).SetAccessControl(protegee);
        return cible;
    }

    // Une racine en jonction posée par un administrateur, vers un dossier qui a l'air fiable : la jonction et sa cible portent
    // le descripteur du dossier de l'App, l'installeur y poserait ses fichiers. Seul son point d'analyse la trahit : elle part
    // de côté.
    [FactAdmin]
    public void Une_racine_en_jonction_vers_un_dossier_d_apparence_fiable_part_de_cote()
    {
        var cible = CibleFiable();
        var avant = Sddl(cible);
        var app = Path.Combine(_dossier, "DrDXT");
        Jonction(app, cible);
        var lien = new DirectorySecurity();
        lien.SetSecurityDescriptorSddlForm(SddlApp);
        new DirectoryInfo(app).SetAccessControl(lien);

        Lancer("app", app, SddlApp).Code.Should().Be(0);

        Sddl(cible).Should().Be(avant);
        File.GetAttributes(app).HasFlag(FileAttributes.ReparsePoint).Should().BeFalse("le dossier neuf remplace la jonction");
        Directory.GetDirectories(_dossier, "DrDXT.ecarte-*").Should().ContainSingle();
    }

    // Après la copie, un arbre dont le compte possède un fichier n'est pas fiable.
    [Fact]
    public void La_verification_refuse_un_arbre_du_compte()
    {
        var app = Path.Combine(_dossier, "DrDXT");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "DrDxt.App.exe"), "a");

        Lancer("verifier", app, "").Code.Should().Be(1);
    }

    // Une racine des données fiable garde ce qui est aux Administrateurs et le fichier du compte (aucun lecteur élevé ne
    // le croit) ; un lien et un dossier du compte partent de côté, la cible du lien intacte. Le chemin complet, vérifié
    // tel quel : naissance verrouillée, rien de récursif sur ce que le compte contrôle.
    [FactAdmin]
    public void Les_donnees_ecartent_les_liens_et_les_dossiers_du_compte_et_gardent_le_reste()
    {
        var cible = CibleFiable();
        var avant = Sddl(cible);
        var racine = Path.Combine(_dossier, "DrDxt");
        Lancer("donnees", racine, SddlDonnees).Code.Should().Be(0);
        Sddl(racine).Should().Be(Canonique(SddlDonnees));
        var manifeste = Path.Combine(racine, "manifest.json");
        File.WriteAllText(manifeste, "m");
        var fs = new FileInfo(manifeste).GetAccessControl();
        fs.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new FileInfo(manifeste).SetAccessControl(fs);
        File.WriteAllText(Path.Combine(racine, "helper.journal.log"), "j");
        AuCompte(Path.Combine(racine, "helper.journal.log"));
        Directory.CreateDirectory(Path.Combine(racine, "journaux"));
        AuCompte(Path.Combine(racine, "journaux"));
        Jonction(Path.Combine(racine, "piege"), cible);

        var (code, _) = Lancer("donnees", racine, SddlDonnees);

        code.Should().Be(0);
        Sddl(cible).Should().Be(avant);
        Directory.EnumerateFileSystemEntries(racine).Select(Path.GetFileName).Should().Contain(["manifest.json", "helper.journal.log"])
            .And.NotContain(["journaux", "piege"]);
        Directory.GetDirectories(racine, "ecarte-*").Should().HaveCount(2);
    }

    // Un dossier de l'App neuf, puis des fichiers copiés au compte : la vérification les refuse tant qu'ils ne sont pas
    // passés aux Administrateurs, puis les accepte ; un dossier fiable reste à sa place.
    [FactAdmin]
    public void Un_dossier_neuf_se_verifie_apres_le_changement_de_proprietaire_des_fichiers_copies()
    {
        var app = Path.Combine(_dossier, "DrDXT");
        Lancer("app", app, SddlApp).Code.Should().Be(0);
        Directory.CreateDirectory(Path.Combine(app, "service"));
        File.WriteAllText(Path.Combine(app, "service", "hote.exe"), "h");
        AuCompte(Path.Combine(app, "service"));
        AuCompte(Path.Combine(app, "service", "hote.exe"));
        Lancer("verifier", app, "").Code.Should().Be(1, "les fichiers copiés appartiennent au compte");

        using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "icacls.exe"),
                   $"\"{app}\\*\" /setowner *S-1-5-32-544 /T /C /Q") { UseShellExecute = false, CreateNoWindow = true })!)
            p.WaitForExit();

        Lancer("verifier", app, "").Code.Should().Be(0);
        Lancer("app", app, SddlApp).Sortie.Should().NotContain("ecarte", "un dossier fiable ne part pas de côté");
    }
}
