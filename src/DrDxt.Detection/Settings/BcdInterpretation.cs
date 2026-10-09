using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Ce qu'une entree BCD veut dire — et l'argument qui se passe de valeur par defaut.
///
/// `bcdedit /enum` ne liste que les elements presents dans le magasin. En lire un prouve donc
/// que quelqu'un l'a ecrit : c'est une propriete de l'outil, pas une supposition sur un etat
/// d'origine. Aucune source n'est necessaire pour l'affirmer, et c'est ce qui rend cette famille
/// jugeable la ou le MMCSS ne l'etait pas.
///
/// Ce que ce juge ne dit pas : qu'un de ces elements coute des performances. Il dit ce qu'ils
/// FONT — imposer a Windows un choix qu'il fait seul — et que les retirer lui rend la decision.
/// Chiffrer un effet demanderait une mesure que personne n'a faite.
///
/// Pourquoi cette logique vit ici et non dans l'outil. Elle etait un `switch` en ligne dans
/// `tools/repairaudit`, donc intestable — et la machine de reference ne pose aucun element de la
/// famille horloge, si bien que ce chemin n'etait jamais exercé. « Ca compile » n'est pas « ca
/// marche » : c'est le meme motif que le regroupement des echecs de services, extrait le meme jour
/// pour la meme raison.
/// </summary>
public static class BcdInterpretation
{
    /// <summary>Les elements qui imposent a Windows une source d'horloge ou un comportement de
    /// minuterie. Leur simple presence est le constat.</summary>
    private static readonly string[] FamilleHorloge =
        ["useplatformclock", "useplatformtick", "disabledynamictick", "tscsyncpolicy"];

    /// <summary>Juge une paire (cle, valeur) lue par `bcdedit /enum`.</summary>
    public static RepairFinding Juger(string cle, string valeur) => cle.ToLowerInvariant() switch
    {
        // Valeur attendue verifiee le 17/09/2026 sur la doc Microsoft « BCDEdit /set » (page mise a jour
        // le 29/04/2024) : « IgnoreShutdownFailures … This is the default setting for Windows 8 ». La doc
        // ne dit rien de Windows 10/11 ; « IgnoreAllFailures » y est decrit comme « attempt to boot
        // normally after an error occurs », ce que la phrase ci-dessous traduit.
        "bootstatuspolicy" when valeur.Equals("IgnoreAllFailures", StringComparison.OrdinalIgnoreCase) =>
            new RepairFinding("BCD", cle, valeur, "IgnoreShutdownFailures", RepairStatus.Suspicious,
                Action: RepairAction.Recommandation,
                Explication:
                "Windows n'affichera plus l'écran de récupération après un démarrage raté. C'est "
                + "une modification fréquente des scripts d'optimisation, et elle cache les "
                // Revue adverse du 09/10/2026 : une recommandation porte son geste ; DrDXT a la carte qui le fait (et l'annule).
                + "symptômes au lieu de les corriger. DrDXT te propose de remettre le réglage de Windows, et de l'annuler "
                + "si tu changes d'avis."),

        "hypervisorlaunchtype" when valeur.Equals("Auto", StringComparison.OrdinalIgnoreCase) =>
            new RepairFinding("BCD", cle, valeur, "Auto", RepairStatus.ExpectedDefault,
                // Revue adverse du 09/10/2026 (R12) : « coûte un peu en jeu » sans source ni lecture de l'état de la VBS ;
                // le coût mesuré connu est celui de l'intégrité de la mémoire, qui a son propre constat.
                "L'hyperviseur démarre : c'est le réglage par défaut de Windows. Son coût en jeu, s'il y en a, se lit au "
                + "constat de l'intégrité de la mémoire. DrDXT ne le désactive pas à ta place."),

        var k when FamilleHorloge.Contains(k) =>
            new RepairFinding("BCD", cle, valeur, "élément absent", RepairStatus.NonDefault,
                Action: RepairAction.Recommandation,
                Explication:
                // 17/09/2026, doc Microsoft « BCDEdit /set » (29/04/2024) : useplatformclock, useplatformtick,
                // tscsyncpolicy et disabledynamictick portent chacun la note « This option should only be
                // used for debugging ». C'est ce que la phrase dit, et rien de plus.
                "Windows ne pose pas ce réglage de lui-même : un programme ou un script l'a ajouté. "
                + "Il force l'horloge que Windows utilise, alors que Windows la choisit seul selon "
                + "ton matériel, et Microsoft réserve cette option au débogage. Le retirer rend la "
                + "décision à Windows ; ce n'est pas un gain promis, et rien ici ne chiffre un effet."),

        "increaseuserva" =>
            new RepairFinding("BCD", cle, valeur, "élément absent", RepairStatus.NonDefault,
                "Windows ne pose pas ce réglage de lui-même : quelqu'un l'a ajouté. Il donne plus "
                + "de mémoire aux anciens programmes 32 bits, n'a aucun effet sur un jeu 64 bits, "
                + "et réduit la mémoire réservée à Windows. C'est un constat, pas une panne."),

        // Le repli est une vraie reponse : le produit lit l'entree et dit qu'il ne sait pas la
        // juger. La ranger en `Observed` plutot qu'en `NonDefault` evite de transformer chaque
        // ligne de `bcdedit` en anomalie — la plupart sont posees par Windows lui-meme.
        _ => new RepairFinding("BCD", cle, valeur, null, RepairStatus.Observed,
            "Réglage lu, mais DrDXT ne sait pas encore dire ce qu'il change."),
    };
}
