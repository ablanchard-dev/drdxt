using DrDxt.Core;
using DrDxt.GpuNative.Nvml;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>
/// GPU-OC (08/10/2026) : le ventilateur d'une carte NVIDIA en mode Expert, par NVML (docs.nvidia.com/deploy/nvml-api).
/// NVIDIA prévient qu'une vitesse fixe trop basse peut abîmer la carte, et un retour à l'automatique raté a été signalé
/// (forum NVIDIA, 2022, sous Linux). D'où trois règles, tenues ici et pas chez l'appelant :
/// on ne pose qu'en partant de l'automatique, jamais sous la vitesse que l'automatique vise à cet instant ni sous le
/// minimum de la carte ; tout est relu après l'écriture ; le retour à l'automatique se prouve par la politique relue.
/// </summary>
public sealed class NvmlVentilateurActions(INvmlNative nvml) : IVentilateurActions
{
    public NvmlVentilateurActions() : this(new NvmlNative()) { }

    public Result Poser(int gpuIndex, int pourcent)
    {
        if (pourcent is < 0 or > 100) return Echec("Fan speed out of 0..100.");
        if (!nvml.TryLireVentilateurs(gpuIndex, out var avant) || avant.Count == 0)
            return Echec("NVIDIA fans unreadable: nothing was written.");
        if (avant.Any(v => !v.Automatique))
            return Echec("A fan is already in manual mode: restore it to automatic first. Nothing was written.");
        if (avant.FirstOrDefault(v => pourcent < v.CiblePct || pourcent < v.MinPct) is { } bas)
            return Echec($"Refused: {pourcent} % is below what the card aims for now ({bas.CiblePct} %, minimum {bas.MinPct} %). " +
                         "DrDXT only raises the fan. Nothing was written.");

        foreach (var v in avant)
            if (!nvml.TryPoserVentilateur(gpuIndex, v.Index, (uint)pourcent))
                return RendreApresEchec(gpuIndex, $"Fan {v.Index} refused the speed.");

        if (!nvml.TryLireVentilateurs(gpuIndex, out var apres) || apres.Any(v => v.Automatique || v.CiblePct != (uint)pourcent))
            return RendreApresEchec(gpuIndex, "The speed read back is not the one written.");
        return Result.Ok();
    }

    public Result Rendre(int gpuIndex)
    {
        if (!nvml.TryLireVentilateurs(gpuIndex, out var avant) || avant.Count == 0)
            return Echec("NVIDIA fans unreadable: automatic mode not confirmed.");
        foreach (var v in avant) nvml.TryRendreVentilateur(gpuIndex, v.Index);
        return nvml.TryLireVentilateurs(gpuIndex, out var apres) && apres.Count > 0 && apres.All(v => v.Automatique)
            ? Result.Ok()
            : Result.Fail(ErrorCodes.TweakPartiallyApplied, "A fan did not return to automatic mode: check it in the NVIDIA app.");
    }

    private Result RendreApresEchec(int gpuIndex, string raison)
    {
        var rendu = Rendre(gpuIndex);
        return rendu.IsOk
            ? Echec(raison + " Fans returned to automatic.")
            : Result.Fail(ErrorCodes.TweakPartiallyApplied, raison + " " + rendu.Error?.Message);
    }

    private static Result Echec(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}
