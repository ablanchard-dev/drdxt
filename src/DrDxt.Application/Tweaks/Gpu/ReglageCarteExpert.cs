using DrDxt.Application.Autopilot;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

/// <summary>
/// Le décalage de fréquence de la carte, posé à la main par le joueur en mode Expert, sur son
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
    // 120 s. Le helper sert une opération à la fois ; une opération longue (un point de
    // restauration) pouvait retenir les battements plus de 30 s et faire rendre la carte en silence.
    public const int BailSecondes = 120;

    public async Task<Result<EssaiCarte>> PoserAsync(int coeurMhz, int memoireMhz = 0)
    {
        // Le helper n'écrit que le cœur : une mémoire demandée serait affichée sans jamais être posée.
        if (memoireMhz != 0)
            return Result<EssaiCarte>.Fail(ErrorCodes.TweakNotApplicable, Docteur.T("GX.MemoireNonEcrite"));
        if (coeurMhz == 0)
            return Result<EssaiCarte>.Fail(ErrorCodes.TweakNotApplicable, Docteur.T("GX.DecalageNul"));
        if (!GpuSafeBounds.IsCoreOffsetSafe(coeurMhz))
            return Result<EssaiCarte>.Fail(ErrorCodes.TweakNotApplicable,
                Docteur.T("GX.HorsBornes", GpuSafeBounds.MinCoreOffsetMhz, GpuSafeBounds.MaxCoreOffsetMhz));

        var pose = journal.Poser(levier.Id, null);
        if (!pose.IsOk)
            return Result<EssaiCarte>.Fail(pose.Error?.Code ?? FichierRevertEnAttente.CodeErreur,
                Docteur.T("GX.SansFilet", pose.Error?.Message));

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
            if (rendu.IsOk)
            {
                await bail.LibererAsync().ConfigureAwait(false);
                journal.Lever();
            }
            else await bail.DisposeAsync().ConfigureAwait(false);
            return Result<EssaiCarte>.Fail(FichierRevertEnAttente.CodeErreur,
                rendu.IsOk ? Docteur.T("GX.JournalCarteRendue") : Docteur.T("GX.JournalCarteNonRendue"));
        }

        return Result<EssaiCarte>.Ok(new EssaiCarte(levier, journal, snapshot, bail));
    }
}

/// <summary>Le bail perdu, le helper a rendu l'origine : le journal n'a plus rien à rendre (sinon l'Expert et l'autopilote se
/// croiraient « encore en essai » jusqu'au redémarrage de l'App). Seulement si le journal tient encore ce levier : il n'en tient
/// qu'un, et un bail perdu qui signale tard effacerait l'entrée d'un autre levier posé entre-temps.</summary>
internal static class SuiviDuBail
{
    public static bool LeverAuBailPerdu(HeartbeatSender bail, IRevertEnAttente journal, string tweakId)
    {
        bail.Perdu += () =>
        {
            if (journal.Lire() is { IsOk: true, Value: { } entree } && string.Equals(entree.TweakId, tweakId, StringComparison.Ordinal))
                journal.Lever();
        };
        return true;
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
    private readonly bool _journalSuitLeBail = SuiviDuBail.LeverAuBailPerdu(bail, journal, snapshot.TweakId);

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
