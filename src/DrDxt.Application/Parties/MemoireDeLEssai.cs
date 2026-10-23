using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>L'essai en alternance en cours, gardé entre deux lancements (le relevé de fond et l'App
/// le lisent). <paramref name="PoseALOrigine"/> : l'état d'avant l'essai, rendu si rien ne se dégage.</summary>
public sealed record EssaiEnAlternance(string LevierId, bool PoseALOrigine, bool PoseMaintenant, DateTimeOffset Debut,
    bool EnPause = false, DateTimeOffset? DerniereBascule = null)
{
    /// <summary>Seules les parties commencées après la dernière bascule portent l'état de l'essai.</summary>
    public DateTimeOffset Depuis => DerniereBascule ?? Debut;
}

public sealed class MemoireDeLEssai
{
    public const string CodeErreur = "ESSAI_ALTERNANCE";
    private readonly FichierJsonAtomique _fichier;

    public MemoireDeLEssai(string chemin) => _fichier = new FichierJsonAtomique(chemin, CodeErreur);

    public static MemoireDeLEssai ParDefaut() => new(FichierJsonAtomique.CheminLocal("essai-alternance.json"));

    /// <summary>Le verrou que l'App et le relevé de fond tiennent pour changer l'essai (pris par <see cref="VerrouDuReleve.Prendre"/>).</summary>
    public static string CheminDuVerrou() => FichierJsonAtomique.CheminLocal("essai-alternance.verrou");

    /// <summary>L'essai en cours, ou <c>null</c> (aucun, ou un fichier abîmé : on n'alterne pas sur ce qu'on ne sait pas lire).</summary>
    public EssaiEnAlternance? Lire()
    {
        var texte = _fichier.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value) || texte.Value.Trim() == "null") return null;
        try { return JsonSerializer.Deserialize<EssaiEnAlternance>(texte.Value); }
        catch (JsonException) { return null; }
    }

    public Result Ecrire(EssaiEnAlternance? essai) => _fichier.WriteAtomic(essai is null ? "null" : JsonSerializer.Serialize(essai));
}
