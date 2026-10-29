using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.Core;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// FirmwareTrip : « on te dit quoi changer, et on prouve que ca a pris », entre le store du marqueur, le verifieur
// post-redemarrage et le canal privilegie.
//   1. Armer  : le marqueur avec l'instantane des grandeurs observables (2133 MHz maintenant), puis le redemarrage.
//   2. Relire : au retour, l'etat frais contre le marqueur, un verdict par reglage (Took / DidNotTake / Unreadable).
// Ordre et echecs :
//   * marqueur d'abord : l'inverse laisserait partir la machine dans le BIOS sans rien a verifier au retour ;
//   * marqueur non ecrit ⇒ pas de redemarrage (aucune preuve possible au retour) ;
//   * redemarrage refuse ⇒ marqueur efface, sinon un verdict sur un changement que personne n'a fait.
// Aucune variable firmware ecrite : le marqueur est un fichier, le redemarrage passe par l'op allowlistee du helper
// (shutdown.exe /r /fw), Windows detient le bit OsIndications.
// -----------------------------------------------------------------------

/// <summary>La couture « emmene-moi dans le BIOS, et prouve-moi au retour que ca a pris ».</summary>
public interface IFirmwareTrip
{
    /// <summary>
    /// Ecrit le marqueur pour <paramref name="findings"/> puis demande le redemarrage vers le setup firmware. Echec sans
    /// redemarrer si rien n'est a changer, si le marqueur ne s'ecrit pas, ou si le helper refuse.
    /// </summary>
    Task<Result> ArmAndRebootAsync(
        IReadOnlyList<BiosFinding> findings, SettingsState before, int delaySeconds, CancellationToken ct);

    /// <summary>Le verdict au retour de Windows, contre l'etat frais relu par le scan.</summary>
    Result<BiosLoopResult> VerifyOnReturn(SettingsState fresh);

    /// <summary>Se retracter pendant le delai : annule le redemarrage et le depart vers le firmware, puis retire le
    /// marqueur.</summary>
    Task<Result> AnnulerAsync(CancellationToken ct)
        => Task.FromResult(Result.Fail(ErrorCodes.BiosInputInvalid, "Cancelling is not available on this host."));

    /// <summary>Le joueur renonce au changement : sans ceci, un changement qui n'a pas pris garderait son marqueur, et
    /// chaque scan redirait « n'a pas pris ».</summary>
    Result Oublier() => Result.Fail(ErrorCodes.BiosInputInvalid, "Forgetting is not available on this host.");
}

public sealed class FirmwareTrip : IFirmwareTrip
{
    private readonly IPendingBiosChangeStore _store;
    private readonly IBiosLoopVerifier _verifier;
    private readonly IElevatedClient _elevated;
    private readonly IBiosClock _clock;
    private readonly IBiosMachineId _machineId;

    public FirmwareTrip(
        IPendingBiosChangeStore store,
        IBiosLoopVerifier verifier,
        IElevatedClient elevated,
        IBiosClock clock,
        IBiosMachineId machineId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _elevated = elevated ?? throw new ArgumentNullException(nameof(elevated));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _machineId = machineId ?? throw new ArgumentNullException(nameof(machineId));
    }

    /// <inheritdoc />
    public async Task<Result> ArmAndRebootAsync(
        IReadOnlyList<BiosFinding> findings, SettingsState before, int delaySeconds, CancellationToken ct)
    {
        if (before is null)
            return Result.Fail(ErrorCodes.BiosInputInvalid, "No settings snapshot to arm the firmware trip with.");

        var targets = TargetsFrom(findings, before);
        if (targets.Count == 0)
            // Rien d'actionnable (un constat informatif ne se verifie pas) : le voyage est refuse, et dit pourquoi.
            return Result.Fail(ErrorCodes.BiosInputInvalid,
                "No firmware change to verify: nothing would be proven by this reboot.");

        // Temps 1 : le marqueur avant le redemarrage.
        var save = _store.Save(new PendingBiosChange(targets, _clock.UtcNow, _machineId.Current));
        if (!save.IsOk)
            return Result.Fail(save.Error!.Value.Code, save.Error.Value.Message);

        var reboot = await _elevated
            .ExecuteAsync(new RebootToFirmwareSetupOp(delaySeconds), ct)
            .ConfigureAwait(false);

        if (!reboot.IsOk)
        {
            // Le voyage n'aura pas lieu : le marqueur ne lui survit pas. Un nettoyage rate se dit en plus du motif d'origine.
            var clear = _store.Clear();
            return clear.IsOk
                ? Result.Fail(reboot.Error!.Value.Code, reboot.Error.Value.Message)
                : Result.Fail(reboot.Error!.Value.Code,
                    reboot.Error.Value.Message + " (and the pending marker could not be removed).");
        }

        return Result.Ok();
    }

    /// <inheritdoc />
    public Result<BiosLoopResult> VerifyOnReturn(SettingsState fresh) => _verifier.Verify(fresh);

    /// <inheritdoc />
    public Result Oublier() => _store.Clear();

    /// <inheritdoc />
    public async Task<Result> AnnulerAsync(CancellationToken ct)
    {
        var annule = await _elevated.ExecuteAsync(new CancelPendingFirmwareBootOp(), ct).ConfigureAwait(false);
        if (!annule.IsOk)
            // Le redemarrage n'est peut-etre pas annule : le marqueur reste, pour verifier le retour s'il a lieu.
            return annule;

        var clear = _store.Clear();
        return clear.IsOk
            ? Result.Ok()
            : Result.Fail(clear.Error!.Value.Code,
                "The restart was cancelled, but the pending marker could not be removed.");
    }

    // ── Ce qu'on s'attend a lire au retour, par reglage. ────────────────────────────────
    // Seuls Enable/Disable nomment un etat concret ; `None` et `SetSupportedRoute` (pas un booleen) n'ont pas d'attente
    // verifiable : en fabriquer une ferait trancher le verifieur au hasard.
    private static IReadOnlyList<PendingSettingTarget> TargetsFrom(
        IReadOnlyList<BiosFinding>? findings, SettingsState before)
    {
        if (findings is not { Count: > 0 })
            return Array.Empty<PendingSettingTarget>();

        return findings
            .Where(f => f.MatrixClass == BiosMatrixClass.Guide)
            .Select(f => (f, expected: Expected(f.RecommendedAction)))
            .Where(x => x.expected != TriState.Unknown)
            .Select(x => new PendingSettingTarget(x.f.Kind, x.expected, ObservedBefore(x.f.Kind, before)))
            .ToList();
    }

    private static TriState Expected(BiosRecommendedAction action) => action switch
    {
        BiosRecommendedAction.Enable => TriState.On,
        BiosRecommendedAction.Disable => TriState.Off,
        // Pas un booleen : Unknown = « pas d'attente », la cible est ecartee.
        _ => TriState.Unknown,
    };

    // L'instantane fait d'une inference un fait : `XmpExpo` s'etiquette « probably », 2133 -> 3800 non. 0 = jamais lu,
    // donc null au marqueur, pas « 0 MHz ».
    private static long? ObservedBefore(FirmwareSettingKind kind, SettingsState before) => kind switch
    {
        FirmwareSettingKind.XmpExpo => before.MemoryConfiguredMhz > 0 ? before.MemoryConfiguredMhz : null,
        _ => null,
    };
}
