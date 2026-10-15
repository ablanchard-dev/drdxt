namespace DrDxt.Core;

/// <summary>Ce qu'une experience a reellement etabli. Quatre etats, et deux d'entre eux ne se
/// confondent jamais : <see cref="NoResolvedChange"/> a mesure sans rien voir, <see cref="Invalid"/>
/// n'a pas mesure.</summary>
public enum EvidenceOutcome
{
    /// <summary>L'ecart depasse ce que l'experience sait resoudre, dans le bon sens.</summary>
    ProvenGain,

    /// <summary>Il le depasse dans le mauvais sens. Une boucle qui ne sait pas dire ca ne peut
    /// pas « conserver ce qui ameliore » : elle garde ce qui a aide par hasard.</summary>
    ProvenRegression,

    /// <summary>Le signal est sous le bruit de cette experience. Ce n'est pas « pas de gain » :
    /// c'est « pas resolu ici ». Le meme ecart peut etre prouve sur un banc plus fin.</summary>
    NoResolvedChange,

    /// <summary>L'experience elle-meme ne tient pas : perte de focus, capture defaillante, charge
    /// concurrente. Aucun chiffre n'en sort, et surtout pas un « pas de changement ».</summary>
    Invalid,
}

/// <summary>
/// Un ecart, et ce que l'experience pouvait resoudre : les deux ensemble, toujours.
///
/// Ce type remplace un booleen, `AbVerdict.IsMeasurableGain`, et c'est lui qui forcait a mentir.
/// Le banc deterministe resout **0,1 %** (mesure du 06/09 : sigma/moyenne 0,122 % sur 10 passes) ;
/// le rejeu in-game resout **~13 %** (sigma/base 6,46 %, 28 paires, meme date). Un booleen ne peut
/// pas porter cette difference : il oblige a jeter un gain sous-systeme reel, ou a le survendre.
///
/// Le cas qui a impose ce modele (07/09, RTX 4060) : l'offset `+100/100` rend **+2,7 % de calcul
/// GPU a watts identiques**, reproductible, controle A/A' a 0,72 %, et c'est **3,6 fois sous le
/// plancher in-game de 9,9 %**. Les deux affirmations sont vraies en meme temps.
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

    /// <summary>Juge un ecart contre la resolution de l'experience qui l'a produit.
    ///
    /// La borne se resout du cote prudent : un ecart egal a la resolution ne prouve rien : un
    /// instrument qui conclut pile a son propre seuil prouve son seuil, pas un gain. Et une
    /// resolution non finie ne permet aucune conclusion : affirmer reviendrait a inventer une
    /// resolution que personne n'a mesuree.</summary>
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

    /// <summary>L'experience n'a pas tenu. Aucun chiffre n'en sort : le delta est mis a
    /// <c>NaN</c> pour qu'aucun appelant ne puisse l'afficher par megarde.</summary>
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
/// Les deux preuves coexistent, elles ne s'excluent pas.
///
/// C'est la correction de fond apportee au modele a trois niveaux « A ou B ou C » : un changement
/// produit une preuve sous-systeme et une preuve en jeu, chacune avec sa propre resolution, et il
/// est parfaitement normal que l'une conclue et pas l'autre. La phrase cible, litteralement :
/// « debit GPU +2,7 % prouve a +/-0,3 % ; impact FPS +1,2 %, non resolu avec une resolution de
/// +/-4,8 % ».
///
/// Aucune phrase n'est construite ici. Le rapport client est localise, et ce depot a deja paye
/// une fois le prix d'une phrase assemblee dans le moteur : le motif du classifieur de goulot
/// sortait en anglais au milieu d'un rapport francais. L'affichage recoit des identifiants et des
/// nombres, et c'est lui qui parle.
/// </summary>
public sealed record PerformanceEvidence(SubsystemEvidence? Subsystem, GameEvidence? Game)
{
    /// <summary>Y a-t-il quoi que ce soit a affirmer ? Vrai des qu'une des deux preuves tranche,
    /// **y compris une regression** : « ce reglage degrade ta machine » est une conclusion utile.</summary>
    public bool QuelqueChoseDeProuve =>
        (Subsystem?.Preuve.EstConcluante ?? false) || (Game?.Preuve.EstConcluante ?? false);

    /// <summary>Un gain que le joueur ressent, prouve au-dessus du bruit de sa session. C'est la
    /// seule affirmation qui justifie de promettre des FPS.</summary>
    public bool GainEnJeuProuve => Game?.Preuve.Outcome == EvidenceOutcome.ProvenGain;

    /// <summary>Une degradation en jeu, prouvee. La moitie sans laquelle une boucle d'optimisation
    /// ne peut pas annuler ce qu'elle vient d'appliquer.</summary>
    public bool RegressionEnJeuProuvee => Game?.Preuve.Outcome == EvidenceOutcome.ProvenRegression;
}
