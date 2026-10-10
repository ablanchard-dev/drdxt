using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrDxt.Core;

// -----------------------------------------------------------------------
// PrivilegedOpType — the CLOSED discriminant for the wire form of a
// PrivilegedOp. Each member maps 1:1 to exactly one sealed leaf. The codec
// switches on this enum explicitly; it never resolves a type by name.
//
// This is the on-wire mirror of the PrivilegedOp leaf allowlist. It grows only
// by adding a member here AND a matching arm in the codec's two switches.
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
    // M3 lot 2. AJOUTES le 26/08 : sans eux, Serialize rendait le discriminant -1 et l'op ne
    // pouvait pas traverser l'IPC -- donc le trajet vers le BIOS etait inerte des que le helper
    // tourne hors du processus, c'est-a-dire en production. Les valeurs sont APPENDUES : ne
    // jamais renumeroter, un ancien client et un nouveau serveur partagent ce fil.
    RebootToFirmwareSetup = 15,
    CancelPendingFirmwareBoot = 16,
    // Lecture seule. Il n'existe deliberement aucune op d'ecriture firmware dans cette
    // enumeration fermee : ecrire au mauvais offset d'un varstore peut rendre une carte non
    // demarrable, et l'offset depend du couple (modele x version de BIOS). Appendue, comme
    // les precedentes -- ne jamais renumeroter.
    ReadFirmwareVariable = 17,
    // PHASE 18 F1 lot 3 (10/09/2026) : l'inverse d'une ecriture au profil d'un jeu. Appendue,
    // comme les precedentes -- ne jamais renumeroter.
    DeleteNvApiDrsSetting = 18,
    // 11/09/2026 : le manifeste signe, ecrit par le helper eleve. Appendue -- ne jamais renumeroter.
    CommitManifest = 19,
    // 24/09/2026 : le plafond d'envoi QoS et son inverse. Appendues -- ne jamais renumeroter.
    ApplyUploadCap = 20,
    RemoveUploadCap = 21,
    // 24/09/2026 : retirer / reposer un element BCD de l'ensemble ferme. Appendues -- ne jamais renumeroter.
    DeleteBcdValue = 22,
    SetBcdValue = 23,
    // 25/09/2026 : lecture de l'etat du magasin de composants (DISM /CheckHealth). Appendue -- ne jamais renumeroter.
    ReadComponentStoreHealth = 24,
    // 28/09/2026 : la protection du ping retenue pour le service, et son inverse. Appendues -- ne jamais renumeroter.
    RetainServiceUploadCap = 25,
    ForgetServiceUploadCap = 26,
    // 07/10/2026 (L-CPU) : le mode de boost secteur d'un plan. Appendue -- ne jamais renumeroter.
    SetProcessorBoostMode = 27,
    // 08/10/2026 (GPU-OC) : les ventilateurs rendus à l'automatique. Appendue -- ne jamais renumeroter.
    RestoreGpuFanAuto = 28,
    // 10/10/2026 (B1) : la limite de puissance du processeur par PawnIO. Appendue -- ne jamais renumeroter.
    SetCpuPackagePowerLimit = 29,
}

// -----------------------------------------------------------------------
// PrivilegedOpCodec — serialize/deserialize a PrivilegedOp to/from JSON via a
// BOUNDED discriminant enum + EXPLICIT switch.
//
// SECURITY (master plan §9.2, §9.13; M2 risk #2 = polymorphic JSON = RCE):
//   * NO Newtonsoft TypeNameHandling, NO System.Text.Json polymorphism by
//     discriminator that maps to arbitrary types, NO Type.GetType. The wire
//     carries a numeric "op" discriminant only; an unknown / out-of-range
//     discriminant deserializes to nothing — it fails clean
//     (Result.Fail("HELPER.NOT_ALLOWLISTED", ...)).
//   * Deserialization is total: every malformed / hostile payload returns a
//     failed Result. It never throws and never instantiates an arbitrary type.
// -----------------------------------------------------------------------
public static class PrivilegedOpCodec
{
    // The discriminant property name on the wire. Deliberately not "$type"
    // (which evokes type-name handling) — it is a plain numeric enum field.
    private const string Discriminator = "op";

    private static readonly JsonSerializerOptions Options = new()
    {
        // Enums travel as their integer value (the closed discriminant), never by
        // name. Unmapped members are tolerated on the payload (forward-compat) but
        // can never select a type — only the explicit switch can.
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes a known PrivilegedOp leaf to a discriminated JSON
    /// object. Returns Fail(HELPER.NOT_ALLOWLISTED) if the leaf is somehow not
    /// in the closed set (defensive; unreachable for the sealed hierarchy).</summary>
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
            _ =>((PrivilegedOpType)(-1), (object?)null),
        };

        if (payload is null)
            return Result<string>.Fail(ErrorCodes.NotAllowlisted, "Unknown privileged op type.");

        try
        {
            // Emit the discriminant, then merge the leaf's own fields into the
            // same object. We build a JSON node so the discriminant and the leaf
            // payload share one flat object (no nested polymorphic envelope).
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

    /// <summary>Deserializes a discriminated JSON object to its closed leaf via an
    /// EXPLICIT switch on the bounded discriminant. Total: any malformed / unknown
    /// / out-of-range payload fails clean (never throws, never resolves a type).</summary>
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

            // The discriminant must be a numeric member of the closed enum.
            if (!root.TryGetProperty(Discriminator, out var disc)
                || disc.ValueKind != JsonValueKind.Number
                || !disc.TryGetInt32(out var rawType)
                || !Enum.IsDefined(typeof(PrivilegedOpType), rawType))
            {
                return Result<PrivilegedOp>.Fail(
                    ErrorCodes.NotAllowlisted, "Missing or unknown privileged-op discriminant.");
            }

            var rawJson = root.GetRawText();

            // EXPLICIT switch — the only place a discriminant becomes a type. No
            // reflection, no Type.GetType, no polymorphic by-name resolution.
            PrivilegedOp? op = (PrivilegedOpType)rawType switch
            {
                PrivilegedOpType.Ping => new PingOp(),
                PrivilegedOpType.SetRegistryValue => Read<SetRegistryValueOp>(rawJson),
                PrivilegedOpType.DeleteRegistryValue => Read<DeleteRegistryValueOp>(rawJson),
                PrivilegedOpType.SetActivePowerPlan => Read<SetActivePowerPlanOp>(rawJson),
                PrivilegedOpType.SetProcessorBoostMode => Read<SetProcessorBoostModeOp>(rawJson),
                PrivilegedOpType.SetCpuPackagePowerLimit => Read<SetCpuPackagePowerLimitOp>(rawJson),
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
                _ => null,
            };

            return op is null
                ? Result<PrivilegedOp>.Fail(ErrorCodes.NotAllowlisted, "Privileged-op payload could not be bound.")
                : Result<PrivilegedOp>.Ok(op);
        }
        catch (Exception)
        {
            // Any binding error (e.g. wrong field types for the leaf) → fail clean.
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
