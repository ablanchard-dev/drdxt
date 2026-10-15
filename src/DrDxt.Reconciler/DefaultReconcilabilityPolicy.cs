namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// DefaultReconcilabilityPolicy : la table fermée de production.
//
// Elle répond à trois questions pour un tweakId : quelle surface OS il écrit (ou null s'il
// n'est pas réconciliable), s'il vise une surface protégée (refus strict), et à quel
// constructeur GPU il est lié (pour le signal de dérive du pilote). Tout est une table
// explicite : un tweakId inconnu n'est, par prudence, pas réconciliable (un id bidon ne peut
// donc jamais être réappliqué en silence).
//
// La liste noire des surfaces protégées est la garde de défense en profondeur que la boucle de
// réconciliation impose aussi : même si un futur bug en laissait entrer une dans le manifeste,
// elle serait refusée strictement. Defender / Windows Update / BITS ne sont jamais touchés.
//
// Note : la table contient les surfaces connues de la fondation. Le branchement sur tout le
// catalogue réel de leviers (pour que chaque levier livré soit traduit) arrive avec l'hôte ; un
// nouveau levier réconciliable s'ajoute ici comme une entrée de table explicite et relue.
// -----------------------------------------------------------------------
public sealed class DefaultReconcilabilityPolicy : IReconcilabilityPolicy
{
    // tweakId → surface OS. Les leviers de catégorie Firmware sont absents exprès (ils ne sont
    // pas réconciliables ; le store les rejette à l'enregistrement).
    private static readonly IReadOnlyDictionary<string, ReconcileSurface> Surfaces =
        new Dictionary<string, ReconcileSurface>(StringComparer.Ordinal)
        {
            // Réglages de latence / du pilote dans le registre (adossés au NVCP / DRS).
            ["latency.reflex.lowLatencyMode"] = ReconcileSurface.Registry,
            ["latency.renderQueueCap"] = ReconcileSurface.Registry,
            ["latency.vrrVsyncCap"] = ReconcileSurface.Registry,
            // Plan d'alimentation système (une mise à jour de fonctionnalité le remet à Équilibré).
            ["fixit.sanePowerPlan"] = ReconcileSurface.PowerPlan,
            // Service d'optimisation AMD 3D V-Cache.
            ["fixit.x3dVCacheService"] = ReconcileSurface.Service,
            // HAGS : registre HKLM (une mise à jour de fonctionnalité peut le rebasculer ; LocalSystem
            // réapplique HKLM correctement). Les leviers HKCU (Game DVR / mode Jeu / VRR) ne sont pas
            // listés : le réconciliateur de démarrage tourne en LocalSystem, où une écriture HKCU touche la
            // ruche de SYSTEM ; ils demandent donc une réconciliation par utilisateur (voir AppliedTweakManifestMapper).
            ["os.hags"] = ReconcileSurface.Registry,
        };

    // tweakId → constructeur GPU (pour le signal de dérive DriverVersion par constructeur, I8).
    private static readonly IReadOnlyDictionary<string, string> Vendors =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["latency.reflex.lowLatencyMode"] = "NVIDIA",
            ["latency.renderQueueCap"] = "NVIDIA",
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
