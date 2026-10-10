using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// B1 (10/10/2026) : la limite de puissance du processeur Intel (PL1) montée à chaud, jugée en jeu.
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
// Refusé, et dit par le docteur : verrou du BIOS (bit 63), registre « sans limite », PL1 déjà à la PL2, PawnIO absent,
// portable (sa carte est faite pour sa puissance), processeur non Intel. Jugé en jeu (AutoHot) : l'essai en alternance
// garde la montée seulement si les parties gagnent ; sans gain, rendu.
// -----------------------------------------------------------------------
public sealed class LimitePuissanceCpuTweak : FixItTweakBase
{
    private readonly ICpuPowerLimitReader _lecteur;

    public LimitePuissanceCpuTweak(ICpuPowerLimitReader lecteur, IElevatedClient elevated) : base(elevated) => _lecteur = lecteur;

    public override string Id => "fixit.cpu_power_limit";
    public override string SummaryKey => "tweak.fixit.cpu_power_limit.summary";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        hw.Chassis == ChassisType.Desktop
        && hw.Cpu.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)
        && state.LimitePuissance is { } lu && Cible(lu) is not null;

    public override bool EtatNonLu(SettingsState state) => state.LimitePuissance is null;

    /// <summary>La PL1 à poser, en watts, ou null quand il n'y a pas de marge sûre : le plafond est la PL2 de la carte,
    /// bornée par la puissance maximale publiée (quand elle l'est) et par la copie MMIO active (quand elle l'est) ; il
    /// faut qu'il dépasse la PL1 lue d'au moins un demi-watt, sans verrou ni registre « sans limite ».</summary>
    public static double? Cible(LimiteDePuissanceLue lu)
    {
        if (lu.Verrou || lu.SansLimite || lu.Pl2W <= 0) return null;
        var plafond = lu.Pl2W;
        if (lu.MaximaleW > 0) plafond = Math.Min(plafond, lu.MaximaleW);
        if (lu.CopieMmioPl1W is > 0 and var mmio) plafond = Math.Min(plafond, mmio);
        return plafond > lu.Pl1W + 0.5 ? plafond : null;
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var lecture = _lecteur.LireRegistres();
            if (!lecture.IsOk || lecture.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit registers are not readable here.");
            var (registres, lu) = (lecture.Value.Registres, lecture.Value.Lue);
            if (Cible(lu) is not { } cible)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit has no safe headroom: nothing to write.");

            var valeur = RegistreDeLimiteCpu.ComposerPl1(registres.PkgPowerLimit, cible, registres.RaplPowerUnit);
            var op = new SetCpuPackagePowerLimitOp(valeur, registres.PkgPowerLimit);
            var inverse = PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(registres.PkgPowerLimit, valeur));
            var pose = PrivilegedOpCodec.Serialize(op);
            if (!inverse.IsOk || inverse.Value is null || !pose.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            return applied.IsOk
                ? Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, inverse.Value, pose.Value))
                : Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, $"CPU power limit apply boundary error: {ex.Message}");
        }
    }
}
