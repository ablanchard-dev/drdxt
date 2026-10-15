using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// La frontière disque du marqueur de changement BIOS en attente, isolée pour que la logique du
/// store se teste entièrement sans toucher au vrai disque. Chaque méthode échoue proprement (rend
/// un <see cref="Result"/>, ne lève jamais) : la frontière d'E/S est emballée ici pour que le store
/// au-dessus reste pur.
/// </summary>
public interface IBiosMarkerFile
{
    /// <summary>
    /// Écrit <paramref name="json"/> atomiquement (fichier temporaire + déplacement avec écrasement sur
    /// la cible : un lecteur ne voit jamais un fichier déchiré). Échec propre sur une panne d'E/S.
    /// </summary>
    Result WriteAtomic(string json);

    /// <summary>
    /// Lit le marqueur. <c>Ok(null)</c> quand le fichier n'existe pas (rien en attente) ;
    /// <c>Ok(content)</c> s'il est présent ; <c>Fail</c> sur une panne d'E/S. Ne lève jamais.
    /// </summary>
    Result<string?> Read();

    /// <summary>Supprime le marqueur s'il est présent (idempotent). Échec propre sur une panne d'E/S.</summary>
    Result Delete();
}

/// <summary>
/// <see cref="IBiosMarkerFile"/> sur le vrai disque. Les écritures sont atomiques (temporaire +
/// déplacement) ; le dossier parent est créé à la demande. Toutes les frontières sont emballées :
/// aucune exception ne s'échappe. Le fichier temporaire est à côté de la cible (même volume) : le
/// déplacement est un vrai renommage atomique, pas une copie entre volumes.
/// </summary>
public sealed class FileSystemBiosMarkerFile : IBiosMarkerFile
{
    private readonly string _path;

    public FileSystemBiosMarkerFile(string path)
        => _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Marker path is required.", nameof(path))
            : path;

    // L'écriture atomique a une seule implémentation (Core).
    private FichierJsonAtomique File_ => new(_path, ErrorCodes.BiosPendingChangeIo);

    /// <inheritdoc />
    public Result WriteAtomic(string json) => File_.WriteAtomic(json);

    /// <inheritdoc />
    public Result<string?> Read() => File_.Read();

    /// <inheritdoc />
    public Result Delete()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.BiosPendingChangeIo, $"Marker delete failed: {ex.Message}");
        }
    }
}
