using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// D1 — NvidiaUltraLowLatencyTweak (anciennement « NvidiaUltraLowLatencyTweak »).
//
// Applique NVIDIA Ultra Low Latency Mode (NULL) = Ultra, via la feuille de registre
// allowlistee ...\NVTweak\LowLatencyMode, ecrite par l'helper eleve.
//
// Dette semantique payee le 08/09/2026. Ce tweak s'appelait « Reflex » et son titre
// affichait « NVIDIA Reflex — plancher de faible latence pilote ». **Ce sont deux
// technologies differentes**, verifie dans la documentation NVIDIA :
//
//   * NULL (ce reglage) limite la file de frames cote pilote. Il ne fonctionne qu'en
//     DirectX 9 a 11 : en DX12 et Vulkan, le moteur du jeu gere lui-meme la file et le
//     pilote n'a pas la main.
//   * Reflex aligne le travail du MOTEUR juste-a-temps. C'est un SDK integre au jeu,
//     que DrDXT ne peut ni activer ni desactiver.
//
// Les confondre parce qu'elles poursuivent un but voisin a produit un titre faux et,
// pire, un candidat qu'on aurait essaye sur Warzone — un jeu DirectX 12, ou ce reglage
// ne peut rien faire.
//
// L'identifiant `latency.reflex` est conserve : c'est une cle de PERSISTANCE, lue par
// le reconciliateur et par les manifestes deja ecrits. La renommer est une migration a
// part entiere, tracee dans GATES.md — pas un renommage de confort.
//
// Gating: NVIDIA render GPU on a desktop. False on AMD-only / iGPU / handheld /
// laptop (mobile GPUs are driver-locked — guided, not scripted).
//
// Apply: capture the real original LowLatencyMode (registry-read seam) → store as a
// serialized inverse PrivilegedOp in the snapshot → write Ultra (=2) via the helper.
// Any unavailable SDK / registry path → TWEAK.SDK_UNAVAILABLE (fail clean, no throw).
// -----------------------------------------------------------------------
public sealed class NvidiaUltraLowLatencyTweak : LatencyTweakBase
{
    // Ultra = 2 (0 = Off, 1 = On, 2 = Ultra). The "floor" we set.
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

    // 24/09/2026 : jamais proposé. Rien ne prouve que le pilote lise cette valeur : absente de
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

            // Capture the real original value before writing (faithful revert).
            var read = _registry.ReadDword(RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original LowLatencyMode: {read.Error?.Code}.");

            var inverse = BuildRegistryInverse(
                RegistryHiveKind.LocalMachine, NvTweakPath, LowLatencyName, read.Value);
            if (!inverse.IsOk || inverse.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Apply the driver floor (Ultra) through the allowlisted typed op.
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
            // Every external boundary wrapped → fail clean, never throw.
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"NVIDIA Ultra Low Latency apply boundary error: {ex.Message}");
        }
    }
}
