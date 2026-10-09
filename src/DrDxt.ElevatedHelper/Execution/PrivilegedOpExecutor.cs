using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.Adlx;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// PrivilegedOpExecutor — the real R1 executor for the SAFE (non-GPU) ops.
//
// Reached only after TypedPrivilegedServer has run, in order: HMAC integrity →
// anti-replay → caller authorization → bounded codec bind → OpAllowlist.Validate.
// So the op handed here is a typed, closed, ALREADY-ALLOWLISTED leaf. The
// dispatch maps each leaf to a concrete Windows action behind a thin seam (the
// native call is the m2smoke boundary); every action is wrapped → Result so an
// exception never crosses the IPC.
//
// R2 (RISKY half): the RISKY NVAPI clock-offset + power-limit writes ARE now wired
// (recipe §A/§B) via INvApiTuningActions (GET-then-SET, fail-clean). The DRS
// driver-settings surface (recipe §C) is wired too. ADLX (AMD) tuning is now wired
// (T3) via IADLXTuningActions (the AMD mirror, GET-then-SET, default-DENY band,
// fail-clean). The multi-point GPU fan-curve remains DEFERRED here (T6) with an
// HONEST HELPER.OP_FAILED message and NO partial action — fan-write (E4) is a
// dedicated future task, deferred for both vendors (ADLX + NVAPI) until the
// verified fan-write recipe lands. The read seam being live (T5) does not imply a
// fan write exists; AdlxGpuTuningWriter is read-only by design.
// -----------------------------------------------------------------------
public sealed class PrivilegedOpExecutor : IPrivilegedOpExecutor
{
    private const string FirmwareBootNotWiredMessage =
        "The firmware-setup reboot seam is not wired in this host: no reboot was requested (M3 lot 2).";

    private const string FanCurveDeferredMessage =
        "GPU fan-curve control is not yet available: no fan write is performed (E4).";

    private readonly IRegistryActions _registry;
    private readonly IPowerPlanActions _power;
    private readonly IRestorePointActions _restore;
    private readonly IServiceActions _service;
    private readonly INvApiDrsActions _drs;
    private readonly INvApiTuningActions _tuning;
    private readonly IADLXTuningActions _adlx;
    private readonly IFirmwareBootActions? _firmwareBoot;
    private readonly IManifestCommitActions? _manifeste;
    private readonly INetworkQosActions? _qos;
    private readonly DrDxt.Reconciler.Reseau.PlafondEnvoiDuService? _plafondDuService;
    private readonly IBcdActions? _bcd;
    private readonly IVentilateurActions? _ventilateur;
    private const string BcdNotWiredMessage = "The BCD seam is not wired in this host: nothing was written.";

    private const string QosNotWiredMessage =
        "The network QoS seam is not wired in this host: no policy was written.";

    private const string PlafondDuServiceNotWiredMessage =
        "The service ping-protection store is not wired in this host: nothing was retained.";

    private const string ManifestCommitNotWiredMessage =
        "The signed-manifest writer is not wired in this host: nothing was signed or written.";

