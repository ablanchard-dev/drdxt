using System.Collections.Generic;
using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.App;

// -----------------------------------------------------------------------
// AppliedTweakManifestMapper : le pont, côté App, entre une application validée et le
// manifeste d'état voulu du reconciler (la persistance).
//
// Le pipeline d'application mesure déjà et annule tout seul les régressions : les leviers
// encore gardés après ApplyAsync (Applied && !AutoReverted) sont l'ensemble validé. Ce
// mapper transforme chaque résultat gardé en AppliedTweak du reconciler, pour que l'App
// puisse les enregistrer.
//
// Il comble deux vrais écarts avec une table fermée, explicite et relue :
//
//   (1) L'espace des ids. Le catalogue d'Application nomme les leviers par des ids comme
//       « fixit.power_plan » / « latency.reflex » ; la politique et le catalogue de
//       descripteurs du reconciler utilisent « fixit.sanePowerPlan » /
//       « latency.reflex.lowLatencyMode » (voir DefaultReconcilabilityPolicy /
//       DefaultReconcilableTweakCatalog : ces couches ne référencent pas Application,
//       exprès). Cette table est l'unique correspondance, relue, entre les deux espaces.
//
//   (2) La valeur voulue. Le TweakSnapshot de l'ApplyReport ne porte que la valeur
//       d'origine (l'opération inverse) ; le jeton voulu canonique que le reconciler
//       réapplique est connu par levier (Ultra=2, le GUID du plan Performances élevées,
//       service « running ») et produit ici par le même ReconcilableValueCanonicalizer que
//       celui de la sonde : un jeton voulu enregistré correspond octet pour octet à une
//       lecture ultérieure de la sonde (comparaison ordinale).
//
// Portée : seuls sont émis les leviers qui ont une surface réconciliable côté OS et un
// jeton voulu connu. Un levier gardé absent de la table (réglage GPU, ou composite dont le
// reconciler ne possède qu'une feuille) est exclu : le manifeste ne contient que de l'état
// voulu validé et réconciliable. Double sécurité avec SignedDesiredStateStore, qui rejette
// de son côté tout id non réconciliable à l'enregistrement.
//
// Réserve sur la valeur d'origine : OriginalValueJson est noté depuis le snapshot gardé,
// tel que relevé (la charge de l'opération inverse), trace honnête de l'état d'avant. Le
// reconciler ne s'en sert que pour classer la dérive (voulue par l'utilisateur ou remise à
// zéro par Windows), jamais pour réappliquer ; le jeton voulu est le champ qui compte, et il
// est exact. On pourra rendre canonique le jeton d'origine pour qu'il corresponde à la sonde
// si le classement de la dérive en a besoin ; le chemin de sécurité et de réapplication n'en
// dépend pas.
// -----------------------------------------------------------------------
public sealed class AppliedTweakManifestMapper
{
    /// <summary>Le jeton voulu canonique du plan d'alimentation actif, une fois que
    /// SanePowerPlanTweak l'a passé en Performances élevées : la même forme 'D' que celle lue
    /// par la sonde du reconciler (voulu enregistré == courant sondé en comparaison ordinale).</summary>
    public static readonly string HighPerformancePlanToken =
        ReconcilableValueCanonicalizer.PowerPlan(KnownPowerPlans.HighPerformance);

    // tweakId d'Application → (tweakId du reconciler, jeton voulu canonique). Une table fermée
    // et relue, calquée sur les emplacements de lecture du catalogue de descripteurs du reconciler.
    private static readonly IReadOnlyDictionary<string, (string ReconcilerId, string DesiredToken)> Map =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            // SanePowerPlanTweak passe le plan actif en Performances élevées.
            ["fixit.power_plan"] = ("fixit.sanePowerPlan", HighPerformancePlanToken),

            // NvidiaUltraLowLatencyTweak écrit LowLatencyMode = Ultra (2) sur la feuille NVTweak.
            ["latency.reflex"] = ("latency.reflex.lowLatencyMode", "2"),

            // X3dCcdParkingFixTweak démarre le service AMD 3D V-Cache optimizer. Le reconciler possède
            // la surface du service (sa seule surface volatile côté OS) ; le jeton voulu est l'état
            // canonique « running ».
            ["fixit.x3d_ccd"] = (
                "fixit.x3dVCacheService",
                ReconcilableValueCanonicalizer.Service(ServiceRunState.Running)),

            // HagsOnTweak écrit HwSchMode = 2 (On) sur la feuille HKLM GraphicsDrivers.
            // HKLM est la surface que le reconciler de démarrage, en LocalSystem, réapplique correctement.
            ["latency.hagsOn"] = ("os.hags", ReconcilableValueCanonicalizer.Dword(2)),

