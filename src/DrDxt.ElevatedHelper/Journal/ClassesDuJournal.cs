using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Journal;

/// <summary>Ce que le journal d'écriture anticipée fait d'une op, avant de l'exécuter.</summary>
public enum ClasseDuJournal
{
    /// <summary>Journalisée : son image avant se lit (LectureDesCiblesWin32.Lecteur) ; illisible, l'op n'est pas exécutée.</summary>
    Journalisee,

    /// <summary>À journaliser quand sa lecture existera ; d'ici là, le comportement actuel (l'App le dit). Jamais refusée à
    /// cause du journal.</summary>
    LecteurAVenir,

    /// <summary>Volatile ou sous bail : rendue par le bail, le redémarrage ou la désinstallation, jamais journalisée.</summary>
    Volatile,

    /// <summary>Sans état à rendre, ou déjà journalisée ailleurs.</summary>
    SansEtat,
}

/// <summary>La classe de chaque op privilégiée. Une op nouvelle sans classe fait rougir le test qui parcourt toutes les
/// ops : elle ne passe jamais au journal par défaut.</summary>
public static class ClassesDuJournal
{
    private static readonly IReadOnlyDictionary<Type, ClasseDuJournal> ParType = new Dictionary<Type, ClasseDuJournal>
    {
        [typeof(SetRegistryValueOp)] = ClasseDuJournal.Journalisee,
        [typeof(SetActivePowerPlanOp)] = ClasseDuJournal.Journalisee,
        [typeof(SetProcessorBoostModeOp)] = ClasseDuJournal.Journalisee,
        [typeof(SetWirelessPowerSavingOp)] = ClasseDuJournal.Journalisee,
        [typeof(SetCpuPackagePowerLimitOp)] = ClasseDuJournal.Journalisee,
        [typeof(RunServiceControlOp)] = ClasseDuJournal.Journalisee,
        // L'allowlist n'admet que le profil de base et un chemin d'exécutable qualifié : les deux portées se relisent.
        [typeof(ApplyNvApiDrsSettingOp)] = ClasseDuJournal.Journalisee,
        [typeof(ApplyNvApiPowerLimitOp)] = ClasseDuJournal.Journalisee,

        [typeof(DeleteRegistryValueOp)] = ClasseDuJournal.LecteurAVenir,
        [typeof(SetStartupApprovalOp)] = ClasseDuJournal.LecteurAVenir,
        [typeof(DeleteNvApiDrsSettingOp)] = ClasseDuJournal.LecteurAVenir,
        [typeof(SetBcdValueOp)] = ClasseDuJournal.LecteurAVenir,
        [typeof(DeleteBcdValueOp)] = ClasseDuJournal.LecteurAVenir,
        // Le magasin actif de Windows, sous bail : le redémarrage efface le plafond d'une partie.
        [typeof(ApplyUploadCapOp)] = ClasseDuJournal.Volatile,
        [typeof(RetainServiceUploadCapOp)] = ClasseDuJournal.LecteurAVenir,

        // Rendues par le bail (LeaseWatchdog), le redémarrage, ou la désinstallation de la carte. Le ventilateur ne
        // s'écrit que par NVML, volatil au redémarrage ; une courbe à plusieurs points est refusée.
        [typeof(SetCpuVoltageOffsetOp)] = ClasseDuJournal.Volatile,
        [typeof(ApplyNvApiClockOffsetOp)] = ClasseDuJournal.Volatile,
        [typeof(ApplyGpuFanCurveOp)] = ClasseDuJournal.Volatile,
        [typeof(RestoreGpuFanAutoOp)] = ClasseDuJournal.Volatile,

        [typeof(CreateRestorePointOp)] = ClasseDuJournal.SansEtat,
        [typeof(EnableSystemRestoreOp)] = ClasseDuJournal.SansEtat,
        [typeof(RestartDeviceOp)] = ClasseDuJournal.SansEtat,
        [typeof(RebootToFirmwareSetupOp)] = ClasseDuJournal.SansEtat,
        [typeof(CancelPendingFirmwareBootOp)] = ClasseDuJournal.SansEtat,
        [typeof(ScheduleMemoryDiagnosticOp)] = ClasseDuJournal.SansEtat,
        [typeof(CancelMemoryDiagnosticOp)] = ClasseDuJournal.SansEtat,
        // Le dossier pilotes-retires de ProgramData garde déjà de quoi les rendre.
        [typeof(RemoveDriverPackageOp)] = ClasseDuJournal.SansEtat,
        [typeof(RestoreDriverPackageOp)] = ClasseDuJournal.SansEtat,
        [typeof(InstallPresentMonServiceOp)] = ClasseDuJournal.SansEtat,
        [typeof(PingOp)] = ClasseDuJournal.SansEtat,
        [typeof(ReadComponentStoreHealthOp)] = ClasseDuJournal.SansEtat,
        [typeof(ReadFirmwareVariableOp)] = ClasseDuJournal.SansEtat,
        [typeof(CommitManifestOp)] = ClasseDuJournal.SansEtat,
        [typeof(EstablishLeaseOp)] = ClasseDuJournal.SansEtat,
        [typeof(RenewLeaseOp)] = ClasseDuJournal.SansEtat,
        [typeof(ForgetServiceUploadCapOp)] = ClasseDuJournal.SansEtat,
        // Le fichier de pause est lui-même l'état : la reprise est le même geste.
        [typeof(SetMaintenancePauseOp)] = ClasseDuJournal.SansEtat,
        [typeof(RemoveUploadCapOp)] = ClasseDuJournal.SansEtat,
        // L'enveloppe n'a pas d'état propre : c'est l'op qu'elle porte qui se classe (ExecuteurJournalise). L'acquit retire.
        [typeof(PoseEnAttenteDOrigineOp)] = ClasseDuJournal.SansEtat,
        [typeof(AcquitterEcritureOp)] = ClasseDuJournal.SansEtat,
    };

