using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrDxt.Core;

// -----------------------------------------------------------------------
// PrivilegedOpType : le discriminant fermé de la forme filaire d'un PrivilegedOp.
// Chaque membre correspond à exactement une feuille scellée. Le codec fait un switch
// explicite sur cette énumération ; il ne résout jamais un type par son nom.
//
// C'est le reflet sur le fil de l'allowlist des feuilles PrivilegedOp. Il ne grandit
// qu'en ajoutant un membre ici et un bras correspondant dans les deux switch du codec.
// -----------------------------------------------------------------------
public enum PrivilegedOpType
{
    Ping = 0,
    SetRegistryValue = 1,
    DeleteRegistryValue = 2,
    SetActivePowerPlan = 3,
    CreateRestorePoint = 4,
    ApplyNvApiClockOffset = 5,
    ApplyNvApiPowerLimit = 6,
    ApplyNvApiDrsSetting = 7,
    ApplyAdlxTuning = 8,
    RunServiceControl = 9,
    InstallPresentMonService = 10,
    ApplyGpuFanCurve = 11,
    EnableSystemRestore = 12,
    EstablishLease = 13,
    RenewLease = 14,
    // Sans ces entrees, Serialize rendrait le discriminant -1 et l'op ne traverserait pas l'IPC :
    // le trajet vers le BIOS serait inerte des que le helper tourne hors du processus, c'est-a-dire
    // en production. Les valeurs sont ajoutees en fin : ne jamais renumeroter, un ancien client et
    // un nouveau serveur partagent ce fil.
    RebootToFirmwareSetup = 15,
    CancelPendingFirmwareBoot = 16,
    // Lecture seule. Il n'existe deliberement aucune op d'ecriture firmware dans cette
    // enumeration fermee : ecrire au mauvais offset d'un varstore peut rendre une carte non
    // demarrable, et l'offset depend du couple (modele x version de BIOS). Appendue, comme
    // les precedentes : ne jamais renumeroter.
    ReadFirmwareVariable = 17,
    // L'inverse d'une ecriture au profil d'un jeu. Appendue,
    // comme les precedentes : ne jamais renumeroter.
    DeleteNvApiDrsSetting = 18,
    // Le manifeste signe, ecrit par le helper eleve. Appendue : ne jamais renumeroter.
    CommitManifest = 19,
    // Le plafond d'envoi QoS et son inverse. Appendues : ne jamais renumeroter.
    ApplyUploadCap = 20,
    RemoveUploadCap = 21,
    // Retirer / reposer un element BCD de l'ensemble ferme. Appendues : ne jamais renumeroter.
    DeleteBcdValue = 22,
    SetBcdValue = 23,
    // Lecture de l'etat du magasin de composants (DISM /CheckHealth). Appendue : ne jamais renumeroter.
    ReadComponentStoreHealth = 24,
    // La protection du ping retenue pour le service, et son inverse. Appendues : ne jamais renumeroter.
    RetainServiceUploadCap = 25,
    ForgetServiceUploadCap = 26,
    // Le mode de boost secteur d'un plan. Appendue : ne jamais renumeroter.
    SetProcessorBoostMode = 27,
    // Les ventilateurs rendus à l'automatique. Appendue : ne jamais renumeroter.
    RestoreGpuFanAuto = 28,
    // La limite de puissance du processeur par PawnIO. Appendue : ne jamais renumeroter.
    SetCpuPackagePowerLimit = 29,
    // L'economie d'energie de la carte sans fil (secteur) d'un plan. Appendue : ne jamais renumeroter.
    SetWirelessPowerSaving = 30,
    // Le décalage de tension des cœurs et du cache. Appendue : ne jamais renumeroter.
    SetCpuVoltageOffset = 31,
    // Un programme au démarrage coupé ou réactivé (StartupApproved). Appendue : ne jamais renumeroter.
    SetStartupApproval = 32,
    // Le test memoire au prochain demarrage, et son annulation. Appendues : ne jamais renumeroter.
    ScheduleMemoryDiagnostic = 40,
    CancelMemoryDiagnostic = 41,
    // Relancer un peripherique, retirer un vieux pilote d'affichage exporte, le remettre. Appendues : ne jamais renumeroter.
    RestartDevice = 42,
    RemoveDriverPackage = 43,
    RestoreDriverPackage = 44,
    PoseEnAttenteDOrigine = 45,
    AcquitterEcriture = 46,
    // La pause du maintien posée ou levée par le joueur. Appendue : ne jamais renumeroter.
    SetMaintenancePause = 47,
}

