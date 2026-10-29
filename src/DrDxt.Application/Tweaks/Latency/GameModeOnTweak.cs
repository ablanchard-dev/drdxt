using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// GameModeOnTweak (système, persistant) : le mode Jeu de Windows, les deux feuilles GameBar de l'utilisateur à 1,
// AllowAutoGameMode (l'autorisation, que la détection lit) et AutoGameModeEnabled (la bascule des Paramètres récents).
// Ensemble, comme les Paramètres : une seule peut laisser le mode Jeu inactif. Snapshot composite (comme
// X3dCcdParkingFixTweak), chaque original relu ; un échec défait les feuilles écrites, la dernière d'abord.
// Effet au prochain lancement du jeu : le moteur saute l'A/B immédiat (TakesEffectAfterReboot range redémarrage et
// prochain lancement ensemble), la preuve vient du prochain scan en jeu ; annulation et retrait disent « au prochain
// lancement du jeu » (ScanViewModel.AuLancementDuJeu). Proposé quand il est inactif, jamais posé en silence, sans
// « +FPS ».
// -----------------------------------------------------------------------
/// <summary>Active le mode Jeu de Windows (AllowAutoGameMode et AutoGameModeEnabled à 1), proposé quand il est lu inactif.</summary>
public sealed class GameModeOnTweak : LatencyTweakBase
{
    private const uint Enable = 1;
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string AllowName = "AllowAutoGameMode";
    private const string EnabledName = "AutoGameModeEnabled";

    private readonly IRegistryReader _registry;

    public GameModeOnTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated)
        => _registry = registry;

    public override string Id => "latency.gameModeOn";
    public override string SummaryKey => "tweak.latency.game_mode.enable";

    // Le mode Jeu agit au prochain lancement du jeu ou redémarrage → le moteur diffère sa preuve A/B.
    public override bool TakesEffectAfterReboot => true;

    /// <summary>Seulement quand le mode Jeu est lu désactivé (des préférences utilisateur, sûres et réversibles à écrire) ;
    /// `Unknown` ne déclenche rien.</summary>
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        state.GameMode == TriState.Off;

    // Unknown = une valeur GameBar d'un type inattendu, ou le registre illisible (absentes, c'est le defaut de Windows,
    // actif). Ne rien ecrire, mais le dire.
    public override bool EtatNonLu(SettingsState state) => state.GameMode == TriState.Unknown;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Relever les vraies origines avant d'écrire (retour composite fidèle).
            var allow = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, AllowName);
            if (!allow.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original AllowAutoGameMode: {allow.Error?.Code}.");
            var enabled = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, EnabledName);
            if (!enabled.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original AutoGameModeEnabled: {enabled.Error?.Code}.");

            var allowInverse = Inverse(AllowName, allow.Value);
            var enabledInverse = Inverse(EnabledName, enabled.Value);

            var snapshotJson = CompositeSnapshotCodec.Encode(new List<PrivilegedOp> { allowInverse, enabledInverse });
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert ops.");

            var value = Enable.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var steps = new (PrivilegedOp Forward, PrivilegedOp Inverse)[]
            {
                (new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, AllowName,
                    RegistryValueType.DWord, value), allowInverse),
                (new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, EnabledName,
                    RegistryValueType.DWord, value), enabledInverse),
            };

            var appliedInverses = new List<PrivilegedOp>(steps.Length);
            foreach (var (forward, inverse) in steps)
            {
                var res = await Elevated.ExecuteAsync(forward, CancellationToken.None).ConfigureAwait(false);
                if (!res.IsOk)
                {
                    return await RetourArriere.EchecAsync(Elevated, appliedInverses, res.Error!.Value.Code, res.Error!.Value.Message)
                        .ConfigureAwait(false);
                }
                appliedInverses.Add(inverse);
            }

            // Les valeurs posees : la desinstallation ne rend une feuille que si elle porte encore celle de DrDXT.
            var posees = CompositeSnapshotCodec.Encode(steps.Select(s => s.Forward).ToList());
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, posees.IsOk ? posees.Value : null));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Game Mode enable apply boundary error: {ex.Message}");
        }
    }

    // Retour composite : rejoue chaque op d'origine relevée. Snapshot malformé →
    // SAFETY.SNAPSHOT_INVALID ; ne lève jamais, ne lance jamais une op inconnue.
    public override async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        // Une annulation part hors portée de pose : marquée, le helper la rejouerait après un arrêt sec et remettrait le levier.
        using var horsPortee = PorteeDePose.Suspendre();
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        Result<IReadOnlyList<PrivilegedOp>> decoded;
        try
        {
            decoded = CompositeSnapshotCodec.Decode(snapshot.OriginalValueJson);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SnapshotInvalid, $"Snapshot decode error: {ex.Message}");
        }

        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid,
                "Snapshot original value is not a known composite of typed ops.");

        try
        {
            return await RetourArriere.ToutRendreAsync(Elevated, decoded.Value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }

    // L'inverse d'une feuille GameBar : la valeur relevée, ou une suppression si elle était absente.
    private static PrivilegedOp Inverse(string name, uint? capturedOriginal) =>
        capturedOriginal is uint original
            ? new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, name, RegistryValueType.DWord,
                original.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, name);
}
