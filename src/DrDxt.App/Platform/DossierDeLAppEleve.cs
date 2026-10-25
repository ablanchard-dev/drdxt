using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DrDxt.App.Platform;

/// <summary>
/// L'App relancée en administrateur (« Relancer en administrateur », « --analyser ») écrit, renomme et supprime sous
/// <c>%LOCALAPPDATA%\DrDXT</c>, un dossier que tout programme du compte peut modifier. Une jonction posée d'avance (vers
/// <c>\RPC Control</c> et des liens objet, ou vers un dossier de Windows) faisait du « Oui » de l'invite une écriture ou une
/// suppression en administrateur ailleurs. Avant la moindre écriture, l'App élevée crée les sous-dossiers qu'elle utilise,
/// vérifie sans suivre les liens que ni le dossier, ni un sous-dossier, ni un fichier n'est un point de reparse ou un lien dur,
/// puis tient le dossier et ses sous-dossiers ouverts sans partage de suppression, chacun avec un fichier témoin tenu de même :
/// tant qu'elle tourne, aucun ne peut être renommé, ni vidé pour recevoir une jonction.
/// La création des dossiers précède leur vérification, et les dossiers parents sous le profil ne sont pas contrôlés : seule la
/// vérification faite sur le handle tenu garantit qu'aucune écriture élevée ne part ailleurs, et rien n'est écrit avant elle.
/// </summary>
public sealed class DossierDeLAppEleve : IDisposable
{
    /// <summary>Les sous-dossiers que l'App crée sous ses données (journaux, rapports de diagnostic).</summary>
    public static readonly string[] SousDossiers = ["diagnostics", "logs"];

    private readonly List<SafeFileHandle> _tenus;
    private DossierDeLAppEleve(List<SafeFileHandle> tenus) => _tenus = tenus;

    /// <summary>Le dossier tenu, ou null avec la raison : l'App élevée ne doit alors rien écrire.</summary>
    public static DossierDeLAppEleve? Verrouiller(string racine, out string? raison)
    {
        var tenus = new List<SafeFileHandle>();
        try
        {
            Directory.CreateDirectory(racine);
            raison = Tenir(racine, tenus);
            if (raison is null)
                foreach (var sous in SousDossiers)
                {
                    var chemin = Path.Combine(racine, sous);
                    Directory.CreateDirectory(chemin);
                    if ((raison = Tenir(chemin, tenus)) is not null) break;
                }
            raison ??= LienDessous(racine);
            if (raison is null) return new DossierDeLAppEleve(tenus);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            raison = ex.Message;
        }
        foreach (var h in tenus) h.Dispose();
        return null;
    }

    public void Dispose()
    {
        foreach (var h in _tenus) h.Dispose();
    }

    // Ouvert sans suivre un point de reparse, et le handle lui-même est vérifié : ce qui est tenu est ce qui a été contrôlé.
    private static string? Tenir(string dossier, List<SafeFileHandle> tenus)
    {
        // Lister le dossier : un handle sans accès n'entre pas dans les contrôles de partage, et ne bloquerait rien.
        var h = CreateFileW(dossier, ListerLeDossier, FileShare.Read | FileShare.Write, IntPtr.Zero, FileMode.Open,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid) return $"{dossier} : {Marshal.GetLastPInvokeErrorMessage()}";
        tenus.Add(h);
        if (!GetFileInformationByHandle(h, out var info)) return $"{dossier} : {Marshal.GetLastPInvokeErrorMessage()}";
        if ((info.Attributs & (uint)FileAttributes.ReparsePoint) != 0) return dossier;

        // Un point de reparse ne se pose que sur un dossier VIDE, et l'accès aux attributs qui suffit à le poser échappe
        // aux contrôles de partage. Un fichier témoin tenu sans partage de suppression garde le dossier non vide tant que
        // l'App tourne. Ouvert en lecture seule : un témoin posé d'avance (lien dur) n'est jamais écrit, et LienDessous
        // le refuse ensuite.
        var temoin = CreateFileW(Path.Combine(dossier, Temoin), LireLesDonnees, FileShare.Read, IntPtr.Zero, FileMode.OpenOrCreate,
            OpenReparsePoint, IntPtr.Zero);
        if (temoin.IsInvalid) return $"{dossier} : {Marshal.GetLastPInvokeErrorMessage()}";
        tenus.Add(temoin);
        return null;
    }

    /// <summary>Le fichier témoin tenu dans chaque dossier.</summary>
    public const string Temoin = ".drdxt-tenu";

    // Un niveau à la fois : on ne descend que dans un dossier qui n'est pas un point de reparse, lu sur l'entrée elle-même. Rien
    // ne dépend de la façon dont une énumération récursive traite les liens.
    private static string? LienDessous(string racine)
    {
        var aVoir = new Stack<string>([racine]);
        while (aVoir.TryPop(out var dossier))
            foreach (var entree in new DirectoryInfo(dossier).EnumerateFileSystemInfos("*",
                         new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                if (entree.Attributes.HasFlag(FileAttributes.ReparsePoint)) return entree.FullName;
                if (entree is FileInfo) { if (LiensDurs(entree.FullName) is not 1) return entree.FullName; }
                else aVoir.Push(entree.FullName);
            }
        return null;
    }

    private static uint? LiensDurs(string fichier)
    {
        using var h = CreateFileW(fichier, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, OpenReparsePoint, IntPtr.Zero);
        return !h.IsInvalid && GetFileInformationByHandle(h, out var info) ? info.Liens : null;
    }

    private const uint ListerLeDossier = 0x0001, LireLesDonnees = 0x80000000, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoFichier
    {
        public uint Attributs;
        public uint Creation1, Creation2, Acces1, Acces2, Ecriture1, Ecriture2;
        public uint Volume, TailleHaute, TailleBasse, Liens, IndexHaut, IndexBas;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string nom, uint acces, FileShare partage, IntPtr securite, FileMode creation,
        uint drapeaux, IntPtr modele);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out InfoFichier info);
}
