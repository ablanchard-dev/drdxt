using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Coutures matérielles injectables des leviers de réglage GPU (sous-tension, limite de puissance, courbe de ventilateur).
//
// Elles lisent l'origine (pour un retour fidèle) et disent si le levier s'applique, jamais n'écrivent : l'écriture passe
// par IElevatedClient et une PrivilegedOp sur liste blanche. Un SDK ou un GPU absent échoue proprement
// (TWEAK.SDK_UNAVAILABLE). Horloge et puissance seulement, jamais la tension (NVIDIA : décalage et limite, pas de courbe
// V/F ; AMD : ADLX), séparées des coutures de latence.
// -----------------------------------------------------------------------

/// <summary>Couture de lecture du pilote NVIDIA (NVAPI) pour le réglage GPU. La pose passe
/// par des ops typées d'IElevatedClient ; ceci ne lit que les valeurs courantes.</summary>
public interface INvApiGpuTuning
{
    /// <summary>Vrai quand un pilote NVIDIA / NVAPI est présent et utilisable.</summary>
    bool IsAvailable { get; }

    /// <summary>Lit le décalage d'horloge cœur appliqué (MHz) d'un GPU.
    /// Chemin indisponible → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetCoreClockOffsetMhz(int gpuIndex);

    /// <summary>Lit le décalage d'horloge mémoire appliqué (MHz) d'un GPU.
    /// Chemin indisponible → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetMemoryClockOffsetMhz(int gpuIndex);

    /// <summary>Lit la limite de puissance courante (% du TDP) d'un GPU.
    /// Chemin indisponible → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetPowerLimitPct(int gpuIndex);

    /// <summary>Lit la courbe de ventilateur courante d'un GPU. Chemin indisponible →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<Gpu.FanCurve> GetFanCurve(int gpuIndex);
}

/// <summary>Architecture / génération du GPU NVIDIA, assez grossière pour conditionner les
/// écritures de tension. NVIDIA a retiré l'écriture directe de la courbe V/F sur Blackwell
/// (RTX 50) : une garde y coupe donc la vraie sous-tension (recette « Blackwell /
/// RTX 50 lockout »). Ada (RTX 40) et les plus anciennes acceptent encore l'ancienne écriture.</summary>
public enum GpuArchitecture
{
    /// <summary>Indéterminé (ni NVIDIA ni Radeon reconnue, lecture ratée). Traité avec prudence par les
    /// gardes (on ne suppose pas l'écriture de tension permise).</summary>
    Unknown = 0,
    /// <summary>Ada Lovelace (RTX 40) ou plus ancienne : l'ancienne écriture de tension marche.</summary>
    AdaOrOlder = 1,
    /// <summary>Blackwell (RTX 50) : la sous-tension directe est verrouillée.</summary>
    Blackwell = 2,

    // ── AMD : fermé par défaut côté matériel ──────────────
    // Sans ces valeurs, `SupportsVoltageUndervolt` rendrait `true` pour tout GPU non NVIDIA.
    // RDNA3 garde le voltage manuel mais n'a jamais été testée en écriture : elle reste fermée.

    /// <summary>RDNA 3 (RX 7000). La seule architecture AMD qui garde le voltage manuel, et
    /// elle n'a jamais ete validee en ecriture sur une vraie carte. Regle : voltage desactive
    /// jusqu'a un test reel.</summary>
    Rdna3 = 3,

    /// <summary>RDNA 4 (RX 9000). Ecriture non validee tant qu'aucun rapport de verification n'est archive
    /// dans tests/Shared/validations ; lecture seule d'ici la.</summary>
    Rdna4 = 4,

    /// <summary>RDNA 2 (RX 6000 : 6400 à 6950 XT). Elles passaient « architecture inconnue » alors
    /// qu'elles sont nombreuses chez les joueurs. Reconnues, lues par ADLX ; jamais validées en écriture (voltage fermé).</summary>
    Rdna2 = 5,

    /// <summary>RDNA 1 (RX 5000 : 5300 à 5700 XT). Reconnue par le nom, lue par ADLX ; aucune écriture validée,
    /// lecture seule jusqu'à un rapport.</summary>
    Rdna1 = 6,

