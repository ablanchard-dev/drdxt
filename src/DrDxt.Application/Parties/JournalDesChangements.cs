using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>Un changement daté sur la machine : un réglage du BIOS, un levier posé. Ce qui se juge ensuite en jeu.
/// <paramref name="Depuis"/> : quand l'heure exacte n'est pas connue, la borne basse (pour un
/// réglage BIOS, la dernière mesure sous l'ancien réglage). Les parties entre <c>Depuis</c> et <c>Quand</c> ne comptent
/// d'aucun côté.
/// <paramref name="Cle"/> : l'identifiant stable du réglage et sa nouvelle valeur
/// (« windows.hags:on ») ; le libellé est traduit, la clé permet de relier le changement au levier qui l'a posé.
/// <paramref name="Fige"/> : le bilan au moment où il a été tranché. Le relevé ne garde que
/// les 20 dernières parties : rejugé à chaque affichage, un gain prouvé redevenait « pas encore jugé » une fois ses parties
/// d'avant sorties de l'anneau. Une fois tranché, il ne bouge plus.</summary>
public sealed record Changement(DateTimeOffset Quand, string Libelle, DateTimeOffset? Depuis = null, string? Cle = null,
    BilanDuChangement? Fige = null);

/// <summary>
/// Garde la date de chaque changement, pour savoir quelles parties sont avant et
/// lesquelles après. Le journal des leviers ne garde que leurs valeurs : sans date, un gain mesuré au banc ne
/// se jugerait pas en jeu. Fichier absent ou abîmé : aucun changement, jamais inventé.
/// </summary>
public sealed class JournalDesChangements
{
    public const string CodeErreur = "JOURNAL_CHANGEMENTS";
    public const int Gardes = 50;
    private readonly FichierJsonAtomique _fichier;

    /// <summary>Le verrou partagé entre processus.</summary>
    private readonly TimeSpan _attente;

    /// <param name="attente">Combien attendre le verrou avant de rendre « occupé » ; 10 s par défaut.</param>
    public JournalDesChangements(string chemin, TimeSpan? attente = null)
    {
        _attente = attente ?? TimeSpan.FromSeconds(10);
        Cle = Path.GetFullPath(chemin);
        _fichier = new FichierJsonAtomique(chemin, CodeErreur);
    }

    /// <summary>Le verrou voisin du journal, partagé entre processus (l'App, élevée ou non, et le relevé de fond) : Ajouter et
    /// Figer lisent, modifient et réécrivent, et deux écrivains en même temps perdaient l'écriture de l'autre (un verdict figé
    /// au retrait disparaissait).</summary>
    public string CheminDuVerrou => Cle + ".verrou";

    private Result SousVerrou(Func<Result> ecrire)
    {
        using var verrou = VerrouDuReleve.Attendre(CheminDuVerrou, _attente);
        return verrou is null ? Result.Fail(CodeErreur, Docteur.T("App.JournalChangementsOccupe")) : ecrire();
    }

    /// <summary>Les ajouts refusés (verrou attendu trop longtemps, écriture refusée), par fichier, dans ce processus. Une pose
    /// perdue couperait le retrait automatique et la garde d'incident de son levier : refusé, un ajout se lit tout de suite et
    /// s'écrit avec le suivant. Limite : un processus qui se ferme avant ce passage le perd.</summary>
    private static readonly Dictionary<string, List<Changement>> EnAttente = new(StringComparer.OrdinalIgnoreCase);

    private readonly string Cle;

    private List<Changement> Attente()
    {
        lock (EnAttente) return EnAttente.TryGetValue(Cle, out var l) ? [.. l] : [];
    }

    public static JournalDesChangements ParDefaut() => new(FichierJsonAtomique.CheminLocal("changements.json"));

    /// <summary>Le plus récent d'abord.</summary>
    public IReadOnlyList<Changement> Lire()
    {
        var texte = _fichier.Read();
        // Les entrées vides d'un JSON valide ([null], [{}]) sont écartées : la frise et l'ajout suivant les liraient.
        List<Changement> lus;
        try
        {
            lus = !texte.IsOk || string.IsNullOrWhiteSpace(texte.Value) ? []
                : JsonSerializer.Deserialize<List<Changement?>>(texte.Value)?.OfType<Changement>().Where(c => c.Libelle is not null).ToList() ?? [];
        }
        catch (JsonException) { lus = []; }
        var attente = Attente().Where(a => !lus.Any(e => e.Libelle == a.Libelle && e.Quand == a.Quand)).ToList();
        return attente.Count == 0 ? lus : [.. lus.Concat(attente).OrderByDescending(e => e.Quand)];
    }

