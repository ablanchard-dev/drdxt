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

    public SuiviDesReglages(string chemin) => _fichier = new FichierJsonAtomique(chemin, CodeErreur);

    public static SuiviDesReglages ParDefaut() => new(FichierJsonAtomique.CheminLocal("reglages-suivis.json"));

    /// <param name="Lus">La date du dernier relevé de chaque réglage. Un relevé qui n'a pas lu un
    /// réglage ne le borne pas : la bascule peut dater d'avant lui.</param>
    private sealed record Releve(DateTimeOffset Le, Dictionary<string, string> Etats, Dictionary<string, DateTimeOffset>? Lus = null);

    /// <summary>Compare aux états du relevé précédent, garde les nouveaux, rend les changements. Un réglage absent d'un
    /// des deux relevés (logiciel désinstallé, lecture ratée) n'est pas un changement.</summary>
    public IReadOnlyList<Changement> Relever(IReadOnlyDictionary<string, string> actuels, DateTimeOffset maintenant,
        Func<string, string, string, string> libeller)
    {
        Releve? avant = null;
        var texte = _fichier.Read();
        if (texte.IsOk && !string.IsNullOrWhiteSpace(texte.Value))
            try { avant = JsonSerializer.Deserialize<Releve>(texte.Value); }
            catch (JsonException) { }

        var changes = avant is null ? [] : actuels
            .Where(a => avant.Etats.TryGetValue(a.Key, out var v) && v != a.Value)
            .Select(a => new Changement(maintenant, libeller(a.Key, avant.Etats[a.Key], a.Value),
                avant.Lus?.TryGetValue(a.Key, out var lu) == true ? lu : avant.Le, $"{a.Key}:{a.Value}"))
            .ToList();

        // Les réglages non relus cette fois gardent leur dernier état connu.
        var etats = new Dictionary<string, string>(avant?.Etats ?? [], StringComparer.Ordinal);
        var lus = new Dictionary<string, DateTimeOffset>(avant?.Lus ?? avant?.Etats.ToDictionary(e => e.Key, _ => avant.Le) ?? [], StringComparer.Ordinal);
        foreach (var a in actuels) { etats[a.Key] = a.Value; lus[a.Key] = maintenant; }
        _fichier.WriteAtomic(JsonSerializer.Serialize(new Releve(maintenant, etats, lus)));
        return changes;
    }
}
