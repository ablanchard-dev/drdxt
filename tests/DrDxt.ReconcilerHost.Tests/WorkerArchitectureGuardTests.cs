using System.Reflection;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

// -----------------------------------------------------------------------
// Garde d'architecture : le Worker n'a aucun chemin d'écriture privilégiée direct.
//
// Toute la posture de sécurité repose sur un hôte lecteur en mode utilisateur dont la seule
// voie vers une écriture privilégiée est le helper élevé (via IElevatedReapplyClient,
// construit au fond du graphe du réconciliateur, jamais injecté dans le Worker). Cette
// garde par réflexion le fige : elle inspecte la surface de dépendances du constructeur du
// Worker et échoue si une surface d'écriture OS interdite (Microsoft.Win32.Registry,
// ServiceController, un type P/Invoke de plan d'alimentation) s'y glisse en dépendance directe.
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
        // Aucun type de surface d'écriture OS interdite n'est une dépendance directe du Worker.
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

            // Le Worker ne doit même pas tenir directement un client élevé générique : son chemin
            // d'écriture est entièrement à l'intérieur du graphe du réconciliateur. (Ceinture et bretelles.)
            t.Should().NotBe(typeof(IElevatedClient),
                "the elevated client is wired deep in the reconciler graph, not injected into the Worker");
        }
    }

    [Fact]
    public void Worker_DependencySurface_IsTheExpectedReadCoordinateOnlySet()
    {
        // Figer la surface pour qu'un changement futur ajoutant une dépendance d'écriture fasse
        // tomber ce test (l'ensemble est petit à dessein, pour rester vérifiable).
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
        // Plus profond que le constructeur : aucun champ du Worker n'est non plus un type de surface d'écriture OS interdite.
        var fieldTypes = typeof(Worker)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.FieldType.FullName ?? f.FieldType.Name);

        fieldTypes.Should().NotContain(n =>
            n.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal)
            || n.Contains("ServiceController", StringComparison.Ordinal)
            || n.Contains("PowrProf", StringComparison.Ordinal));
    }
}
