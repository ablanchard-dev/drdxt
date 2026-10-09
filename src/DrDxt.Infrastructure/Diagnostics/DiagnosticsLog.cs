using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Diagnostics;

/// <summary>
/// File-backed change journal.  Each <see cref="Change"/> call appends one
/// human-readable, UTC-stamped line to a persistent journal file.
/// <see cref="ExportAsync"/> copies the journal to a timestamped snapshot file
/// and returns its absolute path — suitable for the support "export" button.
/// </summary>
public sealed class DiagnosticsLog : IDiagnosticsLog
{
    // One line per entry:
    //   2026-06-21T14:03:22Z [DrDXT] tweak=ReflexLowLatency from='Off' to='On'
    private const string DateFormat = "yyyy-MM-ddTHH:mm:ssZ";

    private readonly string _journalPath;
    private readonly string _exportDirectory;
    private readonly Serilog.ILogger? _logger;
    private readonly long _plafondOctets;

    // Lock protects append + read so concurrent calls never race.
    private readonly object _lock = new();

    /// <param name="directory">
    /// Directory where the journal and exports are written.
    /// Injected by tests via a temp directory; production callers use
    /// <c>%LOCALAPPDATA%\DrDXT\diagnostics</c>.
    /// </param>
    /// <param name="logger">
    /// Optional Serilog logger; used only to record (never throw on) journal-write failures.
    /// </param>
    /// <param name="plafondOctets">
    /// Taille au-dela de laquelle les lignes les plus anciennes partent. Injecte comme le
    /// dossier l'est deja : sans ca, verifier la borne coute 35 000 ecritures et une minute
    /// et demie a chaque execution de la suite -- un garde lent finit par etre saute.
    /// </param>
    public DiagnosticsLog(string directory, Serilog.ILogger? logger = null,
        long plafondOctets = PlafondOctets)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("directory must not be empty", nameof(directory));
        if (plafondOctets <= 0)
            throw new ArgumentOutOfRangeException(nameof(plafondOctets), "le plafond doit être positif");

        Directory.CreateDirectory(directory);

        _journalPath   = Path.Combine(directory, "changes.journal.log");
        _exportDirectory = directory;
        _logger = logger;
        _plafondOctets = plafondOctets;
    }

    // 18/09/2026 - Ce commentaire disait : « values are not newline-sanitized; callers must
    // pass single-line strings. Windows registry/power-plan values are single-line in
    // practice, so this is fine for V1. » Une precondition ecrite dans un commentaire n'est
    // pas une precondition. Et l'enjeu n'est pas un fichier abime : un saut de ligne dans une
    // valeur AJOUTE au journal d'audit une entree horodatee, signee comme les vraies. Un
    // fichier casse se voit ; un fichier qui ment, non.
    // Les sauts sont donc remplaces par une marque visible, et rien n'est coupe : les deux
    // entrees d'un REG_MULTI_SZ restent lisibles sur la meme ligne.
    // (Les deux caracteres sont ecrits par leur code : un antislash dans un litteral ne
    //  survit pas toujours au transport jusqu'ici, et une valeur inventee serait pire.)
    private static readonly char[] Sauts = { (char)13, (char)10 };

    private static string UneSeuleLigne(string valeur) =>
        valeur.IndexOfAny(Sauts) < 0
            ? valeur
            : string.Join(" <saut de ligne> ", valeur.Split(Sauts, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Taille au-dela de laquelle le journal laisse partir ses lignes les plus anciennes.
    ///
    /// 18/09/2026 — Il n'y avait aucune borne : `Change` ajoutait une ligne et rien d'autre.
    /// Le fichier grandissait tant que le produit sert, et `ExportAsync` en copie l'integralite
    /// dans ce que le client envoie au support. L'autre export du produit, `DiagnosticReport`,
    /// porte pourtant cette regle en tete de son fichier : « le client le colle dans un Discord
    /// ou un mail, et il doit pouvoir le relire lui-meme avant de l'envoyer ». Deux surfaces
    /// portaient la meme promesse, une seule etait bornee.
    ///
    /// 2 Mo, soit environ vingt mille changements : personne n'atteint ca en usage normal, et
    /// c'est exactement le but — la borne existe pour le cas qui derape, pas pour l'usage.
    /// </summary>
    public const long PlafondOctets = 2L * 1024 * 1024;

    /// <summary>Ce qu'on ecrit a la place de ce qui est parti. Jamais un silence.</summary>
    private const string MarqueDeTroncature = "entrees plus anciennes retirees";

    /// <summary>
    /// Ramene le journal sous le plafond en jetant les lignes les plus ANCIENNES, et en le
    /// disant a la place exacte ou elles etaient.
    ///
    /// Jeter le passe et pas le present : on consulte un journal juste apres un changement,
    /// pas six mois plus tard. Et rien ne disparait en silence — un journal d'audit qui se tait
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

        File.WriteAllLines(_journalPath, new[] { entete }.Concat(lignes.Skip(retirees)));
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
                File.AppendAllText(_journalPath, line + Environment.NewLine);
                BornerSiBesoin();
            }
            catch (Exception ex)
            {
                // The journal is a secondary, best-effort audit trail. A write failure
                // (disk full, ACL change, AV lock) must never abort the caller's actual
                // optimization operation — swallow and record internally instead.
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

            // Write outside the lock — we already have the snapshot string.
            File.WriteAllText(exportPath, exportContent);

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
