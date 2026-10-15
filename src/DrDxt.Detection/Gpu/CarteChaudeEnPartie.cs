using System.Globalization;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Le pilote NVIDIA dit lui-même,
/// pendant chaque capture d'une partie, si la carte est freinée par la chaleur (raisons SwThermalSlowdown et
/// HwThermalSlowdown, NVML). Pas de seuil en degrés inventé : le seuil est celui de la carte, et c'est elle qui le dit.
/// </summary>
public static class CarteChaudeEnPartie
{
    public const string Nom = "carte graphique chaude en partie";
    /// <summary>Une minute de lectures au moins (une par seconde) pour juger.</summary>
    public const int EchantillonsMinimum = 60;
    /// <summary>**Non chiffré** : le même seuil que la chaleur négligeable du processeur (5 % du temps).</summary>
    public const double PourcentNegligeable = 5;

    /// <summary>Ce que ce constat lit d'une partie relevée : le reste du fichier est ignoré.</summary>
    public sealed record PartieLue(string Jeu, DateTimeOffset Fin, MecanismeCarte? MecanismeCarte, double? CarteCelsiusMax);

    public static IReadOnlyList<PartieLue> Lire(string chemin)
    {
        try
        {
            return File.Exists(chemin)
                ? (JsonSerializer.Deserialize<List<PartieLue?>>(File.ReadAllText(chemin)) ?? []).OfType<PartieLue>().ToList()
                : [];
        }
        catch (Exception) { return []; }
    }

    /// <summary>Pur : la dernière partie où la carte a été lue assez longtemps ; freinée par la chaleur au-delà du négligeable,
    /// un constat ; sinon rien (la température seule ne dit pas qu'elle se freine).</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<PartieLue> parties)
    {
        var fr = CultureInfo.InvariantCulture;
        if (parties.Where(p => p.MecanismeCarte is { Echantillons: >= EchantillonsMinimum }).OrderByDescending(p => p.Fin).FirstOrDefault()
            is not { MecanismeCarte: { } m } derniere)
        {
            // Température relevée sans raisons du pilote (carte AMD ou Intel Arc) : dit, pas tu.
            // Seulement si aucune partie n'a porté de raisons : sur une NVIDIA, une partie trop courte n'est pas « non lue ».
            if (parties.All(p => p.MecanismeCarte is null)
                && parties.OrderByDescending(p => p.Fin).FirstOrDefault(p => p.CarteCelsiusMax is not null) is { CarteCelsiusMax: { } lue })
                yield return new RepairFinding("GPU", Nom, Docteur.T("CC.JusquA", lue.ToString("0", fr)), null, RepairStatus.Unknown,
                    Docteur.T("CC.NonLu", lue.ToString("0", fr)));
            yield break;
        }
        if (m.PourcentChaleur < PourcentNegligeable) yield break;
        var pct = Math.Round(m.PourcentChaleur).ToString("0", fr);
        yield return new RepairFinding("GPU", Nom, Docteur.T("CC.Freinee", pct), Docteur.T("CC.Sous", PourcentNegligeable.ToString("0", fr)),
            RepairStatus.Suspicious,
            Docteur.T("CC.Explication", derniere.Jeu, derniere.Fin.ToLocalTime(), pct)
            + (derniere.CarteCelsiusMax is { } t ? Docteur.T("CC.Temp", t.ToString("0", fr)) : "")
            + Docteur.T("CC.Conseil"), RepairAction.Recommandation);
    }
}
