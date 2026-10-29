using System.Collections.Generic;
using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.App;

// -----------------------------------------------------------------------
// AppliedTweakManifestMapper : transforme chaque levier gardé après ApplyAsync (Applied && !AutoReverted) en
// AppliedTweak du manifeste d'état voulu du reconciler. Une table fermée et relue comble deux écarts :
//   (1) les ids : « fixit.power_plan » côté Application, « fixit.sanePowerPlan » côté reconciler (ces couches ne
//       référencent pas Application, exprès) ;
//   (2) la valeur voulue : le snapshot ne porte que l'origine ; le jeton voulu (Ultra=2, GUID Performances élevées,
//       service « running ») sort du même ReconcilableValueCanonicalizer que la sonde, donc se compare octet pour octet.
// Un levier absent de la table (réglage GPU, composite dont le reconciler n'a qu'une feuille) est exclu ;
// SignedDesiredStateStore rejette aussi tout id non réconciliable. OriginalValueJson, tel que relevé, ne sert qu'à
// classer la dérive, jamais à réappliquer : le chemin de sécurité n'en dépend pas.
// -----------------------------------------------------------------------
public sealed class AppliedTweakManifestMapper
{
    /// <summary>Le jeton voulu du plan Performances élevées, dans la forme 'D' que lit la sonde du reconciler.</summary>
    public static readonly string HighPerformancePlanToken =
        ReconcilableValueCanonicalizer.PowerPlan(KnownPowerPlans.HighPerformance);

    // tweakId d'Application → (tweakId du reconciler, jeton voulu canonique), calquée sur le catalogue de descripteurs.
    private static readonly IReadOnlyDictionary<string, (string ReconcilerId, string DesiredToken)> Map =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            // SanePowerPlanTweak passe le plan actif en Performances élevées.
            ["fixit.power_plan"] = ("fixit.sanePowerPlan", HighPerformancePlanToken),

            // NvidiaUltraLowLatencyTweak écrit LowLatencyMode = Ultra (2) sur la feuille NVTweak.
            ["latency.reflex"] = ("latency.reflex.lowLatencyMode", "2"),

            // X3dCcdParkingFixTweak démarre le service AMD 3D V-Cache optimizer ; le reconciler en possède la surface.
            ["fixit.x3d_ccd"] = (
                "fixit.x3dVCacheService",
                ReconcilableValueCanonicalizer.Service(ServiceRunState.Running)),

            // HagsOnTweak écrit HwSchMode = 2 (On) en HKLM, que le reconciler de démarrage en LocalSystem sait réappliquer.
            ["latency.hagsOn"] = ("os.hags", ReconcilableValueCanonicalizer.Dword(2)),

