using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Le vérificateur de la boucle fermée après redémarrage. Au retour suivant dans Windows, il charge
/// le marqueur de changement en attente (via <see cref="IPendingBiosChangeStore"/>) et compare chaque
/// réglage attendu au <see cref="SettingsState"/> neuf relu par le scan :
///
///   • correspondance        → <see cref="BiosSettingVerdict.Took"/> ;
///   • désaccord lisible     → <see cref="BiosSettingVerdict.DidNotTake"/> ;
///   • illisible depuis l'OS → <see cref="BiosSettingVerdict.Unreadable"/> (« impossible à vérifier » honnête).
///
/// Tout a pris → succès, marqueur effacé. Le moindre DidNotTake ou Unreadable → carte de récupération, marqueur gardé
/// (la boucle reprend après l'essai suivant) : jamais de succès sur un réglage invérifiable. Pas de marqueur → sans effet,
/// idempotent. Un marqueur d'une autre machine (copié d'un autre PC) vaut « rien en attente » et reste intact.
/// N'écrit jamais le BIOS : il ne lit qu'un marqueur de l'OS + les réglages déjà lus.
/// </summary>
public sealed class BiosLoopVerifier : IBiosLoopVerifier
{
    private readonly IPendingBiosChangeStore _store;
    private readonly IBiosMachineId _machineId;

    public BiosLoopVerifier(IPendingBiosChangeStore store, IBiosMachineId machineId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _machineId = machineId ?? throw new ArgumentNullException(nameof(machineId));
    }

    /// <inheritdoc />
    public Result<BiosLoopResult> Verify(SettingsState freshState)
    {
        if (freshState is null)
            return Result<BiosLoopResult>.Fail(
                ErrorCodes.BiosInputInvalid, "Fresh settings state is null; cannot verify the loop.");

        // Charger le marqueur. Un marqueur corrompu / une panne d'E/S se propage en échec propre (le
        // store a déjà emballé la frontière). Marqueur absent → rien en attente.
        var load = _store.Load();
        if (!load.IsOk)
            return Result<BiosLoopResult>.Fail(load.Error!.Value.Code, load.Error.Value.Message);

        if (load.Value is null)
            return Result<BiosLoopResult>.Ok(BiosLoopResult.NothingPending);

        var change = load.Value;

        // Un marqueur copié d'un autre PC n'est jamais honoré : « rien en attente », laissé intact (ni effacé, ni évalué).
        if (!string.Equals(
                change.MachineId?.Trim(), _machineId.Current?.Trim(), StringComparison.OrdinalIgnoreCase))
            return Result<BiosLoopResult>.Ok(BiosLoopResult.NothingPending);

        // ── Evaluate every expected setting honestly. ──
        var outcomes = new List<BiosSettingOutcome>(change.Targets.Count);
        foreach (var target in change.Targets)
        {
            var observed = ReadObserved(freshState, target.Setting);
            // L'écart mesuré l'emporte sur le TriState inféré quand on en a un : la question de la boucle est
            // « qu'est-ce qui a changé à travers ce redémarrage », pas « quel est l'état maintenant ».
            var verdict = DecideByDelta(target, freshState) ?? Decide(target.ExpectedState, observed);
            outcomes.Add(new BiosSettingOutcome(target.Setting, target.ExpectedState, observed, verdict));
        }

        // Une cible que l'OS ne relit jamais au retour (JamaisRelisibles) ne bloque pas un voyage dont les cibles lues ont
        // pris (le marqueur resterait pour toujours) ; elle reste dite illisible. Une cible lisible relue inconnue bloque.
        var jugees = outcomes.Where(o => !(o.Verdict == BiosSettingVerdict.Unreadable && JamaisRelisibles.Contains(o.Setting))).ToList();
        var allTook = jugees.Count > 0 && jugees.All(o => o.Verdict == BiosSettingVerdict.Took);

        if (allTook)
        {
            // Succès propre → retirer le marqueur. Une panne d'E/S à l'effacement remonte (on ne prétend pas
            // la boucle finie si on n'a pas pu nettoyer).
            var clear = _store.Clear();
            if (!clear.IsOk)
                return Result<BiosLoopResult>.Fail(clear.Error!.Value.Code, clear.Error.Value.Message);

            return Result<BiosLoopResult>.Ok(new BiosLoopResult(
                HadPendingChange: true,
                AllTook: true,
                Outcomes: outcomes,
                ShowRecoveryCard: false,
                RecoveryCardKeys: Array.Empty<string>(),
                MarkerCleared: true,
                DepartUtc: change.CreatedUtc));
        }

        // Quelque chose n'a pas pris ou n'a pas pu être vérifié → carte de récupération, marqueur gardé.
        return Result<BiosLoopResult>.Ok(new BiosLoopResult(
            HadPendingChange: true,
            AllTook: false,
            Outcomes: outcomes,
            ShowRecoveryCard: true,
            RecoveryCardKeys: BiosLoopStringKeys.RecoveryCard,
            MarkerCleared: false,
            DepartUtc: change.CreatedUtc));
    }

