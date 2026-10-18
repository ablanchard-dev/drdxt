using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DrDxt.App.Platform;

/// <summary>
/// La machine exportée et le rapport de « Vérifier DrDXT » vont dans Documents, hors du dossier de données que l'App élevée
/// tient (<see cref="DossierDeLAppEleve"/>). Élevée, elle y écrivait sans contrôle : une jonction posée d'avance sur Documents
/// ou un parent, ou un lien posé sous le nom du fichier, faisait de l'écriture une écriture en administrateur ailleurs. Élevée,
/// elle ouvre Documents et chaque parent jusqu'au profil sans suivre de lien et sans partage de suppression, refuse une
/// jonction ou un lien symbolique lus sur ces handles mêmes, les tient jusqu'à la fin de l'écriture (aucun ne peut être
/// renommé ni remplacé entre le contrôle et la création), et crée le fichier sans jamais ouvrir un nom qui existe déjà. Les
/// autres points de reparse sont admis : Documents sous OneDrive porte une étiquette de fichiers cloud. Sans élévation, rien
/// ne change.
/// </summary>
public static class EcritureDansDocuments
{
    private const uint Jonction = 0xA0000003, LienSymbolique = 0xA000000C;

    /// <param name="etiquette">L'étiquette de reparse d'un dossier, ou null s'il n'en porte pas ; par défaut, celle lue sur le
    /// handle tenu. Couture de test : une étiquette cloud.</param>
    /// <param name="pendantLEcriture">Couture de test : appelée dossiers tenus, juste avant la création.</param>
    public static string Ecrire(string documents, string nomDeFichier, string contenu, bool eleve, string? profil = null,
        Func<string, uint?>? etiquette = null, Action? pendantLEcriture = null)
    {
        var chemin = Path.Combine(documents, nomDeFichier);
        var octets = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contenu);
        if (!eleve)
        {
            File.WriteAllBytes(chemin, octets);
            return chemin;
        }
        var limite = Path.TrimEndingDirectorySeparator(profil ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var tenus = new List<SafeFileHandle>();
        try
        {
            for (var d = new DirectoryInfo(documents); d is not null; d = d.Parent)
            {
                // Lister le dossier : un handle sans accès n'entre pas dans les contrôles de partage, et ne bloquerait rien.
                var h = CreateFileW(d.FullName, ListerLeDossier, FileShare.Read | FileShare.Write, IntPtr.Zero, FileMode.Open,
                    BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                if (h.IsInvalid) throw new IOException($"{d.FullName} : {Marshal.GetLastPInvokeErrorMessage()}");
                tenus.Add(h);
                if ((etiquette is not null ? etiquette(d.FullName) : EtiquetteDe(h)) is Jonction or LienSymbolique)
                    throw new IOException(DrDxt.Core.Docteur.T("App.DocumentsAvecLien", d.FullName));
                if (string.Equals(Path.TrimEndingDirectorySeparator(d.FullName), limite, StringComparison.OrdinalIgnoreCase)) break;
            }
            pendantLEcriture?.Invoke();
            using var f = CreateFileW(chemin, EcrireLesDonnees | Supprimer, FileShare.None, IntPtr.Zero, FileMode.CreateNew, 0, IntPtr.Zero);
            if (f.IsInvalid)
            {
                if (File.Exists(chemin) || Directory.Exists(chemin) || new FileInfo(chemin).LinkTarget is not null)
                    throw new IOException(DrDxt.Core.Docteur.T("App.DocumentsNomPris", chemin));
                throw new IOException($"{chemin} : {Marshal.GetLastPInvokeErrorMessage()}");
            }
            // Un Documents VIDE reçoit une jonction même tenu : l'accès aux attributs qui suffit à la poser échappe au
            // partage. Le fichier créé est relu par son chemin final ; hors du Documents tenu, il est retiré par son handle
            // (rien n'y est écrit) et l'écriture refusée.
            if (!string.Equals(Path.GetDirectoryName(CheminFinal(f)), CheminFinal(tenus[0]), StringComparison.OrdinalIgnoreCase))
            {
                var supprimer = new InfoSuppression { Supprimer = 1 };
                SetFileInformationByHandle(f, FileDispositionInfo, ref supprimer, (uint)Marshal.SizeOf<InfoSuppression>());
                throw new IOException(DrDxt.Core.Docteur.T("App.DocumentsAvecLien", documents));
            }
            using (var flux = new FileStream(f, FileAccess.Write)) flux.Write(octets);
            return chemin;
        }
        finally
        {
            foreach (var h in tenus) h.Dispose();
        }
    }

    private static uint? EtiquetteDe(SafeFileHandle h)
        => GetFileInformationByHandleEx(h, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<InfoEtiquette>())
           && (info.Attributs & (uint)FileAttributes.ReparsePoint) != 0 ? info.Etiquette : null;

    private const uint ListerLeDossier = 0x0001, EcrireLesDonnees = 0x0002, Supprimer = 0x00010000,
        BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfo = 9, FileDispositionInfo = 4;

    /// <summary>Le chemin final d'un handle (liens résolus), sans le préfixe \\?\.</summary>
    private static string CheminFinal(SafeFileHandle h)
    {
        var tampon = new System.Text.StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, tampon, (uint)tampon.Capacity, 0);
        if (n == 0 || n >= tampon.Capacity) throw new IOException(Marshal.GetLastPInvokeErrorMessage());
        var s = tampon.ToString();
        return Path.TrimEndingDirectorySeparator(s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + s[8..]
            : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoSuppression
    {
        public byte Supprimer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, System.Text.StringBuilder tampon, uint taille, uint drapeaux);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int classe, ref InfoSuppression info, uint taille);

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoEtiquette
    {
        public uint Attributs, Etiquette;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string nom, uint acces, FileShare partage, IntPtr securite, FileMode creation,
        uint drapeaux, IntPtr modele);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int classe, out InfoEtiquette info, uint taille);
}
