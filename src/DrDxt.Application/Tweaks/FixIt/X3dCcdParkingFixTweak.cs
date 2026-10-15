using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// X3dCcdParkingFixTweak.
//
// Répare un mauvais parcage des blocs (CCD) sur un processeur AMD X3D à deux blocs (7950X3D, etc.), pour
// que le jeu tourne sur le bloc à V-Cache. La seule mesure publique trouvée donne environ 6 % en moyenne en
// 1080p (Hardware Busters, 02/04/2023 : sans parcage, 94,12 % de la machine bien réglée). L'écart réel est
// mesuré par le produit, et seulement par la voie logicielle prise en charge :
//   (1) démarrer le service AMD 3D V-Cache Performance Optimizer (AMD3DVCacheSvc) ;
//   (2) activer le mode Jeu automatique de la Game Bar (l'optimiseur se règle sur la Game Bar).
// CppcEnable sous ...\Control\Processor n'est pas écrit : aucune source trouvée ne montre que Windows
// lise cette valeur ; CPPC se règle au BIOS. La feuille reste dans l'allowlist pour annuler un instantané
// ancien qui la porte. Aucun bricolage du noyau, aucune écriture du BIOS : l'option CPPC du BIOS est
// guidée par le guide BIOS, jamais scriptée ici.
//
// L'état d'origine du service et de la Game Bar est relu, rangé dans un snapshot composite inverse ;
// l'annulation rend les deux. Chaque frontière externe est protégée : échec propre, jamais d'exception.
//
// Le SummaryKey décrit la réparation, jamais « +FPS ». Le gain se prouve par la nouvelle mesure du
// moteur ; sans gain mesuré, aucun gain n'est affiché.
//
// Risque de faux positif : l'heuristique est prudente (X3dParkingHeuristic), et la voie est prise en
// charge et entièrement annulée : un faux positif ne coûte rien de mesurable.
// -----------------------------------------------------------------------
/// <summary>Démarre l'optimiseur 3D V-Cache d'AMD et le mode Jeu automatique pour que le jeu tourne sur le bloc à cache.
/// Proposé sur un Ryzen X3D à deux blocs dont le parcage semble faux.</summary>
public sealed class X3dCcdParkingFixTweak : FixItTweakBase
{
    private const string ServiceName = "AMD3DVCacheSvc";
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string GameBarName = "AllowAutoGameMode";
    private const uint Enable = 1;

    private readonly IServiceStateReader _service;
    private readonly IRegistryReader _registry;

    public X3dCcdParkingFixTweak(
        IServiceStateReader service,
        IRegistryReader registry,
        IElevatedClient elevated)
        : base(elevated)
    {
        _service = service;
        _registry = registry;
    }

    public override string Id => "fixit.x3d_ccd";
    public override string SummaryKey => "tweak.fixit.x3d_ccd.repair_routing";

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        // X3D à deux CCD (les 7800X3D/9800X3D à un CCD, Intel et les non-X3D sont écartés par
        // l'heuristique) dont le parcage semble faux.
        X3dParkingHeuristic.LooksMisparked(hw.Cpu, state);

    /// <summary>Le parking non lu n'est pas « rien n'est parqué ».</summary>
    public override bool EtatNonLu(SettingsState state) => state.ParkedCoreIndexes is null;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // (Relevé) Lire les vraies origines avant d'écrire, pour un retour arrière fidèle.
            var serviceState = _service.GetServiceState(ServiceName);
            if (!serviceState.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read the AMD 3D V-Cache service state: {serviceState.Error?.Code}.");

            var gameBar = _registry.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName);
            if (!gameBar.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original Game Bar auto-mode: {gameBar.Error?.Code}.");

            // Construire le snapshot inverse composite (rend les deux origines).
            var inverseOps = new List<PrivilegedOp>
            {
                BuildRegistryInverse(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName, gameBar.Value),
            };
            // N'ajouter l'inverse du service que s'il ne tournait pas déjà : on ne doit pas
            // arrêter un service que l'utilisateur avait déjà allumé.
            if (serviceState.Value == ServiceState.Stopped)
                inverseOps.Add(new RunServiceControlOp(ServiceName, ServiceAction.Stop));

            var snapshotJson = CompositeSnapshotCodec.Encode(inverseOps);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert ops.");

            // (Pose, voie prise en charge seulement) démarrage du service + Game Bar.
            // Chaque op est appariée à l'inverse qui ramène le système à l'état d'entrée du
            // levier. Si une op échoue en cours de route, on rejoue les inverses des ops déjà
            // posées (LIFO) pour que rien ne reste à moitié posé, puis on rend le Fail d'origine.
            var enableValue = Enable.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var steps = new (PrivilegedOp Forward, PrivilegedOp? Inverse)[]
            {
                (
                    new RunServiceControlOp(ServiceName, ServiceAction.Start),
                    // N'annuler le démarrage que si le service était Stopped à l'entrée ; s'il tournait
                    // déjà, rien à annuler (ne jamais arrêter un service que l'utilisateur avait allumé)
                    // → inverse null, sauté au rollback.
                    serviceState.Value == ServiceState.Stopped
                        ? new RunServiceControlOp(ServiceName, ServiceAction.Stop)
                        : null
                ),
                (
                    new SetRegistryValueOp(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName,
                        RegistryValueType.DWord, enableValue),
                    BuildRegistryInverse(RegistryHiveKind.CurrentUser, GameBarPath, GameBarName, gameBar.Value)
                ),
            };

            var appliedInverses = new List<PrivilegedOp?>(steps.Length);
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

            // Les valeurs posees (service, Game Bar), pour que la desinstallation ne
            // rende une feuille que si elle porte encore ce que DrDXT y a mis.
            var posees = CompositeSnapshotCodec.Encode(steps.Select(s => s.Forward).ToList());
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, posees.IsOk ? posees.Value : null));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"X3D CCD parking fix apply boundary error: {ex.Message}");
        }
    }

    /// <summary>Retour composite : rejoue chaque op d'origine relevée. Snapshot malformé
    /// → SAFETY.SNAPSHOT_INVALID ; ne lève jamais, ne lance jamais une op inconnue.</summary>
    public override async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
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
            foreach (var op in decoded.Value)
            {
                var res = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
                if (!res.IsOk)
                    return res; // rend tel quel le premier échec de retour
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }

    // L'op inverse sérialisée d'une feuille de registre DWORD : si la valeur existait,
    // la remettre ; si elle était absente, l'inverse fidèle est une suppression.
    private static PrivilegedOp BuildRegistryInverse(
        RegistryHiveKind hive, string path, string name, uint? capturedOriginal) =>
        capturedOriginal is uint original
            ? new SetRegistryValueOp(hive, path, name, RegistryValueType.DWord,
                original.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new DeleteRegistryValueOp(hive, path, name);
}
