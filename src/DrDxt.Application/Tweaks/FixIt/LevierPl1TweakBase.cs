using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

/// <summary>
/// Ce que les deux leviers de la limite continue (PL1) ont en commun, la montée (<see cref="LimitePuissanceCpuTweak"/>)
/// et le plafond thermique (<see cref="PlafondThermiqueCpuTweak"/>) : PC fixe Intel, registres relus avant chaque pose
/// (la cible se recalcule sur la lecture fraîche : une garde vaut aussi pour un essai déjà lancé), seul le champ PL1 de
/// MSR 0x610 écrit par le helper, l'inverse exact gardé pour le retour. Volatil : le BIOS remet sa valeur au redémarrage.
/// </summary>
public abstract class LevierPl1TweakBase : FixItTweakBase
{
    private readonly ICpuPowerLimitReader _lecteur;

    protected LevierPl1TweakBase(ICpuPowerLimitReader lecteur, IElevatedClient elevated) : base(elevated) => _lecteur = lecteur;

    /// <summary>La PL1 à poser, en watts, ou null quand ce levier n'a rien à faire sur cette lecture.</summary>
    protected abstract double? CibleDe(LimiteDePuissanceLue lu);

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        hw.Chassis == ChassisType.Desktop
        && hw.Cpu.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)
        && state.LimitePuissance is { } lu && CibleDe(lu) is not null;

    public override bool EtatNonLu(SettingsState state) => state.LimitePuissance is null;

    /// <summary>La valeur posée est-elle encore dans le registre ? Faux après un redémarrage (le BIOS
    /// remet la sienne) : le levier n'est plus en place et ne doit plus s'afficher comme posé. Null = pas su lire.
    /// <paramref name="journal"/> : les autres leviers posés (un plafond posé par-dessus recouvre la montée).</summary>
    public bool? EncorePose(TweakSnapshot snapshot, IReadOnlyList<TweakSnapshot> journal)
    {
        var lecture = _lecteur.LireRegistres();
        return lecture.IsOk && lecture.Value is { } v ? EncorePose(snapshot, journal, v.Registres.PkgPowerLimit) : null;
    }

    /// <summary>Pur. Seul le champ PL1 (bits 14:0), le seul que le levier écrit, est comparé (un outil
    /// qui touche PL2 ou la fenêtre ne fait plus croire à un redémarrage) ; et un levier que le retour d'un autre levier
    /// encore posé rendrait est recouvert, pas rendu (plafond par-dessus une montée : la montée garde son retour vers la PL1
    /// du BIOS). Null quand le levier ne se lit pas.</summary>
    public static bool? EncorePose(TweakSnapshot snapshot, IReadOnlyList<TweakSnapshot> journal, ulong registre)
    {
        if (Op(snapshot.AppliedValueJson) is not { } pose) return null;
        if ((registre & Pl1) == (pose.Valeur & Pl1)) return true;
        var autres = journal.Where(o => o.TweakId != snapshot.TweakId).ToList(); // l'ensemble rétrécit : la récursion finit
        return autres.Any(o => Op(o.OriginalValueJson) is { } retour && (retour.Valeur & Pl1) == (pose.Valeur & Pl1)
                               && EncorePose(o, autres, registre) == true);
    }

    private const ulong Pl1 = 0x7FFF;

    private static SetCpuPackagePowerLimitOp? Op(string? json)
        => json is not null && PrivilegedOpCodec.Deserialize(json) is { IsOk: true, Value: SetCpuPackagePowerLimitOp op } ? op : null;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var lecture = _lecteur.LireRegistres();
            if (!lecture.IsOk || lecture.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit registers are not readable here.");
            var (registres, lu) = (lecture.Value.Registres, lecture.Value.Lue);
            if (CibleDe(lu) is not { } cible)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit: nothing to write on this reading.");

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
