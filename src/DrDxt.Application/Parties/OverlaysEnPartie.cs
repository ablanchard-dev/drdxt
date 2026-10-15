namespace DrDxt.Application.Parties;

/// <summary>
/// Les overlays ouverts pendant une partie, et ce qu'ils coûtent chez
/// le joueur. Source : le support de Riot Games (« Disabling overlays », League of Legends, Performance) les range parmi ce
/// qui peut prendre de la bande passante ou poser des problèmes de compatibilité, sans chiffre. Un chiffre général n'existe
/// donc pas ; le seul qui vaille est celui du joueur : ses parties d'un même jeu avec l'overlay, contre celles sans.
/// Lu par le nom des processus, sans jamais ouvrir le jeu (un anti-triche n'aime pas qu'on lise sa mémoire). Ce qui ne
/// se voit pas ainsi reste inconnu : l'overlay de Discord vit dans le processus du jeu. NET2 : Discord compte
/// donc sous le nom de ce que le processus prouve, « Discord (ouvert) », et l'écart se lit ainsi (Discord ouvert contre
/// fermé). L'overlay d'AMD attend le nom de son processus, relevé sur une vraie Radeon.
/// </summary>
public static class OverlaysEnPartie
{
    /// <summary>Nom du processus (sans « .exe ») ⇒ nom affiché. Steam ne lance GameOverlayUI qu'une fois l'overlay
    /// accroché au jeu ; RTSS dessine son affichage dans les jeux tant qu'il tourne.</summary>
    public static readonly IReadOnlyDictionary<string, string> Connus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RTSS"] = "RivaTuner",
        ["GameOverlayUI"] = "Steam",
        ["NVIDIA Overlay"] = "NVIDIA",
        ["Overwolf"] = "Overwolf",
        ["GameBar"] = "Xbox Game Bar",
        ["Discord"] = "Discord (ouvert)",
    };

    public static IReadOnlyList<string> Reconnaitre(IEnumerable<string> processus)
        => processus.Where(Connus.ContainsKey).Select(n => Connus[n]).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Un écart entre les parties d'un jeu avec l'overlay et celles sans, en FPS médian.
    /// Le verdict suit la règle des autres juges, séparation nette du 1 % low ou rien : « pire » si
    /// toutes les parties avec sont sous toutes celles sans, « mieux » à l'inverse, sinon pas de différence nette.</summary>
    public sealed record Ecart(string Jeu, string Overlay, double FpsAvec, double FpsSans, int PartiesAvec, int PartiesSans,
        VerdictEnJeu Verdict = VerdictEnJeu.PasDeDifference);

    /// <summary>Un overlay vu sur des parties d'un jeu, sans assez de parties sans lui pour le comparer.</summary>
    public sealed record SansComparaison(string Jeu, string Overlay, int PartiesAvec, int PartiesSans);

    /// <summary>Deux parties de chaque côté au moins : une seule partie, c'est une carte et une soirée.</summary>
    public const int PartiesParCote = 2;

    /// <summary>Par jeu et par overlay vu. Une partie sans relevé d'overlays ne compte d'aucun côté ; une partie jouée en
    /// essai d'un réglage non plus, l'essai changerait l'écart. Une partie où l'overlay n'a été vu qu'un moment compte
    /// « avec » : on surestime son coût plutôt que de le cacher.</summary>
    /// <param name="changements">Les changements de DrDXT et les mises à jour de la machine. Seules les
    /// parties d'après le dernier se comparent ; sinon l'écart d'un levier ou d'un pilote passait pour celui de l'overlay.</param>
    public static (IReadOnlyList<Ecart> Ecarts, IReadOnlyList<SansComparaison> Autres) Comparer(IEnumerable<PartieRelevee> parties,
        IEnumerable<DateTimeOffset>? changements = null)
    {
        var ecarts = new List<Ecart>();
        var autres = new List<SansComparaison>();
        var depuis = (changements ?? []).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        foreach (var jeu in parties.Where(p => p.Overlays is not null && p.Essai is null && p.Debut >= depuis).GroupBy(p => p.Jeu, StringComparer.OrdinalIgnoreCase))
            foreach (var overlay in jeu.SelectMany(p => p.Overlays!).Distinct().Order(StringComparer.Ordinal))
            {
                var avec = jeu.Where(p => p.Overlays!.Contains(overlay)).ToList();
                var sans = jeu.Where(p => !p.Overlays!.Contains(overlay)).ToList();
                if (avec.Count >= PartiesParCote && sans.Count >= PartiesParCote)
                    ecarts.Add(new Ecart(jeu.Key, overlay, Mediane(avec), Mediane(sans), avec.Count, sans.Count,
                        avec.Max(p => p.Low1Median) < sans.Min(p => p.Low1Median) ? VerdictEnJeu.Pire
                        : avec.Min(p => p.Low1Median) > sans.Max(p => p.Low1Median) ? VerdictEnJeu.Mieux
                        : VerdictEnJeu.PasDeDifference));
                else
                    autres.Add(new SansComparaison(jeu.Key, overlay, avec.Count, sans.Count));
            }
        return (ecarts, autres);
    }

    private static double Mediane(IReadOnlyList<PartieRelevee> parties)
    {
        var fps = parties.Select(p => p.FpsMedian).Order().ToList();
        return fps.Count % 2 == 1 ? fps[fps.Count / 2] : (fps[fps.Count / 2 - 1] + fps[fps.Count / 2]) / 2;
    }
}
