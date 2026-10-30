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

    private readonly Func<DateTimeOffset> _maintenant;

    public MemoireDeLEssai(string chemin, Func<DateTimeOffset>? maintenant = null)
    {
        _fichier = new FichierJsonAtomique(chemin, CodeErreur);
        _maintenant = maintenant ?? (() => DateTimeOffset.Now);
    }

    public static MemoireDeLEssai ParDefaut() => new(FichierJsonAtomique.CheminLocal("essai-alternance.json"));

    /// <summary>Le verrou que l'App et le relevé de fond tiennent pour changer l'essai (pris par <see cref="VerrouDuReleve.Prendre"/>).</summary>
    public static string CheminDuVerrou() => FichierJsonAtomique.CheminLocal("essai-alternance.verrou");

    /// <summary>L'essai en cours, ou <c>null</c> (aucun, ou un fichier abîmé : on n'alterne pas sur ce qu'on ne sait pas lire).
    /// Un essai neuf ne s'écrit pas sur ce <c>null</c> : il passe par <see cref="Libre"/>.</summary>
    public EssaiEnAlternance? Lire()
    {
        var texte = _fichier.Read();
        if (!texte.IsOk || string.IsNullOrWhiteSpace(texte.Value) || texte.Value.Trim() == "null") return null;
        try { return Borne(JsonSerializer.Deserialize<EssaiEnAlternance>(texte.Value)); }
        catch (JsonException) { return null; }
    }

    // Une date notée sous une horloge partie en avant, remise depuis, ferait attendre à l'essai une partie commencée après
    // elle, que rien n'amènerait. Rien de l'essai n'est plus tard que maintenant.
    private EssaiEnAlternance? Borne(EssaiEnAlternance? e)
    {
        if (string.IsNullOrEmpty(e?.LevierId)) return null;   // sans son levier, le fichier est abîmé
        var m = _maintenant();
        return e with { Debut = e.Debut > m ? m : e.Debut, DerniereBascule = e.DerniereBascule > m ? m : e.DerniereBascule };
    }

    /// <summary>Sous le verrou de l'essai, avant d'en écrire un neuf : aucun essai en cours. Un fichier qui ne se lit pas
    /// n'est pas « aucun » : l'essai neuf écraserait l'origine de celui en cours. Abîmé : mis de côté d'abord (l'origine
    /// perdue s'y lit encore), puis libre ; mise de côté ratée : pas libre.</summary>
    public bool Libre()
    {
        var texte = _fichier.Read();
        if (!texte.IsOk) return false;
        if (string.IsNullOrWhiteSpace(texte.Value) || texte.Value.Trim() == "null") return true;
        try { if (!string.IsNullOrEmpty(JsonSerializer.Deserialize<EssaiEnAlternance>(texte.Value)?.LevierId)) return false; }
        catch (JsonException) { }
        return _fichier.MettreDeCote();
    }

    public Result Ecrire(EssaiEnAlternance? essai) => _fichier.WriteAtomic(essai is null ? "null" : JsonSerializer.Serialize(essai));
}
