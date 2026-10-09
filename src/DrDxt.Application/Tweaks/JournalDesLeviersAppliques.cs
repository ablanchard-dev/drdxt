using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>
/// 24/09/2026 — ce que DrDXT a applique et garde, cote UTILISATEUR (sans elevation).
///
/// Le manifeste signe ne porte que les quatre leviers que le service reapplique au demarrage. Les
/// autres — Game DVR, Game Mode, VRR (ruche de l'utilisateur), leviers pilote — n'etaient notes
/// nulle part : `GainsDisparus` existait, teste, et personne ne pouvait l'appeler faute de
/// snapshots. Ce journal les garde, un par levier (le plus recent l'emporte), pour que le scan
/// suivant REMARQUE un gain defait — sans jamais le reecrire.
/// </summary>
public sealed class JournalDesLeviersAppliques
{
    private readonly string _chemin;

    public JournalDesLeviersAppliques(string chemin) => _chemin = chemin;

    public static string CheminParDefaut() => DrDxt.Core.DossierDonnees.Chemin("leviers-appliques.json");

    /// <summary>Les snapshots gardes. Fichier absent ou illisible : liste vide (rien d'affirme).</summary>
    public IReadOnlyList<TweakSnapshot> Lire()
    {
        try
        {
            return File.Exists(_chemin)
                ? JsonSerializer.Deserialize<List<TweakSnapshot>>(File.ReadAllText(_chemin)) ?? []
                : [];
        }
        catch { return []; }
    }

    /// <summary>07/10/2026 (revue F-39) : absent ou lu sans erreur. <see cref="Lire"/> rend vide dans les deux cas.</summary>
    public bool Lisible()
    {
        try { return !File.Exists(_chemin) || JsonSerializer.Deserialize<List<TweakSnapshot>>(File.ReadAllText(_chemin)) is not null; }
        catch { return false; }
    }

    /// <summary>Ajoute les leviers appliques et non annules du rapport. Jamais d'exception vers l'appelant.</summary>
    public void Enregistrer(ApplyReport rapport)
    {
        try
        {
            // 26/09/2026 : un fichier illisible se lisait comme vide et etait reecrit avec les seuls leviers
            // du jour. Sans lecture sure, on n'ecrit pas (meme regle que les autres journaux, 10/09).
            var existants = File.Exists(_chemin)
                ? JsonSerializer.Deserialize<List<TweakSnapshot>>(File.ReadAllText(_chemin))
                : [];
            if (existants is null) return;
            var parId = existants.ToDictionary(s => s.TweakId);
            foreach (var o in rapport.Outcomes)
                if (o.Applied && !o.AutoReverted && o.Snapshot is { } s)
                    parId[s.TweakId] = s;
            Directory.CreateDirectory(Path.GetDirectoryName(_chemin)!);
            File.WriteAllText(_chemin, JsonSerializer.Serialize(parId.Values.ToList()));
        }
        catch { /* un journal qui echoue ne casse pas l'application d'un reglage */ }
    }

    /// <summary>04/10/2026 (plan 2.3) : un levier retiré (moins bien en jeu) sort du journal, sinon le scan suivant le
    /// dirait « défait » et proposerait de le reposer. Lecture illisible : rien n'est réécrit.</summary>
    public void Retirer(string tweakId)
    {
        try
        {
            if (!File.Exists(_chemin)) return;
            var existants = JsonSerializer.Deserialize<List<TweakSnapshot>>(File.ReadAllText(_chemin));
            if (existants is null) return;
            File.WriteAllText(_chemin, JsonSerializer.Serialize(existants.Where(s => s.TweakId != tweakId).ToList()));
        }
        catch { }
    }

    /// <summary>Le scan : chaque gain revenu a la valeur d'avant devient un constat. Ni NonLu ni
    /// NonRelisable ne comptent (GainsDisparus les ecarte).</summary>
    public IReadOnlyList<RepairFinding> Constats(Hardware.IRegistryReader lecteur)
        => GainsDisparus.Detecter(Lire(), lecteur)
            .Select(g => new RepairFinding("PERSISTANCE", g.TweakId, "revenu à la valeur d'avant",
                "la valeur posée par DrDXT", RepairStatus.Suspicious,
                $"Ce réglage, appliqué et gardé par DrDXT, a été défait depuis ({g.Cible}) : une mise à jour, "
                + "un autre programme ou toi. DrDXT ne le repose pas tout seul : relance l'optimisation pour le reposer.",
                RepairAction.Recommandation))
            .ToList();
}
