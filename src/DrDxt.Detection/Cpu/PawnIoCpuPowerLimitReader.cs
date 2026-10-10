using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.GpuNative.PawnIo;

namespace DrDxt.Detection.Cpu;

/// <summary>B1 (10/10/2026) : la limite de puissance du processeur lue par PawnIO (registres bruts + décodage, copie MMIO
/// comprise), pour l'état du scan et pour le levier. Sans PawnIO, sans Intel, sans droits : « non lu », jamais un chiffre.</summary>
public sealed class PawnIoCpuPowerLimitReader : ICpuPowerLimitReader
{
    public Result<LectureLimiteCpu> LireRegistres()
    {
        var registres = PawnIoIntel.LireRegistres();
        if (registres is null)
            return Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit registers not readable (PawnIO absent, not Intel, or no rights).");
        var lu = RegistreDeLimiteCpu.Decoder(registres.RaplPowerUnit, registres.PkgPowerLimit, registres.PkgPowerInfo);
        if (lu is null)
            return Result<LectureLimiteCpu>.Fail(ErrorCodes.TweakSdkUnavailable, "CPU power limit registers are empty.");
        lu = lu with { CopieMmioPl1W = RegistreDeLimiteCpu.CopieMmioPl1W(PawnIoIntel.LireCopieMmioLimite(), registres.RaplPowerUnit) };
        return Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(
            new RegistresCpuLus(registres.RaplPowerUnit, registres.PkgPowerLimit, registres.PkgPowerInfo), lu));
    }

    /// <summary>La lecture décodée seule, pour l'état du scan ; null = non lu.</summary>
    public LimiteDePuissanceLue? Lire() => LireRegistres() is { IsOk: true, Value: { } v } ? v.Lue : null;
}
