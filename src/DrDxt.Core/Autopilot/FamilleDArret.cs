namespace DrDxt.Core;

/// <summary>
/// La cause probable d'un arrêt brutal (`Kernel-Power 41`), lue dans ses champs. Jamais une cause
/// affirmée : une famille et ses indices. Sources : Microsoft Learn « Event ID 41 The system has rebooted without
/// cleanly shutting down first » (version du 12/02/2026) et « Bug Check Code Reference ». L'appui long sur le bouton n'est pas une famille : c'est un geste,
/// il est écarté avant (`ArretBrutal.HorsBouton`).
/// </summary>
public enum FamilleDArret
{
    /// <summary>`SleepInProgress` non nul, sans code d'arrêt : une transition d'alimentation était signalée en cours
    /// (veille ou sortie de veille). Microsoft ne documente pas le sens exact du champ.</summary>
    Veille,

    /// <summary>Un écran bleu dont le code appartient au sous-système graphique (0x116, 0x117, 0x141, 0x193).</summary>
    EcranBleuGraphique,

    /// <summary>Un écran bleu d'un autre code : il laisse son code, donc un incident suivant reste attribuable.</summary>
    EcranBleu,

    /// <summary>Aucun code, aucune transition : gel, coupure de courant ou alimentation qui décroche (scénario 3 de
    /// Microsoft, qui cite l'alimentation sous-dimensionnée). Le journal ne sépare pas ces cas, et un arrêt sec ne
    /// laisse aucun vidage.</summary>
    SansCode,
}

/// <summary>Un arrêt brutal daté, avec sa famille et, pour un écran bleu, son code. <paramref name="PendantLaPartieDe"/> :
/// le jeu que le relevé voyait tourner juste avant (« sous charge »), null si rien ne le montre.</summary>
public sealed record ArretClasse(DateTimeOffset Quand, FamilleDArret Famille, int CodeArret = 0, string? PendantLaPartieDe = null)
{
    /// <summary>Un arrêt lu sans ses champs (lecteur qui ne rend que des dates) : sans code, faute de mieux. C'est
    /// la lecture la plus prudente, puisque c'est la famille qui ferme le plus.</summary>
    public static ArretClasse SansDetail(DateTimeOffset quand) => new(quand, FamilleDArret.SansCode);
}

/// <summary>Une partie que le relevé n'a pas pu clore (marque « en cours » restée au démarrage suivant),
/// avec la dernière fois qu'il a vu le jeu tourner et l'heure où la marque a été relevée.</summary>
public sealed record PartieInterrompue(string Jeu, DateTimeOffset Debut, DateTimeOffset DernierVu, DateTimeOffset? ReleveeLe = null);

public static class ClasseurDArrets
{
    /// <summary>« Sous charge » : l'événement 41 ne le dit pas, et il est daté du redémarrage, pas de l'arrêt. Une partie
    /// restée ouverte a été coupée entre son dernier regard et le relevé suivant (qui suit le redémarrage) : le premier arrêt
    /// daté dans cet intervalle est le sien, et une partie n'en marque qu'un (des redémarrages en
    /// boucle compteraient la même partie deux fois). Sans heure de relevé, aucune.</summary>
    public static IReadOnlyList<ArretClasse> MarquerSousCharge(IReadOnlyList<ArretClasse> arrets, IReadOnlyList<PartieInterrompue?> interrompues)
    {
        var marques = arrets.ToList();
        foreach (var p in interrompues)
        {
            if (p is not { ReleveeLe: { } relevee }) continue;
            var i = marques.Select((a, n) => (a, n))
                .Where(x => x.a.PendantLaPartieDe is null && x.a.Quand >= p.DernierVu && x.a.Quand <= relevee)
                .OrderBy(x => x.a.Quand).Select(x => (int?)x.n).FirstOrDefault();
            if (i is { } n) marques[n] = marques[n] with { PendantLaPartieDe = p.Jeu };
        }
        return marques;
    }

    /// <summary>Les codes d'arrêt du sous-système graphique (Microsoft, « Bug Check Code Reference »).</summary>
    public static readonly IReadOnlyDictionary<int, string> CodesGraphiques = new Dictionary<int, string>
    {
        [0x116] = "VIDEO_TDR_FAILURE",
        [0x117] = "VIDEO_TDR_TIMEOUT_DETECTED",
        [0x141] = "VIDEO_ENGINE_TIMEOUT_DETECTED",
        [0x193] = "VIDEO_DXGKRNL_LIVEDUMP",
    };

    /// <summary>Le code d'arrêt prime : un écran bleu pendant une transition reste un écran bleu.</summary>
    public static FamilleDArret Classer(int codeArret, bool transitionEnCours) =>
        codeArret != 0
            ? CodesGraphiques.ContainsKey(codeArret) ? FamilleDArret.EcranBleuGraphique : FamilleDArret.EcranBleu
            : transitionEnCours ? FamilleDArret.Veille : FamilleDArret.SansCode;

    /// <summary>La famille en mots, avec ce qui la fonde.</summary>
    public static string Nom(FamilleDArret f) => f switch
    {
        FamilleDArret.Veille => Docteur.T("FA.Veille"),
        FamilleDArret.EcranBleuGraphique => Docteur.T("FA.EcranBleuGraphique"),
        FamilleDArret.EcranBleu => Docteur.T("FA.EcranBleu"),
        _ => Docteur.T("FA.SansCode"),
    };
}
