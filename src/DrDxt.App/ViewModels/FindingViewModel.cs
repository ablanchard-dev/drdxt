using DrDxt.Core;

namespace DrDxt.App.ViewModels;

public sealed class FindingViewModel
{
    /// <summary>Revue du 10/10/2026 : la clé du constat, pour le reconnaître sans comparer des textes traduits.</summary>
    internal string Cle { get; }

    public FindingViewModel(Finding f, Func<string, string> localize)
    {
        Category = f.Category;
        Severity = f.Severity;
        Title = localize(f.TitleKey);
        Cle = f.TitleKey;
        Constat = DrDxt.Core.Constat.Depuis(f);

        // Le dernier reste du score, retire le 05/09/2026.
        //
        // Ce bloc rendait `f.EstimatedGainPct` au client via « ~{0}% ». Or la documentation de
        // `ScoringRules` le dit mot pour mot : « a finding's EstimatedGainPct is exactly the
        // penalty the score removed ». C'est donc une estimation d'expert calibree a la main —
        // la grandeur meme pour laquelle le score /100 a ete retire de la vitrine le 02/09, parce
        // qu'elle ne mesure rien. Chassee de l'entete, elle revenait ligne par ligne a cote de
        // chaque constat.
        //
        // Ni le balayage anti-promesse ni le test d'invariance ne l'ont vue : le premier lit les
        // chaines litterales et le nombre est interpole a l'execution ; le second comparait deux
        // scans dont le decor ne portait aucun constat a gain estime.
        //
        // Doctrine du produit : un probleme prouve dont l'ampleur ne l'est pas se dit sans nombre.
        // Le constat nomme ce qui est mal regle ; il ne chiffre pas ce que ca coute. Le champ
        // reste sur `Finding` — il sert au TRI et au registre cote Detection — mais il n'atteint
        // plus l'ecran.
        GainLabel = string.Empty;
    }

    /// <summary>chantier B0 : la forme commune, ce que l'écran affiche.</summary>
    public DrDxt.Core.Constat Constat { get; }

    public string Category { get; }
    public Severity Severity { get; }
    public string Title { get; }
    public string GainLabel { get; }
}