    /// <summary>GCN (RX 400/500 Polaris, RX Vega, Radeon VII). Reconnue par le nom pour dire ce qu'est la carte ;
    /// aucune écriture validée, lecture seule jusqu'à un rapport.</summary>
    Gcn = 7,
}

/// <summary>Borne partagée de la boucle de sonde des GPU réglables de
/// <see cref="IAdlxGpuTuning.ResolveTunableGpuIndex"/>. Sur une machine iGPU + dGPU (par exemple
/// un APU Ryzen X3D à l'index 0 + une Radeon dédiée à l'index 1), l'index ADLX 0 est souvent
/// l'iGPU non réglable : la couture sonde une petite plage d'index et prend le premier dont
/// le réglage GFX se lit, la carte AMD dédiée. Calqué sur le sélecteur du banc de test AMD
/// (tools/amd-harness/DrDxt.AmdHarness/Program.cs) : garder la même borne, ne pas en inventer une autre.</summary>
public static class AdlxGpuProbe
{
    /// <summary>Borne haute (exclue) de la sonde d'index GPU, calquée sur le
    /// <c>for (int i = 0; i &lt; 8; i++)</c> du sélecteur du banc.</summary>
    public const int MaxProbeIndices = 8;
}

/// <summary>Couture de lecture du runtime AMD ADLX pour le réglage GPU (sous-tension / limite
/// de puissance / ventilateur). La pose passe par des ops typées d'IElevatedClient ; ceci ne fait que lire.</summary>
public interface IAdlxGpuTuning
{
    /// <summary>Vrai quand le runtime AMD ADLX est présent et utilisable.</summary>
    bool IsAvailable { get; }

    /// <summary>Trouve l'index du premier GPU AMD dédié réglable, celui dont le réglage GFX se
    /// lit vraiment (<see cref="GetUndervoltValue"/> IsOk), en sondant les index
    /// [0..<see cref="AdlxGpuProbe.MaxProbeIndices"/>). Sur une machine iGPU + dGPU, l'iGPU à
    /// l'index 0 n'est pas réglable : la carte dédiée (par exemple l'index 1) est choisie.
    /// Calqué sur le sélecteur du banc AMD. Indisponible / aucun index lisible →
    /// Fail(TWEAK.SDK_UNAVAILABLE) (échec propre, ne lève jamais).</summary>
    Result<int> ResolveTunableGpuIndex();

    /// <summary>Lit la valeur de sous-tension ADLX courante (le VoltMv rapporté par la lecture
    /// GFX d'ADLX). Elle ne sert que de sonde de lisibilité <c>.IsOk</c> pour
    /// <see cref="ResolveTunableGpuIndex"/> (la valeur est ignorée). Elle ne doit pas servir à
    /// construire l'op de retour de la sous-tension : pose et retour font l'aller-retour d'un
    /// MaxFreq absolu (voir <see cref="GetGfxMaxFreqMhz"/>), jamais d'une tension.
    /// Chemin indisponible → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetUndervoltValue(int gpuIndex);

    /// <summary>Lit l'horloge GFX maximale absolue courante (MHz), la valeur que l'écriture de
    /// sous-tension ADLX pose vraiment. Le levier la lit avant la pose pour (a) construire la
    /// cible absolue <c>courante + décalage</c> qu'il écrit et (b) relever l'origine absolue comme
    /// op inverse : le retour rend exactement le MaxFreq d'usine (comme la limite de puissance
    /// fait l'aller-retour d'un % absolu).
    /// Indisponible / lecture ratée / index &lt; 0 → Fail(TWEAK.SDK_UNAVAILABLE) ; ne lève jamais.</summary>
    Result<int> GetGfxMaxFreqMhz(int gpuIndex);

    /// <summary>Le réglage de la carte est-il à l'usine (IADLXGPUTuningServices.IsAtFactory) ? null = non lu. Une carte déjà
    /// réglée par un autre outil ne se règle pas par-dessus.</summary>
    bool? EstALUsine(int gpuIndex) => null;