            // Volontairement pas réconciliés par le reconciler de démarrage en LocalSystem :
            //   * latency.gameDvrBackgroundOff : une feuille HKCU. Le reconciler réapplique dans son
            //     propre processus, en LocalSystem (pas d'UAC au démarrage), où Registry.CurrentUser est
            //     la ruche de SYSTEM et non celle de l'utilisateur connecté : une réapplication HKCU
            //     écrirait dans la mauvaise ruche (le reconciler de démarrage ne couvre que HKLM, le plan
            //     d'alimentation et les services). La valeur survit d'elle-même au redémarrage.
            //
            //     L'App ne le reapplique pas au lancement suivant : `App.OnStartup` construit l'injection, affiche
            //     la fenetre, et c'est tout. Aucune reverification, aucune reapplication. Le filet
            //     invoque n'existe pas : si un tiers (autre « optimiseur », mise a jour de
            //     fonctionnalite Windows, l'utilisateur) defait ce reglage, rien ne le remarque,
            //     alors que le produit la promet « persistante ».
            //     Ce qui manque vraiment, dans l'ordre : (1) un enregistrement par utilisateur de
            //     ce qui a ete applique (le manifeste signe ne les porte pas, justement parce
            //     qu'ils sont exclus ici) ; (2) une detection de derive au lancement ; (3) une
            //     notification via `IReconcilerNotifier`, qui notifie sans ecraser (regle I5).
            //     La reapplication devra passer par le levier lui-meme, pas par une op typee :
            //     le tweak sait deja gerer le composite a deux feuilles et la fusion du REG_SZ.
            //   * latency.gameModeOn : composite (deux feuilles HKCU), plus le souci HKCU ci-dessus ;
            //     le modèle un id → une feuille ne sait pas l'exprimer. Il faudrait une réconciliation
            //     sur plusieurs feuilles.
            //   * latency.vrrFlipModelOn : un REG_SZ fusionné (HKCU) ; une réapplication naïve qui
            //     écrase effacerait des clés de l'utilisateur (par exemple AutoHDR). Il faudrait une
            //     réapplication par utilisateur qui sache fusionner.
            // Comme les omissions existantes liées à NVAPI/ADLX : omis exprès, pas perdus.
        };

    /// <summary>Le nom sous lequel le service connaît ce levier, s'il le réaffirme.</summary>
    public static string? IdDuService(string id) => Map.TryGetValue(id, out var m) ? m.ReconcilerId : null;

    /// <summary>Les leviers gardés que le service ne réaffirme pas (absents de la table
    /// ci-dessus : les leviers HKCU, le fichier d'échange) n'entraient pas du tout au manifeste, et la désinstallation
    /// ne relit que lui : elle laissait leur réglage en place. Leur origine y entre en <c>RevertOnly</c> (rendue à la
    /// désinstallation, jamais réaffirmée), comme le fait déjà un levier gardé en jeu (TweakCandidate.Garder).
    /// Seulement une origine que la désinstallation sait rendre : un réglage de carte graphique n'y entre pas.</summary>
    /// <param name="pourUnEssai">Un essai en alternance rend lui-même son levier une partie sur deux : même un levier que le
    /// service réapplique n'est gardé que pour la désinstallation, jamais réappliqué.</param>
    public IReadOnlyList<AppliedTweak> ToRevertOnly(ApplyReport report, bool pourUnEssai = false)
    {
        var quand = DateTimeOffset.UtcNow;
        return report.Outcomes
            // Sans valeur posée, la désinstallation ne sait pas si la valeur est encore
            // celle de DrDXT et n'y touche jamais (le fichier d'échange) : l'inscrire ferait croire qu'elle la rend.
            .Where(o => o.Applied && !o.AutoReverted && o.Snapshot is { AppliedValueJson: { Length: > 0 } }
                        && (pourUnEssai || !Map.ContainsKey(o.TweakId)) && OrigineRendable(o.Snapshot.OriginalValueJson))
            .Select(o => new AppliedTweak(o.TweakId, o.Snapshot!.AppliedValueJson!, o.Snapshot.OriginalValueJson, quand))
            .ToList();
    }

    // Les ops que le mode de désinstallation du helper sait exécuter (même liste que OpAllowlist.ValiderOrigine).
    private static bool OrigineRendable(string origine)
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

    /// <summary>Les leviers d'état voulu, validés et réconciliables, à enregistrer, construits à
    /// partir des résultats gardés (Applied &amp;&amp; !AutoReverted) d'une application réussie.
    /// Les résultats annulés automatiquement, sans snapshot, ou sans correspondance ou non
    /// réconciliables sont exclus.</summary>
    public IReadOnlyList<AppliedTweak> ToDesiredTweaks(ApplyReport report)
    {
        // ApplyOutcome ne porte pas d'horodatage par levier ; l'AppliedTweak du manifeste en veut
        // un. On prend un seul horodatage, celui de l'enregistrement, pour tout le lot : le
        // CommittedAt du manifeste fait foi, et le reconciler ne règle pas son comportement sur
        // l'AppliedAt de chaque levier.
        var appliedAt = DateTimeOffset.UtcNow;

        var tweaks = new List<AppliedTweak>();
        foreach (var outcome in report.Outcomes)
        {
            // Seuls les leviers appliqués et non annulés automatiquement sont validés et gardés.
            if (!outcome.Applied || outcome.AutoReverted)
                continue;

            // Pas de snapshot ⇒ pas de valeur d'origine honnête à noter ⇒ on passe (par prudence).
            if (outcome.Snapshot is null)
                continue;

            // Seuls les ids qui ont une correspondance connue côté reconciler et un jeton voulu sont enregistrés.
            if (!Map.TryGetValue(outcome.TweakId, out var mapping))
                continue;

            tweaks.Add(new AppliedTweak(
                mapping.ReconcilerId,
                mapping.DesiredToken,
                outcome.Snapshot.OriginalValueJson,
                appliedAt,
                // L'origine complete des composites. Le token ci-dessus ne nomme qu'une
                // feuille (celle que le reconciliateur reaffirme). Un levier composite (X3D : service +
                // CppcEnable + Game Bar) en pose plusieurs : sans ce champ, la desinstallation ne savait
                // pas quelle valeur DrDXT avait mise sur les deux autres, donc elle ne pouvait pas les
                // juger rendables, et le tout-ou-rien bloquait le composite entier, pour toujours.
                // Le tweak connaissait deja ses valeurs posees (TweakSnapshot.AppliedValueJson) : on les
                // inscrit telles quelles, sans les recalculer ici.
                outcome.Snapshot.AppliedValueJson));
        }

        return tweaks;
    }
}
