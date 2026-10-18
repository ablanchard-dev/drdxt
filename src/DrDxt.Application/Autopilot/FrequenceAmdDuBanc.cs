using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>
/// La fréquence maximale d'une carte AMD, vérifiée par le banc de validation (« --valider-frequence », geste à part de
/// « --valider »). Le levier du produit (<see cref="GpuUndervoltTweak"/>), avec ses bornes, sa sonde d'écriture et son
/// test de stabilité ; seule la cible change : une BAISSE de <see cref="BaisseMhz"/> MHz, qui ne peut que ralentir la
/// carte, jamais le profil d'overclock léger de l'App. Même identifiant que le levier : le journal du banc le retrouve
/// pour rendre l'essai si la fenêtre meurt. Une carte AMD dédiée de bureau seulement, et aucune NVIDIA à côté : le levier
/// prendrait NVAPI, et le test promet de ne rien faire sur une NVIDIA.
/// </summary>
public sealed class FrequenceAmdDuBanc(GpuUndervoltTweak levier) : ITweak
{
    /// <summary>Dans la bande [-200, +100] MHz du produit (GpuSafeBounds) ; assez pour se relire, trop peu pour se sentir.</summary>
    public const int BaisseMhz = 50;

    public string Id => levier.Id;
    public TweakCategory Category => levier.Category;
    public bool RequiresElevation => levier.RequiresElevation;
    public bool RequiresRestorePoint => levier.RequiresRestorePoint;
    public string SummaryKey => levier.SummaryKey;

    public bool AppliesTo(HardwareInventory hw, SettingsState state)
        => levier.AppliesTo(hw, state) && hw.Gpus.Any(LatencyGating.IsAmd) && !hw.Gpus.Any(LatencyGating.IsNvidia);

    public Task<Result<TweakSnapshot>> ApplyAsync()
        => levier.ApplyAvecCibleAsync(new GpuTuningTarget(GpuIndex: 0, CoreOffsetMhz: -BaisseMhz, MemoryOffsetMhz: 0));

    public Task<Result> RevertAsync(TweakSnapshot snapshot) => levier.RevertAsync(snapshot);
}
