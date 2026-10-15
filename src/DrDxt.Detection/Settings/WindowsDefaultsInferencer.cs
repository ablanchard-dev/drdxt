using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Les degats deja infliges a la machine.
///
/// « Ce qui te coûte des images » dans sa forme la plus fréquente n'est pas un réglage jamais
/// activé : c'est un défaut Windows qu'un « optimiseur » précédent a cassé. Ces deux-là étaient
/// prévus depuis le modèle de notation initial (non publié) et jamais implémentés, faute de lecteurs.
///
/// La règle qui sépare les deux cas ci-dessous, et qui décide s'ils déduisent des points :
/// **on ne pénalise que ce que la recherche du dépôt source comme un vrai risque.** Le reste est
/// signalé comme une régression à annuler, sans promesse et sans point retiré, parce qu'inventer
/// un déficit sur un réglage dont l'impact est documenté comme négligeable est exactement la
/// faute que l'audit adverse (C2) reproche.
/// </summary>
public static class WindowsDefaultsInferencer
{
    // ── SysMain (ex-SuperFetch) ─────────────────────────────────────────────────────────────
    // Valeurs de `Start` d'un service Windows : 2 = automatique, 3 = manuel, 4 = desactive.
    private const int ServiceDisabled = 4;

    /// <summary>
    /// SysMain est-il encore en place ? Off = quelqu'un l'a désactivé, le geste « optimiseur »
    /// le plus courant. Unknown quand la valeur ne se lit pas : ne jamais conclure « désactivé »
    /// sur une lecture ratée.
    ///
    /// Off n'est pas un déficit chiffré, et ce fichier ne prétend pas le contraire.
    /// La note mémoire du projet (non publiée) conclut : gain **non prouvé** sur une install
    /// saine, et sur SSD l'effet de SysMain (positif comme négatif) est négligeable. On le
    /// signale pour ce qu'il est : une valeur par défaut que quelqu'un a changée.
    /// Source : https://learn.microsoft.com/en-us/powershell/module/mmagent/enable-mmagent
    /// </summary>
    public static TriState InferSysMain(object? startValue) => startValue switch
    {
        int start when start == ServiceDisabled => TriState.Off,
        int => TriState.On,
        // Valeur absente ou d'un autre type : le service n'est pas dans l'état qu'on sait lire.
        // On ne devine pas.
        _ => TriState.Unknown,
    };

    // ── MMCSS ───────────────────────────────────────────────────────────────────────────────
    // `SystemResponsiveness` réserve un pourcentage de CPU aux tâches non multimédia. Windows
    // l'écrit à 20 ; 100 désactive MMCSS.
    private const int ResponsivenessDesactive = 100;

    /// <summary>
    /// MMCSS est-il intact ? Off = `SystemResponsiveness` vaut **100**, la seule valeur qui le coupe. La page Microsoft :
    /// « Values below 10 and above 100 are clamped to 20. A value of 100 disables MMCSS. » Le 0 recopié partout est
    /// donc ramené à 20 : sans effet, pas une panne. Même règle que le docteur (MmcssReader).
    ///
    /// Contrairement à SysMain, celui-ci est sourcé comme une régression :
    /// La note latence du projet (non publiée) le classe placebo pouvant **dégrader l'audio** sans
    /// gain prouvé en jeu, et MMCSS est le mécanisme dont dépend le service audio.
    /// Source : https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
    /// </summary>
    /// <param name="responsiveness">
    /// La valeur brute lue. <c>null</c> = absente, ce qui est le cas normal : Windows n'écrit pas
    /// toujours la valeur et applique son défaut. Absent vaut donc intact, jamais Unknown, sans
    /// quoi la quasi-totalité des machines saines ressortirait « non lue ».
    /// </param>
    /// <param name="keyReadable">
    /// La clé a-t-elle pu être ouverte ? C'est ce qui distingue « la valeur n'y est pas » (défaut
    /// Windows, intact) de « je n'ai rien pu lire » (Unknown). Sans ce booléen les deux se
    /// confondent, et l'un des deux est un mensonge.
    /// </param>
    public static TriState InferMmcss(object? responsiveness, bool keyReadable)
    {
        if (!keyReadable) return TriState.Unknown;

        return responsiveness switch
        {
            int value when value == ResponsivenessDesactive => TriState.Off,
            int => TriState.On,
            // Absente = Windows applique son défaut = intact.
            null => TriState.On,
            // Présente mais d'un type inattendu : quelqu'un a écrit là quelque chose qu'on ne
            // sait pas lire. On ne conclut ni dans un sens ni dans l'autre.
            _ => TriState.Unknown,
        };
    }
}
