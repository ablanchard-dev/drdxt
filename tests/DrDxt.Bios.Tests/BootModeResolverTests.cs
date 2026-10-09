using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// BootModeResolver derives the boot mode that gates CSM-off advice (§16 / B2): UEFI
/// → safe; non-UEFI → Legacy (conservative, no-boot-safe); null BIOS → Unknown.
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
