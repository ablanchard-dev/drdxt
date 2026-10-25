using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>
/// Le ventilateur d'une carte AMD en mode Expert, par ADLX : la même vitesse sur chaque état de la courbe de la carte dédiée
/// (AdlxNative.PoserVentilateurFixe), jamais sous <see cref="MinPourcent"/> ; le retour remet la courbe et le Zero RPM par
/// défaut. ADLX ne dit pas la vitesse que la carte vise à l'instant : à la place du « vers le haut seulement » de NVML, le
/// plancher à chaud, et la garde à 80 °C du helper. L'index est celui de l'op ; ADLX vise toujours la carte dédiée.
/// </summary>
public sealed class AdlxVentilateurActions(IAdlxNative adlx) : IVentilateurActions
{
    /// <summary>Le plancher à chaud de la liste blanche des courbes, celui du mode Expert.</summary>
    public const int MinPourcent = 40;

    public AdlxVentilateurActions() : this(new AdlxNative()) { }

    public Result Poser(int gpuIndex, int pourcent)
    {
        if (pourcent is < MinPourcent or > 100) return Echec($"Fan speed {pourcent} % is outside [{MinPourcent}, 100] %. Nothing was written.");
        if (!adlx.IsAvailable) return Echec("ADLX unavailable: nothing was written.");
        if (adlx.PoserVentilateurFixe(pourcent) is not { } raison) return Result.Ok();
        return adlx.RendreVentilateurAuto() is { } retour
            ? Result.Fail(ErrorCodes.TweakPartiallyApplied, $"{raison} {retour}")
            : Echec(raison + " Fan curve restored.");
    }

    public Result Rendre(int gpuIndex) => adlx.RendreVentilateurAuto() is { } raison
        ? Result.Fail(ErrorCodes.TweakPartiallyApplied, raison + " Check the fan curve in AMD Software.")
        : Result.Ok();

    private static Result Echec(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}

/// <summary>Le ventilateur de la carte présente : NVML quand il est là (carte NVIDIA), sinon ADLX.</summary>
public sealed class VentilateurSelonLaCarte(IVentilateurActions nvidia, IVentilateurActions amd, Func<bool> nvidiaPresente) : IVentilateurActions
{
    public static VentilateurSelonLaCarte ParDefaut()
    {
        var nvml = new DrDxt.GpuNative.Nvml.NvmlNative();
        return new(new NvmlVentilateurActions(nvml), new AdlxVentilateurActions(), () => nvml.IsAvailable);
    }

    public Result Poser(int gpuIndex, int pourcent) => (nvidiaPresente() ? nvidia : amd).Poser(gpuIndex, pourcent);

    public Result Rendre(int gpuIndex) => (nvidiaPresente() ? nvidia : amd).Rendre(gpuIndex);
}
