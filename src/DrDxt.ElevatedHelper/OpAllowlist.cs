using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.NvApi;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// OpAllowlist — validate a PrivilegedOp BY TYPE + by target, leaf-exact.
//
// SECURITY (master plan §9.13 — "FEUILLE EXACTE, pas sous-arbre"): this is the
// real authorization, the defense n°1, not the transport HMAC. Every op is
// checked against a CLOSED, leaf-exact allowlist:
//   * Registry ops: only an explicit set of (hive, path, name) leaves. A
//     blacklisted subtree (Defender / Windows Update / BITS / bam / security
//     policies) is always denied; anything not explicitly allowed is denied
//     (default-DENY). "Subtree allowed" is never sufficient.
//   * Power plan: only the known Windows plan GUIDs.
//   * Service control: only AMD3DVCacheSvc (+ the PresentMon service via the
//     dedicated InstallPresentMonServiceOp) with install/start/stop/query.
//   * NVAPI clock-offset ops: the helper ENFORCES the GpuSafeBounds policy band
//     Itself here (core + memory offset rejected if out of band — it never defers
//     value bounds to the client). Other NVAPI/ADLX ops: structural validation
//     (index/kind/range), power-limit bounded 0..100 + driver-clamped.
//   * Default = DENY (HELPER.NOT_ALLOWLISTED). A null or unmodelled op is denied.
// -----------------------------------------------------------------------
public sealed class OpAllowlist
{
    // M3 lot 2 — bounds for the firmware-setup reboot delay. 0 is allowed (the UI already
    // confirmed); 300 s is the ceiling because beyond a few minutes the user has moved on and
    // no longer connects the restart to what they clicked.
    private const int MinFirmwareRebootDelaySeconds = 0;
    private const int MaxFirmwareRebootDelaySeconds = 300;

