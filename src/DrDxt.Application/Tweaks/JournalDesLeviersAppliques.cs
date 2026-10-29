using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>
/// Ce que DrDXT a applique et garde, cote utilisateur (sans elevation), un snapshot par levier (le plus recent
/// l'emporte), y compris ceux que le service ne reapplique pas (Game DVR, Game Mode, VRR en HKCU, leviers pilote) : le
/// scan suivant (`GainsDisparus`) remarque un gain defait, sans jamais le reecrire.
/// </summary>
public sealed class JournalDesLeviersAppliques
{
    private readonly string _chemin;

    private readonly TimeSpan _attente;

    /// <param name="attente">Combien attendre le verrou d'un autre processus avant de renoncer à écrire ; 5 s par défaut.</param>
    public JournalDesLeviersAppliques(string chemin, TimeSpan? attente = null)
        => (_chemin, _attente) = (chemin, attente ?? TimeSpan.FromSeconds(5));

    // Enregistrer et Retirer lisent, modifient et réécrivent : deux processus du compte (l'App, le banc, l'outil) en même temps
    // effaçaient la mise à jour de l'autre. Le verrou voisin est celui des autres journaux du compte.
    private IDisposable? Verrou() => Parties.VerrouDuReleve.Attendre(_chemin + ".verrou", _attente);

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
                    .Where(s => s is { TweakId.Length: > 0 } && DrDxt.Application.Autopilot.RevertAuDemarrage.AnnulationSure(s.TweakId, s.OriginalValueJson))
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
            using var verrou = Verrou();
            if (verrou is null) return false;
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
            // Une entrée vide ou un levier en double (écrits hors de DrDXT) ne bloquent plus chaque enregistrement.
            var parId = existants.OfType<TweakSnapshot>().Where(s => s.TweakId is { Length: > 0 })
                .GroupBy(s => s.TweakId).ToDictionary(g => g.Key, g => g.Last());
            // Reposé sans avoir été rendu (un rendu retire l'entrée), le levier relit ce qu'il trouve. Encore la pose de DrDXT :
            // l'origine du joueur est celle de la première pose, comme à la désinstallation. Une autre valeur : le joueur a
            // repris la main entre-temps, l'origine est la sienne, la nouvelle. Pose d'avant inconnue : la première reste.
            foreach (var o in rapport.Outcomes)
                if (o.Applied && !o.AutoReverted && o.Snapshot is { } s)
                    parId[s.TweakId] = parId.TryGetValue(s.TweakId, out var deja)
                        && (deja.AppliedValueJson is not { Length: > 0 } posee || string.Equals(posee, s.OriginalValueJson, StringComparison.Ordinal))
                        ? s with { OriginalValueJson = deja.OriginalValueJson }
                        : s;
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
            using var verrou = Verrou();
            if (verrou is null) return false;
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

    /// <summary>Des leviers encore dans la liste signée sans être dans ce journal (nommés par leur titre) : le service pourrait
    /// les reposer, et l'écran ne les montrait pas.</summary>
    public static RepairFinding ConstatDesOrphelins(IReadOnlyList<string> titres)
        => new("PERSISTANCE", "manifeste", Docteur.T("JL.Orphelins", string.Join(", ", titres)), null,
            RepairStatus.Suspicious, Docteur.T("JL.OrphelinsExplication"));

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
