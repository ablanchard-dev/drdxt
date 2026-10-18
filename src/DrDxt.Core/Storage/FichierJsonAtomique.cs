namespace DrDxt.Core;

/// <summary>
/// Un fichier JSON ecrit sans jamais etre lu a moitie.
///
/// Fichier temporaire a cote de la cible (meme volume, donc le deplacement est un vrai renommage),
/// puis remplacement. Un lecteur ne voit jamais un fichier a demi ecrit, et **une reference tronquee
/// serait pire qu'absente** : elle se comparerait quand meme.
///
/// Le piege que cette forme evite : ouvrir la cible en ecriture la
/// tronque avant d'ecrire quoi que ce soit. Une panne a cet instant precis laisse un fichier vide
/// la ou il y avait une mesure.
///
/// Vit dans Core parce que cinq magasins en ont besoin (references memoire et CPU, journal de
/// performance, journal des constats, marqueur BIOS) : une correction faite ici les atteint tous. Toutes les frontieres sont enveloppees : aucune exception ne remonte.
/// </summary>
public sealed class FichierJsonAtomique
{
    private readonly string _chemin;
    private readonly string _codeErreur;

    /// <param name="chemin">La cible.</param>
    /// <param name="codeErreur">Le code que porteront ses echecs. Chaque magasin garde le sien :
    /// un appelant qui distingue ses erreurs par leur code ne doit pas les voir fusionner.</param>
    public FichierJsonAtomique(string chemin, string codeErreur)
    {
        _chemin = string.IsNullOrWhiteSpace(chemin)
            ? throw new ArgumentException("A path is required.", nameof(chemin))
            : chemin;
        _codeErreur = codeErreur;
    }

    /// <summary>Un chemin sous le dossier local de l'utilisateur.</summary>
    public static string CheminLocal(string nomFichier)
        => DrDxt.Core.DossierDonnees.Chemin(nomFichier);

    public Result WriteAtomic(string json) => WriteAtomic(new System.Text.UTF8Encoding(false).GetBytes(json));

    /// <summary>Le nom d'un temporaire a cote de <paramref name="chemin"/> : aleatoire, pour qu'aucun lien dur ni fichier
    /// pose a l'avance ne l'occupe. A creer en exclusif (FileMode.CreateNew).</summary>
    public static string NomTemporaire(string chemin) => chemin + "." + Guid.NewGuid().ToString("N") + ".tmp";

    public Result WriteAtomic(byte[] octets)
    {
        string? tmp = null;
        try
        {
            var dossier = Path.GetDirectoryName(_chemin);
            if (!string.IsNullOrEmpty(dossier) && !Directory.Exists(dossier))
                Directory.CreateDirectory(dossier);

            // Le temporaire est force sur le disque avant le renommage : sinon, apres une coupure, le renommage (journalise par
            // le systeme de fichiers) peut avoir pris et le contenu non, et la cible est illisible. Ce sont des journaux
            // d'annulation : illisibles, plus d'Annuler ni de retrait automatique.
            // Nom aleatoire, cree en exclusif : un lien dur ou un fichier pose a l'avance sous un nom previsible
            // (« <fichier>.tmp ») ferait tronquer et reecrire sa cible, en administrateur quand l'App est elevee.
            tmp = NomTemporaire(_chemin);
            using (var flux = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                flux.Write(octets);
                flux.Flush(flushToDisk: true);
            }
            File.Move(tmp, _chemin, overwrite: true);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            if (tmp is not null) try { File.Delete(tmp); } catch (Exception) { /* nettoyage au mieux */ }
            return Result.Fail(_codeErreur, $"Ecriture impossible : {ex.Message}");
        }
    }

    /// <summary>Allonge un journal (le cree s'il manque) sans jamais ecrire a travers un lien : ouvert sans suivre de point de
    /// reparse, il n'est ecrit que s'il n'a qu'un nom. Un nom pose d'avance en lien dur ou symbolique dans un dossier du
    /// compte ferait sinon ecrire l'App elevee dans un autre fichier. Ne leve jamais.</summary>
    /// <param name="encodage">Par defaut UTF-8 sans BOM ; le preambule n'est ecrit que dans un fichier vide.</param>
    public static Result AjouterSansLien(string chemin, string texte, string codeErreur, System.Text.Encoding? encodage = null)
    {
        try
        {
            var dossier = Path.GetDirectoryName(chemin);
            if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);
            using var h = CreateFileW(chemin, AjouterDesDonnees | LireLesAttributs, FileShare.Read, IntPtr.Zero, FileMode.OpenOrCreate,
                OuvrirLePointDeReparse, IntPtr.Zero);
            if (h.IsInvalid)
                return Result.Fail(codeErreur, $"Ajout impossible : {System.Runtime.InteropServices.Marshal.GetLastPInvokeErrorMessage()}");
            if (!GetFileInformationByHandle(h, out var info) || info.Liens != 1 || (info.Attributs & (uint)FileAttributes.ReparsePoint) != 0)
                return Result.Fail(codeErreur, "Ajout refuse : le journal est un lien.");
            using var flux = new FileStream(h, FileAccess.Write);
            flux.Seek(0, SeekOrigin.End);
            var e = encodage ?? new System.Text.UTF8Encoding(false);
            if (flux.Position == 0) flux.Write(e.GetPreamble());
            flux.Write(e.GetBytes(texte));
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(codeErreur, $"Ajout impossible : {ex.Message}");
        }
    }

    // FILE_APPEND_DATA | FILE_READ_ATTRIBUTES ; FILE_FLAG_OPEN_REPARSE_POINT.
    private const uint AjouterDesDonnees = 0x0004, LireLesAttributs = 0x0080, OuvrirLePointDeReparse = 0x00200000;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct InfoFichier
    {
        public uint Attributs;
        public uint Creation1, Creation2, Acces1, Acces2, Ecriture1, Ecriture2;
        public uint Volume, TailleHaute, TailleBasse, Liens, IndexHaut, IndexBas;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string nom, uint acces, FileShare partage, IntPtr securite,
        FileMode creation, uint drapeaux, IntPtr modele);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out InfoFichier info);

    /// <summary>Un fichier lu mais illisible est renomme <c>&lt;fichier&gt;.mis-de-cote-&lt;horodatage UTC&gt;</c> (meme convention que
    /// le manifeste) : sa trace reste, et le magasin repart d'un fichier neuf au lieu d'etre ecrase ou bloque. Faux si le
    /// renommage echoue : rien ne doit alors etre ecrit par-dessus.</summary>
    public bool MettreDeCote()
    {
        try
        {
            File.Move(_chemin, _chemin + ".mis-de-cote-"
                + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture), overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public Result<string?> Read()
    {
        try
        {
            return File.Exists(_chemin)
                ? Result<string?>.Ok(File.ReadAllText(_chemin))
                : Result<string?>.Ok(null);
        }
        catch (Exception ex)
        {
            return Result<string?>.Fail(_codeErreur, $"Lecture impossible : {ex.Message}");
        }
    }
}
