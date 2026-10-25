using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// Un réglage que le joueur bascule lui-même (Instant Replay de NVIDIA, et demain le mode Jeu
/// ou HAGS) doit pouvoir se juger sur ses parties, comme un réglage du BIOS. Chaque scan relève ces états ; ce qui a
/// changé depuis le relevé précédent devient un <see cref="Changement"/>, borné entre les deux relevés (l'heure exacte
/// de la bascule n'est pas connue). Le premier relevé ne produit rien : sans avant, aucun changement n'est inventé.
/// </summary>
public sealed class SuiviDesReglages
{
    public const string CodeErreur = "SUIVI_REGLAGES";
    private readonly FichierJsonAtomique _fichier;
    private readonly string _chemin;
    private readonly Func<Result<string?>> _lire;

    // Deux relevés du même fichier lisaient puis récrivaient sans s'attendre : le second écrasait le premier. Ils viennent
    // d'un même processus (le scan, le relevé d'après Optimiser, celui d'un retrait) ou de deux (l'App et le relevé de fond,
    // l'un élevé, l'autre non). Dans le processus, un verrou qui attend son tour : un relevé n'y est jamais perdu. Entre
    // processus, le verrou du relevé des parties (VerrouDuReleve) : un fichier ouvert sans partage, que Windows rend à la mort
    // du processus ; recréé à chaque relevé, un antivirus peut le tenir un instant, d'où une attente bornée large, au-delà
    // de laquelle rien n'est écrit, comme une lecture ratée.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> VerrousDuProcessus = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TimeSpan AttenteParDefaut = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _attenteMax;

    /// <param name="lire">La lecture du fichier ; par défaut, celle du disque. Couture de test : une lecture ratée à un instant
    /// précis.</param>
    /// <param name="attenteMax">L'attente bornée du verrou ; par défaut <see cref="AttenteParDefaut"/>.</param>
    public SuiviDesReglages(string chemin, Func<Result<string?>>? lire = null, TimeSpan? attenteMax = null)
    {
        _fichier = new FichierJsonAtomique(chemin, CodeErreur);
        _chemin = chemin;
        _lire = lire ?? _fichier.Read;
        _attenteMax = attenteMax ?? AttenteParDefaut;
    }

    public static SuiviDesReglages ParDefaut() => new(FichierJsonAtomique.CheminLocal("reglages-suivis.json"));

    /// <param name="Lus">La date du dernier relevé de chaque réglage. Un relevé qui n'a pas lu un
    /// réglage ne le borne pas : la bascule peut dater d'avant lui.</param>
    private sealed record Releve(DateTimeOffset Le, Dictionary<string, string> Etats, Dictionary<string, DateTimeOffset>? Lus = null);

    /// <summary>Compare aux états du relevé précédent, garde les nouveaux, rend les changements. Un réglage absent d'un
    /// des deux relevés (logiciel désinstallé, lecture ratée) n'est pas un changement.</summary>
    /// <param name="consigner">L'écriture des changements au journal, sous le verrou, avant que les nouveaux états soient
    /// gardés. Elle rend faux (journal occupé, écriture ratée) : rien n'est gardé, les changements reviennent au relevé
    /// suivant, bornés pareil ; gardés quand même, la bascule serait perdue. Absente : les changements sont rendus à
    /// l'appelant seul.</param>
    public IReadOnlyList<Changement> Relever(IReadOnlyDictionary<string, string> actuels, DateTimeOffset maintenant,
        Func<string, string, string, string> libeller, Func<IReadOnlyList<Changement>, bool>? consigner = null)
    {
        lock (VerrousDuProcessus.GetOrAdd(Path.GetFullPath(_chemin), _ => new object()))
        {
            using var verrou = PrendreVerrou();
            return verrou is null ? [] : ReleverSousVerrou(actuels, maintenant, libeller, consigner);
        }
    }

    private IDisposable? PrendreVerrou() => VerrouDuReleve.Attendre(_chemin + ".verrou", _attenteMax);

    private IReadOnlyList<Changement> ReleverSousVerrou(IReadOnlyDictionary<string, string> actuels, DateTimeOffset maintenant,
        Func<string, string, string, string> libeller, Func<IReadOnlyList<Changement>, bool>? consigner)
    {
        // Absent : premier relevé, l'historique vide est le vrai. Lecture ratée (fichier tenu un instant par une écriture) :
        // ce n'est pas un historique vide ; récrit avec les seuls réglages de cet appel, il effaçait les autres (le relevé
        // d'après Optimiser ne porte que le mode Jeu, et les bascules de HAGS et du fichier d'échange ne se relevaient plus).
        // Rien n'est écrit, le relevé suivant reprend l'historique intact.
        var texte = _lire();
        if (!texte.IsOk) return [];
        Releve? avant = null;
        if (!string.IsNullOrWhiteSpace(texte.Value))
        {
            try { avant = JsonSerializer.Deserialize<Releve>(texte.Value); }
            catch (JsonException) { }
            // « {} » se lit sans erreur mais sans états : ce n'est pas un historique.
            if (avant?.Etats is null) avant = null;
            // Lu mais illisible : mis de côté (sa trace reste), et le suivi repart comme d'un premier relevé, au lieu d'être
            // bloqué pour toujours. Si la mise de côté échoue, rien n'est écrit : le relevé suivant retente.
            if (avant is null && !_fichier.MettreDeCote()) return [];
        }

        var changes = avant is null ? [] : actuels
            .Where(a => avant.Etats.TryGetValue(a.Key, out var v) && v != a.Value)
            .Select(a => new Changement(maintenant, libeller(a.Key, avant.Etats[a.Key], a.Value),
                avant.Lus?.TryGetValue(a.Key, out var lu) == true ? lu : avant.Le, $"{a.Key}:{a.Value}"))
            .ToList();
        if (changes.Count > 0 && consigner?.Invoke(changes) == false) return [];

        // Les réglages non relus cette fois gardent leur dernier état connu.
        var etats = new Dictionary<string, string>(avant?.Etats ?? [], StringComparer.Ordinal);
        var lus = new Dictionary<string, DateTimeOffset>(avant?.Lus ?? avant?.Etats.ToDictionary(e => e.Key, _ => avant.Le) ?? [], StringComparer.Ordinal);
        foreach (var a in actuels) { etats[a.Key] = a.Value; lus[a.Key] = maintenant; }
        // Ratée, les changements déjà au journal reviennent au relevé suivant avec la même borne : le journal les reconnaît.
        _fichier.WriteAtomic(JsonSerializer.Serialize(new Releve(maintenant, etats, lus)));
        return changes;
    }
}
