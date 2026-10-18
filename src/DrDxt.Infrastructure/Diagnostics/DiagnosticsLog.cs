using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Diagnostics;

/// <summary>
/// Journal des changements adossé à un fichier. Chaque appel à <see cref="Change"/> ajoute une
/// ligne lisible, horodatée en UTC, à un fichier de journal persistant.
/// <see cref="ExportAsync"/> copie le journal dans un fichier d'instantané horodaté et rend son
/// chemin absolu : de quoi servir le bouton « exporter » du support.
/// </summary>
public sealed class DiagnosticsLog : IDiagnosticsLog
{
    // Une ligne par entrée :
    //   2026-06-21T14:03:22Z [DrDXT] tweak=ReflexLowLatency from='Off' to='On'
    private const string DateFormat = "yyyy-MM-ddTHH:mm:ssZ";

    private readonly string _journalPath;
    private readonly string _exportDirectory;
    private readonly Serilog.ILogger? _logger;
    private readonly long _plafondOctets;

    // Le verrou protège ajout + lecture : des appels concurrents n'entrent jamais en course.
    private readonly object _lock = new();

    /// <param name="directory">
    /// Dossier où s'écrivent le journal et les exports.
    /// Injecté par les tests via un dossier temporaire ; les appelants de production utilisent
    /// <c>%LOCALAPPDATA%\DrDXT\diagnostics</c>.
    /// </param>
    /// <param name="logger">
    /// Logger Serilog facultatif ; ne sert qu'à noter (jamais à lever sur) les échecs d'écriture du journal.
    /// </param>
    /// <param name="plafondOctets">
    /// Taille au-dela de laquelle les lignes les plus anciennes partent. Injecte comme le
    /// dossier l'est deja : sans ca, verifier la borne coute 35 000 ecritures et une minute
    /// et demie a chaque execution de la suite : un garde lent finit par etre saute.
    /// </param>
    public DiagnosticsLog(string directory, Serilog.ILogger? logger = null,
        long plafondOctets = PlafondOctets)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("directory must not be empty", nameof(directory));
        if (plafondOctets <= 0)
            throw new ArgumentOutOfRangeException(nameof(plafondOctets), "the cap must be positive");

        Directory.CreateDirectory(directory);

