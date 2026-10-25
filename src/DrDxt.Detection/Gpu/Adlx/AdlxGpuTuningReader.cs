using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu.Adlx;

// -----------------------------------------------------------------------
// AdlxGpuTuningReader : implémente IAdlxGpuTuning sur IAdlxNative.
//
// Pure logique de domaine : traduit les lectures brutes de la frontière en types du domaine
// et en codes d'erreur Result. Aucun appel natif ici : entièrement testable contre
// FakeAdlxNative sans matériel AMD.
// -----------------------------------------------------------------------
public sealed class AdlxGpuTuningReader : IAdlxGpuTuning
{
    private readonly IAdlxNative _native;

    public AdlxGpuTuningReader(IAdlxNative native) => _native = native;

    public bool IsAvailable => _native.IsAvailable;

    public bool? EstALUsine(int gpuIndex) => _native.IsAvailable && gpuIndex >= 0 ? _native.EstALUsine(gpuIndex) : null;

    public Result<int> ResolveTunableGpuIndex()
    {
        // Repris du sélecteur du banc AMD : sur une machine iGPU + dGPU, l'index ADLX 0 est souvent
        // l'iGPU non réglable ; on sonde une petite plage et on prend le premier GPU dont le réglage
        // GFX se lit proprement, la carte AMD dédiée. Échec propre si aucun n'est réglable.
        if (!_native.IsAvailable)
            return Fail("ADLX SDK not available (AMD driver / GPU absent).");

        for (int i = 0; i < AdlxGpuProbe.MaxProbeIndices; i++)
        {
            if (GetUndervoltValue(i).IsOk)
                return Result<int>.Ok(i);
        }

        return Fail("No tunable AMD GPU found (ADLX GFX tuning did not read on any GPU index).");
    }

    public Result<int> GetUndervoltValue(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("ADLX SDK not available (AMD driver / GPU absent).");
        return _native.TryReadGfx(gpuIndex, out var r)
            ? Result<int>.Ok(r.VoltMv)
            : Fail("ADLX graphics tuning read failed (GPU not supported or interface absent).");
    }

    public Result<int> GetGfxMaxFreqMhz(int gpuIndex)
    {
        // L'horloge GFX max absolue courante (MHz), la valeur que pose l'écriture de sous-tension.
        // Le levier la lit pour construire une cible absolue (courante + décalage) et relever
        // l'origine absolue pour un retour fidèle (comme l'aller-retour du % de limite de puissance).
        // Échec propre sur un mauvais index, un SDK indisponible ou une lecture ratée.
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("ADLX SDK not available (AMD driver / GPU absent).");
        return _native.TryReadGfx(gpuIndex, out var r)
            ? Result<int>.Ok(r.MaxFreqMhz)
            : Fail("ADLX graphics tuning read failed (GPU not supported or interface absent).");
    }

    public Result<int> GetPowerLimitPct(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Fail($"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Fail("ADLX SDK not available (AMD driver / GPU absent).");
        return _native.TryReadPower(gpuIndex, out var r)
            ? Result<int>.Ok(r.PowerLimitPct)
            : Fail("ADLX power-limit read failed (GPU not supported or interface absent).");
    }

    public Result<PlageDePuissanceAmd> GetPowerLimitRange(int gpuIndex)
    {
        if (gpuIndex < 0 || !_native.IsAvailable || !_native.TryReadPower(gpuIndex, out var r))
            return Result<PlageDePuissanceAmd>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX power-limit range read failed.");
        return Result<PlageDePuissanceAmd>.Ok(new PlageDePuissanceAmd(r.PowerRange.Min, r.PowerRange.Max, r.PowerRange.Step));
    }

    public Result<FanCurve> GetFanCurve(int gpuIndex)
    {
        if (gpuIndex < 0)
            return Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Invalid GPU index {gpuIndex}.");
        if (!_native.IsAvailable)
            return Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
                "ADLX SDK not available (AMD driver / GPU absent).");
        if (!_native.TryReadFan(gpuIndex, out var r))
            return Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable,
                "ADLX fan-tuning read failed (GPU not supported or interface absent).");

        // Une lecture réussie sans point n'est pas une courbe : le levier bâtirait son retour d'une courbe vide.
        if (r.Points is not { Count: > 0 } src)
            return Result<FanCurve>.Fail(ErrorCodes.TweakSdkUnavailable, "ADLX returned no fan curve point.");
        var pts = new FanCurvePoint[src.Count];
        for (int i = 0; i < src.Count; i++)
            pts[i] = new FanCurvePoint(src[i].TempC, src[i].SpeedPct);

        return Result<FanCurve>.Ok(new FanCurve(pts));
    }

    private static Result<int> Fail(string msg) =>
        Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, msg);
}