    public PrivilegedOpExecutor(
        IRegistryActions registry,
        IPowerPlanActions power,
        IRestorePointActions restore,
        IServiceActions service,
        INvApiDrsActions drs,
        INvApiTuningActions tuning,
        IADLXTuningActions adlx,
        // M3 lot 2 — optional/trailing so every existing construction stays source-compatible.
        // When it is absent the two firmware-boot ops fail HONESTLY (see FirmwareBootNotWired)
        // rather than silently doing nothing, which is the same contract as the fan curve above.
        IFirmwareBootActions? firmwareBoot = null,
        // 11/09/2026 — l'ecriture du manifeste signe. Passee par le helper eleve seulement : le service
        // SYSTEM (InProcessElevatedClient) ne la recoit pas, donc il ne peut pas signer — il verifie.
        IManifestCommitActions? manifeste = null,
        // 24/09/2026 — le plafond d'envoi QoS. Absent : les deux ops echouent HONNETEMENT.
        INetworkQosActions? qos = null,
        // 24/09/2026 — l'ecriture BCD (ensemble ferme). Absente : les deux ops echouent honnetement.
        IBcdActions? bcd = null,
        // 28/09/2026 : le reglage de protection du ping que portera le service. Branche dans le helper seul :
        // le service le lit, il ne l'ecrit pas. Absent : les deux ops echouent honnetement.
        DrDxt.Reconciler.Reseau.PlafondEnvoiDuService? plafondDuService = null,
        // 08/10/2026 (GPU-OC) : le ventilateur en mode Expert. Absent : il reste refusé honnêtement.
        IVentilateurActions? ventilateur = null)
    {
        _ventilateur = ventilateur;
        _plafondDuService = plafondDuService;
        _qos = qos;
        _bcd = bcd;
        _manifeste = manifeste;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _drs = drs ?? throw new ArgumentNullException(nameof(drs));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _adlx = adlx ?? throw new ArgumentNullException(nameof(adlx));
        _firmwareBoot = firmwareBoot;
    }

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
    {
        try
        {
            var result = op switch
            {
                null => Fail("Null privileged op."),

                PingOp => Result.Ok(),

                SetRegistryValueOp r =>
                    _registry.SetValue(r.Hive, r.Path, r.Name, r.Kind, r.Value),
                DeleteRegistryValueOp r =>
                    _registry.DeleteValue(r.Hive, r.Path, r.Name),

                SetActivePowerPlanOp p =>
                    _power.SetActivePlan(p.PlanGuid),
                SetProcessorBoostModeOp b =>
                    _power.SetBoostModeAc(b.PlanGuid, b.AcValue),

                // M3 lot 2 — the trip and its undo. Both go through shutdown.exe; DrDXT writes
                // no firmware variable itself. A missing seam is SAID, never silently ignored:
                // a user who clicked "take me to the BIOS" and got nothing must be told why.
                RebootToFirmwareSetupOp r =>
                    _firmwareBoot is null
                        ? Fail(FirmwareBootNotWiredMessage)
                        : _firmwareBoot.RequestFirmwareSetupReboot(r.DelaySeconds),
                CancelPendingFirmwareBootOp =>
                    _firmwareBoot is null
                        ? Fail(FirmwareBootNotWiredMessage)
                        : _firmwareBoot.CancelPendingFirmwareBoot(),

                CreateRestorePointOp rp =>
                    _restore.CreatePoint(rp.Description),
                EnableSystemRestoreOp e =>
                    _restore.EnableSystemRestore(e.DriveLetter),

                RunServiceControlOp s =>
                    _service.Control(s.ServiceName, s.Action),

                // Lecture firmware par le chemin d'ACTION. Ce chemin-la ne rend toujours que
                // « lisible / pas lisible », parce que ExecuteAsync n'a pas de retour de donnees.
                // La valeur passe desormais par ReadAsync ci-dessous. Garde ici : un appelant qui
                // se trompe de methode apprend un fait vrai, il ne recoit pas un succes vide qui
                // ressemblerait a une lecture reussie.
                ApplyUploadCapOp uc =>
                    _qos is null ? Fail(QosNotWiredMessage) : _qos.ApplyUploadCap(uc.GameExeName, uc.CapBitsPerSecond),
                RemoveUploadCapOp =>
                    _qos is null ? Fail(QosNotWiredMessage) : _qos.RemoveUploadCap(),
                RetainServiceUploadCapOp rs =>
                    _plafondDuService is null ? Fail(PlafondDuServiceNotWiredMessage)
                        : _plafondDuService.Retenir(new DrDxt.Reconciler.Reseau.PlafondRetenuDuService(rs.GameExeName, rs.CapBitsPerSecond)),
                ForgetServiceUploadCapOp =>
                    _plafondDuService is null ? Fail(PlafondDuServiceNotWiredMessage) : _plafondDuService.Oublier(),
                DeleteBcdValueOp db =>
                    _bcd is null ? Fail(BcdNotWiredMessage) : _bcd.DeleteValue(db.Element),
                SetBcdValueOp sb =>
                    _bcd is null ? Fail(BcdNotWiredMessage) : _bcd.SetValue(sb.Element, sb.Value),

                ReadFirmwareVariableOp fv =>
                    _firmwareBoot is null
                        ? Fail(FirmwareBootNotWiredMessage)
                        : ReadOnlyOutcome(_firmwareBoot.ReadFirmwareVariable(fv.Name, fv.VendorGuid)),
                ReadComponentStoreHealthOp =>
                    ReadOnlyOutcome(SanteDesComposants.Lire()),
                InstallPresentMonServiceOp =>
                    _service.InstallPresentMonService(),

                // --- DRS driver setting: Real now (SAFE, no voltage/clock). ---
                //
                // L2 — la portee est enfin transportee. Cette ligne ne passait que
                // (SettingId, Value) : `ProfileName` etait decoratif, et « cod.exe » produisait
                // la meme ecriture machine entiere que « Base Profile ». L'allowlist refusait
                // donc honnetement toute portee autre que le profil de base plutot que de
                // laisser l'op mentir sur ce qu'elle faisait.
                //
                // Desormais la portee decide de la methode appelee. L'allowlist garantit que
                // ProfileName est soit le profil de base, soit un chemin d'executable complet :
                // il n'existe pas de troisieme cas qui atteindrait ce switch.
                ApplyNvApiDrsSettingOp d =>
                    OpAllowlist.IsBaseProfileScope(d.ProfileName)
                        ? _drs.SetGlobalSetting(d.SettingId, d.Value)
                        : _drs.SetSettingForApplication(d.ProfileName, d.SettingId, d.Value),

                // F1 lot 3 : la suppression n'a qu'une portee (l'allowlist refuse le profil de
                // base) ; la preuve du retour est la provenance relue par l'action.
                DeleteNvApiDrsSettingOp dd =>
                    _drs.DeleteSettingForApplication(dd.ProfileName, dd.SettingId, dd.OrigineAttendue),

                // --- RISKY NVAPI tuning: Real now (R2). GET-then-SET, fail-clean.
                //     The CoreOffset op carries a memory offset too, but the verified
                //     recipe only covers the P0 GRAPHICS clock; we apply only the core
                //     offset (the memory-offset write is not in the verified §A path). ---
                ApplyNvApiClockOffsetOp c =>
                    _tuning.SetCoreClockOffset(c.GpuIndex, c.CoreOffsetMhz),
                ApplyNvApiPowerLimitOp pl =>
                    _tuning.SetPowerLimitPercent(pl.GpuIndex, pl.PowerLimitPercent),

                // --- RISKY ADLX (AMD) tuning: Real now (T3). GET-then-SET, default-DENY
                //     band re-check, fail-clean — the AMD mirror of the NVAPI tuning path. ---
                ApplyAdlxTuningOp a =>
                    _adlx.SetTuning(a.GpuIndex, a.Kind, a.IntValue),

                // --- Still-DEFERRED GPU op: the multi-point fan-curve (T6 — E4 fan-write is a
                //     dedicated future task). Cleanly REFUSED with an honest message, no partial
                //     action; deferred for both vendors, consistent with the read-only writer. ---
                // 08/10/2026 (GPU-OC) : un seul point = une vitesse fixe, qui ne fait que monter ; une vraie courbe
                // (plusieurs points, suivie en température) reste à écrire.
                ApplyGpuFanCurveOp { Points.Count: 1 } f when _ventilateur is not null =>
                    _ventilateur.Poser(f.GpuIndex, f.Points[0].DutyPct),
                ApplyGpuFanCurveOp => FanCurveDeferred(),
                RestoreGpuFanAutoOp rf => _ventilateur is null ? FanCurveDeferred() : _ventilateur.Rendre(rf.GpuIndex),

                // Le manifeste signe : son echec garde son CODE (MANIFEST_UNREADABLE, MANIFEST_KEY_UNUSABLE),
                // parce que l'App en tire la reprise explicite.
                CommitManifestOp cm =>
                    _manifeste is null ? Fail(ManifestCommitNotWiredMessage) : _manifeste.Commit(cm),

                // Defensive default — should be unreachable (allowlist gated upstream).
                _ => Fail("Unsupported privileged op."),
            };

            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any unexpected error from a native action → fail-clean, no detail leak.
            return Task.FromResult(Fail("Privileged operation failed."));
        }
    }

