using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Repair;

/// <summary>
/// Le journal des constats, pour que « qu'est-ce qui a change depuis hier ? » ait une reponse.
///
/// L'audit recalcule tout a chaque passe ; sans ce journal, il ne garderait rien : on verrait un
/// etat, jamais une trajectoire, et aucune reparation ne pourrait etre prouvee dans le temps (le
/// pilier « maintenir » de la north star).
///
/// Meme forme que <c>FileSystemMemoryBaselineFile</c>, volontairement : deux magasins qui se
/// ressemblent se relisent ensemble : ecriture atomique (temporaire a cote de la cible, donc sur
/// le meme volume, puis remplacement), aucune exception qui remonte, et une lecture ratee qui se
/// dit au lieu de rendre une photo vide.
///
/// Une photo vide et une lecture ratee ne doivent jamais se confondre : la premiere ferait
/// croire que tout est apparu aujourd'hui.
/// </summary>
public sealed class RepairHistoryStore
{
    /// <summary>Combien de photos on garde. Non chiffré. Trente laisse voir un mois sans
    /// faire enfler un fichier que personne ne relira.</summary>
    public const int PhotosGardees = 30;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Les statuts sont ecrits en texte : un journal se relit dans six mois, et un entier
        // deviendrait faux au premier ajout dans l'enumeration.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _chemin;

    public static string CheminParDefaut()
        => DrDxt.Core.DossierDonnees.Chemin("repair-history.json");

    public RepairHistoryStore() : this(CheminParDefaut()) { }

    public RepairHistoryStore(string chemin)
        => _chemin = string.IsNullOrWhiteSpace(chemin)
            ? throw new ArgumentException("A path is required.", nameof(chemin))
            : chemin;

    /// <summary>Toutes les photos, de la plus ancienne a la plus recente. Liste vide = aucun
    /// journal encore. `Fail` = le journal existe mais n'a pas pu etre lu.</summary>
    public Result<IReadOnlyList<RepairSnapshot>> Lire()
    {
        try
        {
            if (!File.Exists(_chemin))
                return Result<IReadOnlyList<RepairSnapshot>>.Ok([]);

            var photos = JsonSerializer.Deserialize<List<RepairSnapshot>>(
                File.ReadAllText(_chemin), Options);

            // Un fichier qui deserialise en `null` est un fichier casse, pas un journal vide.
            if (photos is null)
                return Result<IReadOnlyList<RepairSnapshot>>.Fail(ErrorCodes.DetectionFailed,
                    Docteur.T("RHS.Illisible", Docteur.T("RHS.ContenuNul")));

            // Le desérialiseur remplit une photo amputee sans se plaindre (entree nulle,
            // listes absentes), et la comparaison levait en plein scan. Amputee = illisible.
            if (photos.Any(p => p?.DomainesExamines is null || p.Etats is null
                                || p.Etats.Any(e => e?.Domaine is null || e.Nom is null)))
                return Result<IReadOnlyList<RepairSnapshot>>.Fail(ErrorCodes.DetectionFailed,
                    Docteur.T("RHS.Illisible", Docteur.T("RHS.PhotoIncomplete")));

            return Result<IReadOnlyList<RepairSnapshot>>.Ok(photos);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<RepairSnapshot>>.Fail(
                ErrorCodes.DetectionFailed, Docteur.T("RHS.Illisible", ex.Message));
        }
    }

    /// <summary>La photo precedente, ou <c>null</c> s'il n'y en a pas.</summary>
    public Result<RepairSnapshot?> DernierePhoto()
    {
        var lu = Lire();
        return lu.IsOk
            ? Result<RepairSnapshot?>.Ok(lu.Value!.Count > 0 ? lu.Value[^1] : null)
            : Result<RepairSnapshot?>.Fail(lu.Error!.Value.Code, lu.Error.Value.Message);
    }

    /// <summary>
    /// Ajoute une photo et taille le journal.
    ///
    /// Si le journal existant est illisible, on refuse d'ecrire plutot que de repartir de zero :
    /// ecraser un journal casse effacerait l'historique qu'on essaie justement de tenir.
    /// </summary>
    public Result Enregistrer(RepairSnapshot photo)
    {
        var lu = Lire();
        if (!lu.IsOk) return Result.Fail(lu.Error!.Value.Code, lu.Error.Value.Message);

        var photos = lu.Value!.ToList();
        photos.Add(photo);
        if (photos.Count > PhotosGardees)
            photos.RemoveRange(0, photos.Count - PhotosGardees);

        string json;
        try
        {
            json = JsonSerializer.Serialize(photos, Options);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.DetectionFailed,
                Docteur.T("RHS.NonEcrit", ex.Message));
        }

        // L'ecriture atomique a une seule implementation (Core) : elle etait recopiee ici.
        return new FichierJsonAtomique(_chemin, ErrorCodes.DetectionFailed).WriteAtomic(json);
    }
}
