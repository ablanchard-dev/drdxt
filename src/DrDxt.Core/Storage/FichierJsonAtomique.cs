namespace DrDxt.Core;

/// <summary>
/// Un fichier JSON ecrit sans jamais etre lu a moitie : temporaire a cote de la cible (meme volume, un vrai renommage),
/// puis remplacement. **Une reference tronquee serait pire qu'absente** : elle se comparerait quand meme. Ouvrir la
/// cible en ecriture la tronquerait d'abord, et une panne a cet instant laisserait un fichier vide a la place d'une mesure.
/// Dans Core : la plupart des magasins de DrDXT en ont besoin, une correction ici les atteint tous.
/// Lire et ecrire rendent un Result ; le constructeur refuse un chemin vide, et Remplacer relance apres ses attentes.
/// </summary>
public sealed class FichierJsonAtomique
{
    private readonly string _chemin;
    private readonly string _codeErreur;
    private readonly Func<bool> _eleve;

    /// <param name="chemin">La cible.</param>
    /// <param name="codeErreur">Le code que porteront ses echecs. Chaque magasin garde le sien :
    /// un appelant qui distingue ses erreurs par leur code ne doit pas les voir fusionner.</param>
    /// <param name="eleve">Ce processus est-il eleve ? Par defaut, lu de son jeton (<see cref="ProcessusEleve"/>).</param>
    public FichierJsonAtomique(string chemin, string codeErreur, Func<bool>? eleve = null)
    {
        _chemin = string.IsNullOrWhiteSpace(chemin)
            ? throw new ArgumentException("A path is required.", nameof(chemin))
            : chemin;
        _codeErreur = codeErreur;
        _eleve = eleve ?? (() => ProcessusEleve);
    }

    /// <summary>Ce processus tourne-t-il en administrateur ? Lu une fois.</summary>
    public static bool ProcessusEleve => Eleve.Value;

