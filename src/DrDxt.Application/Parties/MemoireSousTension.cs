using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>La recherche de sous-tension en cours ou finie, gardée sur le disque : un plantage en plein palier
/// redémarre le PC, et c'est au démarrage suivant que l'App lit ce palier comme instable.</summary>
public sealed class MemoireSousTension
{
    private readonly FichierJsonAtomique _fichier;

    public MemoireSousTension(string chemin) => _fichier = new FichierJsonAtomique(chemin, "SOUS_TENSION");

    public static MemoireSousTension ParDefaut() => new(FichierJsonAtomique.CheminLocal("sous-tension.json"));

    /// <summary>La recherche, ou null (aucune, ou un fichier abîmé : rien n'est supposé).</summary>
    public RechercheSousTension? Lire()
    {
        var texte = _fichier.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value)) return null;
        try { return JsonSerializer.Deserialize<RechercheSousTension>(texte.Value); }
        catch (JsonException) { return null; }
    }

    public Result Ecrire(RechercheSousTension recherche) => _fichier.WriteAtomic(JsonSerializer.Serialize(recherche));
}
