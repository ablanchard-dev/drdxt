using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// La couture des descripteurs réconciliables.
//
// IReconcilabilityPolicy répond à « quelle surface ce tweakId écrit-il ? ».
// IReconcilableTweakCatalog répond à la question suivante : « où, sur cette surface, vit sa
// valeur actuelle, et en quel JSON canonique se sérialise-t-elle ? » : l'emplacement de lecture
// concret (ruche/chemin/nom/type du registre, nom de service, ou identité du plan
// d'alimentation) de chaque tweakId réconciliable.
//
// Source unique. Cette table n'est que des données (aucune dépendance à l'OS) : elle vit dans la
// couche pure DrDxt.Reconciler, pas dans les adaptateurs liés à l'OS. Les deux consommateurs y
// lisent :
//   * la SettingProbe : pour savoir quoi lire et comment le sérialiser,
//   * le mappeur d'ops : pour savoir quelle feuille PrivilegedOp typée réappliquer.
//
// Elle ne dépend pas exprès de DrDxt.Application : le catalogue Application indexe les leviers
// par un autre id (« latency.reflex », « fixit.power_plan ») et cache ses cibles concrètes dans
// des constantes privées ; il n'y a donc pas de dérivation propre par id, et référencer
// Application tirerait NVAPI/ADLX/D3D11 dans l'hôte en vérification seule. Les descripteurs
// ci-dessous reprennent exactement ces constantes Application (l'id canonique du réconciliateur
// correspond aux mêmes ruche/chemin/nom qu'écrit le levier), relus comme une table explicite.
// -----------------------------------------------------------------------

/// <summary>Un emplacement de lecture dans le registre pour un levier réconciliable.
/// <see cref="Kind"/> est le type de valeur qu'écrit le levier Application correspondant ; il
/// guide la lecture et la sérialisation canonique.</summary>
public sealed record RegistryDescriptor(
    RegistryHiveKind Hive,
    string Path,
    string Name,
    RegistryValueType Kind);

/// <summary>L'emplacement de lecture concret d'un seul tweakId réconciliable. Exactement un des
/// membres propres à une surface est rempli, selon <see cref="Surface"/> :
///   * Registry      → <see cref="Registry"/>,
///   * Service       → <see cref="ServiceName"/>,
///   * PowerPlan     → aucun (le GUID du plan actif se lit directement dans l'OS),
///   * ScheduledTask → aucun (aucune feuille d'op de tâche planifiée n'existe en V1).</summary>
public sealed record ReconcilableDescriptor(
    string TweakId,
    ReconcileSurface Surface,
    RegistryDescriptor? Registry = null,
    string? ServiceName = null);

/// <summary>La table fermée des descripteurs : par tweakId réconciliable, sa surface et son
/// emplacement de lecture concret. Reprend les cibles d'écriture OS des leviers Application pour
/// que la sonde lise exactement ce que le levier a écrit. La sonde et le mappeur d'ops la
/// consomment : le « où » reste en un seul endroit.</summary>
public interface IReconcilableTweakCatalog
{
    /// <summary>Le descripteur d'un tweakId réconciliable, ou null quand l'id n'est pas
    /// réconciliable / pas dans la table fermée.</summary>
    ReconcilableDescriptor? DescriptorFor(string tweakId);
}

