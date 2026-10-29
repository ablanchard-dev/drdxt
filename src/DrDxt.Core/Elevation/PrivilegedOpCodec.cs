using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrDxt.Core;

// -----------------------------------------------------------------------
// PrivilegedOpType : le discriminant fermé de la forme filaire d'un PrivilegedOp, un membre par feuille scellée. Le
// codec fait un switch explicite dessus, jamais une résolution de type par nom. Il ne grandit qu'avec un membre ici et
// un bras dans les deux switch du codec. Les valeurs s'ajoutent en fin et ne se renumérotent jamais : un ancien client
// et un nouveau serveur partagent ce fil.
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
    // Le voyage vers le BIOS : sans ces entrees, Serialize rendrait -1 et l'op ne traverserait pas l'IPC.
    RebootToFirmwareSetup = 15,
    CancelPendingFirmwareBoot = 16,
    // Lecture seule : exprès aucune op d'ecriture firmware, un mauvais offset de varstore (qui depend du couple modele x
    // version de BIOS) peut rendre une carte non demarrable.
    ReadFirmwareVariable = 17,
    // L'inverse d'une ecriture au profil d'un jeu.
    DeleteNvApiDrsSetting = 18,
    // Le manifeste signe, ecrit par le helper eleve.
    CommitManifest = 19,
    // Le plafond d'envoi QoS et son inverse.
    ApplyUploadCap = 20,
    RemoveUploadCap = 21,
    // Retirer / reposer un element BCD de l'ensemble ferme.
    DeleteBcdValue = 22,
    SetBcdValue = 23,
    // Lecture de l'etat du magasin de composants (DISM /CheckHealth).
    ReadComponentStoreHealth = 24,
    // La protection du ping retenue pour le service, et son inverse.
    RetainServiceUploadCap = 25,
    ForgetServiceUploadCap = 26,
    // Le mode de boost secteur d'un plan.
    SetProcessorBoostMode = 27,
    // Les ventilateurs rendus à l'automatique.
    RestoreGpuFanAuto = 28,
    // La limite de puissance du processeur par PawnIO.
    SetCpuPackagePowerLimit = 29,
    // L'economie d'energie de la carte sans fil (secteur) d'un plan.
    SetWirelessPowerSaving = 30,
    // Le décalage de tension des cœurs et du cache.
    SetCpuVoltageOffset = 31,
    // Un programme au démarrage coupé ou réactivé (StartupApproved).
    SetStartupApproval = 32,
    // Le test memoire au prochain demarrage, et son annulation.
    ScheduleMemoryDiagnostic = 40,
    CancelMemoryDiagnostic = 41,
    // Relancer un peripherique, retirer un vieux pilote d'affichage exporte, le remettre.
    RestartDevice = 42,
    RemoveDriverPackage = 43,
    RestoreDriverPackage = 44,
    PoseEnAttenteDOrigine = 45,
    AcquitterEcriture = 46,
    // La pause du maintien posée ou levée par le joueur.
    SetMaintenancePause = 47,
    // Les droits du fichier d'un service corrigés, et rendus.
    CorrigerDroitsDuService = 48,
    RendreDroitsDuService = 49,
}

// -----------------------------------------------------------------------
// PrivilegedOpCodec : un PrivilegedOp en JSON, par un discriminant borné et un switch explicite.
// Sécurité (JSON polymorphe = RCE) :
//   * ni TypeNameHandling de Newtonsoft, ni polymorphisme System.Text.Json vers des types arbitraires, ni Type.GetType :
//     un discriminant numérique « op », inconnu ou hors plage → Result.Fail("HELPER.NOT_ALLOWLISTED", ...) ;
//   * désérialisation totale : une charge malformée ou hostile rend un échec, jamais une exception ni un type arbitraire.
// -----------------------------------------------------------------------
public static class PrivilegedOpCodec
{
    // Le discriminant sur le fil, exprès pas « $type » (qui évoque le type-name handling).
    private const string Discriminator = "op";

    private static readonly JsonSerializerOptions Options = new()
    {
        // Les énumérations voyagent en valeur entière (le discriminant fermé), jamais par nom.
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // La feuille ne se lie qu'à ce qu'elle déclare : un champ absent n'est pas comblé par la valeur par défaut du type
        // (0, chaîne nulle, Guid.Empty), un nul n'entre pas dans un champ non nullable, un champ inconnu est un champ que
        // personne ne vérifie, une énumération hors plage n'est aucune des valeurs prévues. L'allowlist reste l'autorité
        // sur les valeurs ; elle n'a plus à deviner ce qu'un défaut voulait dire. Le helper et l'App s'installent ensemble.
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new EnumerationsDefinies() },
    };

    private sealed class EnumerationsDefinies : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsEnum;

        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(EnumerationDefinie<>).MakeGenericType(type))!;
    }

    private sealed class EnumerationDefinie<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var valeur = JsonSerializer.Deserialize<T>(ref reader);
            return Enum.IsDefined(valeur) ? valeur : throw new JsonException($"{typeof(T).Name} out of range.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value);
    }

    /// <summary>Sérialise une feuille connue en objet JSON discriminé ; hors de l'ensemble fermé (inatteignable, hiérarchie
    /// scellée) → Fail(HELPER.NOT_ALLOWLISTED).</summary>
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
            CorrigerDroitsDuServiceOp cd => (PrivilegedOpType.CorrigerDroitsDuService, cd),
            RendreDroitsDuServiceOp rd2 => (PrivilegedOpType.RendreDroitsDuService, rd2),
            _ =>((PrivilegedOpType)(-1), (object?)null),
        };

        if (payload is null)
            return Result<string>.Fail(ErrorCodes.NotAllowlisted, "Unknown privileged op type.");

        try
        {
            // Le discriminant et les champs de la feuille dans un même objet plat, sans enveloppe polymorphe.
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

    /// <summary>Un objet JSON discriminé vers sa feuille fermée, par switch explicite. Totale : malformé, inconnu ou hors
    /// plage échoue proprement.</summary>
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

            // La feuille ne connaît pas le discriminant : retiré avant la liaison, où tout champ inconnu est refusé.
            var sansDiscriminant = System.Text.Json.Nodes.JsonNode.Parse(root.GetRawText())!.AsObject();
            sansDiscriminant.Remove(Discriminator);
            var rawJson = sansDiscriminant.ToJsonString();

            // Le seul endroit où un discriminant devient un type : ni réflexion, ni Type.GetType, ni résolution par nom.
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
                PrivilegedOpType.CorrigerDroitsDuService => Read<CorrigerDroitsDuServiceOp>(rawJson),
                PrivilegedOpType.RendreDroitsDuService => Read<RendreDroitsDuServiceOp>(rawJson),
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
