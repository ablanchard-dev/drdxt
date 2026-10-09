using DrDxt.Application.Autopilot;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>
/// GPU-OC (08/10/2026) : le décalage de fréquence de la carte, posé à la main par le joueur en mode Expert, sur son
/// accord. Jamais par l'autopilote (la règle de <c>CandidateSelector.ReglagesCarteInterdits</c> tient pour lui).
///
/// L'ordre : le journal d'abord (le démarrage suivant rendrait la carte), puis le levier, qui sonde, arme le bail du
/// helper avant d'écrire, écrit et lance le test de stabilité (instable = rendu tout de suite). Gardé, le décalage vit
/// tant que DrDXT tourne : à l'arrêt ou au plantage, le bail expire et le helper rend l'origine.
/// </summary>
public sealed class ReglageCarteExpert(GpuUndervoltTweak levier, GpuTuningTargetSource cibles, IRevertEnAttente journal)
{
    public const string IdBail = "gpu-expert";

    /// <summary>Un PC fixe à carte dédiée. Sur un portable, les fréquences de la carte sont verrouillées par le pilote.</summary>
    public static bool Possible(HardwareInventory hw) => GpuGating.HasDesktopDiscreteGpu(hw);
    // 08/10/2026 (revue n° 13) : 120 s. Le helper sert une opération à la fois ; une opération longue (un point de
    // restauration) pouvait retenir les battements plus de 30 s et faire rendre la carte en silence.
    public const int BailSecondes = 120;

    public async Task<Result<EssaiCarte>> PoserAsync(int coeurMhz, int memoireMhz = 0)
    {
        if (coeurMhz == 0 && memoireMhz == 0)
            return Result<EssaiCarte>.Fail(ErrorCodes.TweakNotApplicable, "Décalage nul : rien à poser.");
        if (!GpuSafeBounds.IsCoreOffsetSafe(coeurMhz) || !GpuSafeBounds.IsMemoryOffsetSafe(memoireMhz))
            return Result<EssaiCarte>.Fail(ErrorCodes.TweakNotApplicable,
                $"Décalage hors des bornes ({GpuSafeBounds.MinCoreOffsetMhz} à +{GpuSafeBounds.MaxCoreOffsetMhz} MHz) : rien n'est écrit.");

        var pose = journal.Poser(levier.Id, null);
        if (!pose.IsOk)
            return Result<EssaiCarte>.Fail(pose.Error?.Code ?? FichierRevertEnAttente.CodeErreur,
                "rien n'est posé sans filet : " + pose.Error?.Message);

        // La cible passe au levier ; la source partagée (le profil, un scan) n'est jamais modifiée.
        var cible = cibles.Tuning with { CoreOffsetMhz = coeurMhz, MemoryOffsetMhz = memoireMhz };
        var r = await levier.ApplyAvecBailAsync(IdBail, BailSecondes, cible).ConfigureAwait(false);

        if (!r.IsOk)
        {
            // Posé en partie et rendu raté : le journal reste, le démarrage suivant dira de vérifier la carte.
            if (r.Error?.Code != ErrorCodes.TweakPartiallyApplied) journal.Lever();
            return Result<EssaiCarte>.Fail(r.Error!.Value.Code, r.Error.Value.Message);
        }

        var (snapshot, bail) = r.Value;
        if (!journal.Poser(levier.Id, snapshot).IsOk)
        {
            var rendu = await levier.RevertAsync(snapshot).ConfigureAwait(false);
            await bail.DisposeAsync().ConfigureAwait(false);
            if (rendu.IsOk) journal.Lever();
            return Result<EssaiCarte>.Fail(FichierRevertEnAttente.CodeErreur,
                rendu.IsOk ? "journal impossible à compléter : la carte a été rendue"
                           : "journal impossible à compléter, et la carte n'a pas pu être rendue : le bail la rendra");
        }

        return Result<EssaiCarte>.Ok(new EssaiCarte(levier, journal, snapshot, bail));
    }
}

/// <summary>Un décalage posé et gardé pour la session. Rendre remet l'origine ; libérer sans rendre laisse le bail
/// expirer, et le helper rend l'origine de lui-même.</summary>
public interface IEssaiCarte : IAsyncDisposable
{
    Task<Result> RendreAsync();

    /// <summary>Le helper a rendu l'origine de lui-même (le bail a expiré).</summary>
    event Action? BailPerdu;
}

/// <inheritdoc cref="IEssaiCarte"/>
public sealed class EssaiCarte(GpuUndervoltTweak levier, IRevertEnAttente journal, TweakSnapshot snapshot, HeartbeatSender bail)
    : IEssaiCarte
{
    public TweakSnapshot Snapshot { get; } = snapshot;

    public event Action? BailPerdu
    {
        add => bail.Perdu += value;
        remove => bail.Perdu -= value;
    }

    public async Task<Result> RendreAsync()
    {
        var rendu = await levier.RevertAsync(Snapshot).ConfigureAwait(false);
        if (rendu.IsOk)
        {
            journal.Lever();
            await bail.LibererAsync().ConfigureAwait(false);
        }
        return rendu;
    }

    public ValueTask DisposeAsync() => bail.DisposeAsync();
}
