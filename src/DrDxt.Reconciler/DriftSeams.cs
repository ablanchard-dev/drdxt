using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Read seams the reconciler uses — all user-mode, NO admin, never write
// (writes go through IElevatedClient with typed ops). Production adapters
// back these with the existing App readers (IRegistryReader / IPowerPlanReader /
// IServiceStateReader / IHardwareDetector); wiring those adapters is the NEXT
// increment. Unit tests use in-memory fakes.
// -----------------------------------------------------------------------

/// <summary>Reads the CURRENT serialized value of a reconcilable tweak (same JSON
/// shape as <see cref="AppliedTweak.DesiredValueJson"/>) so the reconciler can
/// compare desired-vs-actual and confirm a write took effect. Plan §1.</summary>
public interface ISettingProbe
{
    /// <summary>True when this probe knows how to read the given tweak's surface.</summary>
    bool CanProbe(string tweakId);

    /// <summary>Reads the current serialized value. Unreadable → Fail (no guess).</summary>
    Task<Result<string>> ReadCurrentValueAsync(string tweakId, CancellationToken ct);
}

/// <summary>Reads the Windows feature-update signal — CurrentBuild + UBR from
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion. A change since the value
/// captured at apply time is the PRIMARY, reliable WindowsReset signal (I8 / plan §4).</summary>
public interface IOsBuildProbe
{
    /// <summary>Returns a stable token like "22631.3447" (CurrentBuild.UBR).
    /// Unreadable → Fail.</summary>
    Result<string> ReadCurrentBuild();
}

/// <summary>Reads the installed driver version for a GPU vendor (NVIDIA / AMD),
/// from Win32_VideoController / vendor registry. A change since apply time is the
/// per-vendor driver-update drift signal (I8 / plan T8b/T8c). Vendor is matched
/// case-insensitively against GpuInfo.Vendor.</summary>
public interface IGpuDriverProbe
{
    /// <summary>The current DriverVersion for the adapter whose vendor matches
    /// <paramref name="vendor"/>, or Ok(null) if no such adapter is present.
    /// Unreadable → Fail.</summary>
    Result<string?> ReadDriverVersion(string vendor);
}

/// <summary>Injectable UTC clock — the reconciler computes boot-streak windows and
/// timestamps against this, never DateTimeOffset.UtcNow directly, so tests drive
/// time deterministically (mirrors the ElevatedHelper IClock convention).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock — the real UTC wall-clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