        _journalPath   = Path.Combine(directory, "changes.journal.log");
        _exportDirectory = directory;
        _logger = logger;
        _plafondOctets = plafondOctets;
    }

    // Les sauts de ligne sont neutralises ici, pas laisses a la charge des appelants : une precondition
    // ecrite dans un commentaire n'est pas une precondition. Et l'enjeu n'est pas un fichier abime : un saut de ligne dans une
    // valeur ajoute au journal d'audit une entree horodatee, signee comme les vraies. Un
    // fichier casse se voit ; un fichier qui ment, non.
    // Les sauts sont donc remplaces par une marque visible, et rien n'est coupe : les deux
    // entrees d'un REG_MULTI_SZ restent lisibles sur la meme ligne.
    // Les deux caracteres sont ecrits par leur code (CR, LF).
    private static readonly char[] Sauts = { (char)13, (char)10 };

    private static string UneSeuleLigne(string valeur) =>
        valeur.IndexOfAny(Sauts) < 0
            ? valeur
            : string.Join(" <saut de ligne> ", valeur.Split(Sauts, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Taille au-dela de laquelle le journal laisse partir ses lignes les plus anciennes.
    ///
    /// Sans borne, le fichier grandirait tant que le produit sert, et `ExportAsync` en copie
    /// l'integralite dans ce que le client envoie au support. L'autre export du produit,
    /// `DiagnosticReport`, porte cette regle en tete de son fichier : « le client le colle dans un
    /// Discord ou un mail, et il doit pouvoir le relire lui-meme avant de l'envoyer ». Les deux
    /// surfaces portent la meme promesse : les deux sont bornees.
    ///
    /// 2 Mo, soit environ vingt mille changements : personne n'atteint ca en usage normal, et
    /// c'est exactement le but : la borne existe pour le cas qui derape, pas pour l'usage.
    /// </summary>
    public const long PlafondOctets = 2L * 1024 * 1024;

    /// <summary>Ce qu'on ecrit a la place de ce qui est parti. Jamais un silence.</summary>
    private const string MarqueDeTroncature = "entrees plus anciennes retirees";

    /// <summary>
    /// Ramene le journal sous le plafond en jetant les lignes les plus anciennes, et en le
    /// disant a la place exacte ou elles etaient.
    ///
    /// Jeter le passe et pas le present : on consulte un journal juste apres un changement,
    /// pas six mois plus tard. Et rien ne disparait en silence : un journal d'audit qui se tait
    /// sur ce qu'il a perdu ment par omission.
    ///
    /// Ce que cette borne ne touche pas : la capacite a tout defaire. Le retour aux valeurs
    /// d'origine s'appuie sur le manifeste signe sous %ProgramData% (voir ModeDesinstallation),
    /// jamais sur ce fichier, qui est une trace lisible par un humain.
    ///
    /// Appelee sous `_lock`, juste apres l'ajout : le controle coute la taille du fichier.
    /// </summary>
    private void BornerSiBesoin()
    {
        var fichier = new FileInfo(_journalPath);
        if (!fichier.Exists || fichier.Length <= _plafondOctets) return;

        var lignes = File.ReadAllLines(_journalPath);

        // On garde la moitie la plus recente : borner a chaque ligne ferait une reecriture
        // complete du fichier a chaque changement une fois le plafond atteint.
        var gardees = lignes.Length / 2;
        var retirees = lignes.Length - gardees;
        if (retirees <= 0) return;

        var entete = $"{DateTime.UtcNow.ToString(DateFormat, CultureInfo.InvariantCulture)} "
                   + $"[DrDXT] journal borne a {_plafondOctets} octets : "
                   + $"{retirees} {MarqueDeTroncature}.";

        var borne = new FichierJsonAtomique(_journalPath, ErrorCodes.IoFailure)
            .WriteAtomic(string.Join(Environment.NewLine, new[] { entete }.Concat(lignes.Skip(retirees))) + Environment.NewLine);
        if (!borne.IsOk) throw new IOException(borne.Error!.Value.Message);
    }

    /// <inheritdoc/>
    public void Change(string tweakId, string from, string to, string actor)
    {
        var timestamp = DateTime.UtcNow.ToString(DateFormat, CultureInfo.InvariantCulture);
        var line = $"{timestamp} [{UneSeuleLigne(actor)}] tweak={UneSeuleLigne(tweakId)} "
                 + $"from='{UneSeuleLigne(from)}' to='{UneSeuleLigne(to)}'";

        lock (_lock)
        {
            try
            {
                // Dossier du joueur : jamais à travers un nom posé d'avance en lien.
                var ajout = FichierJsonAtomique.AjouterSansLien(_journalPath, line + Environment.NewLine, ErrorCodes.IoFailure);
                if (!ajout.IsOk) throw new IOException(ajout.Error!.Value.Message);
                BornerSiBesoin();
            }
            catch (Exception ex)
            {
                // Le journal est une trace d'audit secondaire, au mieux. Un échec d'écriture (disque plein,
                // changement d'ACL, verrou d'antivirus) ne doit jamais interrompre l'optimisation réelle de
                // l'appelant : l'avaler et le noter en interne.
                _logger?.Warning(ex, "DiagnosticsLog: failed to append change entry for tweak {TweakId}", tweakId);
            }
        }
    }

    /// <inheritdoc/>
    public Task<Result<string>> ExportAsync()
    {
        try
        {
            string exportContent;

            lock (_lock)
            {
                exportContent = File.Exists(_journalPath)
                    ? File.ReadAllText(_journalPath)
                    : string.Empty;
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var exportPath = Path.Combine(_exportDirectory, $"export_{timestamp}.log");

            // Écrire hors du verrou : on a déjà la chaîne de l'instantané.
            var ecrit = new FichierJsonAtomique(exportPath, ErrorCodes.IoFailure).WriteAtomic(exportContent);
            if (!ecrit.IsOk) throw new IOException(ecrit.Error!.Value.Message);

            return Task.FromResult(Result<string>.Ok(Path.GetFullPath(exportPath)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                Result<string>.Fail(
                    ErrorCodes.IoFailure,
                    $"Failed to export diagnostics log: {ex.Message}"));
        }
    }
}
