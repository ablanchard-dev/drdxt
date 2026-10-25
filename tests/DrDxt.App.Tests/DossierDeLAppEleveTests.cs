using System.Diagnostics;
using System.IO;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// L'App relancée en administrateur écrit sous un dossier que tout programme du compte peut modifier. Une jonction posée
// d'avance faisait du « Oui » de l'invite une écriture ou une suppression en administrateur ailleurs. Vraies jonctions et
// vrais liens durs (mklink, sans droits), dans un dossier temporaire.
public sealed class DossierDeLAppEleveTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("drdxt-eleve-").FullName;
    private string Racine => Path.Combine(_tmp, "DrDXT");
    private string Ailleurs => Directory.CreateDirectory(Path.Combine(_tmp, "ailleurs")).FullName;

    public void Dispose()
    {
        // Les jonctions d'abord (rmdir ne suit pas une jonction), puis le reste.
        foreach (var d in Directory.EnumerateDirectories(_tmp, "*", SearchOption.AllDirectories).Reverse())
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) try { Directory.Delete(d); } catch { }
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static void Mklink(string option, string lien, string cible)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink {option} \"{lien}\" \"{cible}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        p.ExitCode.Should().Be(0, $"mklink {option} doit réussir sans droits");
    }

    [Fact]
    public void Un_dossier_propre_est_tenu_et_ses_sous_dossiers_crees()
    {
        using var tenu = DossierDeLAppEleve.Verrouiller(Racine, out var raison);

        tenu.Should().NotBeNull(raison);
        DossierDeLAppEleve.SousDossiers.Should().HaveCount(2).And.OnlyContain(s => Directory.Exists(Path.Combine(Racine, s)));
    }

    [Fact]
    public void Un_dossier_de_donnees_qui_est_une_jonction_est_refuse()
    {
        Mklink("/J", Racine, Ailleurs);

        DossierDeLAppEleve.Verrouiller(Racine, out var raison).Should().BeNull();
        raison.Should().Contain("DrDXT");
    }

    [Fact]
    public void Un_sous_dossier_pose_d_avance_en_jonction_est_refuse()
    {
        Directory.CreateDirectory(Racine);
        Mklink("/J", Path.Combine(Racine, "logs"), Ailleurs);

        DossierDeLAppEleve.Verrouiller(Racine, out var raison).Should().BeNull();
        raison.Should().EndWith("logs");
    }

    // Plus bas que les dossiers tenus : une jonction au deuxième niveau d'un dossier que l'App ne crée pas est refusée aussi.
    [Fact]
    public void Une_jonction_au_deuxieme_niveau_est_refusee()
    {
        Directory.CreateDirectory(Path.Combine(Racine, "autre"));
        Mklink("/J", Path.Combine(Racine, "autre", "profond"), Ailleurs);

        DossierDeLAppEleve.Verrouiller(Racine, out var raison).Should().BeNull();
        raison.Should().EndWith("profond");
    }

    [Fact]
    public void Un_fichier_lien_dur_est_refuse()
    {
        Directory.CreateDirectory(Racine);
        var vrai = Path.Combine(Ailleurs, "vrai.json");
        File.WriteAllText(vrai, "{}");
        Mklink("/H", Path.Combine(Racine, "pending-revert.json"), vrai);

        DossierDeLAppEleve.Verrouiller(Racine, out var raison).Should().BeNull();
        raison.Should().EndWith("pending-revert.json");
    }

    // Une jonction posée APRÈS le contrôle, sous le nom d'un sous-dossier : il existe déjà et il est tenu, il ne peut être ni
    // supprimé ni renommé pour faire la place ; le dossier de données non plus.
    [Fact]
    public void Tenu_aucun_dossier_ne_peut_etre_remplace_par_une_jonction()
    {
        using var tenu = DossierDeLAppEleve.Verrouiller(Racine, out var raison);
        tenu.Should().NotBeNull(raison);

        var supprimer = () => Directory.Delete(Path.Combine(Racine, "diagnostics"));
        supprimer.Should().Throw<IOException>();
        var renommer = () => Directory.Move(Path.Combine(Racine, "logs"), Path.Combine(Racine, "logs-ailleurs"));
        renommer.Should().Throw<IOException>();
        var deplacer = () => Directory.Move(Racine, Path.Combine(_tmp, "DrDXT-ailleurs"));
        deplacer.Should().Throw<IOException>();
    }

    [Fact]
    public void Relache_les_dossiers_redeviennent_libres()
    {
        DossierDeLAppEleve.Verrouiller(Racine, out _)!.Dispose();

        var renommer = () => Directory.Move(Path.Combine(Racine, "logs"), Path.Combine(Racine, "logs-ailleurs"));
        renommer.Should().NotThrow();
    }

    // Avant toute écriture : la vérification est en tête du démarrage, avant le filet qui écrit le journal de panne.
    [Fact]
    public void Le_demarrage_eleve_verifie_le_dossier_avant_toute_ecriture()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var app = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "App.xaml.cs"));
        var debut = app.IndexOf("protected override void OnStartup(", StringComparison.Ordinal);

        var verrou = app.IndexOf("DossierDeLAppEleve.Verrouiller(", debut, StringComparison.Ordinal);
        verrou.Should().BePositive();
        verrou.Should().BeLessThan(app.IndexOf("DispatcherUnhandledException", debut, StringComparison.Ordinal));
        verrou.Should().BeLessThan(app.IndexOf("new ServiceCollection()", debut, StringComparison.Ordinal));
    }
    // Une panne pendant le démarrage, avant que la fenêtre soit montrée : le filet l'écrit, puis l'App s'arrête. Sans cela, le
    // processus restait sans fenêtre, gardait la place de l'App, et chaque relance ne faisait que le réveiller.
    [Fact]
    public void Une_panne_avant_la_fenetre_arrete_l_App_au_lieu_de_la_laisser_sans_fenetre()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var app = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "App.xaml.cs"));
        var debut = app.IndexOf("DispatcherUnhandledException += ", StringComparison.Ordinal);
        debut.Should().BePositive("témoin : le filet est trouvé");
        var filet = app[debut..app.IndexOf("};", debut, StringComparison.Ordinal)];

        filet.Should().Contain("ReportFatal(args.Exception);")
            .And.Contain("MainWindow is not { IsVisible: true }")
            .And.Contain("Shutdown(1);")
            .And.Contain("ArgumentReleve", "le relevé de fond tourne sans fenêtre : il ne s'arrête pas pour autant");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string nom, uint acces, FileShare partage, IntPtr securite,
        FileMode creation, uint drapeaux, IntPtr modele);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] entree, int tailleEntree,
        IntPtr sortie, int tailleSortie, out int rendu, IntPtr o);

    // Ce qu'un programme du compte enverrait : FSCTL_SET_REPARSE_POINT d'une jonction vers « cible », par un handle ouvert
    // seulement pour écrire les attributs (cet accès échappe aux contrôles de partage). Vrai si la jonction est posée.
    private static bool PoserUneJonction(string dossier, string cible)
    {
        var sub = System.Text.Encoding.Unicode.GetBytes(@"\??\" + cible);
        var print = System.Text.Encoding.Unicode.GetBytes(cible);
        var donnees = 8 + sub.Length + 2 + print.Length + 2;
        var b = new byte[8 + donnees];
        BitConverter.GetBytes(0xA0000003u).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)donnees).CopyTo(b, 4);
        BitConverter.GetBytes((ushort)sub.Length).CopyTo(b, 10);
        BitConverter.GetBytes((ushort)(sub.Length + 2)).CopyTo(b, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(b, 14);
        sub.CopyTo(b, 16);
        print.CopyTo(b, 16 + sub.Length + 2);
        const uint EcrireLesAttributs = 0x0100, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        using var h = CreateFileW(dossier, EcrireLesAttributs, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        return !h.IsInvalid && DeviceIoControl(h, 0x000900A4, b, b.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    // Un dossier tenu mais vide recevait une jonction : l'accès aux attributs qui suffit à la poser échappe au partage. Tenu,
    // chaque dossier garde un fichier témoin : jamais vide, il refuse la jonction ; l'App y écrit toujours atomiquement.
    [Fact]
    public void Un_dossier_tenu_refuse_une_jonction_et_reste_ouvert_aux_ecritures_de_l_App()
    {
        using var tenu = DossierDeLAppEleve.Verrouiller(Racine, out var raison);
        tenu.Should().NotBeNull(raison);
        var logs = Path.Combine(Racine, "logs");

        PoserUneJonction(logs, Ailleurs).Should().BeFalse("un dossier tenu n'est jamais vide");
        new Action(() => File.Delete(Path.Combine(logs, DossierDeLAppEleve.Temoin))).Should().Throw<IOException>("le témoin est tenu");
        new DrDxt.Core.FichierJsonAtomique(Path.Combine(Racine, "f.json"), "E").WriteAtomic("[1]").IsOk.Should().BeTrue();
        new DrDxt.Core.FichierJsonAtomique(Path.Combine(logs, "f.json"), "E").WriteAtomic("[2]").IsOk.Should().BeTrue();
    }

    // Un dossier non vide ne suffit pas s'il se déplace : (a) renommer le dossier tenu, ou (b) son parent, puis recréer un
    // dossier du même nom en jonction. Tenus, ni l'un ni l'autre ne se renomme.
    [Fact]
    public void Ni_le_dossier_tenu_ni_son_parent_ne_se_renomment()
    {
        using var tenu = DossierDeLAppEleve.Verrouiller(Racine, out var raison);
        tenu.Should().NotBeNull(raison);
        var logs = Path.Combine(Racine, "logs");

        new Action(() => Directory.Move(logs, logs + "-deplace")).Should().Throw<IOException>("(a) le dossier tenu");
        new Action(() => Directory.Move(Racine, Racine + "-deplace")).Should().Throw<IOException>("(b) son parent, tenu lui aussi");
        new Action(() => Directory.Move(_tmp, _tmp + "-deplace")).Should().Throw<IOException>("(b) le parent du dossier de données");
    }
}