    // Exact (hive, path, name) registry leaves DrDXT is allowed to write/delete.
    // Path comparison is case-insensitive (registry paths are); the match is
    // Exact on the full path AND the value name — never a prefix/subtree.
    private static readonly RegistryLeaf[] AllowedRegistryLeaves =
    {
        // NVIDIA Low Latency Mode (NVTweak). 24/09/2026 : le levier n'est plus proposé (aucune source
        // ne montre que le pilote lise cette valeur) ; la feuille reste pour défaire une écriture
        // passée. `ReflexEnabled` retirée : aucun levier ne l'écrivait.
        new(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", RegistryValueType.DWord),
        // HAGS (Hardware-Accelerated GPU Scheduling).
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord),
        // NS-4 — configuration du fichier d'échange (REG_MULTI_SZ). Réparer un pagefile
        // Désactivé : c'est un mythe « gaming » répandu, et à la limite de commit Windows
        // tombe en manque de mémoire (freeze/crash, Microsoft : « Introduction to the page file »). Feuille unique : rien
        // d'autre dans « Memory Management » n'est écrivable.
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles", RegistryValueType.MultiString),
        // chantier L-WU — les heures d'activité de Windows Update (le réglage de Paramètres, pas la stratégie) :
        // Windows ne redémarre pas automatiquement pendant ces heures (Microsoft Learn, « Manage device restarts after
        // updates »). Deux feuilles exactes : rien d'autre de Windows Update n'est écrivable, et la stratégie reste interdite.
        new(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomDebut, RegistryValueType.DWord),
        new(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomFin, RegistryValueType.DWord),
        // AMD X3D / CPPC preferred-cores route (supported AMD route, §4 P2.3).
        // 24/09/2026 : `CoalescingTimerInterval` retiré d'ici, aucun levier ne l'écrivait. Et aucune
        // source trouvée ne montre que Windows lise `CppcEnable` (CPPC se règle au BIOS) : à revoir.
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable", RegistryValueType.DWord),
        // Windows Game Bar / Game Mode (per-user) — both the allow gate and the modern
        // Settings toggle are set together to actually enable Game Mode (NS-1).
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", RegistryValueType.DWord),
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueType.DWord),
        // Game DVR "background recording" toggle (Record what happened) — surgical: leaves
        // manual capture (AppCaptureEnabled) untouched.
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", RegistryValueType.DWord),
        // VRR global + flip-model ("optimizations for windowed games") live in this REG_SZ
        // string; the tweak MERGES it (preserving AutoHDREnable et al.).
        new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", RegistryValueType.String),
        // MOUSE1 (09/10/2026) : « Améliorer la précision du pointeur », trois REG_SZ de la session, rendues à l'identique.
        new(RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", RegistryValueType.String),
        new(RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", RegistryValueType.String),
        new(RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", RegistryValueType.String),
    };

    // Subtrees that are always denied, regardless of any other rule. A path that
    // starts with one of these (case-insensitive) can never be written by DrDXT.
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

    // The CLOSED set of NVIDIA driver-setting (DRS) ids DrDXT may write — leaf-exact
    // (§9.13), exactly the ids the D2/D3 latency tweaks emit. A non-blank profile name
    // is not sufficient: any id outside this set is denied (default-DENY).
    //   * 0x007BA09E — pre-render-limit / Max-Pre-Rendered-Frames (D2 RenderQueueCap)
    //   * 0x00A879CF — VSYNCMODE                                   (D3 VrrVsyncCap)
    //   * 0x10835002 — FRL_FPS (frame-rate limiter)               (D3 VrrVsyncCap)
    //   * 0x1057EB71 — PREFERRED_PSTATE / "Power management mode"  (NS-2 PreferMaxPerf)
    // These ids are verified verbatim against NVIDIA's official NvApiDriverSettings.h
    // (github.com/NVIDIA/nvapi): PRERENDERLIMIT_ID = 0x007BA09E, VSYNCMODE_ID = 0x00A879CF,
    // FRL_FPS_ID = 0x10835002, PREFERRED_PSTATE_ID = 0x1057EB71. The earlier M2 values
    // 0x00fc00fa (VSYNC) and 0x10835000 (FRL) were WRONG and are superseded.
    private static readonly uint[] AllowedDrsSettingIds =
    {
        0x007BA09E, 0x00A879CF, 0x10835002, 0x1057EB71,
    };

    // The only drive DrDXT may enable System Restore on — leaf-exact (§9.13): the
    // system drive. Resolved at runtime from the OS so it is correct on a non-C:
    // Windows install; defaults to "C:" when the environment can't be read. A request
    // for any other volume is denied (never enable SR on an arbitrary disk).
    private static readonly string SystemDriveLetter = ResolveSystemDriveLetter();

    // The only services DrDXT may control, with the only actions it may take.
    private const string AmdX3dCacheService = "AMD3DVCacheSvc";

    /// <summary>GUID de l'espace des variables EFI GLOBALES (spec UEFI). Les varstores
    /// proprietaires des fabricants portent d'autres GUID et ne sont pas permis.</summary>
    public const string EfiGlobalVariableGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";

    /// <summary>
    /// GUID du varstore de configuration AMI, ou vit « Setup » sur les cartes a firmware AMI
    /// (la quasi-totalite des cartes grand public, celle de la machine de reference comprise : AMI 1.H0).
    ///
    /// Mesure le 24/09/2026 sur la machine de reference : « Setup » se lit sous ce GUID
    /// (5081 octets, attributs 0x7), et l'IFR du BIOS officiel 1.H0 declare ce meme GUID pour son
    /// VarStore « Setup ». Le texte ci-dessous est l'etat d'avant la mesure, garde pour l'histoire :
    /// Candidat a verifier sur la machine, pas un fait acquis. Ce GUID est celui qu'utilisent
    /// les outils de la famille AMISCE/setup_var ; il n'est pas publie dans une specification
    /// que ce depot puisse citer. La regle du projet est de ne pas figer une inconnue technique
    /// sans source : ici la source sera la mesure sur la carte (helperhandshake, etape 5). Tant
    /// que cette mesure n'a pas eu lieu, le seul fait etabli est celui du 27/08 -- « Setup »
    /// rend 203 (ERROR_ENVVAR_NOT_FOUND) sous le GUID EFI global, donc il vit ailleurs.
    /// </summary>
    public const string AmiSetupVarstoreGuid = "{ec87d643-eba4-4bb5-a1e5-3f3e36b20da9}";

    // Les seules variables firmware que DrDXT lit, par espace de noms. Deux ensembles fermes
    // plutot qu'un, parce que le nom seul ne dit pas le risque : « Setup » sous le GUID
    // constructeur est le varstore de configuration de la carte, et « Setup » sous le GUID EFI
    // global n'existe pas.
    //
    // Lecture seule, et c'est structurel : PrivilegedOpType ne contient aucune op d'ecriture
    // firmware. Ouvrir la lecture de « Setup » n'ouvre donc pas son ecriture -- il n'y a pas de
    // chemin a ouvrir. C'est l'ecriture au mauvais offset qui peut rendre une carte non
    // demarrable, et le clear CMOS ne suffit pas toujours.
    //
    // Prouve par mutation le 27/08 sur l'espace global : ajouter « Setup » a l'ensemble EFI fait
    // rougir ReadFirmwareVariable_UnknownOrProprietaryName_IsDenied. Le refus mord.
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

            SetActivePowerPlanOp p => KnownPowerPlans.IsKnown(p.PlanGuid)
                ? Result.Ok()
                : Deny("Unknown power-plan GUID."),

            // 07/10/2026 (L-CPU) : un seul reglage du plan, fixe par le type (PERFBOOSTMODE, secteur) ; valeur documentee
            // par Microsoft Learn (0 a 4) seulement. Tout plan nomme est permis : un plan inexistant fait echouer
            // PowerWriteACValueIndex, et le pire qu'une valeur permise fasse est couper ou rendre le boost.
            SetProcessorBoostModeOp b => b.PlanGuid != Guid.Empty && b.AcValue <= ModeDeBoostLu.MaxDocumente
                ? Result.Ok()
                : Deny("Boost mode value is not one Microsoft documents (0 to 4), or the plan is empty."),

            // M3 lot 2 — the firmware trip. The op carries only a delay (no command shape at
            // all), so the whole authorization is that bound. It is not cosmetic: an unbounded
            // delay lets a caller park a restart far in the future, which the user experiences
            // as a spontaneous reboot with no visible cause — and a reboot they did not connect
            // to any action is exactly what destroys trust in a paid tool.
            RebootToFirmwareSetupOp r =>
                r.DelaySeconds is >= MinFirmwareRebootDelaySeconds and <= MaxFirmwareRebootDelaySeconds
                    ? Result.Ok()
                    : Deny($"Firmware reboot delay out of band "
                           + $"({MinFirmwareRebootDelaySeconds}..{MaxFirmwareRebootDelaySeconds}s)."),

            // The undo is unconditional: refusing to CANCEL a pending reboot could only ever
            // harm the user. It carries no parameter, so there is nothing to validate.
            CancelPendingFirmwareBootOp => Result.Ok(),

            RunServiceControlOp s => ValidateService(s),
            ReadFirmwareVariableOp fv => ValidateFirmwareRead(fv),
            // 25/09/2026 — lecture sans parametre (DISM /CheckHealth) : rien a valider, rien n'est ecrit.
            ReadComponentStoreHealthOp => Result.Ok(),

            ApplyNvApiClockOffsetOp c => ValidateNvApiClockOffset(c),
            ApplyNvApiPowerLimitOp pl => ValidateNvApiPowerLimit(pl),
            ApplyNvApiDrsSettingOp d => ValidateNvApiDrsSetting(d),
            DeleteNvApiDrsSettingOp dd => ValidateNvApiDrsDelete(dd),
            ApplyAdlxTuningOp a => ValidateAdlx(a),
            ApplyGpuFanCurveOp f => ValidateFanCurve(f),
            // 08/10/2026 (GPU-OC) : rendre à l'automatique ne peut que retirer une vitesse fixe.
            RestoreGpuFanAutoOp rf => rf.GpuIndex >= 0 ? Result.Ok() : Deny("Negative GPU index."),

            // Lease watchdog ops: STRUCTURAL validation only. The REVERT op carried by
            // EstablishLease is itself bound + validated leaf-exact by the watchdog (so a
            // hostile revert can never slip through); here we only reject obviously
            // malformed lease metadata (missing id / non-positive duration / empty revert).
            EstablishLeaseOp el => ValidateEstablishLease(el),
            RenewLeaseOp rl => ValidateRenewLease(rl),

            // 11/09/2026 — le manifeste signe. Ce que le helper signe, le service SYSTEM le rejoue au
            // demarrage et la desinstallation le rejoue elevee : la validation est donc la meme que
            // pour une op executee tout de suite.
            CommitManifestOp cm => ValidateCommitManifest(cm),

            // 24/09/2026 — le plafond d'envoi QoS. L'inverse ne porte aucun parametre : le refuser ne
            // pourrait que nuire, il est donc inconditionnel (meme regle que CancelPendingFirmwareBoot).
            // 28/09/2026 : un lanceur ou un binaire de Windows exempte serait le seul a envoyer librement pendant que
            // tout le reste est bride ; retenu pour le service, a chaque session. Meme regle pour les deux ordres.
            ApplyUploadCapOp uc when !NomExeDeJeu.EstUnJeuPossible(uc.GameExeName)
                => Deny("Not a game executable (launcher, background program or Windows binary)."),
            ApplyUploadCapOp uc => ValidateUploadCap(uc),
            RetainServiceUploadCapOp rs when !NomExeDeJeu.EstUnJeuPossible(rs.GameExeName)
                => Deny("Not a game executable (launcher, background program or Windows binary)."),
            RetainServiceUploadCapOp rs => ValidateUploadCap(new ApplyUploadCapOp(rs.GameExeName, rs.CapBitsPerSecond)),
            ForgetServiceUploadCapOp => Result.Ok(),
            RemoveUploadCapOp => Result.Ok(),

            // 24/09/2026 — BCD : ensemble FERME d'elements, valeurs fermees, `{current}` seulement.
            DeleteBcdValueOp db => BcdPermis.ContainsKey(db.Element ?? "")
                ? Result.Ok() : Deny("BCD element is not allowlisted."),
            SetBcdValueOp sb => BcdPermis.TryGetValue(sb.Element ?? "", out var valeurs) && valeurs.Contains(sb.Value ?? "")
                ? Result.Ok() : Deny("BCD element or value is not allowlisted."),

            // Default-DENY: any op type not explicitly modelled above is refused.
            _ => Deny("Operation type not in the allowlist."),
        };
    }

