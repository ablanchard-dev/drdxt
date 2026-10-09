using DrDxt.Application.Autopilot;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>
/// GPU-OC (08/10/2026) : le ventilateur de la carte en mode Expert, sur geste du joueur seulement. Même filet que la
/// fréquence (<see cref="ReglageCarteExpert"/>) : l'essai est noté avant (le démarrage suivant rend l'automatique si
/// DrDXT plante), le bail du helper est armé avant l'écriture (si DrDXT s'arrête, le helper rend l'automatique seul),
/// puis la vitesse est posée. Le helper ne pose que vers le haut (voir NvmlVentilateurActions) ; ici, la borne basse
/// est le plancher à chaud de la liste blanche (40 %).
/// </summary>
public sealed class VentilateurExpert(IElevatedClient elevated, IRevertEnAttente journal)
{
    public const string IdLevier = "gpu.fan_curve";
    public const string IdBail = "gpu-ventilateur";
    public const int MinPourcent = 40, MaxPourcent = 100;

    /// <summary>Un PC fixe à une seule carte NVIDIA : NVML pilote l'index 0, et sur un portable le constructeur pilote le
    /// ventilateur (revue du 08/10).</summary>
    public static bool Possible(HardwareInventory hw) => ReglageCarteExpert.Possible(hw)
        && hw.Gpus.Count(g => g.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) == 1;

    public async Task<Result<IEssaiCarte>> PoserAsync(int pourcent, int gpuIndex = 0)
    {
        if (pourcent is < MinPourcent or > MaxPourcent)
            return Result<IEssaiCarte>.Fail(ErrorCodes.TweakNotApplicable,
                $"Vitesse hors des bornes ({MinPourcent} à {MaxPourcent} %) : rien n'est écrit.");

        var retour = new RestoreGpuFanAutoOp(gpuIndex);
        var retourJson = PrivilegedOpCodec.Serialize(retour);
        if (!retourJson.IsOk || retourJson.Value is null)
            return Result<IEssaiCarte>.Fail(ErrorCodes.SnapshotInvalid, "retour à l'automatique illisible : rien n'est écrit");
        var snapshot = new TweakSnapshot(IdLevier, retourJson.Value);

        var note = journal.Poser(IdLevier, snapshot);
        if (!note.IsOk)
            return Result<IEssaiCarte>.Fail(note.Error?.Code ?? FichierRevertEnAttente.CodeErreur,
                "rien n'est posé sans filet : " + note.Error?.Message);

        var bail = await elevated.ExecuteAsync(new EstablishLeaseOp(IdBail, retourJson.Value, ReglageCarteExpert.BailSecondes),
            CancellationToken.None).ConfigureAwait(false);
        if (!bail.IsOk)
        {
            journal.Lever();
            return Result<IEssaiCarte>.Fail(bail.Error?.Code ?? ErrorCodes.HelperOpFailed,
                "bail du helper refusé, le ventilateur n'est pas touché : " + bail.Error?.Message);
        }
        var battement = new HeartbeatSender(elevated, IdBail, ReglageCarteExpert.BailSecondes);
        battement.Start();

        var pose = await elevated.ExecuteAsync(new ApplyGpuFanCurveOp(gpuIndex, [new FanCurvePointDto(0, pourcent)]),
            CancellationToken.None).ConfigureAwait(false);
        if (!pose.IsOk)
        {
            // Le helper a déjà rendu l'automatique sur un échec ; s'il n'a pas pu, le journal reste et le bail rejouera.
            await battement.DisposeAsync().ConfigureAwait(false);
            if (pose.Error?.Code != ErrorCodes.TweakPartiallyApplied) journal.Lever();
            return Result<IEssaiCarte>.Fail(pose.Error!.Value.Code, pose.Error.Value.Message);
        }
        return Result<IEssaiCarte>.Ok(new EssaiVentilateur(elevated, journal, retour, battement));
    }
}

/// <summary>Une vitesse fixe gardée pour la session. Rendre remet l'automatique (relu par le helper) ; libérer sans
/// rendre laisse le bail expirer, et le helper rend l'automatique de lui-même.</summary>
public sealed class EssaiVentilateur(IElevatedClient elevated, IRevertEnAttente journal, RestoreGpuFanAutoOp retour, HeartbeatSender bail)
    : IEssaiCarte
{
    public event Action? BailPerdu
    {
        add => bail.Perdu += value;
        remove => bail.Perdu -= value;
    }

    public async Task<Result> RendreAsync()
    {
        var rendu = await elevated.ExecuteAsync(retour, CancellationToken.None).ConfigureAwait(false);
        if (rendu.IsOk)
        {
            journal.Lever();
            await bail.LibererAsync().ConfigureAwait(false);
        }
        return rendu;
    }

    public ValueTask DisposeAsync() => bail.DisposeAsync();
}
