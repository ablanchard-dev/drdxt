namespace DrDxt.Reconciler;

// DefaultReconcilabilityPolicy : la table fermée de production. Pour un tweakId : sa surface OS (null = non
// réconciliable, le cas de tout id inconnu), s'il vise une surface protégée (refus strict), son constructeur GPU (signal
// de dérive du pilote). La liste noire des surfaces protégées double la garde de la boucle : même entrée par un bug dans
// le manifeste, une surface protégée est refusée ; Defender / Windows Update / BITS ne sont jamais touchés. Un nouveau
// levier réconciliable s'ajoute ici comme une entrée explicite et relue.
public sealed class DefaultReconcilabilityPolicy : IReconcilabilityPolicy
{
    // tweakId → surface OS. Les leviers de catégorie Firmware sont absents exprès (ils ne sont
    // pas réconciliables ; le store les rejette à l'enregistrement).
    private static readonly IReadOnlyDictionary<string, ReconcileSurface> Surfaces =
        new Dictionary<string, ReconcileSurface>(StringComparer.Ordinal)
        {
            // Réglage de latence du pilote dans le registre. latency.renderQueueCap et latency.vrrVsyncCap n'y sont pas :
            // portés par DRS, ils n'ont pas de feuille que le service sache relire ou réécrire (ReconcilableTweakCatalog).
            ["latency.reflex.lowLatencyMode"] = ReconcileSurface.Registry,
            // Plan d'alimentation système (une mise à jour de fonctionnalité le remet à Équilibré).
            ["fixit.sanePowerPlan"] = ReconcileSurface.PowerPlan,
            // Service d'optimisation AMD 3D V-Cache.
            ["fixit.x3dVCacheService"] = ReconcileSurface.Service,
            // HAGS : registre HKLM (une mise à jour de fonctionnalité peut le rebasculer). Pas les leviers HKCU (Game DVR,
            // mode Jeu, VRR) : en LocalSystem, HKCU est la ruche de SYSTEM ; voir AppliedTweakManifestMapper.
            ["os.hags"] = ReconcileSurface.Registry,
        };

    // tweakId → constructeur GPU (pour le signal de dérive DriverVersion par constructeur, I8).
    private static readonly IReadOnlyDictionary<string, string> Vendors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["latency.reflex.lowLatencyMode"] = "NVIDIA",
        };

    // Surfaces protégées : jamais réconciliées, refusées strictement.
    private static readonly IReadOnlySet<string> Protected =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "service.WinDefend",
            "service.wuauserv",   // Windows Update
            "service.BITS",
            "service.WdNisSvc",   // inspection réseau de Defender
            "service.SecurityHealthService",
        };

    public ReconcileSurface? SurfaceFor(string tweakId)
        => Surfaces.TryGetValue(tweakId, out var s) ? s : null;

    public bool IsProtected(string tweakId) => Protected.Contains(tweakId);

    public string? VendorFor(string tweakId)
        => Vendors.TryGetValue(tweakId, out var v) ? v : null;
}
