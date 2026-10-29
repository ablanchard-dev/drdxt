using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// Le fichier reel sous <see cref="MemoryBaselineStore"/>, ecrit atomiquement par <see cref="FichierJsonAtomique"/>,
/// partagee avec la reference CPU : une reference tronquee serait pire qu'absente, elle se comparerait quand meme.
/// </summary>
public sealed class FileSystemMemoryBaselineFile : IMemoryBaselineFile
{
    private readonly FichierJsonAtomique _fichier;

    /// <summary>Chemin par defaut, sous le dossier local de l'utilisateur.</summary>
    public static string DefaultPath()
        => DrDxt.Core.DossierDonnees.Chemin("memory-baseline.json");

    public FileSystemMemoryBaselineFile() : this(DefaultPath()) { }

    public FileSystemMemoryBaselineFile(string path)
        => _fichier = new FichierJsonAtomique(path, ErrorCodes.MemoryBaselineIo);

    public Result WriteAtomic(string json) => _fichier.WriteAtomic(json);

    public Result<string?> Read() => _fichier.Read();
}
