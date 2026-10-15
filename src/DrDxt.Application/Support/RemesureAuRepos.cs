namespace DrDxt.Application.Support;

/// <summary>Ce que le produit sait du poste au moment de decider. <paramref name="ChargeCpuPourcent"/>
/// <c>null</c> = non lue (un repos non prouve n'est pas un repos).</summary>
public sealed record EtatDuPoste(
    TimeSpan SansSaisie,
    bool JeuOuvert,
    double? ChargeCpuPourcent,
    bool ReferenceSuspecte,
    DateTimeOffset? DernierEssai);

public enum DecisionRemesure
{
    Mesurer,
    RienASuspecter,
    PasAuRepos,
    TropTot,
}

/// <summary>
/// Remesurer la memoire et le processeur sans demander au joueur.
///
/// Les deux references de la machine de reference ont ete prises pendant une partie (memoire 16/09,
/// processeur 11/09) ; l'ecran le dit depuis le 26/09, et la seule issue etait un geste : « remesure, jeu
/// ferme ». La regle rend ce geste inutile sans rien imposer : on ne remesure qu'une reference deja
/// signalee comme suspecte, et seulement quand le poste est vraiment au repos (aucun jeu, meme reduit ;
/// personne au clavier depuis 5 min ; processeur sous 10 %), au plus une fois par heure. Une mesure
/// trop bruitee garde l'ancienne reference, comme a la main.
///
/// Seuils : les essais du 25/09 ont montre qu'une charge lourde fausse la mesure, pas a partir de quelle
/// charge ; cette borne-la reste inconnue. Ce qui est mesure (27/09, 20 releves d'1 s sur la machine de
/// reference au repos) : mediane 6,7 %, p90 9,0 %, max 12,2 %. 10 % laisse donc passer environ 9 repos
/// sur 10 ; plus bas, la remesure ne partirait presque jamais. 5 min sans saisie : choix prudent, non mesure.
/// </summary>
public static class RemesureAuRepos
{
    public static readonly TimeSpan SansSaisieMin = TimeSpan.FromMinutes(5);
    public const double ChargeCpuMaxPourcent = 10;
    public static readonly TimeSpan EntreDeuxEssais = TimeSpan.FromHours(1);

    public static DecisionRemesure Decider(EtatDuPoste poste, DateTimeOffset maintenant)
    {
        if (!poste.ReferenceSuspecte) return DecisionRemesure.RienASuspecter;
        if (poste.JeuOuvert || poste.SansSaisie < SansSaisieMin
            || poste.ChargeCpuPourcent is not { } charge || charge >= ChargeCpuMaxPourcent)
            return DecisionRemesure.PasAuRepos;
        if (poste.DernierEssai is { } dernier && maintenant - dernier < EntreDeuxEssais)
            return DecisionRemesure.TropTot;
        return DecisionRemesure.Mesurer;
    }
}