    /// <summary>Ce que les écrivains relisent, sous le verrou : une lecture ratée est un échec ; les entrées vides écartées.</summary>
    private Result<List<Changement>> Existants()
    {
        var lu = _fichier.LireListePourReecrire<Changement>();
        return lu.IsOk ? Result<List<Changement>>.Ok(lu.Value!.Where(c => c.Libelle is not null).ToList()) : lu;
    }

    /// <summary>Ajoute, sauf si le même libellé est déjà noté à la même minute (un scan relancé ne double rien), ou la même
    /// clé avec la même borne basse : un relevé dont la référence n'a pas pu s'écrire rend de nouveau ses changements, à une
    /// autre heure, bornés pareil.</summary>
    public Result Ajouter(Changement c)
    {
        var attente = Attente();
        var r = SousVerrou(() => Ecrire([.. attente, c]));
        lock (EnAttente)
        {
            var l = EnAttente.TryGetValue(Cle, out var deja) ? deja : EnAttente[Cle] = [];
            if (r.IsOk) l.RemoveAll(attente.Contains);
            else if (!l.Contains(c)) l.Add(c);
        }
        return r;
    }

    private Result Ecrire(IReadOnlyList<Changement> nouveaux)
    {
        // Un journal qui ne se lit pas n'est pas un journal vide : rien n'est écrit. Abîmé ou d'une autre forme : mis de côté
        // (sa trace reste), il repart de ce changement.
        if (Existants() is not { IsOk: true } lu) return Result.Fail(CodeErreur, "The change log could not be read: nothing written.");
        var existants = lu.Value!;
        var ajoutes = new List<Changement>();
        foreach (var c in nouveaux)
            if (!existants.Concat(ajoutes).Any(e => e.Libelle == c.Libelle && Math.Abs((e.Quand - c.Quand).TotalMinutes) < 1
                || c is { Cle: not null, Depuis: not null } && e.Cle == c.Cle && e.Depuis == c.Depuis)) ajoutes.Add(c);
        if (ajoutes.Count == 0) return Result.Ok();
        // Les nouveaux restent toujours : datés avant tous les autres (horloge reculée), ils sortiraient de l'anneau à peine écrits.
        var gardes = existants.OrderByDescending(e => e.Quand).Take(Math.Max(0, Gardes - ajoutes.Count)).ToList();
        // La dernière pose de chaque levier reste, quel que soit son âge : le retrait automatique et le contrôle des leviers
        // volatils se datent sur elle, et les mises à jour de jeux et de pilotes rempliraient l'anneau en quelques mois.
        // Bornées par le catalogue des leviers.
        var poses = existants.Except(gardes).Where(e => e.Cle?.StartsWith("pose:", StringComparison.Ordinal) == true)
            .GroupBy(e => e.Cle).Select(g => g.MaxBy(e => e.Quand)!)
            .Where(e => !ajoutes.Concat(gardes).Any(x => x.Cle == e.Cle));
        var liste = ajoutes.Concat(gardes).Concat(poses).OrderByDescending(e => e.Quand).ToList();
        return _fichier.WriteAtomic(JsonSerializer.Serialize(liste));
    }

    /// <summary>Fige le bilan tranché d'un changement (même date, même libellé) ; un bilan déjà figé n'est jamais réécrit.</summary>
    public Result Figer(Changement c, BilanDuChangement bilan) => bilan.Verdict == VerdictEnJeu.PasEncoreJuge ? Result.Ok() : SousVerrou(() =>
    {
        // Un ajout refusé attend en mémoire : il s'écrit d'abord, sinon son bilan ne trouverait pas sa ligne dans le fichier
        // et « réussi » ne serait vrai que pour ce processus.
        var attente = Attente();
        if (attente.Count > 0)
        {
            var ecrit = Ecrire(attente);
            if (!ecrit.IsOk) return ecrit;
            lock (EnAttente)
                if (EnAttente.TryGetValue(Cle, out var l)) l.RemoveAll(attente.Contains);
        }
        if (Existants() is not { IsOk: true } lu) return Result.Fail(CodeErreur, "The change log could not be read: nothing written.");
        var existants = lu.Value!;
        var liste = existants.Select(e => e.Fige is null && e.Quand == c.Quand && e.Libelle == c.Libelle ? e with { Fige = bilan } : e).ToList();
        return liste.SequenceEqual(existants) ? Result.Ok() : _fichier.WriteAtomic(JsonSerializer.Serialize(liste));
    });
}
