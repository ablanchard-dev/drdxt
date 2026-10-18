using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.Adlx;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// PrivilegedOpExecutor : le vrai exécuteur des ops sûres (hors GPU).
//
// Atteint seulement après que TypedPrivilegedServer a passé, dans l'ordre : intégrité HMAC →
// anti-rejeu → autorisation de l'appelant → liaison par le codec borné → OpAllowlist.Validate.
// L'op reçue ici est donc une feuille typée, fermée, déjà passée par l'allowlist. L'aiguillage
// relie chaque feuille à une action Windows concrète derrière une couture mince (l'appel natif
// est la frontière du m2smoke) ; chaque action est emballée → Result, pour qu'aucune exception
// ne traverse l'IPC.
//
// Moitié risquée : les écritures NVAPI risquées de décalage d'horloge + limite de puissance sont
// branchées (recette §A/§B) via INvApiTuningActions (GET puis SET, échec propre). La surface DRS
// des réglages du pilote (recette §C) l'est aussi. Le réglage ADLX (AMD) est branché via
// IADLXTuningActions (le miroir AMD, GET puis SET, plage en refus par défaut, échec propre). La
// courbe de ventilateur GPU multipoint reste reportée ici, avec un message HELPER.OP_FAILED
// honnête et aucune action partielle : l'écriture ventilateur est une tâche future à part,
// reportée pour les deux constructeurs (ADLX + NVAPI) jusqu'à une recette d'écriture vérifiée. Une
// couture de lecture active n'implique pas qu'une écriture ventilateur existe ;
// AdlxGpuTuningWriter est en lecture seule par conception.
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
    private readonly IPnpActions? _pnp;
    private readonly DrDxt.Reconciler.PauseDuMaintien? _pauseDuMaintien;
    private const string PnpNotWiredMessage = "The pnputil seam is not wired in this host: nothing was done.";
    private readonly IVentilateurActions? _ventilateur;
    private readonly ICpuPowerActions? _cpu;
    private const string BcdNotWiredMessage = "The BCD seam is not wired in this host: nothing was written.";

    private const string QosNotWiredMessage =
        "The network QoS seam is not wired in this host: no policy was written.";

    private const string PlafondDuServiceNotWiredMessage =
        "The service ping-protection store is not wired in this host: nothing was retained.";

    private const string PauseDuMaintienNotWiredMessage =
        "The maintenance-pause file is not wired in this host: nothing was written.";

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
        // Facultatif et en dernier, pour que chaque construction existante reste compatible à la source.
        // Absent, les deux ops de démarrage firmware échouent honnêtement (voir FirmwareBootNotWired)
        // au lieu de ne rien faire en silence : même contrat que la courbe de ventilateur plus haut.
        IFirmwareBootActions? firmwareBoot = null,
        // L'ecriture du manifeste signe. Passee par le helper eleve seulement : le service
        // SYSTEM (InProcessElevatedClient) ne la recoit pas, donc il ne peut pas signer : il verifie.
        IManifestCommitActions? manifeste = null,
        // Le plafond d'envoi QoS. Absent : les deux ops echouent honnetement.
        INetworkQosActions? qos = null,
        // L'ecriture BCD (ensemble ferme). Absente : les deux ops echouent honnetement.
        IBcdActions? bcd = null,
        // Le reglage de protection du ping que portera le service. Branche dans le helper seul :
        // le service le lit, il ne l'ecrit pas. Absent : les deux ops echouent honnetement.
        DrDxt.Reconciler.Reseau.PlafondEnvoiDuService? plafondDuService = null,
        // Le ventilateur en mode Expert. Absent : il reste refusé honnêtement.
        IVentilateurActions? ventilateur = null,
        // La limite de puissance du processeur par PawnIO. Absente : refusée honnêtement.
        ICpuPowerActions? cpu = null,
        // Les gestes pnputil (peripherique, vieux pilotes d'affichage). Absents : refuses honnetement.
        IPnpActions? pnp = null,
        // La pause du maintien. Branchee dans le helper seul : le service la lit, il ne l'ecrit pas.
        DrDxt.Reconciler.PauseDuMaintien? pauseDuMaintien = null)
    {
        _pauseDuMaintien = pauseDuMaintien;
        _pnp = pnp;
        _cpu = cpu;
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
                SetStartupApprovalOp s =>
                    _registry.SetStartupApproval(s.Hive, s.Liste, s.Nom, s.Actif),

                SetActivePowerPlanOp p =>
                    _power.SetActivePlan(p.PlanGuid),
                SetProcessorBoostModeOp b =>
                    _power.SetBoostModeAc(b.PlanGuid, b.AcValue),
                SetWirelessPowerSavingOp w =>
                    _power.SetWirelessPowerSavingAc(w.PlanGuid, w.AcValue),
                SetCpuPackagePowerLimitOp c => _cpu is not null
                    ? _cpu.SetPackagePowerLimit(c.Valeur, c.Origine)
                    : Fail("CPU power limit write is not wired in this executor."),
                SetCpuVoltageOffsetOp v => _cpu is not null
                    ? _cpu.SetVoltageOffset(v.DecalageMv)
                    : Fail("CPU voltage offset write is not wired in this executor."),

                // Le voyage et son annulation. Les deux passent par shutdown.exe ; DrDXT n'écrit
                // aucune variable firmware lui-même. Une couture absente est dite, jamais ignorée en silence :
                // un utilisateur qui a cliqué « emmène-moi dans le BIOS » sans résultat doit savoir pourquoi.
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

                // Lecture firmware par le chemin d'action. Ce chemin-la ne rend que
                // « lisible / pas lisible », parce que ExecuteAsync n'a pas de retour de donnees.
                // La valeur passe par ReadAsync ci-dessous. Garde ici : un appelant qui
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
                SetMaintenancePauseOp mp =>
                    _pauseDuMaintien is null ? Fail(PauseDuMaintienNotWiredMessage) : _pauseDuMaintien.Poser(mp.EnPause),
                DeleteBcdValueOp db =>
                    _bcd is null ? Fail(BcdNotWiredMessage) : _bcd.DeleteValue(db.Element),
                SetBcdValueOp sb =>
                    _bcd is null ? Fail(BcdNotWiredMessage) : _bcd.SetValue(sb.Element, sb.Value),
                ScheduleMemoryDiagnosticOp =>
                    _bcd is null ? Fail(BcdNotWiredMessage) : _bcd.ScheduleMemoryDiagnostic(),
                CancelMemoryDiagnosticOp =>
                    _bcd is null ? Fail(BcdNotWiredMessage) : _bcd.CancelMemoryDiagnostic(),
                RestartDeviceOp rd =>
                    _pnp is null ? Fail(PnpNotWiredMessage) : _pnp.RestartDevice(rd.InstanceId),
                RemoveDriverPackageOp rp =>
                    _pnp is null ? Fail(PnpNotWiredMessage) : _pnp.RemoveDriverPackage(rp.PublishedName),
                RestoreDriverPackageOp rr =>
                    _pnp is null ? Fail(PnpNotWiredMessage) : _pnp.RestoreDriverPackage(rr.PublishedName),

                ReadFirmwareVariableOp fv =>
                    _firmwareBoot is null
                        ? Fail(FirmwareBootNotWiredMessage)
                        : ReadOnlyOutcome(_firmwareBoot.ReadFirmwareVariable(fv.Name, fv.VendorGuid)),
                ReadComponentStoreHealthOp =>
                    ReadOnlyOutcome(SanteDesComposants.Lire()),
                InstallPresentMonServiceOp =>
                    _service.InstallPresentMonService(),

                // --- Réglage pilote DRS : réel (sûr, ni tension ni horloge). ---
                //
                // La portee est transportee. Si cette ligne ne passait que (SettingId, Value),
                // `ProfileName` serait decoratif, et « cod.exe » produirait la meme ecriture machine
                // entiere que « Base Profile ».
                //
                // La portee decide de la methode appelee. L'allowlist garantit que
                // ProfileName est soit le profil de base, soit un chemin d'executable complet :
                // il n'existe pas de troisieme cas qui atteindrait ce switch.
                ApplyNvApiDrsSettingOp d =>
                    OpAllowlist.IsBaseProfileScope(d.ProfileName)
                        ? _drs.SetGlobalSetting(d.SettingId, d.Value)
                        : _drs.SetSettingForApplication(d.ProfileName, d.SettingId, d.Value),

                // La suppression n'a qu'une portee (l'allowlist refuse le profil de
                // base) ; la preuve du retour est la provenance relue par l'action.
                DeleteNvApiDrsSettingOp dd =>
                    _drs.DeleteSettingForApplication(dd.ProfileName, dd.SettingId, dd.OrigineAttendue),

                // --- Réglage NVAPI risqué : réel. GET puis SET, échec propre.
                //     L'op CoreOffset porte aussi un décalage mémoire, mais la recette vérifiée
                //     ne couvre que l'horloge graphique P0 ; on n'applique que le décalage cœur ;
                //     le champ mémoire est ignoré. L'App l'envoie à 0 (ReglageCarteExpert refuse
                //     toute mémoire demandée). ---
                ApplyNvApiClockOffsetOp c =>
                    _tuning.SetCoreClockOffset(c.GpuIndex, c.CoreOffsetMhz),
                ApplyNvApiPowerLimitOp pl =>
                    _tuning.SetPowerLimitPercent(pl.GpuIndex, pl.PowerLimitPercent),

                // --- Réglage ADLX (AMD) risqué : réel. GET puis SET, nouvelle vérification de plage
                //     en refus par défaut, échec propre : le miroir AMD du chemin NVAPI. ---
                ApplyAdlxTuningOp a =>
                    _adlx.SetTuning(a.GpuIndex, a.Kind, a.IntValue),

                // --- Op GPU encore reportée : la courbe de ventilateur multipoint (l'écriture
                //     ventilateur est une tâche future à part). Refusée proprement, message honnête, aucune
                //     action partielle ; reportée pour les deux constructeurs, comme le writer en lecture seule. ---
                // Un seul point = une vitesse fixe, qui ne fait que monter ; une vraie courbe
                // (plusieurs points, suivie en température) reste à écrire.
                ApplyGpuFanCurveOp { Points.Count: 1 } f when _ventilateur is not null =>
                    _ventilateur.Poser(f.GpuIndex, f.Points[0].DutyPct),
                ApplyGpuFanCurveOp => FanCurveDeferred(),
                RestoreGpuFanAutoOp rf => _ventilateur is null ? FanCurveDeferred() : _ventilateur.Rendre(rf.GpuIndex),

                // Une pose marquée n'arrive ici que sans journal d'écriture anticipée devant l'exécuteur (racine non fiable au
                // lancement du helper) : le journal la déballe lui-même. L'écrire nue rouvrirait la fenêtre de plantage que le
                // journal ferme, et l'App promettrait un rendu sans entrée : refusée, rien n'est écrit. Un acquit n'a rien à retirer.
                PoseEnAttenteDOrigineOp => Result.Fail(ErrorCodes.JournalIndisponible,
                    "No write-ahead journal in this host: a marked pose is refused, nothing was written."),
                AcquitterEcritureOp => Result.Ok(),

                // Le manifeste signe : son echec garde son code (MANIFEST_UNREADABLE, MANIFEST_KEY_UNUSABLE),
                // parce que l'App en tire la reprise explicite.
                CommitManifestOp cm =>
                    _manifeste is null ? Fail(ManifestCommitNotWiredMessage) : _manifeste.Commit(cm),

                // Défaut par prudence : ne devrait pas être atteint (l'allowlist filtre en amont).
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
            // Toute erreur inattendue d'une action native → échec propre, sans fuite de détail.
            return Task.FromResult(Fail("Privileged operation failed."));
        }
    }

    // ── Le chemin des ops qui repondent ────────────────────────────────────────────────
    // Ensemble ferme, comme partout ailleurs ici : une op absente de ce switch echoue au lieu
    // de rendre un succes vide. Rien n'y ecrit : il n'existe aucune op d'ecriture firmware
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

                // Une op d'action passee ici est un defaut d'appel, pas une lecture vide.
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
    // On garde le succes ou l'echec avec son motif, notamment « privilege absent », qui est
    // un non concluant et surtout pas la preuve que le firmware a refuse. Les octets lus sont
    // volontairement abandonnes ici : ce canal execute des actions, il ne repond pas a des
    // requetes, et pretendre le contraire demanderait un canal de retour de donnees.
    private static Result ReadOnlyOutcome(Result<byte[]> read)
        => read.IsOk ? Result.Ok() : Result.Fail(read.Error!.Value.Code, read.Error.Value.Message);

}