    // 26/09/2026 — la cible ne suffit pas : un op forge (le rejeu au demarrage en relit depuis un fichier
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
        // 26/09/2026 : l'executeur ecrit `path` tel quel. Dans le registre, une espace ou un antislash de
        // bord change la cle : on ne verifie que ce qui sera ecrit.
        if (!string.Equals(path, normalized, StringComparison.Ordinal))
            return Deny("Registry path must be canonical (no surrounding spaces or backslashes).");

        // HARD BLACKLIST first — a blacklisted subtree can never be written, even
        // if some future allow-rule overlaps it.
        foreach (var banned in BlacklistedSubtrees)
        {
            if (IsUnderSubtree(normalized, NormalizePath(banned)))
                return Deny("Registry target is on the hard blacklist.");
        }

        // LEAF-exact allow: exact (hive, path, name) must be in the closed set.
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
                // 26/09/2026 : HAGS, 2 = allume (le levier), 1 = eteint (son inverse ; le levier n'est
                // propose que HAGS eteint). Toute autre valeur est un op forge.
                if (leaf.Name == "HwSchMode" && ecriture.Value is not ("1" or "2"))
                    return Deny("HwSchMode value is not one DrDXT writes.");
                // 06/10/2026 (L-WU) : une heure d'activité est une heure, 0 à 23 (Microsoft Learn), écrite sans zéro de tête.
                if (string.Equals(NormalizePath(leaf.Path), HeuresActives.Cle, StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(ecriture.Value, System.Globalization.NumberStyles.None,
                               System.Globalization.CultureInfo.InvariantCulture, out var heure)
                           && heure is >= 0 and <= 23
                           && ecriture.Value == heure.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        ? Result.Ok()
                        : Deny("Active hours value is not an hour (0 to 23).");
                // 04/10/2026 (revue sécurité 6.1) : les autres feuilles machine sont des interrupteurs (0 à 2).
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
    private static bool PagingFilesPermis(string? valeur)
    {
        var entrees = (valeur ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return entrees.Length == 0
            || (entrees.Length == 1 && string.Equals(entrees[0], PagingFilesGereParWindows, StringComparison.Ordinal));
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
    // feuille-exacte dans CET espace. Comparaison Ordinal des deux cotes : les variables EFI sont
    // sensibles a la casse, et une comparaison laxiste elargirait le mur sans le dire.
    //
    // L3.1 — le varstore CONSTRUCTEUR est ouvert, en lecture. Ce qui a change n'est pas le niveau
    // de risque, c'est ce que le produit peut prouver : jusqu'ici, au retour d'un redemarrage
    // dans le BIOS, l'app ne SAVAIT pas si le client avait fait le reglage -- elle l'inferait, et
    // XmpInferencer s'etiquette lui-meme « probably ». Lire la variable met fin a ce silence.
    //
    // Le motif qui a ferme SCEWIN ne s'applique pas ici : SCEWIN charge un driver signe par
    // IOCTL, ce qui ferait de DrDXT un vecteur BYOVD. Ce chemin-ci est
    // GetFirmwareEnvironmentVariableExW — l'API Windows, avec un privilege du jeton. Aucun
    // driver, rien a charger.
    // LEAF-exact : un nom d'executable NU (pas de chemin, pas de guillemet, pas d'espace de
    // commande) et un plafond borne. Le nom finit dans une commande PowerShell : cette regex est
    // ce qui rend l'interpolation sure, elle ne se relache pas. 26/09/2026 : elle vit dans Core
    // (NomExeDeJeu), lue aussi par l'appli avant de rallumer le plafond : une seule regle.
    public const long MinUploadCapBps = 1_000_000;        // 1 Mbit/s
    public const long MaxUploadCapBps = 10_000_000_000;   // 10 Gbit/s

    // Les elements que `BcdInterpretation` sait juger, et seulement les valeurs documentees par
    // « BCDEdit /set » (learn.microsoft.com, lue le 17/09/2026). Comparaison exacte, casse comprise :
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
            // 24/09/2026 — le bloc processeur (C-states, limites de puissance), en lecture, meme regle.
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

    // LEAF-exact (§9.13): System Restore may be enabled only on the system drive.
    // The drive letter is normalized to the canonical "X:" form and compared exactly
    // to the resolved system drive; anything else (a data disk, a path, a UNC, junk)
    // is denied. Enabling SR on an arbitrary volume is never allowed.
    private static Result ValidateEnableSystemRestore(EnableSystemRestoreOp e)
    {
        var normalized = NormalizeDriveLetter(e.DriveLetter);
        if (normalized is null)
            return Deny("Invalid drive letter for EnableSystemRestore.");
        return string.Equals(normalized, SystemDriveLetter, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Deny("EnableSystemRestore is only allowed on the system drive.");
    }

    // The ELEVATED helper enforces the GPU value-bounds policy itself — it never trusts the
    // caller (or the client-side GpuSafeBounds clamp) for the band. A core/memory offset
    // OUTSIDE the policy envelope is REJECTED here (default-DENY), not silently clamped, so a
    // raw IPC op (a tools/r2* smoke, or a future App build that wires the tuning seams) can
    // never push an out-of-policy offset to live hardware. The band is mirrored in
    // NvApiTuningMath, tied to GpuSafeBounds (single source of truth — see the sync test).
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

    /// <summary>The machine-wide DRS scope: the driver's base profile.</summary>
    public const string BaseProfileName = "Base Profile";

    /// <summary>Cette portee est-elle la portee MACHINE ENTIERE ? Un seul endroit repond, et
    /// l'executeur s'en sert pour choisir entre l'ecriture globale et l'ecriture par jeu : deux
    /// definitions de « profil de base » finiraient par diverger, et la divergence enverrait une
    /// ecriture au mauvais endroit.</summary>
    public static bool IsBaseProfileScope(string? profileName) =>
        string.Equals(profileName, BaseProfileName, StringComparison.Ordinal);

    private static Result ValidateNvApiDrsSetting(ApplyNvApiDrsSettingOp d)
    {
        if (string.IsNullOrWhiteSpace(d.ProfileName))
            return Deny("DRS profile name is required.");

        // L2 — deux portees, et exactement deux.
        //
        // Histoire de cette regle, parce qu'elle explique sa forme : l'op portait un nom de
        // profil que personne n'honorait. « cod.exe » et « Base Profile » produisaient la meme
        // ecriture machine entiere, et une chaine non vide suffisait a passer -- un appelant qui
        // demandait un changement par jeu en obtenait un pour toute la machine, en silence.
        // La regle a d'abord refuse toute portee autre que le profil de base, plutot que de
        // laisser l'op mentir. Maintenant que l'executeur sait viser un profil d'application,
        // elle ouvre cette portee-la, et elle seule.
        //
        // Un chemin complet est exige, pas un nom nu : NVIDIA rend
        // NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS sur « cod.exe », donc accepter un nom nu ferait
        // echouer l'ecriture au bord natif apres avoir laisse croire qu'elle etait autorisee.
        if (!IsBaseProfileScope(d.ProfileName) && !IsQualifiedExecutablePath(d.ProfileName))
            return Deny($"A DRS scope must be '{BaseProfileName}' or the FULL path to a game "
                      + "executable (NVIDIA rejects a bare file name as ambiguous).");

        // Leaf-exact (§9.13): the SettingId must be one of the known driver settings.
        // A non-blank profile name is never sufficient on its own.
        if (Array.IndexOf(AllowedDrsSettingIds, d.SettingId) < 0)
            return Deny("DRS setting id is not allowlisted.");
        return Result.Ok();
    }

    /// <summary>
    /// PHASE 18 F1 lot 3 — la SUPPRESSION d'un reglage au profil d'un jeu. Plus etroite que
    /// l'ecriture, et chaque refus a sa raison :
    ///   * portee = un chemin d'executable complet, jamais le profil de base : supprimer au global
    ///     effacerait un reglage pour toute la machine, et ce n'est l'inverse de rien que le
    ///     produit ecrive ;
    ///   * identifiant dans l'ensemble ferme, comme pour l'ecriture ;
    ///   * provenance attendue = un etat que le profil ne POSSEDAIT pas (herite, defaut, livre).
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
    /// Un chemin d'executable pleinement qualifie, valide ORDINALEMENT et sans toucher au disque
    /// (le helper ne doit pas dependre de ce qui est monte a cet instant).
    ///
    /// Ce qui est refuse, et pourquoi :
    ///   * un nom nu (« cod.exe ») — NVIDIA le rejette comme ambigu ;
    ///   * un chemin relatif ou une remontee « .. » — la portee doit etre lisible telle quelle ;
    ///   * un chemin UNC — un profil de jeu vit sur la machine, pas sur un partage ;
    ///   * autre chose que « .exe » — le seul type de fichier auquel un profil DRS s'applique.
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

    // Defense-in-depth structural bounds for a fan curve (independent of the
    // Application-layer GpuSafeBounds, which is the primary clamp in front of this):
    // a hot point must never be silent (anti-overheat), and every point must be in
    // physical range. The conservative business clamp is GpuSafeBounds (Phase E).
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
            // Anti-overheat: a hot point can never run below the minimum safe duty.
            if (p.TempC >= FanHotTempThresholdC && p.DutyPct < FanMinDutyAtHotTempPct)
                return Deny("Fan curve would run silent at a hot temperature.");
        }
        // 24/09/2026 : le point qui gouverne 80 °C (le dernier à 80 °C ou moins) tient aussi le
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

    // ── 11/09/2026 — CommitManifestOp ─────────────────────────────────────────────────────────
    //
    // Une entree du manifeste n'est pas une op executee maintenant, mais elle le SERA : les `Tweaks` par
    // le reconciliateur SYSTEM au demarrage (via DefaultReapplyOpMapper), toutes les origines par la
    // desinstallation (SafetyManager decode l'origine en op typee). La regle est donc :
    //   * identifiants FERMES : `Tweaks` = la table du reconciliateur, `RevertOnly` = le catalogue des
    //     candidats de l'autopilote ;
    //   * `Tweaks` : la valeur voulue doit se traduire en op, et cette op doit passer cette allowlist ;
    //   * origine : si elle se decode en op, ce doit etre une op de RETOUR (registre, plan, DRS, service)
    //     qui passe cette allowlist — jamais un redemarrage firmware, un bail ou un autre commit ; si elle
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
        // 06/10/2026 : retirer un levier annulé. Seuls des identifiants bornés et imprimables ; retirer ne fait
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
        // 11/09/2026 — l'origine complete des composites. Le helper ne l'EXECUTE jamais (elle sert a lire
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

        // 08/10/2026 : la limite de puissance (NVIDIA, AMD), Anti-Lag, FRTC et la Vsync du pilote AMD se rendent aussi. Jamais
        // fréquence, tension ni ventilateur (règle du 29/09) : la sous-tension ADLX reste refusée.
        var estUnRetour = decodee.Value is SetRegistryValueOp or DeleteRegistryValueOp or SetActivePowerPlanOp
            or SetProcessorBoostModeOp
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

    // Trim leading/trailing slashes and collapse comparison to a canonical form.
    private static string NormalizePath(string path) => path.Trim().Trim('\\');

    // The system drive in canonical "X:" form (e.g. "C:"); falls back to "C:". Never throws.
    // 25/09/2026 : lu depuis l'API (racine du dossier Windows), plus depuis %SystemDrive%. Une
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

    // Canonicalizes a drive letter to "X:" (single ASCII letter + colon, no slash) or
    // returns null when the input is not a plain drive letter. Accepts "C", "C:",
    // "C:\\" case-insensitively; rejects paths, UNC, multi-char, and empty input.
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

    // True if 'path' equals 'subtree' or is a child key of it (segment-aware so
    // "...\bam" does not match "...\bamboo"). Comparison is case-insensitive.
    private static bool IsUnderSubtree(string path, string subtree)
    {
        if (string.Equals(path, subtree, StringComparison.Ordinal))
            return true;
        return path.StartsWith(subtree + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct RegistryLeaf(RegistryHiveKind Hive, string Path, string Name, RegistryValueType Kind);
}
