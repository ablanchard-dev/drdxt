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

    /// <summary>Les snapshots gardes. Fichier absent ou illisible : liste vide (rien d'affirme). Ce fichier, ecrivable
    /// par tout programme du compte, nourrit « Annuler » et le retrait automatique, rejoues par le helper eleve : une
    /// entree dont l'annulation n'est pas celle de son levier n'est pas rendue.</summary>
    public IReadOnlyList<TweakSnapshot> Lire()
    {
        try
        {
            return File.Exists(_chemin)
                ? (JsonSerializer.Deserialize<List<TweakSnapshot>>(File.ReadAllText(_chemin)) ?? [])
                    .Where(s => s is not null && DrDxt.Application.Autopilot.RevertAuDemarrage.AnnulationSure(s.TweakId, s.OriginalValueJson))
                    .ToList()
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

    /// <summary>Ajoute les leviers appliques et non annules du rapport. Jamais d'exception vers l'appelant : vrai quand le
    /// journal est ecrit, l'origine de chaque levier gardee.</summary>
    public bool Enregistrer(ApplyReport rapport)
    {
        try
        {
            // Un fichier illisible (coupure, ou fichier ecrit par un autre programme du compte) n'est ni pris pour vide ni garde
            // tel quel : le journal s'arretait alors en silence pour toujours. Il est mis de cote (sa trace reste, le scan l'a
            // dit) et le journal repart des leviers du jour. Mise de cote ratee : rien n'est ecrit.
            List<TweakSnapshot>? existants = [];
            if (File.Exists(_chemin))
            {
                try { existants = JsonSerializer.Deserialize<List<TweakSnapshot>>(File.ReadAllText(_chemin)); }
                catch (JsonException) { existants = null; }
                if (existants is null && !new FichierJsonAtomique(_chemin, "LEVIERS_APPLIQUES").MettreDeCote()) return false;
                existants ??= [];
            }
            var parId = existants.ToDictionary(s => s.TweakId);
            foreach (var o in rapport.Outcomes)
                if (o.Applied && !o.AutoReverted && o.Snapshot is { } s)
                    parId[s.TweakId] = s;
            Directory.CreateDirectory(Path.GetDirectoryName(_chemin)!);
            return Ecrire(parId.Values.ToList());
        }
        catch { return false; /* un journal qui echoue ne casse pas l'application d'un reglage */ }
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

    /// <summary>Après une mise à jour de Windows (build ou UBR changé depuis le dernier scan), le bilan de la relecture
    /// de tout ce que DrDXT a posé : rien n'est reposé ici, la réapplication reste un geste du joueur ou celle du service
    /// pour les réglages de son manifeste. <paramref name="fichierBuild"/> garde le dernier build vu ; le premier scan le
    /// pose sans rien dire. <c>null</c> = pas de changement de build, ou rien de posé.</summary>
    public RepairFinding? ApresMiseAJour(Hardware.IRegistryReader lecteur, string build, string fichierBuild)
    {
        var fichier = new FichierJsonAtomique(fichierBuild, "BUILD_VU");
        // Un repère illisible vaut un premier scan : il ne fait pas tomber le scan.
        var avant = Support.NotesDeVersion.VersionVue(fichier.Read() is { IsOk: true, Value: { } v } ? v : null);
        if (avant == build) return null;
        fichier.WriteAtomic(JsonSerializer.Serialize(build));
        var lus = Lire();
        if (avant is null || lus.Count == 0) return null;
        var (differents, revenus, nonRelus) = GainsDisparus.Bilan(lus, lecteur);
        return new RepairFinding("PERSISTANCE", "releve apres mise a jour de Windows", Docteur.T("JL.Bilan", differents, revenus, nonRelus), null,
            revenus > 0 ? RepairStatus.Suspicious : nonRelus > 0 ? RepairStatus.Unknown : RepairStatus.Observed,
            Docteur.T("JL.BilanExplication", avant, build));
    }

    /// <summary>Le scan : chaque gain revenu a la valeur d'avant devient un constat. Ni NonLu ni
    /// NonRelisable ne comptent (GainsDisparus les ecarte).
    /// Un journal illisible est dit : sans lui, « Annuler » et le retrait automatique ne voient plus ce que DrDXT a posé.</summary>
    public IReadOnlyList<RepairFinding> Constats(Hardware.IRegistryReader lecteur)
        => (Lisible() ? [] : new[] { new RepairFinding("PERSISTANCE", "leviers-appliques.json", Docteur.T("JL.Illisible"), null,
                RepairStatus.Unknown, Docteur.T("JL.IllisibleExplication")) })
            .Concat(GainsDisparus.Detecter(Lire(), lecteur)
            .Select(g => new RepairFinding("PERSISTANCE", g.TweakId, Docteur.T("JL.Revenu"),
                Docteur.T("JL.ValeurDrDxt"), RepairStatus.Suspicious, Docteur.T("JL.Defait", g.Cible),
                RepairAction.Recommandation)))
            .ToList();
}
