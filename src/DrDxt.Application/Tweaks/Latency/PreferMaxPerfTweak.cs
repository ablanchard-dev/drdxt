using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// NS-2 — PreferMaxPerfTweak (NVIDIA "Power management mode" = Prefer maximum performance).
//
// Sets the driver DRS setting PREFERRED_PSTATE (0x1057EB71) to PREFER_MAX (1), so the GPU holds
// its top P-state instead of downclocking. Verified verbatim against NVIDIA's official
// NvApiDriverSettings.h (github.com/NVIDIA/nvapi): PREFERRED_PSTATE_ID = 0x1057EB71,
// PREFERRED_PSTATE_PREFER_MAX = 1 (ADAPTIVE = 0). Applied via the same typed, allowlisted
// ApplyNvApiDrsSettingOp path as the other DRS latency tweaks; the base RevertAsync restores the
// captured original from the single-op snapshot.
//
// Opt-in TRADEOFF (honest, §6): forcing max clocks only helps when the GPU actually downclocks
// in-game (fewer clock-ramp stutters); it costs idle power at the desktop and gives nothing on a
// GPU already pinned at max under load. That tradeoff is not software-detectable, so it is stated
// in the summary text (no "+FPS" claim) and the measured A/B governs whether it is kept.
//
// Gating: desktop NVIDIA render GPU only — never a laptop (battery / Optimus MUX manages power)
// and never a non-NVIDIA GPU. Persists natively in the driver profile (nvdrssel.bin), like the
// other DRS settings, so no reconciler entry is needed (mirrors VrrVsyncCap).
// -----------------------------------------------------------------------
public sealed class PreferMaxPerfTweak : LatencyTweakBase
{
    public const uint PreferredPstateSettingId = 0x1057EB71; // PREFERRED_PSTATE_ID
    public const uint PreferMax = 1;                          // PREFERRED_PSTATE_PREFER_MAX

    /// <summary>30/09/2026 (G1) : « Normal » du panneau NVIDIA = PREFERRED_PSTATE_ADAPTIVE (0) de NvApiDriverSettings.h
    /// (github.com/NVIDIA/nvapi). Depuis les pilotes 460, le panneau a renommé « Adaptive » en « Normal » : la fiche
    /// d'aide NVIDIA 3130 s'intitule « Setting Power management mode from Normal to Maximum Performance » et son
    /// adresse garde « adaptive » (nvidia.custhelp.com/app/answers/detail/a_id/3130).</summary>
    public const uint Normal = 0;                             // PREFERRED_PSTATE_ADAPTIVE

    private readonly INvApi _nvApi;
    private readonly IGameExecutable? _game;

    /// <param name="game">PHASE 18 F1 lot 3 — le jeu en cours. Absent (outils, tests) ou jeu sans
    /// profil propre : ecriture globale, comme avant.</param>
    public PreferMaxPerfTweak(INvApi nvApi, IElevatedClient elevated, IGameExecutable? game = null) : base(elevated)
        => (_nvApi, _game) = (nvApi, game);

    public override string Id => "latency.prefer_max_perf";
    public override string SummaryKey => "tweak.latency.prefer_max_perf.enable";

    // Corrige LE 10/09/2026 (phase 18). Ce gate ne regardait que « bureau + GPU NVIDIA » : il
    // ne demandait pas si la valeur cible etait deja en place. Lecture reelle du 10/09 sur la
    // machine de reference : `PREFERRED_PSTATE = 1`, donc **deja au maximum**. Le produit aurait
    // propose un levier sans effet, l'aurait « applique » sans rien changer, puis aurait mesure le
    // bruit d'une session — exactement le placebo qu'il existe pour ne pas vendre.
    //
    // `Unknown` ne devient pas un candidat : sans lecture de l'etat courant, il n'y a rien a
    // corriger. Seul un `Off` LU ouvre le levier.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        hw.Chassis == ChassisType.Desktop
        && LatencyGating.HasDesktopNvidiaRenderGpu(hw)
        && state.NvidiaPreferMaxPerf == TriState.Off;

    // Le reglage du pilote n'a pas pu etre lu => Unknown. Ne rien ecrire, mais le dire.
    public override bool EtatNonLu(SettingsState state) =>
        state.NvidiaPreferMaxPerf == TriState.Unknown;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            if (!_nvApi.IsAvailable)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "NVIDIA driver / NVAPI is not available to set the power management mode.");

            // Capture the real original before writing (faithful revert), at the scope written.
            var plan = PlanifierDrs(_nvApi, _game is not null, _game?.RunningGameExecutablePath(), PreferredPstateSettingId,
                PreferMax, _nvApi.GetPreferredPstate, "power management mode");
            if (!plan.IsOk)
                return Result<TweakSnapshot>.Fail(plan.Error!.Value.Code, plan.Error.Value.Message);
            var (inverse, op) = plan.Value;

            var inverseWire = PrivilegedOpCodec.Serialize(inverse);
            if (!inverseWire.IsOk || inverseWire.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");
            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            return Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverseWire.Value, op));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Prefer-max-performance apply boundary error: {ex.Message}");
        }
    }
}
