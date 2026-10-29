using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Ce qu'une entree BCD veut dire. `bcdedit /enum` ne liste que les elements presents : en lire un prouve que quelqu'un
/// l'a ecrit, une propriete de l'outil, sans valeur par defaut a supposer. Le juge ne dit pas qu'un element coute des
/// performances (aucune mesure faite) : il dit qu'il impose a Windows un choix qu'il fait seul, et que le retirer lui rend
/// la decision. Ici et non dans `tools/repairaudit`, pour etre teste : la machine de reference ne pose aucun element de
/// la famille horloge.
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
        // Valeur attendue lue le 17/09/2026 sur la doc Microsoft « BCDEdit /set », page publiée et mise a jour le 29/04/2024 :
        // « IgnoreShutdownFailures … This is the default setting for Windows 8 », rien sur Windows 10/11 ;
        // « IgnoreAllFailures » : « attempt to boot normally after an error occurs », ce que la phrase traduit.
        "bootstatuspolicy" when valeur.Equals("IgnoreAllFailures", StringComparison.OrdinalIgnoreCase) =>
            new RepairFinding("BCD", cle, valeur, "IgnoreShutdownFailures", RepairStatus.Suspicious,
                Action: RepairAction.Recommandation,
                Explication:
                Docteur.T("BC.BootStatus"),
                // Une recommandation porte son geste ; DrDXT a la carte qui le fait (et l'annule).
                Remede: Docteur.T("BC.BootStatus.Remede")),

        "hypervisorlaunchtype" when valeur.Equals("Auto", StringComparison.OrdinalIgnoreCase) =>
            new RepairFinding("BCD", cle, valeur, "Auto", RepairStatus.ExpectedDefault,
                // « coûte un peu en jeu » sans source ni lecture de l'état de la VBS ;
                // le coût mesuré connu est celui de l'intégrité de la mémoire, qui a son propre constat.
                Docteur.T("BC.Hyperviseur")),

        var k when FamilleHorloge.Contains(k) =>
            new RepairFinding("BCD", cle, valeur, Docteur.T("BC.Absent"), RepairStatus.NonDefault,
                Action: RepairAction.Recommandation,
                Explication:
                // Doc Microsoft « BCDEdit /set » : useplatformclock, useplatformtick, tscsyncpolicy et disabledynamictick
                // portent chacun « This option should only be used for debugging », et la phrase ne dit rien de plus.
                Docteur.T("BC.Horloge"),
                // Pas de carte pour ces éléments : le geste est la commande, sur l'élément lu.
                Remede: Docteur.T("BC.Horloge.Remede", k)),

        "increaseuserva" =>
            new RepairFinding("BCD", cle, valeur, Docteur.T("BC.Absent"), RepairStatus.NonDefault,
                Docteur.T("BC.UserVa")),

        // Le repli dit « lu, pas jugé » : `Observed` plutot que `NonDefault`, la plupart des lignes de `bcdedit` sont posees
        // par Windows lui-meme.
        _ => new RepairFinding("BCD", cle, valeur, null, RepairStatus.Observed,
            Docteur.T("BC.Inconnu")),
    };
}
