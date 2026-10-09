using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Derives the <see cref="BootMode"/> from the already-detected <see cref="BiosInfo"/>.
/// Used to gate CSM-off advice: CSM may only be disabled when booting UEFI (§16 / B2).
/// A non-UEFI boot is treated as <see cref="BootMode.Legacy"/> — the conservative,
/// no-boot-safe assumption.
/// </summary>
public static class BootModeResolver
{
    public static BootMode Resolve(BiosInfo bios)
        => bios is null
            ? BootMode.Unknown
            : bios.IsUefi ? BootMode.Uefi : BootMode.Legacy;
}
