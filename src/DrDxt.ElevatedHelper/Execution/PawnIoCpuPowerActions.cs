using DrDxt.Core;
using DrDxt.GpuNative.PawnIo;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>
/// B1 (10/10/2026) : la limite de puissance du processeur Intel (MSR 0x610) écrite par PawnIO, dans le helper élevé et
/// nulle part ailleurs. Le registre est relu juste avant : il doit porter exactement l'origine que l'op annonce (ou déjà
/// la valeur demandée : idempotent), sinon le monde a bougé et rien n'est écrit. Après l'écriture, le registre est relu :
/// un verrou du BIOS (bit 63) ou une carte qui refuse rendent un échec dit, jamais un succès sur parole. Volatil : au
/// redémarrage, le BIOS remet sa valeur.
/// </summary>
public sealed class PawnIoCpuPowerActions : ICpuPowerActions
{
    private readonly Func<PawnIoIntel.RegistresDeLimite?> _lire;
    private readonly Func<ulong, bool> _ecrire;

    public PawnIoCpuPowerActions() : this(PawnIoIntel.LireRegistres, PawnIoIntel.EcrireLimites) { }

    /// <summary>Couture de test : lecture et écriture du registre.</summary>
    internal PawnIoCpuPowerActions(Func<PawnIoIntel.RegistresDeLimite?> lire, Func<ulong, bool> ecrire)
    {
        _lire = lire;
        _ecrire = ecrire;
    }

    public Result SetPackagePowerLimit(ulong valeur, ulong origine)
    {
        var courant = _lire();
        if (courant is null)
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit register not readable (PawnIO absent, not an Intel CPU, or read failed).");
        if (courant.PkgPowerLimit == valeur)
            return Result.Ok();
        if (courant.PkgPowerLimit != origine)
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit register changed since it was read; nothing written.");
        if (RegistreDeLimiteCpu.Verrou(courant.PkgPowerLimit))
            return Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit register is locked by the firmware until the next boot; nothing written.");
        return _ecrire(valeur)
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed, "CPU power limit write was not read back; the board refused it.");
    }
}
