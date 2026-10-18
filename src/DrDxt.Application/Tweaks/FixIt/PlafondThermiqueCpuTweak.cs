using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Le plafond de chaleur du processeur Intel, posé à chaud sur la limite continue (PL1) : la médiane de la
// partie plus un tiers, arrondie (ChaleurEnPartie.Plafond, non chiffré), seulement quand la dernière partie
// relevée a été freinée par la chaleur. C'est le « PL1 = PL2 autour de 160 W » qu'un joueur réglerait au BIOS,
// posé sans redémarrer.
//
// Ce qui est écrit : le seul champ PL1 de MSR 0x610, vers le bas (PL1 seule, sous PL2, la forme que le helper
// admet). La PL2 (les pointes de quelques secondes) reste au BIOS : le helper ne l'écrit pas, et ne pas l'abaisser
// garde les pointes courtes du jeu. Volatil : le BIOS remet sa valeur au redémarrage. Posé au clic (AutoReglage),
// pas jugé sur les images (il promet moins de temps freiné, pas plus d'images : l'A/B des 1 % low le rendrait
// presque toujours) ; le docteur le juge sur les secondes freinées des parties suivantes.
// -----------------------------------------------------------------------
/// <summary>Abaisse à chaud la limite PL1 d'un processeur Intel ; volatil. Proposé sur un PC de bureau Intel quand la
/// dernière partie relevée a été freinée par la chaleur.</summary>
public sealed class PlafondThermiqueCpuTweak : LevierPl1TweakBase
{
    public PlafondThermiqueCpuTweak(ICpuPowerLimitReader lecteur, IElevatedClient elevated) : base(lecteur, elevated) { }

    public override string Id => "fixit.cpu_power_cap";
    public override string SummaryKey => "tweak.fixit.cpu_power_cap.summary";

    /// <summary>Le plafond, quand la limite effective (la plus basse du registre et de la copie MMIO active) le dépasse d'au
    /// moins 5 W (sinon il ne coupe rien) et qu'il reste sous la PL2. Jamais sur un registre « sans
    /// limite » ni sur une PL1 au-dessus de la PL2, dont le helper refuserait de rendre l'origine ; jamais sur un verrou.</summary>
    public static double? Cible(LimiteDePuissanceLue lu)
    {
        if (lu.Verrou || lu.SansLimite || lu.PlafondThermiqueW is not { } plafond || lu.Pl2W <= 0 || lu.Pl1W > lu.Pl2W) return null;
        var effective = lu.CopieMmioPl1W is > 0 and var mmio ? Math.Min(lu.Pl1W, mmio) : lu.Pl1W;
        return effective > plafond + 5 && plafond <= lu.Pl2W ? plafond : null;
    }

    protected override double? CibleDe(LimiteDePuissanceLue lu) => Cible(lu);

    // Aucune température relevée en partie (et pas de plafond encore posé) : le freinage par la chaleur n'est pas lu. Seule
    // la chaleur manque quand une partie freinée poserait un plafond (sondé au plus bas, 0 W) : un verrou, un registre
    // « sans limite » ou une PL1 au-dessus de la PL2 l'excluent déjà, chaleur ou pas.
    public override bool EtatNonLu(SettingsState state)
        => base.EtatNonLu(state) || state.LimitePuissance is { ChaleurEnPartieMaxC: null, PlafondThermiqueW: null } lu
            && Cible(lu with { PlafondThermiqueW = 0 }) is not null;
}
