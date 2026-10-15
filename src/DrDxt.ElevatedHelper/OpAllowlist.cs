using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.NvApi;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// OpAllowlist : valide une PrivilegedOp par type et par cible, a la feuille exacte.
//
// Sécurité (« feuille exacte, pas sous-arbre ») : c'est la vraie autorisation, la defense n°1,
// pas le HMAC du transport. Chaque op est comparee a une liste fermee, a la feuille exacte :
//   * Registre : seulement un ensemble explicite de feuilles (ruche, chemin, nom). Un
//     sous-arbre interdit (Defender, Windows Update, BITS, bam, strategies de securite) est
//     toujours refuse ; tout ce qui n'est pas explicitement permis est refuse (refus par
//     defaut). « Sous-arbre permis » ne suffit jamais.
//   * Plan d'alimentation : seulement les GUID de plans Windows connus.
//   * Controle de service : seulement AMD3DVCacheSvc (et le service PresentMon, par l'op
//     dediee InstallPresentMonServiceOp), en installation, demarrage, arret et requete.
//   * Decalages d'horloge NVAPI : le helper impose lui-meme ici la bande GpuSafeBounds
//     (decalage coeur et memoire refuse hors bande ; il ne laisse jamais les bornes de valeur
//     au client). Autres ops NVAPI/ADLX : validation structurelle (index, type, plage), limite
//     de puissance bornee a 0..100 puis bornee par le pilote.
//   * Par defaut : refus (HELPER.NOT_ALLOWLISTED). Une op nulle ou non modelisee est refusee.
// -----------------------------------------------------------------------
/// <summary>
/// Le gardien du helper eleve : avant toute ecriture privilegiee, il verifie que l'op demandee par l'App
/// figure sur une liste fermee (feuille de registre exacte, service, plan, borne GPU). Tout le reste est
/// refuse. Il existe pour qu'un client compromis ne puisse pas faire ecrire n'importe quoi en administrateur.
/// </summary>
public sealed class OpAllowlist
{
    // Bornes du délai de redémarrage vers le setup firmware. Le plancher est les 10 s de l'App : tout programme du compte peut
    // envoyer cette op (et un JSON sans délai se décode en 0), un redémarrage immédiat couperait donc une partie sans possibilité
    // d'annuler. 300 s est le plafond : au-delà de quelques minutes, l'utilisateur ne relie plus le redémarrage au clic.
    private const int MinFirmwareRebootDelaySeconds = 10;
    private const int MaxFirmwareRebootDelaySeconds = 300;

