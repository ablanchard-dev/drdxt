using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>
/// Ce que DrDXT a applique et garde, cote utilisateur (sans elevation).
///
/// Le manifeste signe ne porte que les quatre leviers que le service reapplique au demarrage. Les
/// autres (Game DVR, Game Mode, VRR dans la ruche de l'utilisateur, leviers pilote) ne seraient
/// notes nulle part, et `GainsDisparus` ne pourrait pas etre appele faute de snapshots. Ce
/// journal les garde, un par levier (le plus recent l'emporte), pour que le scan suivant
/// remarque un gain defait, sans jamais le reecrire.
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

    /// <summary>Absent ou lu sans erreur. <see cref="Lire"/> rend vide dans les deux cas.</summary>
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
            // Sans lecture sure, on n'ecrit pas : un fichier illisible pris pour vide serait reecrit
            // avec les seuls leviers du jour (meme regle que les autres journaux).
            var existants = File.Exists(_chemin)
                ? JsonSerializer.Deserialize<List<TweakSnapshot>>(File.ReadAllText(_chemin))
                : [];
            if (existants is null) return;
            var parId = existants.ToDictionary(s => s.TweakId);
            foreach (var o in rapport.Outcomes)
                if (o.Applied && !o.AutoReverted && o.Snapshot is { } s)
                    parId[s.TweakId] = s;
            Directory.CreateDirectory(Path.GetDirectoryName(_chemin)!);
            Ecrire(parId.Values.ToList());
        }
        catch { /* un journal qui echoue ne casse pas l'application d'un reglage */ }
    }

    /// <summary>Un levier retiré (moins bien en jeu) sort du journal, sinon le scan suivant le
    /// dirait « défait » et proposerait de le reposer. Lecture illisible : rien n'est réécrit.
    /// L'échec se rend (faux) au lieu de se taire, et l'écriture est atomique (un fichier à moitié
    /// écrit serait lu « illisible » et figerait le journal).</summary>
    public bool Retirer(string tweakId)
    {
        try
        {
            if (!File.Exists(_chemin)) return true;
            var existants = JsonSerializer.Deserialize<List<TweakSnapshot>>(File.ReadAllText(_chemin));
            if (existants is null) return false;
            return Ecrire(existants.Where(s => s.TweakId != tweakId).ToList());
        }
        catch (Exception) { return false; }
    }

    private bool Ecrire(List<TweakSnapshot> liste)
        => new FichierJsonAtomique(_chemin, "LEVIERS_APPLIQUES").WriteAtomic(JsonSerializer.Serialize(liste)).IsOk;

    /// <summary>Le scan : chaque gain revenu a la valeur d'avant devient un constat. Ni NonLu ni
    /// NonRelisable ne comptent (GainsDisparus les ecarte).</summary>
    public IReadOnlyList<RepairFinding> Constats(Hardware.IRegistryReader lecteur)
        => GainsDisparus.Detecter(Lire(), lecteur)
            .Select(g => new RepairFinding("PERSISTANCE", g.TweakId, Docteur.T("JL.Revenu"),
                Docteur.T("JL.ValeurDrDxt"), RepairStatus.Suspicious, Docteur.T("JL.Defait", g.Cible),
                RepairAction.Recommandation))
            .ToList();
}
