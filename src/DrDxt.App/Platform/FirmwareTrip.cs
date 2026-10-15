using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.Core;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// FirmwareTrip : le protocole en deux temps.
//
// Le module BIOS existait en entier (store de marqueur atomique, verifieur post-redemarrage,
// libelles FR+EN) et n'etait resolu par personne. `PendingBiosChangeStore.Save()` n'avait
// aucun appelant. Le differenciateur « on te dit quoi changer, et on prouve que ca a pris »
// n'avait donc jamais tourne en production, et la regle du depot s'applique : ce qui n'a
// jamais ete execute ne marche pas.
//
// Les deux temps
//   1. Armer  : avant d'envoyer l'utilisateur dans le BIOS, on ecrit le marqueur avec
//               l'instantane des grandeurs observables (2133 MHz maintenant), puis seulement
//               apres on demande le redemarrage.
//   2. Relire : au retour de Windows, le verifieur compare l'etat frais au marqueur et rend
//               un verdict par reglage : Took / DidNotTake / Unreadable.
//
// L'ordre n'est pas negociable, et l'echec non plus :
//   * marqueur d'abord, redemarrage ensuite. L'inverse laisse une fenetre ou la machine part
//     dans le BIOS sans que rien ne sache ce qu'on y attendait : l'utilisateur revient, fait
//     son reglage, et le produit n'a plus rien a verifier.
//   * si l'ecriture du marqueur echoue, on ne redemarre pas. Envoyer quelqu'un dans son BIOS
//     en sachant qu'on ne pourra rien lui prouver au retour, c'est vendre la preuve sans la
//     livrer.
//   * si le redemarrage est refuse apres coup, on efface le marqueur. Sinon il survit a un
//     voyage qui n'a pas eu lieu, et la prochaine ouverture de l'app annonce un verdict sur
//     un changement que personne n'a fait.
//
// N'ecrit aucune variable firmware. Le marqueur est un fichier de l'OS ; le redemarrage passe
// par l'op allowlistee du helper, qui appelle shutdown.exe /r /fw ; Windows detient le bit
// OsIndications, DrDXT n'y touche pas.
// -----------------------------------------------------------------------

/// <summary>La couture « emmene-moi dans le BIOS, et prouve-moi au retour que ca a pris ».</summary>
public interface IFirmwareTrip
{
    /// <summary>
    /// Ecrit le marqueur pour <paramref name="findings"/> puis demande le redemarrage vers le
    /// setup firmware. Rend un echec sans redemarrer si rien n'est a changer, si le marqueur
    /// ne peut pas etre ecrit, ou si le helper refuse.
    /// </summary>
    Task<Result> ArmAndRebootAsync(
        IReadOnlyList<BiosFinding> findings, SettingsState before, int delaySeconds, CancellationToken ct);

    /// <summary>Le verdict au retour de Windows, contre l'etat frais relu par le scan.</summary>
    Result<BiosLoopResult> VerifyOnReturn(SettingsState fresh);

    /// <summary>Se retracter pendant le delai : annule le redemarrage et le depart vers le
    /// firmware, puis retire le marqueur (le voyage n'aura pas lieu, rien ne sera a verifier).</summary>
    Task<Result> AnnulerAsync(CancellationToken ct)
        => Task.FromResult(Result.Fail(ErrorCodes.BiosInputInvalid, "Cancelling is not available on this host."));

    /// <summary>Le joueur renonce au changement. Sans ceci, un changement qui n'a pas pris gardait
    /// son marqueur pour toujours, et chaque scan redisait « n'a pas pris » avec sa carte de recuperation.</summary>
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
            // Rien d'actionnable : un constat informatif ne se verifie pas au retour. On refuse
            // le voyage plutot que d'ecrire un marqueur vide que le store rejetterait de toute
            // facon, et l'utilisateur apprend pourquoi, au lieu de redemarrer pour rien.
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
            // Le voyage n'aura pas lieu : le marqueur ne doit pas lui survivre. Un echec de
            // nettoyage est dit en plus du motif d'origine : il laisse un marqueur perime,
            // et le taire ferait mentir la prochaine ouverture de l'app.
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
            // Le redemarrage n'est peut-etre pas annule : le marqueur reste, pour que le retour, s'il a
            // lieu, soit verifie comme prevu.
            return annule;

        var clear = _store.Clear();
        return clear.IsOk
            ? Result.Ok()
            : Result.Fail(clear.Error!.Value.Code,
                "The restart was cancelled, but the pending marker could not be removed.");
    }

    // ── Ce qu'on s'attend a lire au retour, par reglage. ────────────────────────────────
    // On ne retient que les constats actionnables : un Enable/Disable nomme un etat concret.
    // Un `None` (deja bon, ou juste rapporte) et un `SetSupportedRoute` (qui ne se ramene pas a
    // un booleen) n'ont pas d'attente verifiable ici : les inclure fabriquerait une attente
    // que le verifieur trancherait au hasard.
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
        // Tout le reste n'est pas un booleen : Unknown ici veut dire « pas d'attente », et la
        // cible est ecartee au lieu d'etre inventee.
        _ => TriState.Unknown,
    };

    // L'instantane qui fait la difference entre un fait et une inference. Pour XMP/EXPO,
    // `XmpExpo` s'auto-etiquette « probably » ; 2133 -> 3800 ne s'etiquette pas. 0 = jamais lu,
    // et le contrat du marqueur veut null dans ce cas, pas « 0 MHz ».
    private static long? ObservedBefore(FirmwareSettingKind kind, SettingsState before) => kind switch
    {
        FirmwareSettingKind.XmpExpo => before.MemoryConfiguredMhz > 0 ? before.MemoryConfiguredMhz : null,
        _ => null,
    };
}