    // Les feuilles de registre exactes (ruche, chemin, nom) que DrDXT a le droit d'écrire ou de supprimer.
    // La comparaison des chemins ignore la casse (comme le registre) ; la correspondance est
    // exacte sur le chemin complet et sur le nom de valeur, jamais un préfixe ni un sous-arbre.
    private static readonly RegistryLeaf[] AllowedRegistryLeaves =
    {
        // NVIDIA Low Latency Mode (NVTweak). Le levier n'est plus proposé (aucune source
        // ne montre que le pilote lise cette valeur) ; la feuille reste pour défaire une écriture
        // passée. `ReflexEnabled` retirée : aucun levier ne l'écrivait.
        new(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", RegistryValueType.DWord),
        // HAGS (Hardware-Accelerated GPU Scheduling).
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord),
        // Configuration du fichier d'échange (REG_MULTI_SZ). Réparer un pagefile
        // désactivé : c'est un mythe « gaming » répandu, et à la limite de commit Windows
        // tombe en manque de mémoire (freeze/crash, Microsoft : « Introduction to the page file »). Feuille unique : rien
        // d'autre dans « Memory Management » n'est écrivable.
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles", RegistryValueType.MultiString),
        // Les heures d'activité de Windows Update (le réglage de Paramètres, pas la stratégie) :
        // Windows ne redémarre pas automatiquement pendant ces heures (Microsoft Learn, « Manage device restarts after
        // updates »). Deux feuilles exactes : rien d'autre de Windows Update n'est écrivable, et la stratégie reste interdite.
        new(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomDebut, RegistryValueType.DWord),
        new(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomFin, RegistryValueType.DWord),
        // Route AMD X3D / CPPC preferred cores (route AMD prise en charge).
        // `CoalescingTimerInterval` retiré d'ici, aucun levier ne l'écrivait. Et aucune
        // source trouvée ne montre que Windows lise `CppcEnable` (CPPC se règle au BIOS) : à revoir.
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable", RegistryValueType.DWord),
        // Game Bar / mode Jeu de Windows (par utilisateur) : l'autorisation et la bascule récente des
        // Paramètres se posent ensemble pour vraiment activer le mode Jeu.
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", RegistryValueType.DWord),
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueType.DWord),
        // Bascule « enregistrement en arrière-plan » de Game DVR (Record what happened), chirurgicale :
        // la capture manuelle (AppCaptureEnabled) reste intacte.
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", RegistryValueType.DWord),
        // Le VRR global + le flip-model (« optimisations pour les jeux en fenêtré ») vivent dans cette
        // chaîne REG_SZ ; le levier la fusionne (AutoHDREnable et les autres sont gardés).
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", RegistryValueType.String),
        // « Améliorer la précision du pointeur », trois REG_SZ de la session, rendues à l'identique.
        new(RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", RegistryValueType.String),
        new(RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", RegistryValueType.String),
        new(RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", RegistryValueType.String),
    };

    // Sous-arbres toujours refusés, quelle que soit toute autre règle. Un chemin qui commence
    // par l'un d'eux (sans tenir compte de la casse) ne peut jamais être écrit par DrDXT.
    private static readonly string[] BlacklistedSubtrees =
    {
        @"SYSTEM\CurrentControlSet\Services\WinDefend",
        @"SOFTWARE\Microsoft\Windows Defender",
        @"SOFTWARE\Policies\Microsoft\Windows Defender",
        @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate",
        @"SYSTEM\CurrentControlSet\Services\wuauserv",
        @"SYSTEM\CurrentControlSet\Services\BITS",
        @"SYSTEM\CurrentControlSet\Services\bam",
        @"SOFTWARE\Policies\Microsoft\Windows\Safer",
        @"SECURITY",
        @"SAM",
    };

    // L'ensemble fermé des ids de réglage du pilote NVIDIA (DRS) que DrDXT peut écrire, à la feuille
    // près : exactement les ids qu'émettent les leviers de latence de file de rendu et de plafond VRR.
    // Un nom de profil non vide ne suffit pas : tout id hors de cet ensemble est refusé (refus par défaut).
    //   * 0x007BA09E : limite de pré-rendu / Max-Pre-Rendered-Frames (RenderQueueCap)
    //   * 0x00A879CF : VSYNCMODE                                     (VrrVsyncCap)
    //   * 0x10835002 : FRL_FPS (limiteur d'images)                  (VrrVsyncCap)
    //   * 0x1057EB71 : PREFERRED_PSTATE / « Power management mode »  (PreferMaxPerf)
    // Ces ids sont vérifiés mot pour mot contre le NvApiDriverSettings.h officiel de NVIDIA
    // (github.com/NVIDIA/nvapi) : PRERENDERLIMIT_ID = 0x007BA09E, VSYNCMODE_ID = 0x00A879CF,
    // FRL_FPS_ID = 0x10835002, PREFERRED_PSTATE_ID = 0x1057EB71. Les valeurs 0x00fc00fa (VSYNC)
    // et 0x10835000 (FRL) sont fausses : ne pas les reprendre.
    private static readonly uint[] AllowedDrsSettingIds =
    {
        0x007BA09E, 0x00A879CF, 0x10835002, 0x1057EB71,
        // NGX_DLSS_SR_OVERRIDE et NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION (même en-tête) : la surcouche DLSS d'un jeu.
        0x10E41E01, 0x10E41DF3,
    };

    // Le seul lecteur sur lequel DrDXT peut activer la Restauration du système, à la feuille près :
    // le lecteur système. Résolu à l'exécution depuis l'OS, pour être juste sur une installation de
    // Windows hors C: ; « C: » par défaut si l'environnement est illisible. Une demande pour tout
    // autre volume est refusée (jamais d'activation sur un disque quelconque).
    private static readonly string SystemDriveLetter = ResolveSystemDriveLetter();

    // Les seuls services que DrDXT peut piloter, avec les seules actions permises.
    private const string AmdX3dCacheService = "AMD3DVCacheSvc";

    /// <summary>GUID de l'espace des variables EFI globales (spec UEFI). Les varstores
    /// proprietaires des fabricants portent d'autres GUID et ne sont pas permis.</summary>
    public const string EfiGlobalVariableGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";

    /// <summary>
    /// GUID du varstore de configuration AMI, ou vit « Setup » sur les cartes a firmware AMI
    /// (la quasi-totalite des cartes grand public, celle de la machine de reference comprise : AMI 1.H0).
    ///
    /// Mesure le 24/09/2026 sur la machine de reference : « Setup » se lit sous ce GUID
    /// (5081 octets, attributs 0x7), et l'IFR du BIOS officiel 1.H0 declare ce meme GUID pour son
    /// VarStore « Setup ». Ce GUID est celui qu'utilisent les outils de la famille AMISCE/setup_var ;
    /// il n'est pas publie dans une specification que ce depot puisse citer : la source est la
    /// mesure sur la carte. Sous le GUID EFI global, « Setup » rend 203 (ERROR_ENVVAR_NOT_FOUND) :
    /// il vit ailleurs.
    /// </summary>
    public const string AmiSetupVarstoreGuid = "{ec87d643-eba4-4bb5-a1e5-3f3e36b20da9}";

    // Les seules variables firmware que DrDXT lit, par espace de noms. Deux ensembles fermes
    // plutot qu'un, parce que le nom seul ne dit pas le risque : « Setup » sous le GUID
    // constructeur est le varstore de configuration de la carte, et « Setup » sous le GUID EFI
    // global n'existe pas.
    //
    // Lecture seule, et c'est structurel : PrivilegedOpType ne contient aucune op d'ecriture
    // firmware. Ouvrir la lecture de « Setup » n'ouvre donc pas son ecriture : il n'y a pas de
    // chemin a ouvrir. C'est l'ecriture au mauvais offset qui peut rendre une carte non
    // demarrable, et le clear CMOS ne suffit pas toujours.
    //
    // Garde par ReadFirmwareVariable_UnknownOrProprietaryName_IsDenied : ajouter « Setup » a
    // l'ensemble EFI global fait echouer ce test.
    private static readonly IReadOnlySet<string> ReadableEfiGlobalVariables =
        new HashSet<string>(StringComparer.Ordinal) { "BootOrder", "SecureBoot", "BootCurrent" };

    private static readonly IReadOnlySet<string> ReadableVendorVariables =
        new HashSet<string>(StringComparer.Ordinal) { "Setup" };

    /// <summary>GUID du varstore « CpuSetup » (reference Intel), mesure le 24/09/2026 sur la machine
    /// de reference (961 octets) et declare tel quel par l'IFR du BIOS officiel 1.H0.</summary>
    public const string IntelCpuSetupGuid = "{b08f97ff-e6e8-4193-a997-5e9e9b0adb32}";
    private static readonly IReadOnlySet<string> ReadableCpuVariables =
        new HashSet<string>(StringComparer.Ordinal) { "CpuSetup" };
    private static readonly ServiceAction[] AllowedServiceActions =
    {
        ServiceAction.Install, ServiceAction.Start, ServiceAction.Stop, ServiceAction.Query,
    };

    public Result Validate(PrivilegedOp op)
    {
        return op switch
        {
            null => Deny("Null privileged op."),
            PingOp => Result.Ok(),
            CreateRestorePointOp c => c.Description == CreateRestorePointOp.NomDrDxt
                ? Result.Ok()
                : Deny("A restore point is only created under DrDXT's fixed name."),
            InstallPresentMonServiceOp => Result.Ok(),
            EnableSystemRestoreOp e => ValidateEnableSystemRestore(e),

            SetRegistryValueOp r => ValidateRegistry(r.Hive, r.Path, r.Name, r),
            DeleteRegistryValueOp r => ValidateRegistry(r.Hive, r.Path, r.Name, ecriture: null),

            SetStartupApprovalOp sa => ValidateStartupApproval(sa),
            SetActivePowerPlanOp p => KnownPowerPlans.IsKnown(p.PlanGuid)
                ? Result.Ok()
                : Deny("Unknown power-plan GUID."),

            // Un seul reglage du plan, fixe par le type (PERFBOOSTMODE, secteur) ; valeur documentee
            // par Microsoft Learn (0 a 4) seulement. Tout plan nomme est permis : un plan inexistant fait echouer
            // PowerWriteACValueIndex, et le pire qu'une valeur permise fasse est couper ou rendre le boost.
            SetProcessorBoostModeOp b => b.PlanGuid != Guid.Empty && b.AcValue <= ModeDeBoostLu.MaxDocumente
                ? Result.Ok()
                : Deny("Boost mode value is not one Microsoft documents (0 to 4), or the plan is empty."),

            // Un seul reglage du plan, fixe par le type (economie d'energie de la carte sans fil, secteur) ;
            // valeur documentee (0 a 3) seulement ; plan non vide.
            SetWirelessPowerSavingOp w => w.PlanGuid != Guid.Empty && w.AcValue <= EconomieSansFilLue.MaxDocumente
                ? Result.Ok()
                : Deny("Wireless power saving value is not one Windows documents (0 to 3), or the plan is empty."),

            // La limite de puissance du processeur. La seule forme admise : une valeur qui ne diffère de
            // l'origine que par PL1 (bits 15:0), sans verrou, PL1 entre 1 et la PL2 que le registre porte déjà. Jamais de
            // PL2, de fenêtre, de courant ni de tension : le registre ne bouge que là où la carte mère laisse déjà monter.
            // Le plancher de la PL1 (jamais sous le cinquième de la PL2, sauf la PL1 d'usine de ce démarrage) vit dans
            // PawnIoCpuPowerActions, qui connaît cette origine.
            SetCpuPackagePowerLimitOp c => RegistreDeLimiteCpu.NeToucheQuePl1(c.Valeur, c.Origine)
                ? Result.Ok()
                : Deny("CPU power limit write may only change PL1, within the board's PL2, without a lock."),

            // Le décalage de tension. Seulement vers le bas, jamais sous le plancher ; SGX et la relecture sont
            // gardés à l'exécution (PawnIoCpuPowerActions), ce qui n'est pas lisible dans l'op.
            SetCpuVoltageOffsetOp v => v.DecalageMv is <= 0 and >= -TensionCpu.PlancherMv
                ? Result.Ok()
                : Deny("CPU voltage offset may only go down, between 0 and -125 mV."),

            // Le voyage firmware. L'op ne porte qu'un délai (aucune forme de commande) : toute
            // l'autorisation tient dans cette borne. Ce n'est pas décoratif : un délai sans borne laisse un
            // appelant garer un redémarrage loin dans le futur, vécu comme un redémarrage spontané sans
            // cause visible, et un redémarrage que l'utilisateur ne relie à aucun geste est exactement ce
            // qui détruit la confiance dans un outil payant.
            RebootToFirmwareSetupOp r =>
                r.DelaySeconds is >= MinFirmwareRebootDelaySeconds and <= MaxFirmwareRebootDelaySeconds
                    ? Result.Ok()
                    : Deny($"Firmware reboot delay out of band "
                           + $"({MinFirmwareRebootDelaySeconds}..{MaxFirmwareRebootDelaySeconds}s)."),

            // L'annulation est sans condition : refuser d'annuler un redémarrage en attente ne pourrait que
            // nuire à l'utilisateur. Elle ne porte aucun paramètre : rien à valider.
            CancelPendingFirmwareBootOp => Result.Ok(),

            RunServiceControlOp s => ValidateService(s),
            ReadFirmwareVariableOp fv => ValidateFirmwareRead(fv),
            // Lecture sans parametre (DISM /CheckHealth) : rien a valider, rien n'est ecrit.
            ReadComponentStoreHealthOp => Result.Ok(),

            ApplyNvApiClockOffsetOp c => ValidateNvApiClockOffset(c),
            ApplyNvApiPowerLimitOp pl => ValidateNvApiPowerLimit(pl),
            ApplyNvApiDrsSettingOp d => ValidateNvApiDrsSetting(d),
            DeleteNvApiDrsSettingOp dd => ValidateNvApiDrsDelete(dd),
            ApplyAdlxTuningOp a => ValidateAdlx(a),
            ApplyGpuFanCurveOp f => ValidateFanCurve(f),
            // Rendre à l'automatique ne peut que retirer une vitesse fixe.
            RestoreGpuFanAutoOp rf => rf.GpuIndex >= 0 ? Result.Ok() : Deny("Negative GPU index."),

            // Ops du chien de garde des baux : validation structurelle seulement. L'op de retour portée
            // par EstablishLease est elle-même reliée et validée à la feuille près par le chien de garde
            // (un retour hostile ne passe donc jamais) ; ici on ne rejette que des métadonnées de bail
            // manifestement malformées (id absent, durée non positive, retour vide).
            EstablishLeaseOp el => ValidateEstablishLease(el),
            RenewLeaseOp rl => ValidateRenewLease(rl),

            // Le manifeste signe. Ce que le helper signe, le service SYSTEM le rejoue au
            // demarrage et la desinstallation le rejoue elevee : la validation est donc la meme que
            // pour une op executee tout de suite.
            CommitManifestOp cm => ValidateCommitManifest(cm),

            // Le plafond d'envoi QoS. L'inverse ne porte aucun parametre : le refuser ne
            // pourrait que nuire, il est donc inconditionnel (meme regle que CancelPendingFirmwareBoot).
            // Un lanceur ou un binaire de Windows exempte serait le seul a envoyer librement pendant que
            // tout le reste est bride ; retenu pour le service, a chaque session. Meme regle pour les deux ordres.
            ApplyUploadCapOp uc when !NomExeDeJeu.EstUnJeuPossible(uc.GameExeName)
                => Deny("Not a game executable (launcher, background program or Windows binary)."),
            ApplyUploadCapOp uc => ValidateUploadCap(uc),
            RetainServiceUploadCapOp rs when !NomExeDeJeu.EstUnJeuPossible(rs.GameExeName)
                => Deny("Not a game executable (launcher, background program or Windows binary)."),
            RetainServiceUploadCapOp rs => ValidateUploadCap(new ApplyUploadCapOp(rs.GameExeName, rs.CapBitsPerSecond)),
            ForgetServiceUploadCapOp => Result.Ok(),
            RemoveUploadCapOp => Result.Ok(),

            // BCD : ensemble ferme d'elements, valeurs fermees, `{current}` seulement.
            DeleteBcdValueOp db => BcdPermis.ContainsKey(db.Element ?? "")
                ? Result.Ok() : Deny("BCD element is not allowlisted."),
            SetBcdValueOp sb => BcdPermis.TryGetValue(sb.Element ?? "", out var valeurs) && valeurs.Contains(sb.Value ?? "")
                ? Result.Ok() : Deny("BCD element or value is not allowlisted."),
            // Sans parametre : l'entree ({memdiag}) et l'objet ({bootmgr}) sont ecrits en dur dans Win32BcdActions.
            ScheduleMemoryDiagnosticOp => Result.Ok(),
            CancelMemoryDiagnosticOp => Result.Ok(),
            // pnputil : un identifiant de peripherique ou un nom publie oemNN.inf, rien d'autre (ni espace, ni guillemet,
            // ni option : l'argument ne peut pas devenir un commutateur).
            RestartDeviceOp rd => IdentifiantDePeripherique.IsMatch(rd.InstanceId ?? "")
                ? Result.Ok() : Deny("Device instance id is not well formed."),
            RemoveDriverPackageOp rp => PaquetPublie.IsMatch(rp.PublishedName ?? "")
                ? Result.Ok() : Deny("Driver package name is not an oemNN.inf name."),
            RestoreDriverPackageOp rr => PaquetPublie.IsMatch(rr.PublishedName ?? "")
                ? Result.Ok() : Deny("Driver package name is not an oemNN.inf name."),

            // Refus par défaut : tout type d'op non modélisé explicitement plus haut est refusé.
            _ => Deny("Operation type not in the allowlist."),
        };
    }

    // La cible ne suffit pas : un op forge (le rejeu au demarrage en relit depuis un fichier
    // du compte) ecrivait n'importe quel type et n'importe quelle valeur dans une feuille permise. Chaque
    // feuille porte donc son type, et `PagingFiles` ses deux seules valeurs : la gestion automatique que
    // le levier pose, et le vide qu'il a lu et que son Annuler remet. Un fichier d'echange de 16 Mo, ou
    // pose hors d'un disque, est refuse. Limite : le vide reste permis, puisque c'est l'etat d'origine.
    private const string PagingFilesGereParWindows = @"?:\pagefile.sys";

    private static Result ValidateRegistry(RegistryHiveKind hive, string path, string name, SetRegistryValueOp? ecriture)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
            return Deny("Registry op missing path/name.");

        var normalized = NormalizePath(path);
        // L'executeur ecrit `path` tel quel. Dans le registre, une espace ou un antislash de
        // bord change la cle : on ne verifie que ce qui sera ecrit.
        if (!string.Equals(path, normalized, StringComparison.Ordinal))
            return Deny("Registry path must be canonical (no surrounding spaces or backslashes).");

        // La liste noire stricte d'abord : un sous-arbre interdit ne peut jamais être écrit, même si
        // une future règle d'autorisation le recoupe.
        foreach (var banned in BlacklistedSubtrees)
        {
            if (IsUnderSubtree(normalized, NormalizePath(banned)))
                return Deny("Registry target is on the hard blacklist.");
        }

        // Autorisation à la feuille près : le triplet exact (ruche, chemin, nom) doit être dans l'ensemble fermé.
        foreach (var leaf in AllowedRegistryLeaves)
        {
            if (leaf.Hive == hive
                && string.Equals(NormalizePath(leaf.Path), normalized, StringComparison.OrdinalIgnoreCase)
                && string.Equals(leaf.Name, name, StringComparison.Ordinal))
            {
                if (ecriture is null)
                    return Result.Ok();
                if (ecriture.Kind != leaf.Kind)
                    return Deny("Registry value type does not match the allowlisted leaf.");
                if (leaf.Kind == RegistryValueType.MultiString && !PagingFilesPermis(ecriture.Value))
                    return Deny("Page file value is not one DrDXT writes.");
                // HAGS, 2 = allume (le levier), 1 = eteint (son inverse ; le levier n'est
                // propose que HAGS eteint). Toute autre valeur est un op forge.
                if (leaf.Name == "HwSchMode" && ecriture.Value is not ("1" or "2"))
                    return Deny("HwSchMode value is not one DrDXT writes.");
                // Une heure d'activité est une heure, 0 à 23 (Microsoft Learn), écrite sans zéro de tête.
                if (string.Equals(NormalizePath(leaf.Path), HeuresActives.Cle, StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(ecriture.Value, System.Globalization.NumberStyles.None,
                               System.Globalization.CultureInfo.InvariantCulture, out var heure)
                           && heure is >= 0 and <= 23
                           && ecriture.Value == heure.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        ? Result.Ok()
                        : Deny("Active hours value is not an hour (0 to 23).");
                // Les autres feuilles machine sont des interrupteurs (0 à 2).
                // Celles du compte restent libres : l'utilisateur les écrit déjà sans droits.
                if (hive == RegistryHiveKind.LocalMachine && leaf.Kind == RegistryValueType.DWord
                    && ecriture.Value is not ("0" or "1" or "2"))
                    return Deny("Machine registry value is not one DrDXT writes.");
                return Result.Ok();
            }
        }

        return Deny("Registry target is not an allowlisted leaf.");
    }

    // Memes lignes que l'executeur ecrit : decoupees sur '\n', rognees, les vides retirees.
    // Trois listes, deux ruches (Run32 n'existe qu'en machine), un nom simple : la valeur ecrite ne vise que StartupApproved,
    // jamais un autre chemin. La securite de Windows (SecurityHealth) ne se coupe pas.
    private static Result ValidateStartupApproval(SetStartupApprovalOp s)
    {
        if (s.Hive is not (RegistryHiveKind.CurrentUser or RegistryHiveKind.LocalMachine))
            return Deny("Startup approval hive is not allowlisted.");
        if (s.Liste is not (SetStartupApprovalOp.Run or SetStartupApprovalOp.Run32 or SetStartupApprovalOp.DossierDemarrage)
            || (s.Liste == SetStartupApprovalOp.Run32 && s.Hive != RegistryHiveKind.LocalMachine))
            return Deny("Startup approval list is not allowlisted.");
        if (string.IsNullOrWhiteSpace(s.Nom) || s.Nom.Length > 260 || s.Nom.IndexOfAny(['\\', '/']) >= 0 || s.Nom.Any(char.IsControl))
            return Deny("Startup approval name is not a plain entry name.");
        if (!s.Actif && s.Nom.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase))
            return Deny("Windows Security stays at startup.");
        return Result.Ok();
    }

    private static bool PagingFilesPermis(string? valeur)
    {
        var entrees = (valeur ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        // Ou ce que l'Annuler de fixit.pagefile_ssd remet : des tailles gérées par le système sur des lecteurs nommés.
        return entrees.Length == 0
            || (entrees.Length == 1 && string.Equals(entrees[0], PagingFilesGereParWindows, StringComparison.Ordinal))
            || (entrees.Length <= 26 && entrees.All(FichierDEchangeADeplacer.EntreeGereeParLeSysteme));
    }

    private static Result ValidateService(RunServiceControlOp s)
    {
        if (!string.Equals(s.ServiceName, AmdX3dCacheService, StringComparison.Ordinal))
            return Deny("Service is not allowlisted.");
        if (Array.IndexOf(AllowedServiceActions, s.Action) < 0)
            return Deny("Service action is not allowlisted.");
        return Result.Ok();
    }

    // Lecture firmware. Deux verrous, dans cet ordre : l'espace de noms d'abord, puis le nom
    // feuille-exacte dans cet espace. Comparaison Ordinal des deux cotes : les variables EFI sont
    // sensibles a la casse, et une comparaison laxiste elargirait le mur sans le dire.
    //
    // Le varstore constructeur est ouvert, en lecture. Ce qui le justifie n'est pas le niveau
    // de risque, c'est ce que le produit peut prouver : sans cette lecture, au retour d'un redemarrage
    // dans le BIOS, l'app ne saurait pas si le client a fait le reglage ; elle l'infererait, et
    // XmpInferencer s'etiquette lui-meme « probably ». Lire la variable met fin a ce silence.
    //
    // Le motif qui a ferme SCEWIN ne s'applique pas ici : SCEWIN charge un driver signe par
    // IOCTL, ce qui ferait de DrDXT un vecteur BYOVD. Ce chemin-ci est
    // GetFirmwareEnvironmentVariableExW : l'API Windows, avec un privilege du jeton. Aucun
    // driver, rien a charger.
    // À la feuille près : un nom d'executable nu (pas de chemin, pas de guillemet, pas d'espace de
    // commande) et un plafond borne. Le nom finit dans une commande PowerShell : cette regex est
    // ce qui rend l'interpolation sure, elle ne se relache pas. Elle vit dans Core
    // (NomExeDeJeu), lue aussi par l'appli avant de rallumer le plafond : une seule regle.
    public const long MinUploadCapBps = 1_000_000;        // 1 Mbit/s
    public const long MaxUploadCapBps = 10_000_000_000;   // 10 Gbit/s

    // Les elements que `BcdInterpretation` sait juger, et seulement les valeurs documentees par
    // « BCDEdit /set » (source : learn.microsoft.com, lue le 17/09/2026). Comparaison exacte, casse comprise :
    // bcdedit accepte les deux, mais une allowlist qui tolere tout ne ferme plus rien.
    private static readonly IReadOnlyDictionary<string, string[]> BcdPermis = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["bootstatuspolicy"] = ["DisplayAllFailures", "IgnoreAllFailures", "IgnoreShutdownFailures", "IgnoreBootFailures"],
        ["useplatformclock"] = ["Yes", "No"],
        ["useplatformtick"] = ["Yes", "No"],
        ["disabledynamictick"] = ["Yes", "No"],
        ["tscsyncpolicy"] = ["Default", "Legacy", "Enhanced"],
    };

    private static Result ValidateUploadCap(ApplyUploadCapOp op)
    {
        if (!NomExeDeJeu.EstValide(op.GameExeName))
            return Deny("Game executable must be a bare .exe name.");
        if (op.CapBitsPerSecond is < MinUploadCapBps or > MaxUploadCapBps)
            return Deny($"Upload cap out of band ({MinUploadCapBps}..{MaxUploadCapBps} bit/s).");
        return Result.Ok();
    }

    private static Result ValidateFirmwareRead(ReadFirmwareVariableOp op)
    {
        var allowedNames = op.VendorGuid switch
        {
            _ when string.Equals(op.VendorGuid, EfiGlobalVariableGuid, StringComparison.Ordinal)
                => ReadableEfiGlobalVariables,
            _ when string.Equals(op.VendorGuid, AmiSetupVarstoreGuid, StringComparison.Ordinal)
                => ReadableVendorVariables,
            // Le bloc processeur (C-states, limites de puissance), en lecture, meme regle.
            _ when string.Equals(op.VendorGuid, IntelCpuSetupGuid, StringComparison.Ordinal)
                => ReadableCpuVariables,
            // Tout autre espace de noms reste hors de portee par construction.
            _ => null,
        };

        if (allowedNames is null)
            return Deny("Firmware variable GUID is not an allowlisted namespace.");
        if (!allowedNames.Contains(op.Name))
            return Deny("Firmware variable is not allowlisted for reading in this namespace.");
        return Result.Ok();
    }

    // À la feuille près : la Restauration du système ne s'active que sur le lecteur système.
    // La lettre est ramenée à la forme canonique « X: » et comparée exactement au lecteur
    // système résolu ; tout le reste (disque de données, chemin, UNC, déchet) est refusé.
    // Activer la restauration sur un volume quelconque n'est jamais permis.
    private static Result ValidateEnableSystemRestore(EnableSystemRestoreOp e)
    {
        var normalized = NormalizeDriveLetter(e.DriveLetter);
        if (normalized is null)
            return Deny("Invalid drive letter for EnableSystemRestore.");
        return string.Equals(normalized, SystemDriveLetter, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Deny("EnableSystemRestore is only allowed on the system drive.");
    }

    // Le helper élevé impose lui-même la règle de bornes des valeurs GPU : il ne fait jamais
    // confiance à l'appelant (ni au bornage GpuSafeBounds côté client) pour la plage. Un décalage
    // cœur/mémoire hors de l'enveloppe est rejeté ici (refus par défaut), pas borné en silence :
    // une op IPC brute (un smoke test tools/r2*, ou une future version de l'App qui branche les
    // coutures de réglage) ne peut jamais pousser un décalage hors règle sur le vrai matériel. La
    // plage est reprise dans NvApiTuningMath, liée à GpuSafeBounds (source unique : voir le test de synchro).
    private static Result ValidateNvApiClockOffset(ApplyNvApiClockOffsetOp c)
    {
        if (c.GpuIndex < 0)
            return Deny("Negative GPU index.");
        if (!NvApiTuningMath.IsCoreOffsetWithinHardRail(c.CoreOffsetMhz))
            return Deny($"Core-clock offset {c.CoreOffsetMhz} MHz is outside the GPU safe-bounds policy band " +
                        $"[{NvApiTuningMath.MinCoreOffsetMhz}, {NvApiTuningMath.MaxCoreOffsetMhz}] MHz.");
        if (!NvApiTuningMath.IsMemoryOffsetWithinHardRail(c.MemoryOffsetMhz))
            return Deny($"Memory-clock offset {c.MemoryOffsetMhz} MHz is outside the GPU safe-bounds policy band " +
                        $"[{NvApiTuningMath.MinMemoryOffsetMhz}, {NvApiTuningMath.MaxMemoryOffsetMhz}] MHz.");
        return Result.Ok();
    }

    private static Result ValidateNvApiPowerLimit(ApplyNvApiPowerLimitOp pl)
    {
        if (pl.GpuIndex < 0)
            return Deny("Negative GPU index.");
        if (pl.PowerLimitPercent is < 0 or > 100)
            return Deny("Power-limit percent out of 0..100 range.");
        return Result.Ok();
    }

    /// <summary>La portée DRS de toute la machine : le profil de base du pilote.</summary>
    public const string BaseProfileName = "Base Profile";

    /// <summary>Cette portee est-elle la portee machine entiere ? Un seul endroit repond, et
    /// l'executeur s'en sert pour choisir entre l'ecriture globale et l'ecriture par jeu : deux
    /// definitions de « profil de base » finiraient par diverger, et la divergence enverrait une
    /// ecriture au mauvais endroit.</summary>
    public static bool IsBaseProfileScope(string? profileName) =>
        string.Equals(profileName, BaseProfileName, StringComparison.Ordinal);

    private static Result ValidateNvApiDrsSetting(ApplyNvApiDrsSettingOp d)
    {
        if (string.IsNullOrWhiteSpace(d.ProfileName))
            return Deny("DRS profile name is required.");

        // Deux portees, et exactement deux.
        //
        // Pourquoi cette forme : si l'op portait un nom de profil que personne n'honore, « cod.exe »
        // et « Base Profile » produiraient la meme ecriture machine entiere, et une chaine non vide
        // suffirait a passer : un appelant qui demande un changement par jeu en obtiendrait un pour
        // toute la machine, en silence. L'executeur sait viser un profil d'application : la regle ouvre
        // cette portee-la en plus du profil de base, et elle seule.
        //
        // Un chemin complet est exige, pas un nom nu : NVIDIA rend
        // NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS sur « cod.exe », donc accepter un nom nu ferait
        // echouer l'ecriture au bord natif apres avoir laisse croire qu'elle etait autorisee.
        if (!IsBaseProfileScope(d.ProfileName) && !IsQualifiedExecutablePath(d.ProfileName))
            return Deny($"A DRS scope must be '{BaseProfileName}' or the FULL path to a game "
                      + "executable (NVIDIA rejects a bare file name as ambiguous).");

        // À la feuille près : le SettingId doit être l'un des réglages connus du pilote.
        // Un nom de profil non vide ne suffit jamais à lui seul.
        if (Array.IndexOf(AllowedDrsSettingIds, d.SettingId) < 0)
            return Deny("DRS setting id is not allowlisted.");
        // La valeur aussi : l'énumération documentée par NVIDIA, rien de plus strict (un inverse rejoue la valeur lue chez le
        // joueur). Limite assumée : une valeur documentée mais gênante (un plafond bas sur le profil de base) reste admise,
        // réversible par le joueur dans le panneau NVIDIA ; voir docs/NON-NUISANCE.md.
        return ValeursDrs.Documentee(d.SettingId, d.Value)
            ? Result.Ok()
            : Deny($"DRS value 0x{d.Value:X} for setting 0x{d.SettingId:X8} is outside NVIDIA's documented values.");
    }


    /// <summary>
    /// La suppression d'un reglage au profil d'un jeu. Plus etroite que
    /// l'ecriture, et chaque refus a sa raison :
    ///   * portee = un chemin d'executable complet, jamais le profil de base : supprimer au global
    ///     effacerait un reglage pour toute la machine, et ce n'est l'inverse de rien que le
    ///     produit ecrive ;
    ///   * identifiant dans l'ensemble ferme, comme pour l'ecriture ;
    ///   * provenance attendue = un etat que le profil ne possedait pas (herite, defaut, livre).
    ///     `SetByUser` se restaure par reecriture, jamais par suppression ; `Unknown` ne fonde
    ///     aucun geste ; une valeur hors enum est un fil forge.
    /// </summary>
    private static Result ValidateNvApiDrsDelete(DeleteNvApiDrsSettingOp d)
    {
        if (string.IsNullOrWhiteSpace(d.ProfileName) || IsBaseProfileScope(d.ProfileName)
            || !IsQualifiedExecutablePath(d.ProfileName))
            return Deny("A DRS delete must target the FULL path to a game executable; "
                      + "nothing is ever deleted from the base profile.");

        if (Array.IndexOf(AllowedDrsSettingIds, d.SettingId) < 0)
            return Deny("DRS setting id is not allowlisted.");

        if (!Enum.IsDefined(d.OrigineAttendue)
            || d.OrigineAttendue is DrsValueOrigin.SetByUser or DrsValueOrigin.Unknown)
            return Deny("A DRS delete only restores a setting the game's profile did not own "
                      + "(inherited, driver default, or shipped by NVIDIA).");

        return Result.Ok();
    }

    /// <summary>
    /// Un chemin d'executable pleinement qualifie, valide ordinalement et sans toucher au disque
    /// (le helper ne doit pas dependre de ce qui est monte a cet instant).
    ///
    /// Ce qui est refuse, et pourquoi :
    ///   * un nom nu (« cod.exe ») : NVIDIA le rejette comme ambigu ;
    ///   * un chemin relatif ou une remontee « .. » : la portee doit etre lisible telle quelle ;
    ///   * un chemin UNC : un profil de jeu vit sur la machine, pas sur un partage ;
    ///   * autre chose que « .exe » : le seul type de fichier auquel un profil DRS s'applique.
    /// </summary>
    internal static bool IsQualifiedExecutablePath(string path)
    {
        // Path.IsPathFullyQualified fait le gros du travail, et c'est deliberement du BCL :
        // reecrire la reconnaissance d'un chemin Windows a la main est le genre de code ou
        // l'on oublie un cas, et un cas oublie ici est une portee acceptee a tort.
        if (path.Length < 4 || path.Length > 260) return false;
        if (path.Any(char.IsControl)) return false;
        if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return false;
        if (!System.IO.Path.IsPathFullyQualified(path)) return false;
        // Exclut l'UNC : un chemin pleinement qualifie peut etre un partage reseau, et un
        // profil de jeu vit sur la machine. Une lettre de lecteur suivie de ':' l'exige.
        if (!char.IsAsciiLetter(path[0]) || path[1] != ':') return false;
        // Aucune remontee : la portee doit se lire telle quelle, sans resoudre le disque.
        if (path.Contains("..", StringComparison.Ordinal)) return false;
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static Result ValidateAdlx(ApplyAdlxTuningOp a)
    {
        if (a.GpuIndex < 0)
            return Deny("Negative GPU index.");
        if (!Enum.IsDefined(typeof(AdlxTuningKind), a.Kind))
            return Deny("Unknown ADLX tuning kind.");
        return Result.Ok();
    }

    // Bornes structurelles de défense en profondeur pour une courbe de ventilateur (indépendantes
    // de GpuSafeBounds de la couche Application, la borne principale placée devant) : un point chaud
    // ne doit jamais être silencieux (anti-surchauffe), et chaque point doit être dans une plage
    // physique. La borne métier prudente est GpuSafeBounds (Phase E).
    private const int FanHotTempThresholdC = 80;
    private const int FanMinDutyAtHotTempPct = 40;

    private static Result ValidateFanCurve(ApplyGpuFanCurveOp f)
    {
        if (f.GpuIndex < 0)
            return Deny("Negative GPU index.");
        if (f.Points is null || f.Points.Count == 0)
            return Deny("Fan curve has no points.");
        foreach (var p in f.Points)
        {
            if (p.TempC is < 0 or > 120)
                return Deny("Fan-curve temperature out of physical range.");
            if (p.DutyPct is < 0 or > 100)
                return Deny("Fan-curve duty out of 0..100 range.");
            // Anti-surchauffe : un point chaud ne peut jamais tourner sous le cycle minimal sûr.
            if (p.TempC >= FanHotTempThresholdC && p.DutyPct < FanMinDutyAtHotTempPct)
                return Deny("Fan curve would run silent at a hot temperature.");
        }
        // Le point qui gouverne 80 °C (le dernier à 80 °C ou moins) tient aussi le
        // plancher, sinon le pilote, qu'il tienne ou interpole, descend sous 40 % au-dessus de 80 °C.
        if (f.Points.Where(p => p.TempC <= FanHotTempThresholdC).MaxBy(p => p.TempC) is { } g
            && g.DutyPct < FanMinDutyAtHotTempPct)
            return Deny("Fan curve would run below the hot-temperature floor just above 80 °C.");
        return Result.Ok();
    }

    private static Result ValidateEstablishLease(EstablishLeaseOp el)
    {
        if (string.IsNullOrWhiteSpace(el.LeaseId))
            return Deny("Lease op missing lease id.");
        if (el.LeaseSeconds <= 0)
            return Deny("Lease duration must be positive.");
        if (string.IsNullOrWhiteSpace(el.RevertOpJson))
            return Deny("Lease op missing revert payload.");
        return Result.Ok();
    }

    private static Result ValidateRenewLease(RenewLeaseOp rl)
    {
        if (string.IsNullOrWhiteSpace(rl.LeaseId))
            return Deny("Lease op missing lease id.");
        if (rl.LeaseSeconds <= 0)
            return Deny("Lease duration must be positive.");
        return Result.Ok();
    }

    // ── CommitManifestOp ─────────────────────────────────────────────────────────
    //
    // Une entree du manifeste n'est pas une op executee maintenant, mais elle le sera : les `Tweaks` par
    // le reconciliateur SYSTEM au demarrage (via DefaultReapplyOpMapper), toutes les origines par la
    // desinstallation (SafetyManager decode l'origine en op typee). La regle est donc :
    //   * identifiants fermes : `Tweaks` = la table du reconciliateur, `RevertOnly` = le catalogue des
    //     candidats de l'autopilote ;
    //   * `Tweaks` : la valeur voulue doit se traduire en op, et cette op doit passer cette allowlist ;
    //   * origine : si elle se decode en op, ce doit etre une op de retour (registre, plan, DRS, service)
    //     qui passe cette allowlist, jamais un redemarrage firmware, un bail ou un autre commit ; si elle
    //     ne se decode pas (instantane composite), elle reste une donnee bornee que la desinstallation
    //     refuse en le disant (SAFETY.SNAPSHOT_INVALID) ;
    //   * tailles bornees, pas de doublon.
    private const int MaxEntreesParListe = 32;
    private const int MaxLongueurIdentifiant = 128;
    private const int MaxLongueurValeur = 4096;

    private static readonly DrDxt.Reconciler.IReconcilabilityPolicy PolitiqueDuManifeste =
        new DrDxt.Reconciler.DefaultReconcilabilityPolicy();

    private static readonly DrDxt.Reconciler.IReapplyOpMapper ReaffirmationDuManifeste =
        new DrDxt.Reconciler.DefaultReapplyOpMapper(new DrDxt.Reconciler.DefaultReconcilableTweakCatalog());

    private Result ValidateCommitManifest(CommitManifestOp c)
    {
        if (c.Tweaks is null || c.RevertOnly is null)
            return Deny("Manifest commit is missing an entry list.");
        if (c.Tweaks.Count > MaxEntreesParListe || c.RevertOnly.Count > MaxEntreesParListe)
            return Deny($"Manifest commit carries more than {MaxEntreesParListe} entries in a list.");
        // Retirer un levier annulé. Seuls des identifiants bornés et imprimables ; retirer ne fait
        // qu'ôter une entrée (rien n'est exécuté, et la désinstallation rendra moins, jamais autre chose).
        var retrait = c.Retirer ?? Array.Empty<string>();
        if (retrait.Count > MaxEntreesParListe)
            return Deny($"Manifest commit removes more than {MaxEntreesParListe} entries.");
        if (retrait.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > MaxLongueurIdentifiant || id.Any(char.IsControl)))
            return Deny("Manifest removal id is empty, too long or not printable.");
        if (!c.RepartirDUnManifesteVide && c.Tweaks.Count + c.RevertOnly.Count + retrait.Count == 0)
            return Deny("Manifest commit carries nothing to commit.");

        foreach (var liste in new[] { c.Tweaks, c.RevertOnly })
        {
            var vus = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in liste)
            {
                if (t is null) return Deny("Manifest commit carries a null entry.");
                var forme = ValiderFormeDEntree(t);
                if (!forme.IsOk) return forme;
                if (!vus.Add(t.TweakId)) return Deny($"Manifest commit repeats '{t.TweakId}' in one list.");
            }
        }

        foreach (var t in c.Tweaks)
        {
            if (!PolitiqueDuManifeste.IsReconcilable(t.TweakId))
                return Deny($"'{t.TweakId}' is not a reconcilable manifest entry.");
            var reaffirmee = ReaffirmationDuManifeste.Map(t.TweakId, t.DesiredValueJson);
            if (!reaffirmee.IsOk || reaffirmee.Value is null)
                return Deny($"The desired value of '{t.TweakId}' does not translate to a re-appliable op.");
            var autorisee = Validate(reaffirmee.Value);
            if (!autorisee.IsOk)
                return Deny($"The desired value of '{t.TweakId}' re-applies an op this helper refuses.");
            var origine = ValiderOrigine(t);
            if (!origine.IsOk) return origine;
        }

        foreach (var t in c.RevertOnly)
        {
            if (CatalogueDeCandidats.Decrire(t.TweakId) is null)
                return Deny($"'{t.TweakId}' is not a known lever.");
            var origine = ValiderOrigine(t);
            if (!origine.IsOk) return origine;
        }

        return Result.Ok();
    }

    private static Result ValiderFormeDEntree(AppliedTweak t)
    {
        if (string.IsNullOrWhiteSpace(t.TweakId) || t.TweakId.Length > MaxLongueurIdentifiant
            || t.TweakId.Any(char.IsControl))
            return Deny("Manifest entry id is empty, too long or not printable.");
        if (t.DesiredValueJson is null || t.OriginalValueJson is null)
            return Deny($"Manifest entry '{t.TweakId}' is missing a value.");
        if (t.DesiredValueJson.Length > MaxLongueurValeur || t.OriginalValueJson.Length > MaxLongueurValeur)
            return Deny($"Manifest entry '{t.TweakId}' carries a value longer than {MaxLongueurValeur} characters.");
        // L'origine complete des composites. Le helper ne l'execute jamais (elle sert a lire
        // la valeur courante a la desinstallation) : il la borne, comme le reste de ce qu'une App non
        // elevee fait ecrire dans la racine durcie.
        if (t.PoseesJson is not null && t.PoseesJson.Length > MaxLongueurValeur)
            return Deny($"Manifest entry '{t.TweakId}' carries posed values longer than {MaxLongueurValeur} characters.");
        return Result.Ok();
    }

    private Result ValiderOrigine(AppliedTweak t)
    {
        if (string.IsNullOrWhiteSpace(t.OriginalValueJson))
            return Deny($"Manifest entry '{t.TweakId}' has no original value to restore.");

        var decodee = PrivilegedOpCodec.Deserialize(t.OriginalValueJson);
        if (!decodee.IsOk || decodee.Value is null)
            return Result.Ok(); // instantane composite : donnee bornee, jamais executee telle quelle.

        // La limite de puissance (NVIDIA, AMD), Anti-Lag, FRTC et la Vsync du pilote AMD se rendent aussi. Jamais
        // fréquence, tension ni ventilateur (règle de la carte) : la sous-tension ADLX reste refusée.
        var estUnRetour = decodee.Value is SetRegistryValueOp or DeleteRegistryValueOp or SetActivePowerPlanOp
            or SetProcessorBoostModeOp
            or SetCpuPackagePowerLimitOp or SetWirelessPowerSavingOp
            or ApplyNvApiDrsSettingOp or DeleteNvApiDrsSettingOp or RunServiceControlOp
            or ApplyNvApiPowerLimitOp
            or ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit or AdlxTuningKind.AntiLag or AdlxTuningKind.FrameRateTarget or AdlxTuningKind.VerticalRefresh };
        if (!estUnRetour)
            return Deny($"The original value of '{t.TweakId}' is a {decodee.Value.GetType().Name}, not a restore op.");
        return Validate(decodee.Value).IsOk
            ? Result.Ok()
            : Deny($"The original value of '{t.TweakId}' restores through an op this helper refuses.");
    }

    private static Result Deny(string message) => Result.Fail(ErrorCodes.NotAllowlisted, message);

    // Un identifiant d'instance (Microsoft Learn, « Device Instance IDs ») : l'enumerateur, une barre, puis l'identifiant
    // materiel et l'instance. Lettres, chiffres et les separateurs qu'on y voit (& _ . - # { } \), 200 caracteres au plus.
    private static readonly System.Text.RegularExpressions.Regex IdentifiantDePeripherique =
        new(@"^[A-Za-z0-9_]{1,32}\\[A-Za-z0-9_&.\-#{}\\:+,]{1,200}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Le nom que Windows donne a un paquet tiers publie dans son magasin (Microsoft Learn, « PnPUtil » : oem<n>.inf).
    private static readonly System.Text.RegularExpressions.Regex PaquetPublie =
        new(@"^oem[0-9]{1,5}\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Retire les barres de début et de fin et ramène la comparaison à une forme canonique.
    private static string NormalizePath(string path) => path.Trim().Trim('\\');

    // Le lecteur système sous la forme canonique « X: » (par exemple « C: ») ; repli sur « C: ». Ne lève jamais.
    // Lu depuis l'API (racine du dossier Windows), plus depuis %SystemDrive%. Une
    // variable d'environnement se redefinit dans les variables de l'utilisateur, que ce processus
    // eleve herite : elle aurait designe n'importe quel disque.
    internal static string ResolveSystemDriveLetter()
    {
        try
        {
            var sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var normalized = NormalizeDriveLetter(sysDrive);
            return normalized ?? "C:";
        }
        catch
        {
            return "C:";
        }
    }

    // Ramène une lettre de lecteur à « X: » (une seule lettre ASCII + deux-points, sans barre), ou
    // rend null si l'entrée n'est pas une simple lettre de lecteur. Accepte « C », « C: »,
    // « C:\\ » sans tenir compte de la casse ; refuse chemins, UNC, plusieurs caractères et entrée vide.
    private static string? NormalizeDriveLetter(string? drive)
    {
        if (string.IsNullOrWhiteSpace(drive))
            return null;
        var trimmed = drive.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 1 && char.IsLetter(trimmed[0]))
            return char.ToUpperInvariant(trimmed[0]) + ":";
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            return char.ToUpperInvariant(trimmed[0]) + ":";
        return null;
    }

    // Vrai si 'path' est égal à 'subtree' ou en est une clé enfant (par segment, pour que
    // « ...\bam » ne corresponde pas à « ...\bamboo »). La comparaison ignore la casse.
    private static bool IsUnderSubtree(string path, string subtree)
    {
        if (string.Equals(path, subtree, StringComparison.Ordinal))
            return true;
        return path.StartsWith(subtree + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct RegistryLeaf(RegistryHiveKind Hive, string Path, string Name, RegistryValueType Kind);
}
