namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// HostInstaller (plan T30) — the pure install-definition generator.
//
// Produces the descriptor for how the reconciler host is installed as a background
// service. This is DATA only: BuildDefinition does not call the SCM or Task Scheduler —
// a separate (OS-coupled, smoke-tested) installer consumes the descriptor to actually
// register the service. Keeping the policy as a pure function lets the install posture
// be asserted in unit tests.
//
// MOAT WIRING (decision M4/M5, Option B — see M4-M5-moat-wiring-decision.md): boot
// reapply of the reconcilable tweaks (HKLM registry / power-plan / service control)
// inherently requires a SYSTEM context — a boot service cannot raise an interactive UAC.
// So the reconciler runs as LocalSystem and executes the typed ops IN-PROCESS through the
// Same closed leaf-exact OpAllowlist the elevated helper uses, driven only by the
// Ed25519-signature-verified manifest. The control is the SIGNED MANIFEST + ALLOWLIST,
// not privilege starvation: the service exposes NO generic primitive and NO open IPC
// endpoint. So the service:
//   * runs as LocalSystem (required for in-process boot reapply; constrained by the
//     signed manifest + closed allowlist, never a generic primitive),
//   * requests NO privileges beyond the SYSTEM account,
//   * never touches the interactive desktop,
//   * starts automatically (it must reconcile unattended on boot),
//   * has exactly the four triggers it needs: boot, logon, daily, on the Setup event.
//
// The low-privilege reconciler + helper-broker channel is the documented V2 hardening
// (its code is retained + tested), not the V1 default.
// -----------------------------------------------------------------------

/// <summary>The triggers the reconciler host responds to. Each maps to a Task
/// Scheduler / service trigger at install time; the set is deliberately minimal.</summary>
public enum HostTrigger
{
    /// <summary>At machine boot — a feature update most often lands across a reboot.</summary>
    Boot = 0,
    /// <summary>At user logon — a second, cheap corroborating opportunity.</summary>
    Logon = 1,
    /// <summary>Once a day — a backstop poll so drift is never missed indefinitely.</summary>
    Daily = 2,
    /// <summary>On a Windows Setup / Update completion event — the early corroborating
    /// signal (T28) so reconcile can run as soon as setup finishes.</summary>
    OnSetupEvent = 3,
}

/// <summary>The service account the host runs under. The V1 reconciler executes the
/// privileged reapply IN-PROCESS at boot (no interactive UAC is possible for a boot
/// service), so it runs as LocalSystem — constrained by the signed manifest + closed
/// allowlist, not by privilege starvation.</summary>
public enum HostServiceAccount
{
    /// <summary>A dedicated, low-privilege service account (e.g. a virtual service
    /// account / NT SERVICE\DrDxtReconciler). This is the V2 hardening (a low-priv reconciler
    /// delegating to the elevated helper-broker); not the V1 default.</summary>
    DedicatedLowPrivilege = 0,
    /// <summary>LocalSystem (SYSTEM). The V1 default: required so the boot reconciler can
    /// reapply HKLM / power-plan / service settings in-process. The bound is the signed
    /// manifest + closed leaf-exact allowlist, never a generic primitive.</summary>
    LocalSystem = 1,
}

/// <summary>Service start type. The reconciler starts automatically so it runs
/// unattended on boot.</summary>
public enum HostStartType
{
    Automatic = 0,
    Manual = 1,
    Disabled = 2,
}

/// <summary>The least-privilege install descriptor. Pure data — the OS-coupled
/// installer consumes it; nothing here touches the SCM/Task Scheduler.</summary>
public sealed record HostInstallDefinition(
    IReadOnlyList<HostTrigger> Triggers,
    HostServiceAccount Account,
    HostStartType StartType,
    bool AllowInteractiveDesktop,
    IReadOnlyList<string> RequestedPrivileges)
{
    /// <summary>True iff the descriptor would run the service as SYSTEM. True for the reconciler
    /// host: it reapplies at boot in-process (see <see cref="HostInstaller.BuildDefinition"/>).</summary>
    public bool RunsAsSystem => Account == HostServiceAccount.LocalSystem;
}

/// <summary>Builds the host's least-privilege install definition.</summary>
public static class HostInstaller
{
    /// <summary>The pure, deterministic install descriptor: the four needed triggers, the
    /// LocalSystem account (required for in-process boot reapply; bounded by the signed
    /// manifest + closed allowlist), auto-start, no interactive desktop, and NO requested
    /// privileges beyond the SYSTEM account.</summary>
    public static HostInstallDefinition BuildDefinition()
        => new(
            Triggers: new[]
            {
                HostTrigger.Boot,
                HostTrigger.Logon,
                HostTrigger.Daily,
                HostTrigger.OnSetupEvent,
            },
            Account: HostServiceAccount.LocalSystem,
            StartType: HostStartType.Automatic,
            AllowInteractiveDesktop: false,
            RequestedPrivileges: Array.Empty<string>());
}
