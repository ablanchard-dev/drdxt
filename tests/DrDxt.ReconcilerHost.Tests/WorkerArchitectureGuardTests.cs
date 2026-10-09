using System.Reflection;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// T31 — architectural guard: the Worker has NO direct privileged-write path.
//
// The whole security posture relies on the host being a user-mode READER whose only
// route to a privileged write is the elevated helper (via IElevatedReapplyClient,
// constructed deep in the reconciler graph — never injected into the Worker). This
// reflection guard pins that: it inspects the Worker's constructor dependency surface
// and fails if a banned OS write-surface (Microsoft.Win32.Registry, ServiceController,
// a power-plan P/Invoke type) ever leaks in as a direct dependency.
// -----------------------------------------------------------------------
public sealed class WorkerArchitectureGuardTests
{
    private static readonly Type[] WorkerCtorParamTypes =
        typeof(Worker)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();

    [Fact]
    public void Worker_HasNoDirectPrivilegedWritePath()
    {
        // No banned OS write-surface type is a direct Worker dependency.
        foreach (var t in WorkerCtorParamTypes)
        {
            var full = t.FullName ?? t.Name;

            full.Should().NotStartWith("Microsoft.Win32.Registry",
                "the Worker must never hold a direct registry handle — writes go through the elevated helper");
            full.Should().NotContain("ServiceController",
                "the Worker must never hold a direct ServiceController — service writes go through the elevated helper");
            full.Should().NotContain("ServiceProcess",
                "the Worker must never hold a direct ServiceProcess write dependency");
            full.Should().NotContain("PowrProf",
                "the Worker must never hold a direct power-plan P/Invoke dependency");

            // The Worker must not even hold a generic elevated CLIENT directly: its write
            // path is entirely INSIDE the reconciler graph. (Belt-and-suspenders.)
            t.Should().NotBe(typeof(IElevatedClient),
                "the elevated client is wired deep in the reconciler graph, not injected into the Worker");
        }
    }

    [Fact]
    public void Worker_DependencySurface_IsTheExpectedReadCoordinateOnlySet()
    {
        // Pin the surface so a future change that adds a write dependency trips this test
        // (the set is deliberately small to keep T31 checkable).
        WorkerCtorParamTypes.Should().BeEquivalentTo(new[]
        {
            typeof(SingleFlightReconcile),
            typeof(FeatureUpdateTrigger),
            typeof(BootSessionMarker),
            typeof(IDiagnosticsLog),
            typeof(IDelay),
            typeof(HostTimingOptions),
        });
    }

    [Fact]
    public void Worker_ReferencesNoBannedWriteSurface_AcrossAllItsFields()
    {
        // Deeper than the ctor: no Worker FIELD is a banned OS write-surface type either.
        var fieldTypes = typeof(Worker)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.FieldType.FullName ?? f.FieldType.Name);

        fieldTypes.Should().NotContain(n =>
            n.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal)
            || n.Contains("ServiceController", StringComparison.Ordinal)
            || n.Contains("PowrProf", StringComparison.Ordinal));
    }
}