    /// <summary>Lit la limite de puissance ADLX courante, dans l'unité du pilote (% du TDP ou décalage autour de l'usine :
    /// la plage le dit, voir <see cref="Gpu.LimitePuissanceAmd"/>). Chemin indisponible → Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<int> GetPowerLimitPct(int gpuIndex);

    /// <summary>Lit la plage de la limite de puissance (GetPowerLimitRange), dans l'unité du pilote. Chemin indisponible →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<PlageDePuissanceAmd> GetPowerLimitRange(int gpuIndex) =>
        Result<PlageDePuissanceAmd>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX power-limit range not read.");

    /// <summary>Lit la courbe de ventilateur courante d'un GPU. Chemin indisponible →
    /// Fail(TWEAK.SDK_UNAVAILABLE).</summary>
    Result<Gpu.FanCurve> GetFanCurve(int gpuIndex);
}

/// <summary>La plage de la limite de puissance d'une carte AMD, dans l'unité du pilote.</summary>
public sealed record PlageDePuissanceAmd(int Min, int Max, int Pas);

/// <summary>Le résultat d'un court test de stabilité GPU.</summary>
public enum StabilityOutcome
{
    /// <summary>Le GPU a tenu la charge : ni blocage, ni artefact, ni plantage.</summary>
    Stable = 0,
    /// <summary>Un artefact, un blocage, une réinitialisation du pilote ou un plantage observé
    /// → le changement doit être annulé aussitôt.</summary>
    Unstable = 1,
    /// <summary>Le test de stabilité n'a pas pu tourner (pas de banc de charge GPU) →
    /// traité avec prudence (le levier échoue proprement, n'affirme jamais Ok).</summary>
    Inconclusive = 2,
}

/// <summary>Couture d'un court test de stabilité GPU (une brève charge qui guette blocages,
/// artefacts et réinitialisations du pilote). Le levier de sous-tension s'en sert pour vérifier
/// qu'un décalage tout juste posé est stable avant de rendre Ok. Rend un Result : un banc
/// absent échoue proprement (TWEAK.SDK_UNAVAILABLE), sans jamais lever.</summary>
public interface IStabilityTester
{
    /// <summary>Lance une courte charge sur le GPU donné et rapporte la stabilité.
    /// Un plantage ou un artefact doit sortir en StabilityOutcome.Unstable, pas en exception.</summary>
    Task<Result<StabilityOutcome>> RunAsync(int gpuIndex, CancellationToken ct);
}

/// <summary>Le résultat d'une brève charge GPU qui sonde la stabilité d'une sous-tension.</summary>
public enum GpuLoadResult
{
    /// <summary>La charge a tourné toute sa durée et le périphérique est resté sain.</summary>
    CompletedDeviceHealthy = 0,
    /// <summary>Le périphérique a été perdu, retiré ou réinitialisé (un TDR) pendant la charge → instable.</summary>
    DeviceLost = 1,
    /// <summary>La charge n'a pas pu tourner (pas de banc D3D / création du périphérique ratée) →
    /// le testeur de stabilité le traite avec prudence comme Inconclusive.</summary>
    CouldNotRun = 2,
}

/// <summary>Couture d'une brève et vraie charge GPU (quelques secondes de travail qui font
/// apparaître un décalage d'horloge instable comme périphérique perdu / TDR). Implémentée en
/// D3D11 dans Detection et validée par smoke test ; l'orchestration (GpuStabilityTester) est
/// pure et testée avec une doublure. Rend un Result : un banc absent échoue proprement ; elle ne
/// doit pas lever sur une perte de périphérique, c'est GpuLoadResult.DeviceLost.</summary>
public interface IGpuLoadProbe
{
    /// <summary>Lance une brève charge sur le GPU donné pendant au plus <paramref name="duration" />
    /// et dit si le périphérique a tenu.</summary>
    Task<Result<GpuLoadResult>> RunBriefLoadAsync(int gpuIndex, TimeSpan duration, CancellationToken ct);
}