    // ── Le chemin des ops qui repondent ────────────────────────────────────────────────
    // Ensemble ferme, comme partout ailleurs ici : une op absente de ce switch echoue au lieu
    // de rendre un succes vide. Rien n'y ecrit -- il n'existe aucune op d'ecriture firmware
    // dans l'enumeration, et ce switch ne peut donc pas en atteindre une.
    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
    {
        try
        {
            var result = op switch
            {
                ReadFirmwareVariableOp fv =>
                    _firmwareBoot is null
                        ? Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, FirmwareBootNotWiredMessage)
                        : _firmwareBoot.ReadFirmwareVariable(fv.Name, fv.VendorGuid),
                ReadComponentStoreHealthOp => SanteDesComposants.Lire(),

                // Une op d'ACTION passee ici est un defaut d'appel, pas une lecture vide.
                _ => Result<byte[]>.Fail(ErrorCodes.NotAllowlisted, "This op does not return data."),
            };

            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Meme regle que ExecuteAsync : aucune exception ne traverse l'IPC, aucun detail du host.
            return Task.FromResult(Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "Privileged read failed."));
        }
    }

    private static Result FanCurveDeferred() => Result.Fail(ErrorCodes.HelperOpFailed, FanCurveDeferredMessage);

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);

    // Reduit une lecture (Result<byte[]>) au Result sans donnees que ce canal sait porter.
    // On garde le succes ou l'echec avec son motif -- notamment « privilege absent », qui est
    // un NON CONCLUANT et surtout pas la preuve que le firmware a refuse. Les octets lus sont
    // volontairement abandonnes ici : ce canal execute des actions, il ne repond pas a des
    // requetes, et pretendre le contraire demanderait un canal de retour de donnees.
    private static Result ReadOnlyOutcome(Result<byte[]> read)
        => read.IsOk ? Result.Ok() : Result.Fail(read.Error!.Value.Code, read.Error.Value.Message);
}