            // Omis exprès (comme NVAPI/ADLX), pas perdus :
            //   * latency.gameDvrBackgroundOff : HKCU ; en LocalSystem, Registry.CurrentUser est la ruche de SYSTEM, la
            //     réapplication écrirait dans la mauvaise ruche. La valeur survit seule au redémarrage. Son origine entre en
            //     RevertOnly (ToRevertOnly) : la désinstallation la rend, l'App la relit au lancement
            //     (VerificationDesReglages) sans la réécrire (règle I5).
            //   * latency.gameModeOn : composite de deux feuilles HKCU, hors du modèle un id → une feuille.
            //   * latency.vrrFlipModelOn : REG_SZ fusionné (HKCU) ; l'écraser effacerait des clés de l'utilisateur (AutoHDR).
        };

    /// <summary>Le nom sous lequel le service connaît ce levier, s'il le réaffirme.</summary>
    public static string? IdDuService(string id) => Map.TryGetValue(id, out var m) ? m.ReconcilerId : null;

    /// <summary>L'id de l'App d'une entrée du manifeste : celui du levier pour un id du service, sinon le même.</summary>
    public static string IdDeLApp(string idDuManifeste)
        => Map.FirstOrDefault(m => m.Value.ReconcilerId == idDuManifeste).Key ?? idDuManifeste;

    /// <summary>Les leviers gardés que le service ne réaffirme pas (absents de la table : HKCU, fichier d'échange)
    /// entrent au manifeste en <c>RevertOnly</c>, car la désinstallation ne relit que lui : rendus, jamais réaffirmés,
    /// comme un levier gardé en jeu (TweakCandidate.Garder). Seulement une origine que la désinstallation sait rendre.</summary>
    /// <param name="pourUnEssai">Un essai en alternance rend lui-même son levier une partie sur deux : même un levier que le
    /// service réapplique n'est gardé que pour la désinstallation, jamais réappliqué.</param>
    public IReadOnlyList<AppliedTweak> ToRevertOnly(ApplyReport report, bool pourUnEssai = false)
    {
        var quand = DateTimeOffset.UtcNow;
        return report.Outcomes
            // Sans valeur posée, la désinstallation ne touche jamais la valeur (le fichier d'échange) : l'inscrire ferait
            // croire qu'elle la rend.
            .Where(o => o.Applied && !o.AutoReverted && o.Snapshot is { AppliedValueJson: { Length: > 0 } }
                        && (pourUnEssai || !Map.ContainsKey(o.TweakId)) && OrigineRendable(o.Snapshot.OriginalValueJson))
            .Select(o => new AppliedTweak(o.TweakId, o.Snapshot!.AppliedValueJson!, o.Snapshot.OriginalValueJson, quand))
            .ToList();
    }

    // Les ops que le mode de désinstallation du helper sait exécuter (même liste que OpAllowlist.ValiderOrigine).
    internal static bool OrigineRendable(string origine)
    {
        static bool Rendable(PrivilegedOp op) => op is SetRegistryValueOp or DeleteRegistryValueOp or SetActivePowerPlanOp
            or SetProcessorBoostModeOp or SetCpuPackagePowerLimitOp or SetWirelessPowerSavingOp
            or ApplyNvApiDrsSettingOp or DeleteNvApiDrsSettingOp or RunServiceControlOp
            // Les réglages de la carte que la désinstallation sait relire et rendre (jamais la sous-tension).
            or ApplyNvApiPowerLimitOp
            or ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit or AdlxTuningKind.AntiLag or AdlxTuningKind.FrameRateTarget or AdlxTuningKind.VerticalRefresh };
        try
        {
            if (PrivilegedOpCodec.Deserialize(origine) is { IsOk: true, Value: { } une }) return Rendable(une);
            return CompositeSnapshotCodec.Decode(origine) is { IsOk: true, Value: { Count: > 0 } toutes } && toutes.All(Rendable);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Une origine que la désinstallation rend sans manifeste (RendreLaCarteNvidiaAsync) : le décalage du cœur et le
    /// ventilateur NVIDIA, volatils, remis à 0 et à l'automatique. Aucune autre.</summary>
    public static bool RemiseALAutomatiqueALaDesinstallation(string origine)
    {
        try { return PrivilegedOpCodec.Deserialize(origine) is { IsOk: true, Value: ApplyNvApiClockOffsetOp or RestoreGpuFanAutoOp }; }
        catch (Exception) { return false; }
    }

    /// <summary>Les leviers d'état voulu à enregistrer, depuis les résultats gardés (Applied &amp;&amp; !AutoReverted).
    /// Sans snapshot, sans correspondance ou non réconciliables : exclus.</summary>
    public IReadOnlyList<AppliedTweak> ToDesiredTweaks(ApplyReport report)
    {
        // Un seul horodatage pour le lot : le CommittedAt du manifeste fait foi, le reconciler ignore l'AppliedAt.
        var appliedAt = DateTimeOffset.UtcNow;

        var tweaks = new List<AppliedTweak>();
        foreach (var outcome in report.Outcomes)
        {
            if (!outcome.Applied || outcome.AutoReverted)
                continue;

            // Pas de snapshot ⇒ pas de valeur d'origine honnête à noter.
            if (outcome.Snapshot is null)
                continue;

            if (!Map.TryGetValue(outcome.TweakId, out var mapping))
                continue;

            tweaks.Add(new AppliedTweak(
                mapping.ReconcilerId,
                mapping.DesiredToken,
                outcome.Snapshot.OriginalValueJson,
                appliedAt,
                // Les valeurs posées de toutes les feuilles : le jeton ne nomme que celle que le reconciler réaffirme. Un
                // composite (X3D : service + CppcEnable + Game Bar) sans elles ne serait pas jugé rendable, et le
                // tout-ou-rien de la désinstallation le bloquerait entier.
                outcome.Snapshot.AppliedValueJson));
        }

        return tweaks;
    }
}
