namespace DrDxt.Core;

/// <summary>Ce qu'une experience a reellement etabli. Quatre etats, et deux d'entre eux ne se
/// confondent jamais : <see cref="NoResolvedChange"/> a mesure sans rien voir, <see cref="Invalid"/>
/// n'a pas mesure.</summary>
public enum EvidenceOutcome
{
    /// <summary>L'ecart depasse ce que l'experience sait resoudre, dans le bon sens.</summary>
    ProvenGain,

    /// <summary>Il le depasse dans le mauvais sens : sans ce cas, une boucle garde ce qui a aide par hasard.</summary>
    ProvenRegression,

    /// <summary>Le signal est sous le bruit de cette experience : « pas resolu ici », pas « pas de gain ».</summary>
    NoResolvedChange,

    /// <summary>L'experience ne tient pas (perte de focus, capture defaillante, charge concurrente) : aucun chiffre, pas
    /// meme « pas de changement ».</summary>
    Invalid,
}

/// <summary>
/// Un ecart, et ce que l'experience pouvait resoudre, toujours ensemble. Le banc deterministe resout 0,1 % (mesuré le 06/09 :
/// sigma/moyenne 0,122 % sur 10 passes), le rejeu in-game ~13 % (sigma/base 6,46 %, 28 paires) : un booleen obligerait a
/// jeter un gain sous-systeme reel ou a le survendre. Mesuré le 07/09 sur RTX 4060 : `+100/100` rend +2,7 % de calcul GPU a watts
/// identiques (A/A' a 0,72 %), 3,6 fois sous le plancher in-game de 9,9 % ; les deux sont vrais en meme temps.
/// </summary>
/// <param name="DeltaPct">L'ecart mesure, en pourcent, signe. Conserve meme quand rien n'est
/// prouve : il faut bien l'afficher pour dire « +2,7 %, non resolu a +/-4,8 % ».</param>
/// <param name="ResolutionPct">Ce que cette experience sait resoudre. <c>+∞</c> quand elle ne le
/// sait pas : c'est la valeur que rend `InterRunMarginOfError` sous 3 passes.</param>
/// <param name="InvalidReason">Renseigne uniquement pour <see cref="EvidenceOutcome.Invalid"/>.</param>
public readonly record struct Evidence(
    double DeltaPct,
    double ResolutionPct,
    EvidenceOutcome Outcome,
    string? InvalidReason)
{
    /// <summary>Une experience qui a tenu et qui a tranche, dans un sens ou dans l'autre.</summary>
    public bool EstConcluante =>
        Outcome is EvidenceOutcome.ProvenGain or EvidenceOutcome.ProvenRegression;

    /// <summary>Juge un ecart contre la resolution de son experience. Un ecart egal a la resolution ne prouve rien ; une
    /// resolution non finie ne permet aucune conclusion.</summary>
    public static Evidence From(double deltaPct, double resolutionPct)
    {
        if (!double.IsFinite(deltaPct) || !double.IsFinite(resolutionPct) || resolutionPct < 0)
            return new Evidence(deltaPct, resolutionPct, EvidenceOutcome.NoResolvedChange, null);

        var resolu = Math.Abs(deltaPct) > resolutionPct;
        var issue = !resolu
            ? EvidenceOutcome.NoResolvedChange
            : deltaPct > 0 ? EvidenceOutcome.ProvenGain : EvidenceOutcome.ProvenRegression;

        return new Evidence(deltaPct, resolutionPct, issue, null);
    }

    /// <summary>L'experience n'a pas tenu : delta a <c>NaN</c>, qu'aucun appelant ne l'affiche par megarde.</summary>
    public static Evidence Invalide(string motif) =>
        new(double.NaN, double.NaN, EvidenceOutcome.Invalid, motif);
}

/// <summary>Ce qu'un sous-systeme mesurable a fait : debit GPU, bande passante memoire, latence
/// reseau. Resolution fine, donc capable de prouver un micro-gain que le jeu ne verra pas.</summary>
/// <param name="Nom">L'identifiant du sous-systeme mesure, ex. <c>gpu.debit</c>.</param>
public sealed record SubsystemEvidence(string Nom, Evidence Preuve);

/// <summary>Ce que le jeu a fait : la seule chose que le joueur ressent, et la moins resolue.</summary>
/// <param name="Metrique">L'identifiant de la grandeur, ex. <c>fps.moyen</c>, <c>fps.p1low</c>.</param>
public sealed record GameEvidence(string Metrique, Evidence Preuve);

/// <summary>
/// Les deux preuves coexistent : un changement produit une preuve sous-systeme et une preuve en jeu, chacune a sa
/// resolution, et l'une peut conclure sans l'autre (« debit GPU +2,7 % prouve a +/-0,3 % ; impact FPS +1,2 %, non
/// resolu a +/-4,8 % »). Aucune phrase ici : le rapport est localise, l'affichage recoit identifiants et nombres.
/// </summary>
public sealed record PerformanceEvidence(SubsystemEvidence? Subsystem, GameEvidence? Game)
{
    /// <summary>Y a-t-il quoi que ce soit a affirmer ? Vrai des qu'une preuve tranche, regression comprise.</summary>
    public bool QuelqueChoseDeProuve =>
        (Subsystem?.Preuve.EstConcluante ?? false) || (Game?.Preuve.EstConcluante ?? false);

    /// <summary>Un gain en jeu prouve au-dessus du bruit de la session : la seule base pour promettre des FPS.</summary>
    public bool GainEnJeuProuve => Game?.Preuve.Outcome == EvidenceOutcome.ProvenGain;

    /// <summary>Une degradation en jeu, prouvee : ce qui permet d'annuler ce qui vient d'etre applique.</summary>
    public bool RegressionEnJeuProuvee => Game?.Preuve.Outcome == EvidenceOutcome.ProvenRegression;
}
