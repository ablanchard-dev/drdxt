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
/// Honnêteté : chaque réglage a pris → succès + le marqueur est effacé. Le moindre DidNotTake ou
/// Unreadable → la carte de récupération s'affiche et le marqueur est gardé (la boucle peut
/// retourner après l'essai suivant). On ne rapporte jamais un faux succès et on n'annonce jamais de
/// succès sur un réglage invérifiable. Pas de marqueur → sans effet, idempotent.
/// Un marqueur dont l'id de machine ne correspond pas à cette machine (copié d'un autre PC) n'est
/// jamais honoré : il est traité comme « rien en attente » et laissé intact.
///
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

        // ── Comparaison d'id de machine fail-safe : un marqueur copié d'un autre PC ne doit jamais être
        //    honoré ici. En cas de désaccord, on le traite comme « rien en attente » et on laisse le
        //    marqueur étranger intact (ni effacé, ni évalué) : on n'annonce jamais qu'un changement
        //    étranger a pris ou échoué sur cette machine. ──
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

        var allTook = outcomes.Count > 0 && outcomes.All(o => o.Verdict == BiosSettingVerdict.Took);

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

    // ── Le verdict par écart : la grandeur mesurée a-t-elle bougé, et dans le sens attendu ?
    //    Rend null quand aucun écart n'est disponible (aucune valeur capturée avant, ou aucune lisible
    //    maintenant) : l'appelant se rabat alors sur la comparaison TriState. On n'invente jamais de
    //    mouvement.
    //
    //    Pourquoi il existe : XmpExpo est une inférence étiquetée « probably » par son propre
    //    producteur. La comparer à une attente compare une supposition à une supposition ; et sur un
    //    kit dont le plancher JEDEC est dépassé dans les deux états, elle ne bouge pas du tout alors que
    //    la machine, si. 2133 -> 3800 est un fait. ──
    private static BiosSettingVerdict? DecideByDelta(PendingSettingTarget target, SettingsState state)
    {
        // 0 = « jamais lu », jamais « 0 MHz », des deux côtés. La source qui alimente ce champ
        // (`SettingsState.MemoryConfiguredMhz`) documente elle-même que 0 signifie non lu.
        // Sans la garde sur `before`, une machine dont la RAM n'a pas été lue donnerait before=0,
        // after=3800, donc `Took` : un succès fabriqué depuis une ligne de base inexistante, dans la
        // fonction dont la raison d'être est précisément de refuser ça.
        if (target.ObservedBefore is not { } before || before == 0) return null;

        long after = ObservableNow(state, target.Setting);
        if (after == 0) return null;

        return target.ExpectedState switch
        {
            // Activer doit faire monter la grandeur ; désactiver doit la faire baisser. Aucun mouvement veut
            // dire que le changement n'a pas pris, quoi que dise l'inférence sur l'état.
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

    // ── Relier un réglage firmware à la lecture neuve de SettingsState. Tout ce qu'on ne peut pas
    //    lire est honnêtement Unknown : la boucle rapporte Unreadable au lieu de fabriquer un état. ──
    private static TriState ReadObserved(SettingsState state, FirmwareSettingKind kind) => kind switch
    {
        FirmwareSettingKind.XmpExpo => state.XmpExpo,
        FirmwareSettingKind.ReBar => state.ReBar,

        // Above 4G Decoding est un prérequis du ReBAR : le ReBAR ne peut pas être actif sans lui, un
        // ReBAR actif le prouve donc activé. L'inverse ne prouve rien (Above4G peut être activé avec le
        // ReBAR désactivé), d'où Unknown plutôt qu'un « désactivé » fabriqué.
        FirmwareSettingKind.Above4G => state.ReBar == TriState.On ? TriState.On : TriState.Unknown,

        // CSM : rempli par le scan d'après le mode de démarrage. Un démarrage Legacy prouve le CSM actif ;
        // un démarrage UEFI ne le prouve pas inactif. Le lecteur porte déjà cette asymétrie.
        FirmwareSettingKind.Csm => state.Csm,

        // Encore vraiment illisibles, chacun pour une raison nommée (pas un cas oublié du switch) :
        //   X3dCppc     : demande le routage CPPC des cœurs préférés, pas seulement les cœurs parqués.
        //   PcieGen     : demande nvmlDeviceGetCurrPcieLinkGeneration/Width ; INvmlNative n'expose
        //                 aujourd'hui que BAR1/puissance/température/pilote.
        //   BiosVersion : pas une bascule : un TriState ne peut pas exprimer « 1.H0 -> 1.J0 ». Il relève
        //                 du chemin par écart avec un comparant chaîne, pas d'ici.
        _ => TriState.Unknown,
    };
}
