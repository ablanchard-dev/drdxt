using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// T30 — HostInstaller.BuildDefinition is a pure function producing the install descriptor
// (no real SCM / Task Scheduler call). Asserts:
//   * triggers = exactly {Boot, Logon, Daily, OnEvent(Setup)}
//   * principal is LocalSystem (V1 moat Option B: boot reapply runs in-process, bounded by
//     the signed manifest + closed allowlist — see M4-M5-moat-wiring-decision.md)
//   * no privileges beyond the SYSTEM account are requested.
// -----------------------------------------------------------------------
public sealed class HostInstallerTests
{
    [Fact]
    public void BuildDefinition_HasExactlyTheFourLeastPrivilegeTriggers()
    {
        var def = HostInstaller.BuildDefinition();

        def.Triggers.Should().BeEquivalentTo(new[]
        {
            HostTrigger.Boot,
            HostTrigger.Logon,
            HostTrigger.Daily,
            HostTrigger.OnSetupEvent,
        });
    }

    [Fact]
    public void BuildDefinition_RunsAsSystem_ForInProcessBootReapply()
    {
        var def = HostInstaller.BuildDefinition();

        def.Account.Should().Be(HostServiceAccount.LocalSystem,
            "boot reapply of HKLM/power/service requires SYSTEM — a boot service cannot raise UAC; " +
            "the bound is the signed manifest + closed allowlist, not privilege starvation (moat Option B)");
        def.RunsAsSystem.Should().BeTrue();
    }

    [Fact]
    public void BuildDefinition_RequestsNoSuperfluousPrivileges()
    {
        var def = HostInstaller.BuildDefinition();

        def.RequestedPrivileges.Should().BeEmpty(
            "the SYSTEM account already has the rights it needs; no extra privileges are requested");
        def.AllowInteractiveDesktop.Should().BeFalse("a background service never touches the interactive desktop");
        def.StartType.Should().Be(HostStartType.Automatic, "the reconciler must run unattended on boot");
    }

    [Fact]
    public void BuildDefinition_IsDeterministic_PureFunction()
    {
        var a = HostInstaller.BuildDefinition();
        var b = HostInstaller.BuildDefinition();

        a.Should().BeEquivalentTo(b, "the generator is a pure function of no external state");
    }
}
