using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// La limite de puissance du processeur Intel (PL1) montée à chaud, jugée en jeu.
//
// Ce qui est sourcé : TechPowerUp (i9-14900K, PL1 = PL2 forcés, RTX 4090) mesure −15,5 % d'images en 720p et ~−16 % en
// 1080p quand la limite est tenue à 65 W, −2 % à 125 W, −2,5 % à 65 W en 4K. Les non-K sortent de boîte à 65 W de
// puissance de base ; bien des cartes appliquent ce 65 W, ou le profil « Baseline » 125/188 W d'Intel (mai 2024). Un
// processeur tenu sous sa puissance tient sa fréquence quelques secondes (PL2, fenêtre tau) puis redescend.
//
// Ce qui est écrit : le seul champ PL1 (bits 14:0) de MSR 0x610, activé, jusqu'à la PL2 que la carte mère laisse déjà
// passer, bornée par la puissance maximale que le processeur publie (0x614) et par la copie MMIO de la carte quand elle
// est active (la limite effective est la plus basse des deux). Jamais PL2, fenêtre, courant (Iccmax), tension ni
// fréquence : rien au-delà de ce que la carte autorise déjà en pointe (instabilité 13e/14e gén., 2024). Volatil : le
// BIOS remet sa valeur au redémarrage. Le registre est relu avant d'écrire, et relu après par le helper.
//
// Refusé, et dit par le docteur : microcode d'avant le correctif Vmin sur un Core 13e/14e, verrou du BIOS (bit 63), registre « sans limite », PL1 déjà à la PL2, PawnIO absent,
// portable (sa carte est faite pour sa puissance), processeur non Intel. Jugé en jeu (AutoHot) : l'essai en alternance
// garde la montée seulement si les parties gagnent ; sans gain, rendu.
// -----------------------------------------------------------------------
/// <summary>Monte à chaud la limite PL1 d'un processeur Intel jusqu'à la PL2 que la carte mère laisse passer ; volatil.
/// Proposé sur un PC de bureau Intel quand une montée est possible, gardé seulement si les parties gagnent.</summary>
public sealed class LimitePuissanceCpuTweak : LevierPl1TweakBase
{
    public LimitePuissanceCpuTweak(ICpuPowerLimitReader lecteur, IElevatedClient elevated) : base(lecteur, elevated) { }

    public override string Id => "fixit.cpu_power_limit";
    public override string SummaryKey => "tweak.fixit.cpu_power_limit.summary";

    protected override double? CibleDe(LimiteDePuissanceLue lu) => Cible(lu);

    /// <summary>La cible de la montée : une seule source, partagée avec ce que le docteur promet
    /// (<see cref="LimiteDePuissance.CibleDeMontee"/>).</summary>
    public static double? Cible(LimiteDePuissanceLue lu) => LimiteDePuissance.CibleDeMontee(lu);
}
