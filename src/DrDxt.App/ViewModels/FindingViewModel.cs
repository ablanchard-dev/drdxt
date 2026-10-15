using DrDxt.Core;

namespace DrDxt.App.ViewModels;

public sealed class FindingViewModel
{
    /// <summary>La clé du constat, pour le reconnaître sans comparer des textes traduits.</summary>
    internal string Cle { get; }

    public FindingViewModel(Finding f, Func<string, string> localize)
    {
        Category = f.Category;
        Severity = f.Severity;
        Title = localize(f.TitleKey);
        Cle = f.TitleKey;
        Constat = DrDxt.Core.Constat.Depuis(f);

        // `f.EstimatedGainPct` n'atteint pas l'écran : c'est une estimation d'expert calibrée à la main, qui ne mesure rien.
        // Un problème prouvé dont l'ampleur ne l'est pas se dit sans nombre : le constat nomme ce qui est mal réglé, il ne
        // chiffre pas ce que ça coûte. Le champ reste sur `Finding`, il sert au tri et au registre côté Detection.
        GainLabel = string.Empty;
    }

    /// <summary>La forme commune, ce que l'écran affiche.</summary>
    public DrDxt.Core.Constat Constat { get; }

    public string Category { get; }
    public Severity Severity { get; }
    public string Title { get; }
    public string GainLabel { get; }
}
