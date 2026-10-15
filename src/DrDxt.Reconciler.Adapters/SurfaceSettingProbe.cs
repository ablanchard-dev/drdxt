using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.Reconciler.Adapters;

// -----------------------------------------------------------------------
// SurfaceSettingProbe : ISettingProbe sur le catalogue de descripteurs.
//
// CanProbe(tweakId) = le catalogue a un descripteur réconciliable pour l'id et sa surface est
// lisible par cette sonde (Registry avec une feuille concrète, PowerPlan ou Service).
// ScheduledTask / Firmware / non associé → false.
//
// ReadCurrentValueAsync aiguille selon la surface et rend la valeur déjà canonicalisée par le
// lecteur brut (même forme JSON que le DesiredValueJson du manifeste), pour que la comparaison
// ordinale voulu / réel du réconciliateur tienne :
//   * Registry      → lire la feuille, sérialiser selon son type,
//   * PowerPlan      → le GUID du plan actif,
//   * Service        → le jeton d'état de marche,
//   * ScheduledTask  → Fail « not supported in V1 » (pas encore de feuille d'op de tâche planifiée),
//   * inconnu / Registry sans feuille → Fail(ProbeFailed).
//
// Les trois lectures OS brutes sont derrière des coutures internes (IRawRegistryValueReader /
// IRawPowerPlanReader / IRawServiceStateReader) : cet aiguillage et la résolution du descripteur
// sont entièrement testés avec des faux ; les vrais lecteurs sont le bord non testé.
// -----------------------------------------------------------------------
public sealed class SurfaceSettingProbe : ISettingProbe
{
    private readonly IReconcilableTweakCatalog _catalog;
    private readonly IRawRegistryValueReader _registry;
    private readonly IRawPowerPlanReader _power;
    private readonly IRawServiceStateReader _service;

    /// <summary>Constructeur de production : les vrais lecteurs OS derrière chaque surface.</summary>
    public SurfaceSettingProbe(IReconcilableTweakCatalog catalog)
        : this(catalog,
               new Win32RegistryValueReader(),
               new PInvokePowerPlanReader(),
               new ServiceControllerStateReader())
    {
    }

    internal SurfaceSettingProbe(
        IReconcilableTweakCatalog catalog,
        IRawRegistryValueReader registry,
        IRawPowerPlanReader power,
        IRawServiceStateReader service)
    {
        _catalog = catalog;
        _registry = registry;
        _power = power;
        _service = service;
    }

    public bool CanProbe(string tweakId)
    {
        var d = _catalog.DescriptorFor(tweakId);
        if (d is null)
            return false;

        return d.Surface switch
        {
            ReconcileSurface.Registry => d.Registry is not null,
            ReconcileSurface.PowerPlan => true,
            ReconcileSurface.Service => !string.IsNullOrEmpty(d.ServiceName),
            _ => false, // ScheduledTask / Firmware : non lisibles en V1.
        };
    }

    public Task<Result<string>> ReadCurrentValueAsync(string tweakId, CancellationToken ct)
    {
        var d = _catalog.DescriptorFor(tweakId);
        if (d is null)
            return Fail($"No reconcilable descriptor for tweak '{tweakId}'.");

        switch (d.Surface)
        {
            case ReconcileSurface.Registry:
                if (d.Registry is null)
                    return Fail($"Registry descriptor for '{tweakId}' has no read location.");
                return Task.FromResult(_registry.ReadCanonical(d.Registry));

            case ReconcileSurface.PowerPlan:
                return Task.FromResult(_power.ReadActiveSchemeCanonical());

            case ReconcileSurface.Service:
                if (string.IsNullOrEmpty(d.ServiceName))
                    return Fail($"Service descriptor for '{tweakId}' has no service name.");
                return Task.FromResult(_service.ReadStateCanonical(d.ServiceName));

            case ReconcileSurface.ScheduledTask:
                return Task.FromResult(Result<string>.Fail(
                    ReconcilerErrorCodes.ProbeFailed,
                    $"Scheduled-task surface for '{tweakId}' is not supported in V1 (no op leaf)."));

            default:
                return Fail($"Surface '{d.Surface}' for '{tweakId}' is not readable.");
        }
    }

    private static Task<Result<string>> Fail(string message)
        => Task.FromResult(Result<string>.Fail(ReconcilerErrorCodes.ProbeFailed, message));
}
