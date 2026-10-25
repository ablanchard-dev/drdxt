namespace DrDxt.Core;

/// <summary>Ce qu'un palier de sous-tension a donné : tenu (avec sa chaleur), instable (erreur matérielle, plantage) ou refusé
/// (Undervolt Protection, BIOS verrouillé).</summary>
public sealed record IssuePalier(IssuePalier.Genre Type, int MaxC = 0, double FreinePourcent = 0, string? Raison = null)
{
    public enum Genre { Stable, Instable, Refuse }
    public static IssuePalier Stable(int maxC, double freinePourcent) => new(Genre.Stable, maxC, freinePourcent);
    public static IssuePalier Instable(string raison) => new(Genre.Instable, Raison: raison);
    public static IssuePalier Refuse(string raison) => new(Genre.Refuse, Raison: raison);
}

/// <summary>Un palier tenu : son décalage, la chaleur la plus haute et la part de temps freinée pendant le test.</summary>
public sealed record PalierStable(int Mv, int MaxC, double FreinePourcent);

/// <summary>
/// La recherche de la sous-tension stable des cœurs, par paliers, en pur.
/// Chaque palier passe le test de refroidissement (charge pleine, chaleur lue chaque seconde) ; une erreur matérielle corrigée
/// (WHEA), un plantage (le PC a redémarré en plein palier : vu au démarrage suivant grâce à <see cref="PalierCommenceLe"/>) ou
/// une écriture refusée arrêtent la recherche. Le verdict est le dernier palier tenu, jamais un palier qui a failli. Les paliers
/// sont écrits ici, jamais calculés : **non chiffré**, les sous-tensions tenues sur Raptor Lake publiées par les joueurs vont de
/// −50 à −100 mV ; on part prudemment de −30, par pas de 20, et on s'arrête au-dessus du plancher.
/// </summary>
public sealed record RechercheSousTension(int Index, IReadOnlyList<PalierStable> Stables, bool Finie, int? Verdict, string? Raison,
    DateTimeOffset Debut, DateTimeOffset? PalierCommenceLe = null, PalierStable? Reference = null)
{
    public static readonly int[] Paliers = [-30, -50, -70, -90];

    public static RechercheSousTension Nouvelle(DateTimeOffset debut) => new(0, [], false, null, null, debut);

    /// <summary>Le décalage à essayer maintenant, ou null quand la recherche est finie.</summary>
    public int? PalierEnCours => Finie ? null : Paliers[Index];

    public RechercheSousTension Apres(IssuePalier issue)
    {
        if (Finie) return this;
        var mv = Paliers[Index];
        switch (issue.Type)
        {
            case IssuePalier.Genre.Stable:
                var stables = Stables.Append(new PalierStable(mv, issue.MaxC, issue.FreinePourcent)).ToList();
                return Index + 1 >= Paliers.Length
                    ? this with { Stables = stables, Finie = true, Verdict = mv, Raison = Docteur.T("RST.TousTenus"), PalierCommenceLe = null }
                    : this with { Index = Index + 1, Stables = stables, PalierCommenceLe = null };
            case IssuePalier.Genre.Instable:
                return this with { Finie = true, Verdict = Stables.LastOrDefault()?.Mv, Raison = $"{mv} mV{Docteur.DeuxPoints}{issue.Raison}", PalierCommenceLe = null };
            default:
                return this with { Finie = true, Verdict = Stables.LastOrDefault()?.Mv, Raison = issue.Raison, PalierCommenceLe = null };
        }
    }

    /// <summary>Au démarrage de l'App : si le PC a démarré après le début du palier en cours, ce palier a planté.</summary>
    public RechercheSousTension ApresRedemarrage(DateTimeOffset demarrage)
        => !Finie && PalierCommenceLe is { } t && demarrage > t
            ? Apres(IssuePalier.Instable(RaisonPlantage))
            : this;

    /// <summary>La clé de texte de la raison « le PC a redémarré pendant ce palier » (traduite par l'App).</summary>
    public const string RaisonPlantage = "SousTension.Raison.Plantage";

    /// <summary>Arrêtée par le joueur, l'App fermée ou une panne : finie sans verdict, aucun palier en cours (jamais lue comme un
    /// plantage au démarrage suivant), les paliers déjà tenus gardés.</summary>
    public RechercheSousTension Interrompre(string raison)
        => this with { Finie = true, Verdict = null, Raison = raison, PalierCommenceLe = null };
}