    private static readonly Lazy<bool> Eleve = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var moi = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(moi).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    });

    /// <summary>
    /// Le premier composant du chemin, du dossier racine au fichier, qui est un point d'analyse (jonction, lien symbolique) ;
    /// null s'il n'y en a pas. Eleve, un processus de DrDXT ecrit dans des dossiers que le compte possede (%LOCALAPPDATA%\DrDXT) :
    /// une jonction que le compte y poserait vers une racine protegee y ferait ecrire l'administrateur. Aucune ecriture, aucun
    /// ajout ni aucune mise de cote eleves ne traversent donc un point d'analyse. Limites : un composant qui n'existe pas encore
    /// se cree sans etre revu (RedirectionGuard, ConfianceDesRedirections, ferme cette fenetre sur Windows 10 22H2 et 11), et une
    /// jonction posee par un administrateur sur le chemin du profil refuse aussi les ecritures elevees.
    /// </summary>
    public static string? PointDAnalyseSurLeChemin(string chemin)
    {
        var complet = Path.GetFullPath(chemin);
        var racine = Path.GetPathRoot(complet) ?? string.Empty;
        var courant = racine;
        foreach (var morceau in complet[racine.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            courant = Path.Combine(courant, morceau);
            FileAttributes attributs;
            try { attributs = File.GetAttributes(courant); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
            if (attributs.HasFlag(FileAttributes.ReparsePoint)) return courant;
        }
        return null;
    }

    private Result? RefusEleve() => RefusEleve(_chemin, _codeErreur, _eleve);

    /// <summary>Le refus d'un ecrivain qui ne passe pas par cette classe (suppression, copie, dossier entier) : null quand il peut
    /// ecrire, le refus quand ce processus est eleve et qu'un point d'analyse est sur le chemin.</summary>
    public static Result? RefusEleve(string chemin, string codeErreur) => RefusEleve(chemin, codeErreur, () => ProcessusEleve);

    private static Result? RefusEleve(string chemin, string codeErreur, Func<bool> eleve)
    {
        if (!eleve()) return null;
        try
        {
            return PointDAnalyseSurLeChemin(chemin) is { } lien
                ? Result.Fail(codeErreur, $"Elevated write refused: '{lien}' is a reparse point.")
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result.Fail(codeErreur, $"Elevated write refused: the path cannot be checked ({ex.Message}).");
        }
    }

    /// <summary>Un chemin sous le dossier local de l'utilisateur.</summary>
    public static string CheminLocal(string nomFichier)
        => DrDxt.Core.DossierDonnees.Chemin(nomFichier);

    public Result WriteAtomic(string json) => WriteAtomic(new System.Text.UTF8Encoding(false).GetBytes(json));

    /// <summary>Le nom d'un temporaire a cote de <paramref name="chemin"/> : aleatoire, pour qu'aucun lien dur ni fichier
    /// pose a l'avance ne l'occupe. A creer en exclusif (FileMode.CreateNew).</summary>
    public static string NomTemporaire(string chemin) => chemin + "." + Guid.NewGuid().ToString("N") + ".tmp";

    /// <summary>Les attentes entre deux essais de remplacement : l'App, le relevé de fond et le service lisent les mêmes
    /// fichiers, et un lecteur qui tient la cible fait refuser le renommage (accès refusé, fichier utilisé) ; l'antivirus
    /// tient le temporaire le temps de l'analyse, plus d'une seconde sur un PC chargé. Environ cinq secondes en tout ; un
    /// refus durable (droits) échoue quand même, dit.</summary>
    internal static readonly int[] AttentesDeRemplacementMs = [20, 50, 100, 200, 400, 600, 800, 1000, 1000, 1000];

    /// <summary>Renomme <paramref name="tmp"/> par-dessus <paramref name="cible"/>, avec les reprises ci-dessus.</summary>
    public static void Remplacer(string tmp, string cible)
    {
        for (var essai = 0; ; essai++)
        {
            try
            {
                Renommer(tmp, cible);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && essai < AttentesDeRemplacementMs.Length)
            {
                Thread.Sleep(AttentesDeRemplacementMs[essai]);
            }
        }
    }

    /// <summary>Le renommage lui-même sur le disque avant de rendre la main (MOVEFILE_WRITE_THROUGH, Microsoft Learn,
    /// MoveFileExW), comme le contenu : sinon une coupure juste après rendrait le fichier d'avant, sans l'entrée que le journal
    /// venait de noter, alors que l'écriture qu'elle protège a tenu.</summary>
    private static void Renommer(string tmp, string cible)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(tmp, cible, overwrite: true);
            return;
        }
        if (MoveFileExW(tmp, cible, RemplacerExistant | EcritureATravers)) return;
        var erreur = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        // Les mêmes exceptions que File.Move : l'appelant reprend sur un accès refusé ou un fichier tenu.
        throw erreur == AccesRefuse
            ? new UnauthorizedAccessException($"Access to '{cible}' is denied.")
            : new IOException($"Could not move '{tmp}' to '{cible}' (Win32 error {erreur}).", erreur);
    }

    private const uint RemplacerExistant = 0x1, EcritureATravers = 0x8;
    private const int AccesRefuse = 5;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existant, string nouveau, uint drapeaux);

    public Result WriteAtomic(byte[] octets)
    {
        if (RefusEleve() is { } refus) return refus;
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
            Remplacer(tmp, _chemin);
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
    /// <param name="eleve">Ce processus est-il eleve ? Par defaut, lu de son jeton.</param>
    public static Result AjouterSansLien(string chemin, string texte, string codeErreur, System.Text.Encoding? encodage = null,
        Func<bool>? eleve = null)
    {
        if (RefusEleve(chemin, codeErreur, eleve ?? (() => ProcessusEleve)) is { } refus) return refus;
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

    /// <summary>Borne un journal qui ne fait qu'allonger : au-delà de <paramref name="octetsMax"/>, il garde sa moitié la plus
    /// récente, coupée au début d'une ligne, réécrite d'un coup (<see cref="WriteAtomic(byte[])"/>, qui remplace le nom sans
    /// suivre de lien). Lu sans suivre de lien. Sous le plafond, rien n'est écrit. Ne lève jamais.</summary>
    public static Result BornerSansLien(string chemin, long octetsMax, string codeErreur)
    {
        try
        {
            if (!File.Exists(chemin) || new FileInfo(chemin).Length <= octetsMax) return Result.Ok();
            var lu = LireSansLien(chemin, codeErreur);
            if (!lu.IsOk) return Result.Fail(codeErreur, lu.Error!.Value.Message);
            var texte = lu.Value!;
            var debut = Math.Max(0, texte.Length - (int)Math.Min(int.MaxValue, octetsMax / 2));
            var ligne = texte.IndexOf('\n', debut);
            var fin = ligne >= 0 && ligne + 1 < texte.Length ? texte[(ligne + 1)..] : texte[debut..];
            var encodage = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return new FichierJsonAtomique(chemin, codeErreur).WriteAtomic([.. encodage.GetPreamble(), .. encodage.GetBytes(fin)]);
        }
        catch (Exception ex)
        {
            return Result.Fail(codeErreur, $"Journal non borne : {ex.Message}");
        }
    }

    /// <summary>Lit un journal sans jamais suivre un lien, comme <see cref="AjouterSansLien"/> l'écrit : ouvert sans suivre de
    /// point de reparse, il n'est lu que s'il n'a qu'un nom. Absent : un texte vide. Ne lève jamais.</summary>
    public static Result<string> LireSansLien(string chemin, string codeErreur)
    {
        try
        {
            if (!File.Exists(chemin)) return Result<string>.Ok(string.Empty);
            using var h = CreateFileW(chemin, LireLesDonnees | LireLesAttributs, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
                OuvrirLePointDeReparse, IntPtr.Zero);
            if (h.IsInvalid)
                return Result<string>.Fail(codeErreur, $"Lecture impossible : {System.Runtime.InteropServices.Marshal.GetLastPInvokeErrorMessage()}");
            if (!GetFileInformationByHandle(h, out var info) || info.Liens != 1 || (info.Attributs & (uint)FileAttributes.ReparsePoint) != 0)
                return Result<string>.Fail(codeErreur, "Lecture refusee : le journal est un lien.");
            using var flux = new FileStream(h, FileAccess.Read);
            using var lecteur = new StreamReader(flux, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return Result<string>.Ok(lecteur.ReadToEnd());
        }
        catch (Exception ex)
        {
            return Result<string>.Fail(codeErreur, $"Lecture impossible : {ex.Message}");
        }
    }

    // FILE_READ_DATA, FILE_APPEND_DATA, FILE_READ_ATTRIBUTES ; FILE_FLAG_OPEN_REPARSE_POINT.
    private const uint LireLesDonnees = 0x0001, AjouterDesDonnees = 0x0004, LireLesAttributs = 0x0080, OuvrirLePointDeReparse = 0x00200000;

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
        if (RefusEleve() is not null) return false;
        try
        {
            File.Move(_chemin, _chemin + ".mis-de-cote-"
                + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture), overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>La liste qu'un écrivain va relire puis réécrire. Une lecture ratée est un échec, jamais une liste vide qu'il
    /// écraserait : il n'écrit rien. Absent ou vide : une liste vide. Illisible, ou d'une autre forme que des éléments de
    /// <typeparamref name="T"/> (un objet, une liste de nombres) : mis de côté d'abord, puis une liste vide ; mise de côté
    /// ratée : un échec. Les éléments nuls d'une liste valide sont écartés.</summary>
    public Result<List<T>> LireListePourReecrire<T>() where T : class
    {
        var texte = Read();
        if (!texte.IsOk) return Result<List<T>>.Fail(texte.Error!.Value.Code, texte.Error.Value.Message);
        if (string.IsNullOrWhiteSpace(texte.Value)) return Result<List<T>>.Ok([]);
        try
        {
            return Result<List<T>>.Ok((System.Text.Json.JsonSerializer.Deserialize<List<T?>>(texte.Value) ?? []).OfType<T>().ToList());
        }
        catch (System.Text.Json.JsonException)
        {
            return MettreDeCote() ? Result<List<T>>.Ok([]) : Result<List<T>>.Fail(_codeErreur, "The unreadable list could not be set aside.");
        }
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
