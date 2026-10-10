using System.Globalization;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// 10/10/2026 soir : sur la machine de référence, 100 °C en partie (registres du processeur) et des 1 % low de 63 contre
/// 91 deux jours avant, avec un refroidisseur sain. La cause n'était pas le montage : la limite de puissance avait été
/// ouverte au BIOS (65 → 253 W) et le refroidisseur saturait sur les pics, alors que le jeu tirait 117 W en médiane.
/// Le docteur relie donc les deux : la chaleur relevée en partie et la limite de puissance lue, et propose le plafond
/// qui garde le jeu tel quel (la médiane, plus une marge) en coupant les pics. Lecture seule du relevé des parties.
/// </summary>
public static class ChaleurEnPartie
{
    public const string Nom = "chaleur en partie";
    /// <summary>Le seuil d'Intel : 100 °C = freinage ; 95 laisse voir les pics que six captures par partie ratent.</summary>
    public const double SeuilC = 95;
    public const int FenetreJours = 14;
    public const int PartiesMaximum = 5;

    /// <summary>Ce que le docteur lit d'une partie relevée : le reste du fichier est ignoré.</summary>
    public sealed record PartieLue(string Jeu, DateTimeOffset Fin, double? CelsiusMax, double? WattsMedian, double Low1Median);

    /// <summary>Les dernières parties (deux semaines, cinq au plus, la plus récente d'abord) ; vide sans fichier, sans
    /// droit ou sans forme connue : rien n'est inventé.</summary>
    public static IReadOnlyList<PartieLue> Lire(string chemin, DateTimeOffset? maintenant = null)
    {
        try
        {
            if (!File.Exists(chemin)) return [];
            var lues = JsonSerializer.Deserialize<List<PartieLue?>>(File.ReadAllText(chemin)) ?? [];
            var depuis = (maintenant ?? DateTimeOffset.UtcNow) - TimeSpan.FromDays(FenetreJours);
            return lues.OfType<PartieLue>().Where(p => p.Fin >= depuis).OrderByDescending(p => p.Fin).Take(PartiesMaximum).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Pur : le plafond de puissance qui garde la partie telle quelle : la médiane plus un tiers, arrondie à la
    /// dizaine, jamais sous 125 W (le profil de base d'Intel pour les K).</summary>
    public static int Plafond(double wattsMedian) => Math.Max(125, (int)Math.Ceiling(wattsMedian * 1.35 / 10) * 10);

    /// <summary>Pur : la température la plus haute relevée sur ces parties, ou null si aucune ne l'a lue.</summary>
    public static double? MaxC(IReadOnlyList<PartieLue> parties)
        => parties.Select(p => p.CelsiusMax).OfType<double>().DefaultIfEmpty().Max() is var m and > 0 ? m : null;

    /// <summary>Pur : le constat. Rien sans température relevée ; sous le seuil, une information ; au seuil, à regarder,
    /// avec le plafond conseillé quand la limite lue laisse passer plus, sinon le refroidissement.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<PartieLue> parties, LimiteDePuissanceLue? limite)
    {
        if (MaxC(parties) is not { } max) yield break;
        var fr = CultureInfo.InvariantCulture;
        var chaude = parties.Where(p => p.CelsiusMax is { } c && c >= SeuilC).OrderByDescending(p => p.CelsiusMax).FirstOrDefault();
        var n = parties.Count(p => p.CelsiusMax is not null);
        if (chaude is null)
        {
            yield return new RepairFinding("CPU", Nom, $"jusqu'à {max.ToString("0", fr)} °C", null, RepairStatus.Observed,
                $"sur tes {n} dernière(s) partie(s) relevée(s), le processeur est resté sous {SeuilC.ToString("0", fr)} °C : il ne se freine pas.");
            yield break;
        }

        var watts = chaude.WattsMedian is { } w && w > 0 ? w : (double?)null;
        var tete = $"{chaude.CelsiusMax!.Value.ToString("0", fr)} °C au plus sur « {chaude.Jeu} » le {chaude.Fin.ToLocalTime():dd/MM} ({n} partie(s) lue(s)) : "
                   + "à 100 °C le processeur se freine lui-même, et ce sont tes 1 % low qui en pâtissent. ";
        string suite;
        if (watts is { } wm && (limite is null || limite.SansLimite || limite.Pl2W > Plafond(wm) + 5))
        {
            var cap = Plafond(wm);
            suite = $"Il tirait {wm.ToString("0", fr)} W en médiane"
                    + (limite is { SansLimite: false } l ? $", et sa limite de puissance (PL1 {l.Pl1W.ToString("0", fr)} W, PL2 {l.Pl2W.ToString("0", fr)} W) laisse passer des pics bien au-dessus" : ", sans limite de puissance qui tienne les pics")
                    + $". Au BIOS, mets PL1 = PL2 autour de {cap} W (guide 5.3) : ton jeu tourne pareil, seuls les pics qui chauffent sautent."
                    + $" Encore {SeuilC.ToString("0", fr)} °C ou plus à {cap} W : c'est le refroidissement (montage, pâte, second ventilateur).";
        }
        else
        {
            suite = (limite is { SansLimite: false } l
                        ? $"Sa limite de puissance est déjà tenue (PL1 {l.Pl1W.ToString("0", fr)} W, PL2 {l.Pl2W.ToString("0", fr)} W) : "
                        : "")
                    + "c'est le refroidissement qui ne suit pas : montage du ventirad, pâte, ventilateurs, poussière (guide 5.3, dernière étape).";
        }
        yield return new RepairFinding("CPU", Nom, $"jusqu'à {max.ToString("0", fr)} °C",
            $"sous {SeuilC.ToString("0", fr)} °C en partie", RepairStatus.Suspicious, tete + suite, RepairAction.Recommandation);
    }
}
