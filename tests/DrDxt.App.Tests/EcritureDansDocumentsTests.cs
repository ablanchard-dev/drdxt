using System.Diagnostics;
using System.IO;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// L'App élevée écrit la machine exportée et le rapport de vérification sous Documents, hors du dossier qu'elle tient. Une
// jonction sur Documents ou un parent, ou un lien posé sous le nom du fichier, en faisait une écriture en administrateur
// ailleurs. Vraies jonctions et vrais liens durs (mklink, sans droits) ; l'étiquette cloud de OneDrive est simulée.
public sealed class EcritureDansDocumentsTests : IDisposable
{
    private readonly string _profil = Directory.CreateTempSubdirectory("drdxt-profil-").FullName;
    private string Documents => Path.Combine(_profil, "Documents");
    private string Ailleurs => Directory.CreateDirectory(Path.Combine(_profil, "ailleurs")).FullName;

    public void Dispose()
    {
        foreach (var d in Directory.EnumerateDirectories(_profil, "*", SearchOption.AllDirectories).Reverse())
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) try { Directory.Delete(d); } catch { }
        try { Directory.Delete(_profil, recursive: true); } catch { }
    }

    private static void Mklink(string option, string lien, string cible)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink {option} \"{lien}\" \"{cible}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        p.ExitCode.Should().Be(0, $"mklink {option} doit réussir sans droits");
    }

    private string Ecrire(string nom, bool eleve = true, Func<string, uint?>? etiquette = null)
        => EcritureDansDocuments.Ecrire(Documents, nom, "{}", eleve, _profil, etiquette);

    [Fact]
    public void Eleve_un_dossier_ordinaire_recoit_le_fichier()
    {
        Directory.CreateDirectory(Documents);

        File.ReadAllText(Ecrire("rapport.txt")).Should().Be("{}");
    }

    [Fact]
    public void Eleve_documents_en_jonction_est_refuse()
    {
        Mklink("/J", Documents, Ailleurs);

        var act = () => Ecrire("rapport.txt");

        act.Should().Throw<IOException>();
        File.Exists(Path.Combine(Ailleurs, "rapport.txt")).Should().BeFalse("rien n'est écrit à travers la jonction");
    }

    [Fact]
    public void Eleve_un_parent_en_jonction_est_refuse()
    {
        Mklink("/J", Path.Combine(_profil, "OneDrive"), Ailleurs);
        Directory.CreateDirectory(Path.Combine(Ailleurs, "Documents"));

        var act = () => EcritureDansDocuments.Ecrire(Path.Combine(_profil, "OneDrive", "Documents"), "rapport.txt", "{}", true, _profil);

        act.Should().Throw<IOException>();
    }

    // Documents sous OneDrive porte une étiquette de fichiers cloud (relevée sur la machine de référence : 0x9000601A sur
    // Documents, 0x9000701A sur OneDrive) : ce n'est pas un lien.
    [Fact]
    public void Eleve_une_etiquette_cloud_est_admise()
    {
        Directory.CreateDirectory(Documents);

        var chemin = Ecrire("rapport.txt", etiquette: d => d == Documents ? 0x9000601Au : d == _profil ? 0x9000701Au : null);

        File.ReadAllText(chemin).Should().Be("{}");
    }

    // Un lien dur posé d'avance sous le nom du fichier : la création échoue au lieu d'écrire à travers.
    [Fact]
    public void Eleve_un_nom_deja_pose_en_lien_dur_n_est_pas_ouvert()
    {
        Directory.CreateDirectory(Documents);
        var cible = Path.Combine(Ailleurs, "cible.txt");
        File.WriteAllText(cible, "intact");
        Mklink("/H", Path.Combine(Documents, "rapport.txt"), cible);

        var act = () => Ecrire("rapport.txt");

        act.Should().Throw<IOException>().Which.Message.Should().Contain("rapport.txt");
        File.ReadAllText(cible).Should().Be("intact");
        File.Exists(Path.Combine(Documents, "rapport.txt")).Should().BeTrue("rien n'est supprimé");
    }

    // Les deux écrivains de Documents passent par la garde.
    [Fact]
    public void L_export_et_le_rapport_passent_par_la_garde()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var s = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.App", "AppServiceCollectionExtensions.cs"));
        var ecrire = s[s.IndexOf("private static string EcrireDansDocuments(", StringComparison.Ordinal)..];
        ecrire = ecrire[..ecrire.IndexOf("private static", 1, StringComparison.Ordinal)];

        ecrire.Should().Contain("EcritureDansDocuments.Ecrire(").And.NotContain("File.WriteAllText");
        s.Should().Contain("return EcrireDansDocuments($\"verification-drdxt-").And.Contain("=> EcrireDansDocuments(nomDeFichier, json);");
    }

    // Entre le contrôle et la création, Documents ou un parent remplacé par une jonction (fenêtre élargie par un oplock) : les
    // dossiers sont tenus sans partage de suppression jusqu'à la fin de l'écriture, ni renommables ni supprimables.
    [Fact]
    public void Eleve_documents_et_ses_parents_sont_tenus_pendant_l_ecriture()
    {
        var sous = Directory.CreateDirectory(Path.Combine(_profil, "OneDrive", "Documents")).FullName;
        Exception? renommerDocuments = null, renommerParent = null;

        EcritureDansDocuments.Ecrire(sous, "rapport.txt", "{}", true, _profil, pendantLEcriture: () =>
        {
            renommerDocuments = Record.Exception(() => Directory.Move(sous, sous + "-ailleurs"));
            renommerParent = Record.Exception(() => Directory.Move(Path.Combine(_profil, "OneDrive"), Path.Combine(_profil, "OneDrive-ailleurs")));
        });

        renommerDocuments.Should().BeOfType<IOException>();
        renommerParent.Should().BeOfType<IOException>();
        File.ReadAllText(Path.Combine(sous, "rapport.txt")).Should().Be("{}");
        Directory.Move(sous, sous + "-ailleurs");   // relâchés après l'écriture
    }

    [Fact]
    public void Sans_elevation_rien_ne_change()
    {
        Directory.CreateDirectory(Documents);
        File.WriteAllText(Path.Combine(Documents, "rapport.txt"), "ancien");

        File.ReadAllText(Ecrire("rapport.txt", eleve: false)).Should().Be("{}", "le joueur écrit déjà là sans droits");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string nom, uint acces, FileShare partage, IntPtr securite,
        FileMode creation, uint drapeaux, IntPtr modele);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] entree, int tailleEntree,
        IntPtr sortie, int tailleSortie, out int rendu, IntPtr o);

    // FSCTL_SET_REPARSE_POINT d'une jonction, par un handle ouvert seulement pour écrire les attributs (accès qui échappe aux
    // contrôles de partage). Vrai si la jonction est posée.
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
        using var h = CreateFileW(dossier, 0x0100, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            0x02000000 | 0x00200000, IntPtr.Zero);
        return !h.IsInvalid && DeviceIoControl(h, 0x000900A4, b, b.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    // Documents VIDE, tenu : une jonction s'y pose quand même entre le contrôle et la création (l'accès aux attributs
    // échappe au partage). Le fichier créé est relu par son chemin final : hors de Documents, il est retiré et rien n'est
    // écrit ailleurs.
    [Fact]
    public void Eleve_une_jonction_posee_pendant_l_ecriture_sur_un_documents_vide_ne_fait_rien_ecrire_ailleurs()
    {
        Directory.CreateDirectory(Documents);
        var ailleurs = Ailleurs;
        var posee = false;

        var act = () => EcritureDansDocuments.Ecrire(Documents, "rapport.txt", "{}", eleve: true, _profil,
            pendantLEcriture: () => posee = PoserUneJonction(Documents, ailleurs));

        act.Should().Throw<IOException>();
        posee.Should().BeTrue("la jonction du test doit se poser");
        Directory.GetFiles(ailleurs).Should().BeEmpty();
    }
}
