using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// PreferMaxPerfTweak (NVIDIA « Mode de gestion de l'alimentation » = Performances maximales).
//
// Pose le réglage DRS du pilote PREFERRED_PSTATE (0x1057EB71) sur PREFER_MAX (1) : la carte garde
// son état de performance le plus haut au lieu de baisser sa fréquence. Valeurs vérifiées dans
// l'en-tête officiel NvApiDriverSettings.h (github.com/NVIDIA/nvapi) : PREFERRED_PSTATE_ID = 0x1057EB71,
// PREFERRED_PSTATE_PREFER_MAX = 1 (ADAPTIVE = 0). Écrit par le même chemin typé et autorisé
// (ApplyNvApiDrsSettingOp) que les autres leviers DRS ; le RevertAsync de la base rend l'original
// relu, depuis le snapshot à une seule op.
//
// Compromis, sur accord : forcer les fréquences maximales n'aide que si la carte baisse vraiment sa
// fréquence en jeu (moins d'à-coups de montée) ; ça coûte de la consommation au bureau et n'apporte
// rien à une carte déjà au maximum en charge. Ce compromis ne se détecte pas par logiciel : le texte le
// dit (pas de « +FPS ») et l'A/B mesuré décide s'il est gardé.
//
// Gating : carte de rendu NVIDIA sur un PC de bureau seulement, jamais un portable (la batterie et le
// MUX Optimus gèrent l'alimentation), jamais une carte non NVIDIA. Persiste dans le profil du pilote
// (nvdrssel.bin), comme les autres réglages DRS : pas d'entrée au réconciliateur (comme VrrVsyncCap).
// -----------------------------------------------------------------------
/// <summary>Garde la carte NVIDIA à son état de performance maximal (PREFERRED_PSTATE = PREFER_MAX), au profil du jeu en cours quand il est connu.
/// Proposé sur un PC de bureau à carte NVIDIA quand le réglage est lu sur « adaptatif ».</summary>
public sealed class PreferMaxPerfTweak : LatencyTweakBase
{
    public const uint PreferredPstateSettingId = 0x1057EB71; // PREFERRED_PSTATE_ID
    public const uint PreferMax = 1;                          // PREFERRED_PSTATE_PREFER_MAX

    /// <summary>« Normal » du panneau NVIDIA = PREFERRED_PSTATE_ADAPTIVE (0) de NvApiDriverSettings.h
    /// (github.com/NVIDIA/nvapi). Depuis les pilotes 460, le panneau a renommé « Adaptive » en « Normal » : la fiche
    /// d'aide NVIDIA 3130 s'intitule « Setting Power management mode from Normal to Maximum Performance » et son
    /// adresse garde « adaptive » (nvidia.custhelp.com/app/answers/detail/a_id/3130).</summary>
    public const uint Normal = 0;                             // PREFERRED_PSTATE_ADAPTIVE

    private readonly INvApi _nvApi;
    private readonly IGameExecutable? _game;

    /// <param name="game">Le jeu en cours. Absent (outils, tests) ou jeu sans
    /// profil propre : ecriture globale, comme avant.</param>
    public PreferMaxPerfTweak(INvApi nvApi, IElevatedClient elevated, IGameExecutable? game = null) : base(elevated)
        => (_nvApi, _game) = (nvApi, game);

    public override string Id => "latency.prefer_max_perf";
    public override string SummaryKey => "tweak.latency.prefer_max_perf.enable";

    // Le gate demande aussi si la valeur cible est deja en place : sinon, sur une carte deja au
    // maximum (`PREFERRED_PSTATE = 1`, relevé sur la machine de reference), le produit proposerait un
    // levier sans effet puis mesurerait le bruit d'une session, le placebo qu'il refuse de vendre.
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

            // Relever la vraie origine avant d'écrire (retour fidèle), au scope écrit.
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
