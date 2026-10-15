using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// BootModeResolver déduit le mode de démarrage qui filtre le conseil de couper le CSM : UEFI
/// → sûr ; hors UEFI → Legacy (prudent, sans risque de non-démarrage) ; BIOS null → Unknown.
/// </summary>
public class BootModeResolverTests
{
    [Fact]
    public void Uefi_true_resolves_uefi()
        => BootModeResolver.Resolve(new BiosInfo("1.0", null, IsUefi:true))
            .Should().Be(BootMode.Uefi);

    [Fact]
    public void Uefi_false_resolves_legacy()
        => BootModeResolver.Resolve(new BiosInfo("1.0", null, IsUefi:false))
            .Should().Be(BootMode.Legacy);

    [Fact]
    public void Null_bios_resolves_unknown()
        => BootModeResolver.Resolve(null!).Should().Be(BootMode.Unknown);
}