// -----------------------------------------------------------------------
// PrivilegedOpCodec : sérialise et désérialise un PrivilegedOp en JSON via une
// énumération discriminante bornée + un switch explicite.
//
// Sécurité (risque : JSON polymorphe = RCE) :
//   * pas de TypeNameHandling de Newtonsoft, pas de polymorphisme System.Text.Json par
//     discriminant vers des types arbitraires, pas de Type.GetType. Le fil ne porte
//     qu'un discriminant numérique « op » ; un discriminant inconnu ou hors plage ne
//     donne rien, il échoue proprement
//     (Result.Fail("HELPER.NOT_ALLOWLISTED", ...)).
//   * La désérialisation est totale : toute charge malformée ou hostile rend un Result
//     en échec. Elle ne lève jamais et n'instancie jamais un type arbitraire.
// -----------------------------------------------------------------------
public static class PrivilegedOpCodec
{
    // Le nom de la propriété discriminante sur le fil. Volontairement pas « $type »
    // (qui évoque le type-name handling) : c'est un simple champ d'énumération numérique.
    private const string Discriminator = "op";

    private static readonly JsonSerializerOptions Options = new()
    {
        // Les énumérations voyagent en valeur entière (le discriminant fermé), jamais par
        // nom. Les membres inconnus sont tolérés dans la charge (compatibilité future) mais
        // ne peuvent jamais choisir un type : seul le switch explicite le peut.
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Sérialise une feuille PrivilegedOp connue en objet JSON discriminé.
    /// Rend Fail(HELPER.NOT_ALLOWLISTED) si la feuille n'est pas dans l'ensemble
    /// fermé (prudence ; inatteignable pour la hiérarchie scellée).</summary>
    public static Result<string> Serialize(PrivilegedOp op)
    {
        if (op is null)
            return Result<string>.Fail(ErrorCodes.NotAllowlisted, "Null privileged op.");

        var (type, payload) = op switch
        {
            PingOp p => (PrivilegedOpType.Ping, (object)p),
            SetRegistryValueOp r => (PrivilegedOpType.SetRegistryValue, r),
            DeleteRegistryValueOp r => (PrivilegedOpType.DeleteRegistryValue, r),
            SetActivePowerPlanOp pp => (PrivilegedOpType.SetActivePowerPlan, pp),
            SetProcessorBoostModeOp bm => (PrivilegedOpType.SetProcessorBoostMode, bm),
            SetCpuPackagePowerLimitOp cl => (PrivilegedOpType.SetCpuPackagePowerLimit, cl),
            SetCpuVoltageOffsetOp cv => (PrivilegedOpType.SetCpuVoltageOffset, cv),
            SetWirelessPowerSavingOp ws => (PrivilegedOpType.SetWirelessPowerSaving, ws),
            SetStartupApprovalOp sa => (PrivilegedOpType.SetStartupApproval, sa),
            RestoreGpuFanAutoOp rf => (PrivilegedOpType.RestoreGpuFanAuto, rf),
            CreateRestorePointOp rp => (PrivilegedOpType.CreateRestorePoint, rp),
            ApplyNvApiClockOffsetOp c => (PrivilegedOpType.ApplyNvApiClockOffset, c),
            ApplyNvApiPowerLimitOp pl => (PrivilegedOpType.ApplyNvApiPowerLimit, pl),
            ApplyNvApiDrsSettingOp d => (PrivilegedOpType.ApplyNvApiDrsSetting, d),
            ApplyAdlxTuningOp a => (PrivilegedOpType.ApplyAdlxTuning, a),
            RunServiceControlOp s => (PrivilegedOpType.RunServiceControl, s),
            InstallPresentMonServiceOp ip => (PrivilegedOpType.InstallPresentMonService, ip),
            ApplyGpuFanCurveOp f => (PrivilegedOpType.ApplyGpuFanCurve, f),
            EnableSystemRestoreOp e => (PrivilegedOpType.EnableSystemRestore, e),
            EstablishLeaseOp el => (PrivilegedOpType.EstablishLease, el),
            RenewLeaseOp rl => (PrivilegedOpType.RenewLease, rl),
            RebootToFirmwareSetupOp fw => (PrivilegedOpType.RebootToFirmwareSetup, fw),
            CancelPendingFirmwareBootOp cf => (PrivilegedOpType.CancelPendingFirmwareBoot, cf),
            ReadFirmwareVariableOp rfv => (PrivilegedOpType.ReadFirmwareVariable, rfv),
            DeleteNvApiDrsSettingOp dd => (PrivilegedOpType.DeleteNvApiDrsSetting, dd),
            CommitManifestOp cm => (PrivilegedOpType.CommitManifest, cm),
            ApplyUploadCapOp uc => (PrivilegedOpType.ApplyUploadCap, uc),
            RemoveUploadCapOp ru => (PrivilegedOpType.RemoveUploadCap, ru),
            RetainServiceUploadCapOp rs => (PrivilegedOpType.RetainServiceUploadCap, rs),
            ForgetServiceUploadCapOp fs => (PrivilegedOpType.ForgetServiceUploadCap, fs),
            DeleteBcdValueOp db => (PrivilegedOpType.DeleteBcdValue, db),
            SetBcdValueOp sb => (PrivilegedOpType.SetBcdValue, sb),
            ReadComponentStoreHealthOp ch => (PrivilegedOpType.ReadComponentStoreHealth, ch),
            ScheduleMemoryDiagnosticOp sm => (PrivilegedOpType.ScheduleMemoryDiagnostic, sm),
            CancelMemoryDiagnosticOp am => (PrivilegedOpType.CancelMemoryDiagnostic, am),
            RestartDeviceOp rd => (PrivilegedOpType.RestartDevice, rd),
            RemoveDriverPackageOp rp2 => (PrivilegedOpType.RemoveDriverPackage, rp2),
            RestoreDriverPackageOp rr => (PrivilegedOpType.RestoreDriverPackage, rr),
            PoseEnAttenteDOrigineOp pe => (PrivilegedOpType.PoseEnAttenteDOrigine, pe),
            AcquitterEcritureOp ae => (PrivilegedOpType.AcquitterEcriture, ae),
            SetMaintenancePauseOp mp => (PrivilegedOpType.SetMaintenancePause, mp),
            _ =>((PrivilegedOpType)(-1), (object?)null),
        };

        if (payload is null)
            return Result<string>.Fail(ErrorCodes.NotAllowlisted, "Unknown privileged op type.");

        try
        {
            // Écrit le discriminant, puis fusionne les champs de la feuille dans le même
            // objet. On construit un nœud JSON pour que le discriminant et la charge de la
            // feuille partagent un objet plat (pas d'enveloppe polymorphe imbriquée).
            using var doc = JsonSerializer.SerializeToDocument(payload, payload.GetType(), Options);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber(Discriminator, (int)type);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WriteEndObject();
            }
            return Result<string>.Ok(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (Exception)
        {
            return Result<string>.Fail(ErrorCodes.HelperOpFailed, "Failed to serialize privileged op.");
        }
    }

    /// <summary>Désérialise un objet JSON discriminé en sa feuille fermée, par un switch
    /// explicite sur le discriminant borné. Totale : toute charge malformée, inconnue ou
    /// hors plage échoue proprement (jamais d'exception, jamais de type résolu).</summary>
    public static Result<PrivilegedOp> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<PrivilegedOp>.Fail(ErrorCodes.NotAllowlisted, "Empty privileged-op payload.");

        JsonElement root;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
            root = doc.RootElement;
        }
        catch (JsonException)
        {
            doc?.Dispose();
            return Result<PrivilegedOp>.Fail(ErrorCodes.NotAllowlisted, "Malformed privileged-op JSON.");
        }

        try
        {
            if (root.ValueKind != JsonValueKind.Object)
                return Result<PrivilegedOp>.Fail(ErrorCodes.NotAllowlisted, "Privileged-op payload is not an object.");

            // Le discriminant doit être un membre numérique de l'énumération fermée.
            if (!root.TryGetProperty(Discriminator, out var disc)
                || disc.ValueKind != JsonValueKind.Number
                || !disc.TryGetInt32(out var rawType)
                || !Enum.IsDefined(typeof(PrivilegedOpType), rawType))
            {
                return Result<PrivilegedOp>.Fail(
                    ErrorCodes.NotAllowlisted, "Missing or unknown privileged-op discriminant.");
            }

            var rawJson = root.GetRawText();

            // Switch explicite : le seul endroit où un discriminant devient un type. Pas de
            // réflexion, pas de Type.GetType, pas de résolution polymorphe par nom.
            PrivilegedOp? op = (PrivilegedOpType)rawType switch
            {
                PrivilegedOpType.Ping => new PingOp(),
                PrivilegedOpType.SetRegistryValue => Read<SetRegistryValueOp>(rawJson),
                PrivilegedOpType.DeleteRegistryValue => Read<DeleteRegistryValueOp>(rawJson),
                PrivilegedOpType.SetActivePowerPlan => Read<SetActivePowerPlanOp>(rawJson),
                PrivilegedOpType.SetProcessorBoostMode => Read<SetProcessorBoostModeOp>(rawJson),
                PrivilegedOpType.SetCpuPackagePowerLimit => Read<SetCpuPackagePowerLimitOp>(rawJson),
                PrivilegedOpType.SetCpuVoltageOffset => Read<SetCpuVoltageOffsetOp>(rawJson),
                PrivilegedOpType.SetWirelessPowerSaving => Read<SetWirelessPowerSavingOp>(rawJson),
                PrivilegedOpType.SetStartupApproval => Read<SetStartupApprovalOp>(rawJson),
                PrivilegedOpType.RestoreGpuFanAuto => Read<RestoreGpuFanAutoOp>(rawJson),
                PrivilegedOpType.CreateRestorePoint => Read<CreateRestorePointOp>(rawJson),
                PrivilegedOpType.ApplyNvApiClockOffset => Read<ApplyNvApiClockOffsetOp>(rawJson),
                PrivilegedOpType.ApplyNvApiPowerLimit => Read<ApplyNvApiPowerLimitOp>(rawJson),
                PrivilegedOpType.ApplyNvApiDrsSetting => Read<ApplyNvApiDrsSettingOp>(rawJson),
                PrivilegedOpType.ApplyAdlxTuning => Read<ApplyAdlxTuningOp>(rawJson),
                PrivilegedOpType.RunServiceControl => Read<RunServiceControlOp>(rawJson),
                PrivilegedOpType.InstallPresentMonService => new InstallPresentMonServiceOp(),
                PrivilegedOpType.ApplyGpuFanCurve => Read<ApplyGpuFanCurveOp>(rawJson),
                PrivilegedOpType.EnableSystemRestore => Read<EnableSystemRestoreOp>(rawJson),
                PrivilegedOpType.EstablishLease => Read<EstablishLeaseOp>(rawJson),
                PrivilegedOpType.RenewLease => Read<RenewLeaseOp>(rawJson),
                PrivilegedOpType.RebootToFirmwareSetup => Read<RebootToFirmwareSetupOp>(rawJson),
                PrivilegedOpType.CancelPendingFirmwareBoot => Read<CancelPendingFirmwareBootOp>(rawJson),
                PrivilegedOpType.ReadFirmwareVariable => Read<ReadFirmwareVariableOp>(rawJson),
                PrivilegedOpType.DeleteNvApiDrsSetting => Read<DeleteNvApiDrsSettingOp>(rawJson),
                PrivilegedOpType.CommitManifest => Read<CommitManifestOp>(rawJson),
                PrivilegedOpType.ApplyUploadCap => Read<ApplyUploadCapOp>(rawJson),
                PrivilegedOpType.RemoveUploadCap => new RemoveUploadCapOp(),
                PrivilegedOpType.RetainServiceUploadCap => Read<RetainServiceUploadCapOp>(rawJson),
                PrivilegedOpType.ForgetServiceUploadCap => new ForgetServiceUploadCapOp(),
                PrivilegedOpType.DeleteBcdValue => Read<DeleteBcdValueOp>(rawJson),
                PrivilegedOpType.SetBcdValue => Read<SetBcdValueOp>(rawJson),
                PrivilegedOpType.ReadComponentStoreHealth => new ReadComponentStoreHealthOp(),
                PrivilegedOpType.ScheduleMemoryDiagnostic => new ScheduleMemoryDiagnosticOp(),
                PrivilegedOpType.CancelMemoryDiagnostic => new CancelMemoryDiagnosticOp(),
                PrivilegedOpType.RestartDevice => Read<RestartDeviceOp>(rawJson),
                PrivilegedOpType.RemoveDriverPackage => Read<RemoveDriverPackageOp>(rawJson),
                PrivilegedOpType.RestoreDriverPackage => Read<RestoreDriverPackageOp>(rawJson),
                PrivilegedOpType.PoseEnAttenteDOrigine => Read<PoseEnAttenteDOrigineOp>(rawJson),
                PrivilegedOpType.AcquitterEcriture => Read<AcquitterEcritureOp>(rawJson),
                PrivilegedOpType.SetMaintenancePause => Read<SetMaintenancePauseOp>(rawJson),
                _ => null,
            };

            return op is null
                ? Result<PrivilegedOp>.Fail(ErrorCodes.NotAllowlisted, "Privileged-op payload could not be bound.")
                : Result<PrivilegedOp>.Ok(op);
        }
        catch (Exception)
        {
            // Toute erreur de liaison (par exemple mauvais types de champ pour la feuille) → échec propre.
            return Result<PrivilegedOp>.Fail(ErrorCodes.NotAllowlisted, "Privileged-op payload could not be bound.");
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static T? Read<T>(string rawJson) where T : PrivilegedOp =>
        JsonSerializer.Deserialize<T>(rawJson, Options);
}