    // ── Le verdict honnête pour un réglage. ──
    private static BiosSettingVerdict Decide(TriState expected, TriState observed)
    {
        // Illisible depuis l'OS (ou la lecture neuve est elle-même Unknown) → impossible de confirmer.
        if (observed == TriState.Unknown)
            return BiosSettingVerdict.Unreadable;

        return observed == expected
            ? BiosSettingVerdict.Took
            : BiosSettingVerdict.DidNotTake;
    }

    // Le verdict par écart : la grandeur mesurée a-t-elle bougé, et dans le sens attendu ? Null sans écart disponible
    // (rien capturé avant, ou rien de lisible) : repli sur le TriState. XmpExpo est une inférence « probably », immobile
    // sur un kit au-dessus du plancher JEDEC dans les deux états ; 2133 -> 3800 est un fait.
    private static BiosSettingVerdict? DecideByDelta(PendingSettingTarget target, SettingsState state)
    {
        // 0 = « jamais lu » des deux côtés (`SettingsState.MemoryConfiguredMhz` le documente) : sans la garde sur
        // `before`, before=0 et after=3800 donneraient un `Took` fabriqué.
        if (target.ObservedBefore is not { } before || before == 0) return null;

        // Lu dans la NVRAM, l'état est un fait, plus une inférence : un profil XMP à la fréquence d'un réglage à la main
        // ne fait pas bouger l'horloge et a pourtant pris.
        if (target.Setting == FirmwareSettingKind.XmpExpo && state.BiosLu?.Any(l => l.Setting == "XMP") == true) return null;

        long after = ObservableNow(state, target.Setting);
        if (after == 0) return null;

        return target.ExpectedState switch
        {
            // Activer fait monter, désactiver fait baisser ; aucun mouvement = pas pris, quoi que dise l'inférence.
            TriState.On => after > before ? BiosSettingVerdict.Took : BiosSettingVerdict.DidNotTake,
            TriState.Off => after < before ? BiosSettingVerdict.Took : BiosSettingVerdict.DidNotTake,
            _ => null,
        };
    }

    // ── Le chiffre dur derrière un réglage, quand il y en a un. 0 = non lu. ──
    private static long ObservableNow(SettingsState state, FirmwareSettingKind kind) => kind switch
    {
        FirmwareSettingKind.XmpExpo => state.MemoryConfiguredMhz,
        _ => 0,
    };

    /// <summary>Les réglages que Windows ne confirme jamais au retour : le CSM en démarrage UEFI (seul un démarrage Legacy le
    /// prouve), le routage CPPC, la génération PCIe, la version du BIOS (voir ReadObserved).</summary>
    private static readonly HashSet<FirmwareSettingKind> JamaisRelisibles =
        [FirmwareSettingKind.Csm, FirmwareSettingKind.X3dCppc, FirmwareSettingKind.PcieGen, FirmwareSettingKind.BiosVersion];

    // ── Relier un réglage firmware à la lecture neuve de SettingsState. Tout ce qu'on ne peut pas
    //    lire est honnêtement Unknown : la boucle rapporte Unreadable au lieu de fabriquer un état. ──
    private static TriState ReadObserved(SettingsState state, FirmwareSettingKind kind) => kind switch
    {
        FirmwareSettingKind.XmpExpo => state.XmpExpo,
        FirmwareSettingKind.ReBar => state.ReBar,

        // Above 4G est un prérequis du ReBAR : un ReBAR actif le prouve activé ; l'inverse ne prouve rien, d'où Unknown.
        FirmwareSettingKind.Above4G => state.ReBar == TriState.On ? TriState.On : TriState.Unknown,

        // CSM : le scan le laisse Unknown (SettingsStateReader ne sait pas distinguer un démarrage Legacy d'un type de
        // firmware non lu) ; seule la lecture NVRAM du BIOS le remplit. Un démarrage UEFI ne le prouverait pas inactif.
        FirmwareSettingKind.Csm => state.Csm,

        // Encore vraiment illisibles, chacun pour une raison nommée (pas un cas oublié du switch) :
        //   X3dCppc     : demande le routage CPPC des cœurs préférés, pas seulement les cœurs parqués.
        //   PcieGen     : INvmlNative lit le lien PCIe, mais l'état relu au retour (SettingsState) ne le porte
        //                 pas.
        //   BiosVersion : pas une bascule : un TriState ne peut pas exprimer « 1.H0 -> 1.J0 ». Il relève
        //                 du chemin par écart avec un comparant chaîne, pas d'ici.
        _ => TriState.Unknown,
    };
}