    // Le réglage ADLX se classe par genre : la sous-tension et la remise à l'usine n'ont pas encore de lecture (une
    // lecture de MaxFreq fera passer la sous-tension au journal ; la remise à l'usine est un geste explicite).
    private static readonly IReadOnlyDictionary<AdlxTuningKind, ClasseDuJournal> ParGenreAdlx = new Dictionary<AdlxTuningKind, ClasseDuJournal>
    {
        [AdlxTuningKind.PowerLimit] = ClasseDuJournal.Journalisee,
        [AdlxTuningKind.AntiLag] = ClasseDuJournal.Journalisee,
        [AdlxTuningKind.FrameRateTarget] = ClasseDuJournal.Journalisee,
        [AdlxTuningKind.VerticalRefresh] = ClasseDuJournal.Journalisee,
        [AdlxTuningKind.Undervolt] = ClasseDuJournal.LecteurAVenir,
        [AdlxTuningKind.RemiseUsine] = ClasseDuJournal.LecteurAVenir,
    };

    public static ClasseDuJournal? De(PrivilegedOp op) => op is ApplyAdlxTuningOp a
        ? ParGenreAdlx.TryGetValue(a.Kind, out var g) ? g : null
        : DuType(op.GetType());

    /// <summary>La classe d'un type d'op ; <c>null</c> pour le réglage ADLX, qui se classe par genre.</summary>
    internal static ClasseDuJournal? DuType(Type op) => ParType.TryGetValue(op, out var c) ? c : null;

    /// <summary>Les types classés : ceux de la table, et le réglage ADLX, classé genre par genre.</summary>
    public static IEnumerable<Type> TypesClasses => ParType.Keys.Append(typeof(ApplyAdlxTuningOp));

    public static IEnumerable<AdlxTuningKind> GenresAdlxClasses => ParGenreAdlx.Keys;
}
