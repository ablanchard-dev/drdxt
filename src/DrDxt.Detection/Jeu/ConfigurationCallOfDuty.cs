using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Core;

namespace DrDxt.Detection.Jeu;

/// <summary>
/// Le nombre de fils de rendu de Call of Duty, lu dans son fichier de
/// réglages, comparé aux cœurs performants du processeur. Lecture seule : le jeu réécrit ce fichier à chaque sortie et à
/// chaque mise à jour, et le réglage existe dans son menu ; DrDXT n'y écrit jamais. Les guides conseillent « cœurs
/// performants moins un » ; aucune mesure indépendante publiée (les « +15 à 25 % » viennent d'optimiseurs commerciaux) :
/// le docteur dit où le régler et ce que DrDXT fera (juger sur les parties), sans chiffre promis.
///
/// Forme relevée sur la machine de référence (10/10/2026, `Documents\Call of Duty\players\s.1.0.cod24.txt0`) :
/// <c>RendererWorkerCount@0;51989;59387 = 6 // -1 to 16</c>. −1 = le jeu décide.
/// </summary>
public static partial class ConfigurationCallOfDuty
{
    public const string Nom = "fils de rendu de Call of Duty";
    public const int Automatique = -1;

    [GeneratedRegex(@"^\s*RendererWorkerCount(?:@[^=]*)?\s*=\s*(-?\d+)", RegexOptions.Multiline)]
    private static partial Regex LigneFilsDeRendu();

    /// <summary>Pur : la valeur dans le texte d'un fichier de réglages, ou null si la ligne n'y est pas.</summary>
    public static int? FilsDeRendu(string? texte)
        => texte is not null && LigneFilsDeRendu().Match(texte) is { Success: true } m
           && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Le fichier de réglages le plus récent du dossier des joueurs qui porte la ligne, et sa valeur ; null sans
    /// dossier, sans fichier ou sans ligne. Le dossier : celui que rend <c>ReglagesCod.Trouver</c>.</summary>
    public static (int Valeur, string Fichier)? Lire(string dossierPlayers)
    {
        try
        {
            if (!Directory.Exists(dossierPlayers)) return null;
            foreach (var f in new DirectoryInfo(dossierPlayers).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (f.Length > 4 << 20) continue;
                if (FilsDeRendu(File.ReadAllText(f.FullName)) is { } v) return (v, f.Name);
            }
            return null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : les cœurs performants estimés. Intel hybride (12e gén. et plus) : les cœurs performants ont deux fils,
    /// les efficaces un ; donc fils − cœurs = cœurs performants quand c'est moins que les cœurs. Intel sans hyperthreading
    /// (Core Ultra 200S : 8 P + 16 E, 24 fils) : hybride ou non ne se lit pas aux comptes, donc 0 = inconnu. Secours seulement :
    /// l'appelant lit d'abord les cœurs rapides par CpuTopology.</summary>
    public static int CoeursPerformants(string vendeur, int coeurs, int fils)
    {
        if (coeurs <= 0) return 0;
        var intel = vendeur.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        if (intel && fils == coeurs) return 0;
        var performants = fils - coeurs;
        return intel && performants > 0 && performants < coeurs ? performants : coeurs;
    }

    /// <summary>Pur : le constat. Rien sans fichier ; automatique = information ; une valeur loin des cœurs performants
    /// (sous P − 2 ou au-dessus de P) = à regarder, avec l'étape du guide, jamais un chiffre de gain.</summary>
    public static IEnumerable<RepairFinding> Juger((int Valeur, string Fichier)? lu, int coeursPerformants)
    {
        if (lu is not { } l) yield break;
        var p = coeursPerformants;
        var guide = Docteur.T("CF.Guide");
        // Cœurs performants inconnus (0, Intel sans hyperthreading) : la valeur se dit, sans « 1 pour 0 estimés ».
        if (p <= 0)
        {
            yield return new RepairFinding("CPU", Nom, l.Valeur == Automatique ? Docteur.T("CF.Auto") : Docteur.T("CF.Fils", l.Valeur.ToString(CultureInfo.InvariantCulture)),
                null, RepairStatus.Observed, Docteur.T("CF.Inconnu", l.Fichier, l.Valeur) + guide);
            yield break;
        }
        if (l.Valeur == Automatique)
        {
            yield return new RepairFinding("CPU", Nom, Docteur.T("CF.Auto"), null, RepairStatus.Observed,
                Docteur.T("CF.AutoExplication", l.Fichier, Math.Max(1, p - 1), p) + guide);
            yield break;
        }
        var loin = p > 0 && (l.Valeur < p - 2 || l.Valeur > p);
        yield return new RepairFinding("CPU", Nom, Docteur.T("CF.Fils", l.Valeur.ToString(CultureInfo.InvariantCulture)),
            p > 0 ? Docteur.T("CF.Plage", Math.Max(1, p - 2), p) : null,
            loin ? RepairStatus.Suspicious : RepairStatus.Observed,
            Docteur.T("CF.Valeur", l.Fichier, l.Valeur, p)
            + Docteur.T(loin ? "CF.Loin" : "CF.DansPlage")
            + guide,
            loin ? RepairAction.Recommandation : RepairAction.Aucune);
    }
}
