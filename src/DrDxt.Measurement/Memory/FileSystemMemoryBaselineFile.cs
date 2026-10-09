using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// Le fichier reel sous <see cref="MemoryBaselineStore"/>, ecrit ATOMIQUEMENT : fichier temporaire
/// a cote de la cible (meme volume, donc le deplacement est un vrai renommage) puis remplacement.
/// Un lecteur ne voit jamais un fichier a moitie ecrit -- et une reference tronquee serait pire
/// qu'absente, puisqu'elle se comparerait quand meme.
///
/// L'ecriture elle-meme vit dans <see cref="FichierJsonAtomique"/> depuis le 09/09/2026 : la
/// reference CPU en avait besoin a l'identique, et recopier ces vingt lignes aurait garanti
/// qu'elles derivent un jour d'un seul cote.
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
