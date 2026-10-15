using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// NvidiaUltraLowLatencyTweak.
//
// Pose le mode faible latence de NVIDIA (NULL) sur Ultra, par la feuille de registre autorisée
// ...\NVTweak\LowLatencyMode, écrite par le helper élevé.
//
// Ce n'est pas Reflex : ce sont deux technologies différentes (documentation NVIDIA).
//   * NULL (ce réglage) limite la file d'images côté pilote. Il ne fonctionne qu'en DirectX 9 à 11 :
//     en DX12 et Vulkan, le moteur du jeu gère lui-même la file et le pilote n'a pas la main.
//   * Reflex aligne le travail du moteur juste à temps. C'est un SDK intégré au jeu, que DrDXT ne peut
//     ni activer ni désactiver.
// Les confondre donnerait un titre faux et un candidat essayé sur un jeu DirectX 12, où ce réglage
// ne peut rien.
//
// L'identifiant `latency.reflex` est gardé : c'est une clé de persistance, lue par le réconciliateur
// et par les manifestes déjà écrits. La renommer est une migration à part entière (GATES.md).
//
// Jamais proposé (voir AppliesTo) ; le code reste pour défaire une écriture passée.
//
// Application : l'original de LowLatencyMode est relu, l'op inverse sérialisée rangée dans le
// snapshot, puis Ultra (= 2) est écrit par le helper. SDK ou chemin de registre absent :
// TWEAK.SDK_UNAVAILABLE (échec propre, sans exception).
// -----------------------------------------------------------------------
/// <summary>Pose LowLatencyMode = Ultra dans le registre NVIDIA. Jamais proposé : rien ne prouve que le pilote lise
/// cette valeur ; il ne sert plus qu'à défaire une écriture passée.</summary>
public sealed class NvidiaUltraLowLatencyTweak : LatencyTweakBase
{
    // Ultra = 2 (0 = Off, 1 = On, 2 = Ultra). Le « plancher » qu'on pose.
    private const uint UltraLowLatency = 2;
    private const string NvTweakPath = @"SOFTWARE\NVIDIA Corporation\Global\NVTweak";
    private const string LowLatencyName = "LowLatencyMode";

    private readonly INvApi _nvApi;
    private readonly IRegistryReader _registry;
    private readonly IGameExecutable? _game;

    public NvidiaUltraLowLatencyTweak(INvApi nvApi, IRegistryReader registry, IElevatedClient elevated,
        IGameExecutable? game = null)
        : base(elevated)
    {
        _nvApi = nvApi;
        _registry = registry;
        _game = game;
    }

    public override string Id => "latency.reflex";
    public override string SummaryKey => "tweak.latency.reflex.driver_floor";

    // Jamais proposé. Rien ne prouve que le pilote lise cette valeur : absente de
    // l'en-tête public NvApiDriverSettings.h, absente du registre de la machine de référence (seul
    // `OverridePRR` y vit), et le « Low Latency Mode » du panneau passe par PRERENDERLIMIT, déjà
    // posé par `latency.render_queue_cap`. Le code reste pour défaire une écriture passée ;
    // le supprimer est la décision produit.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) => false;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            if (!_nvApi.IsAvailable)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "NVIDIA driver / NVAPI is not available.");

            // Relever la vraie valeur d'origine avant d'écrire (retour arrière fidèle).
            var read = _registry.ReadDword(RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original LowLatencyMode: {read.Error?.Code}.");

            var inverse = BuildRegistryInverse(
                RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName, read.Value);
            if (!inverse.IsOk || inverse.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Poser le plancher du pilote (Ultra) par l'op typée sur liste blanche.
            var op = new SetRegistryValueOp(
                RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName,
                RegistryValueType.DWord,
                UltraLowLatency.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(
                    applied.Error!.Value.Code, applied.Error!.Value.Message);

            return Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverse.Value, op));
        }
        catch (Exception ex)
        {
            // Chaque frontière externe protégée → échec propre, jamais d'exception.
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"NVIDIA Ultra Low Latency apply boundary error: {ex.Message}");
        }
    }
}