/// <summary>Sérialisation canonique de la valeur actuelle d'un réglage réconciliable, pour
/// qu'une lecture de sonde corresponde à l'octet près au DesiredValueJson du manifeste en égalité
/// ordinale (le réconciliateur compare avec StringComparison.Ordinal). La forme reprend celle des
/// leviers Application : un DWORD/QWORD est sa chaîne décimale invariante (« 2 »), une valeur
/// chaîne est elle-même, une valeur de plan / de service est le jeton stable défini ici. Aucun
/// emballage JSON : la valeur est le jeton, exactement ce que porte le manifeste existant (par
/// exemple « 1 »/« 0 »/« 2 »).</summary>
public static class ReconcilableValueCanonicalizer
{
    /// <summary>Une valeur de registre DWORD/QWORD → sa chaîne décimale invariante. Reprend le
    /// <c>value.ToString(CultureInfo.InvariantCulture)</c> des leviers Application.</summary>
    public static string Dword(uint value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Une valeur de registre QWORD → sa chaîne décimale invariante.</summary>
    public static string Qword(ulong value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Une valeur de registre chaîne → elle-même (pas de guillemets en plus ; le manifeste
    /// porte la valeur brute).</summary>
    public static string String(string value) => value;

    /// <summary>Le GUID du plan d'alimentation actif → sa forme canonique « D » (minuscules, tirets,
    /// sans accolades) : le jeton stable que stocke le manifeste.</summary>
    public static string PowerPlan(Guid activeScheme)
        => activeScheme.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Un état de démarrage / de marche de service → un jeton stable en minuscules
    /// (« notinstalled » / « stopped » / « running »).</summary>
    public static string Service(ServiceRunState state) => state switch
    {
        ServiceRunState.NotInstalled => "notinstalled",
        ServiceRunState.Stopped => "stopped",
        ServiceRunState.Running => "running",
        _ => "unknown",
    };
}

/// <summary>L'état de marche du service que rapporte la sonde pour un levier de surface
/// Service. Un reflet neutre (aucun lien à ServiceController dans la couche pure) pour que le
/// jeton canonique soit défini ici, à côté du catalogue auquel appartient la valeur.</summary>
public enum ServiceRunState
{
    NotInstalled = 0,
    Stopped = 1,
    Running = 2,
}

/// <summary>La table fermée des descripteurs de production. Indexée par le tweakId canonique du
/// réconciliateur (les mêmes ids que <see cref="DefaultReconcilabilityPolicy"/>). Seuls les ids
/// à emplacement OS concret et lisible y sont : la politique peut associer un id à une surface
/// sans feuille lisible simple (par exemple un id « Registry » adossé à NVAPI/ADLX), et ces ids
/// sont absents exprès pour que la sonde rapporte CanProbe=false au lieu de lire le mauvais emplacement.</summary>
public sealed class DefaultReconcilableTweakCatalog : IReconcilableTweakCatalog
{
    private static readonly IReadOnlyDictionary<string, ReconcilableDescriptor> Descriptors =
        new Dictionary<string, ReconcilableDescriptor>(StringComparer.Ordinal)
        {
            // « Low Latency Mode » du pilote NVIDIA : une vraie feuille DWORD lisible
            // (NvidiaUltraLowLatencyTweak: SOFTWARE\NVIDIA Corporation\Global\NVTweak\LowLatencyMode).
            ["latency.reflex.lowLatencyMode"] = new(
                "latency.reflex.lowLatencyMode",
                ReconcileSurface.Registry,
                Registry: new RegistryDescriptor(
                    RegistryHiveKind.LocalMachine,
                    @"SOFTWARE\NVIDIA Corporation\Global\NVTweak",
                    "LowLatencyMode",
                    RegistryValueType.DWord)),

            // Plan d'alimentation actif du système : lu via PowerGetActiveScheme (SanePowerPlanTweak).
            ["fixit.sanePowerPlan"] = new(
                "fixit.sanePowerPlan",
                ReconcileSurface.PowerPlan),

            // Service AMD 3D V-Cache Performance Optimizer (X3dCcdParkingFixTweak).
            ["fixit.x3dVCacheService"] = new(
                "fixit.x3dVCacheService",
                ReconcileSurface.Service,
                ServiceName: "AMD3DVCacheSvc"),

            // HAGS : HwSchMode = 2 (activé) sur la feuille HKLM GraphicsDrivers (HagsOnTweak).
            // HKLM est la surface que le réconciliateur de démarrage LocalSystem réapplique correctement.
            ["os.hags"] = new(
                "os.hags",
                ReconcileSurface.Registry,
                Registry: new RegistryDescriptor(
                    RegistryHiveKind.LocalMachine,
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                    "HwSchMode",
                    RegistryValueType.DWord)),

            // Note : les leviers HKCU (Game DVR / mode Jeu / VRR) sont absents exprès : le réconciliateur
            // de démarrage tourne en LocalSystem, où Registry.CurrentUser est la ruche de SYSTEM ; une
            // réapplication HKCU viserait donc la mauvaise ruche. Ils persistent d'eux-mêmes au redémarrage
            // et l'App les réapplique au lancement suivant ; les réconcilier demande une réconciliation par
            // utilisateur (voir AppliedTweakManifestMapper / DefaultReconcilabilityPolicy).

            // latency.renderQueueCap / latency.vrrVsyncCap restent omis. Ils n'ont pas de feuille de
            // registre lisible : les sonder comme une feuille lirait un emplacement bidon. Leur lecture
            // existe pourtant : `NvApiLatencyReader` (Detection/Gpu/Nvapi) les lit via
            // `TryGetDrsGlobalSetting`, en NVAPI non eleve (GetMaxPreRenderedFrames, GetVsyncMode,
            // GetFrameRateLimit, GetPreferredPstate).
            //
            // La raison de l'omission est structurelle : le reconciliateur de boot
            // tourne en LocalSystem avec `DisabledNvApiActions` / `DisabledAdlxActions` : il est
            // volontairement incapable d'ecrire le GPU (posture de securite). Les sonder
            // produirait donc une derive qu'il ne peut pas corriger. Le jour ou on les veut
            // reconciliables, la question n'est pas « comment les lire » (c'est fait) mais
            // « qui a le droit de les reecrire », et la reponse ne doit pas etre le service SYSTEM.
        };

    public ReconcilableDescriptor? DescriptorFor(string tweakId)
        => Descriptors.TryGetValue(tweakId, out var d) ? d : null;
}
