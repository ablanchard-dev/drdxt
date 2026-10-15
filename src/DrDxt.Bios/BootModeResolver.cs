using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Déduit le <see cref="BootMode"/> du <see cref="BiosInfo"/> déjà détecté.
/// Sert à filtrer le conseil de désactiver le CSM : il ne peut l'être que sur un démarrage UEFI.
/// Un démarrage non UEFI est traité comme <see cref="BootMode.Legacy"/> : l'hypothèse prudente,
/// sans risque de non-démarrage.
/// </summary>
public static class BootModeResolver
{
    public static BootMode Resolve(BiosInfo bios)
        => bios is null
            ? BootMode.Unknown
            : bios.IsUefi ? BootMode.Uefi : BootMode.Legacy;
}
