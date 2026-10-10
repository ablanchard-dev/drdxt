namespace DrDxt.Core;

// --- Elevation ---

public interface IElevatedClient
{
    Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct);

    /// <summary>Heartbeat: extends the expiry of a held tweak's lease in the elevated
    /// helper's dead-man's-switch. A thin wrapper that sends a <see cref="RenewLeaseOp"/>
    /// through the same <see cref="ExecuteAsync"/> IPC pipeline — so it inherits the full
    /// signature → nonce → caller-authz → bind → allowlist path, and is authorized by the
    /// Same caller identity (only the lease owner can renew). A default method so every
    /// existing implementer (fakes, stubs, the unavailable client) gets it for free
    /// without a privileged-path change.</summary>
    Task<Result> RenewLease(string leaseId, int leaseSeconds, CancellationToken ct)
        => ExecuteAsync(new RenewLeaseOp(leaseId, leaseSeconds), ct);

    /// <summary>Envoie une op qui REPOND au lieu d'agir, et rend ses octets. Même chemin
    /// signé/allowlisté que <see cref="ExecuteAsync"/> ; seul le retour diffère.
    ///
    /// Défaut : echec franc. Un client qui n'a pas de canal de retour de données le DIT — il ne
    /// rend pas un tableau vide, qui se lirait comme « le firmware a rendu zéro octet ». Comme
    /// <see cref="RenewLease"/>, c'est une méthode par défaut pour que les implémenteurs
    /// existants (doublures, client indisponible, réconciliateur en processus) héritent du bon
    /// refus sans toucher au chemin privilégié.</summary>
    Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(Result<byte[]>.Fail(
            ErrorCodes.NotAllowlisted, "This elevated client has no data return channel."));
}

// -----------------------------------------------------------------------
// Typed privileged-op contract (master plan §9.13 — "FEUILLE EXACTE").
//
// PrivilegedOp is SEALED at the boundary: the only way to ask the elevated
// helper to do anything is to construct one of the closed sealed leaves below.
// There is NO generic "write this registry key" / "run this command" leaf —
// that banned, WinRing0-class primitive is exactly what we refuse to build.
//
// Each leaf carries TYPED, validated fields (not an opaque blob), so the helper
// can validate it leaf-exact (OpAllowlist) before doing anything privileged.
// -----------------------------------------------------------------------
public abstract record PrivilegedOp;

// M0: the no-op health-check. The closed set of leaves IS the allowlist.
public sealed record PingOp : PrivilegedOp;

// --- Registry leaves (LEAF-exact: hive + path + name + typed value) ---

/// <summary>Sets a single registry value. The (hive, path, name) is the exact
/// leaf the helper validates against its allowlist — never a free subtree.</summary>
public sealed record SetRegistryValueOp(
    RegistryHiveKind Hive,
    string Path,
    string Name,
    RegistryValueType Kind,
    string Value) : PrivilegedOp;

/// <summary>Deletes a single named registry value (used for clean revert).</summary>
public sealed record DeleteRegistryValueOp(
    RegistryHiveKind Hive,
    string Path,
    string Name) : PrivilegedOp;

// --- Power / restore-point leaves ---

/// <summary>Switches the active Windows power plan. The GUID must be one of the
/// known Windows plan GUIDs (validated by the helper allowlist).</summary>
public sealed record SetActivePowerPlanOp(Guid PlanGuid) : PrivilegedOp;

/// <summary>07/10/2026 (L-CPU) : le mode de boost du processeur (PERFBOOSTMODE) sur secteur, dans un plan, et rien
/// d'autre du plan. Le réglage est fixé par le type : l'appelant ne choisit que le plan et la valeur (0 à 4, bornée
/// par le helper). Le helper réactive le plan s'il est actif (PowerWriteACValueIndex : sans PowerSetActiveScheme, la
/// valeur du plan actif ne prend pas effet).</summary>
public sealed record SetProcessorBoostModeOp(Guid PlanGuid, uint AcValue) : PrivilegedOp;

/// <summary>B1 (10/10/2026) : la limite de puissance du processeur Intel (registre MSR_PKG_POWER_LIMIT, 0x610) écrite
/// par PawnIO. <paramref name="Valeur"/> est le registre entier à écrire, <paramref name="Origine"/> le registre tel qu'il
/// a été lu avant : le helper n'écrit que si le registre porte encore l'origine, et seulement une valeur qui ne diffère
/// de l'origine que par PL1 (bits 15:0), sans verrou, PL1 au plus égale à PL2. L'inverse est la même op, les deux
/// valeurs échangées. Volatil : le BIOS remet sa valeur au redémarrage.</summary>
public sealed record SetCpuPackagePowerLimitOp(ulong Valeur, ulong Origine) : PrivilegedOp;

/// <summary>Creates a System Restore point before a batch is applied.</summary>
public sealed record CreateRestorePointOp(string Description) : PrivilegedOp
{
    /// <summary>06/10/2026 (plan 4.5b) : le seul nom sous lequel le helper crée un point. Il retire ses anciens
    /// points d'après ce nom exact : un nom libre ferait retirer les points d'un autre logiciel.</summary>
    public const string NomDrDxt = "DrDXT : avant optimisation";

    /// <summary>Le nom des versions d'avant le 06/10/2026 : ces points-là sont aussi les siens.</summary>
    public const string AncienNom = "DrDXT batch";
}

/// <summary>Enables System Restore for the given drive (e.g. "C:") and provisions a
/// small shadow-storage allocation so a restore point can actually be created. The
/// drive is validated leaf-exact by the helper (only the system drive is allowed) —
/// this never enables SR on an arbitrary volume. Used when a client has System
/// Restore OFF: with explicit consent the app routes this through the helper so the
/// reversibility guarantee (a verifiable restore point) holds before any apply.</summary>
public sealed record EnableSystemRestoreOp(string DriveLetter) : PrivilegedOp;

// --- GPU tuning leaves (Option A: P-state offset + power limit; see master §11) ---

/// <summary>Applies an NVAPI core/memory clock offset (MHz) to a GPU. Bounds are
/// clamped by GpuSafeBounds (Phase E); here the helper validates structure only.</summary>
public sealed record ApplyNvApiClockOffsetOp(
    int GpuIndex,
    int CoreOffsetMhz,
    int MemoryOffsetMhz) : PrivilegedOp;

/// <summary>Applies an NVAPI power-limit (% of TDP) to a GPU.</summary>
public sealed record ApplyNvApiPowerLimitOp(
    int GpuIndex,
    int PowerLimitPercent) : PrivilegedOp;

/// <summary>Applies a single NVIDIA driver setting (DRS) — e.g. Low Latency Mode.
/// SettingId/Value are bounded numeric driver-setting ids, not free strings.</summary>
public sealed record ApplyNvApiDrsSettingOp(
    uint SettingId,
    uint Value,
    string ProfileName) : PrivilegedOp;

/// <summary>PHASE 18 F1 lot 3 — SUPPRIME un reglage du profil d'UN jeu, pour le rendre exactement
/// a ce qu'il etait quand le profil ne le possedait pas (herite, defaut pilote, ou livre par
/// NVIDIA : « deletes a setting or sets it back to predefined value », doc NVAPI). C'est l'INVERSE
/// d'une ecriture au profil du jeu ; il n'existe que pour le revert.
/// <para><see cref="OrigineAttendue"/> est la provenance lue avant l'ecriture : le helper la
/// relit apres la suppression, et c'est elle — pas la valeur seule — qui prouve le retour.
/// <see cref="ValeurAttendue"/> est la valeur d'avant, rapportee, jamais exigee (le global a pu
/// bouger entre-temps).</para></summary>
public sealed record DeleteNvApiDrsSettingOp(
    uint SettingId,
    string ProfileName,
    DrsValueOrigin OrigineAttendue,
    uint ValeurAttendue) : PrivilegedOp;

/// <summary>Applies an AMD ADLX tuning value (anti-lag / undervolt / power).</summary>
public sealed record ApplyAdlxTuningOp(
    int GpuIndex,
    AdlxTuningKind Kind,
    int IntValue) : PrivilegedOp;

/// <summary>Applies a GPU fan curve (NVAPI / ADLX) as an ordered list of
/// (temp °C, duty %) points. A dedicated leaf because a multi-point curve can't be
/// expressed by the single-IntValue tuning ops. The helper validates it leaf-exact
/// (GpuIndex >= 0, every point in-range, hot points never silent); the conservative
/// anti-overheat clamp is GpuSafeBounds (Phase E) in front of this.</summary>
public sealed record ApplyGpuFanCurveOp(
    int GpuIndex,
    IReadOnlyList<FanCurvePointDto> Points) : PrivilegedOp;

/// <summary>One (temp °C, duty %) point of a fan curve on the privileged wire.
/// A plain DTO in Core so the op stays serializable by the bounded codec without a
/// dependency on the Application-layer FanCurve type.</summary>
public sealed record FanCurvePointDto(int TempC, int DutyPct);

/// <summary>GPU-OC (08/10/2026) : rend les ventilateurs d'une carte à leur contrôle automatique. L'inverse d'une vitesse
/// fixe posée par <see cref="ApplyGpuFanCurveOp"/> (un seul point) ; le helper relit la politique pour le prouver.</summary>
public sealed record RestoreGpuFanAutoOp(int GpuIndex) : PrivilegedOp;

// --- Service-control leaf (AMD 3D V-Cache optimizer; PresentMon service) ---

/// <summary>Controls a named Windows service. ServiceName + action are validated
/// leaf-exact (only the AMD3DVCacheSvc + the PresentMon service are allowed).</summary>
public sealed record RunServiceControlOp(
    string ServiceName,
    ServiceAction Action) : PrivilegedOp;

/// <summary>Installs (and starts) the vendored PresentMon measurement service.
/// Carries no free path: the helper installs only the binary it ships, by name.</summary>
public sealed record InstallPresentMonServiceOp : PrivilegedOp;

// --- Lease / dead-man's-switch leaves (R2 auto-revert watchdog) ---

/// <summary>Registers a held/reversible tweak's auto-revert with the elevated
/// helper's in-memory lease watchdog. If the controller dies (or simply stops
/// sending heartbeats), the watchdog runs the recorded revert at expiry so a held
/// privileged tweak (a GPU clock offset / power limit) is never left applied.
///
/// SECURITY: <see cref="RevertOpJson"/> is the codec-JSON of an existing typed
/// PrivilegedOp leaf (the inverse already produced by the Application tweak), not a
/// free command. The watchdog re-binds it through the same bounded PrivilegedOpCodec
/// and re-validates it leaf-exact (OpAllowlist) before ever executing it — there is
/// no new generic privileged primitive. The lease is owned by the AUTHENTICATED
/// caller (process identity); only that owner may renew/complete it. Expiry =
/// now + <see cref="LeaseSeconds"/>; the lease lives in helper memory only (no disk
/// persistence — that would be attack surface).</summary>
public sealed record EstablishLeaseOp(
    string LeaseId,
    string RevertOpJson,
    int LeaseSeconds) : PrivilegedOp;

/// <summary>Heartbeat: extends the expiry of an existing lease (now +
/// <see cref="LeaseSeconds"/>) so the watchdog does not fire while the controller is
/// alive. Goes through the full IPC pipeline and is authorized by the same caller-
/// identity path as every other op: only the lease OWNER (the authenticated caller
/// that established it) may renew it; a different caller is refused (cannot hijack
/// or keep alive another caller's lease).</summary>
public sealed record RenewLeaseOp(
    string LeaseId,
    int LeaseSeconds) : PrivilegedOp;

// -----------------------------------------------------------------------
// Supporting closed enums. Defined in Core (BCL-only, no Microsoft.Win32
// dependency) so Core stays platform-neutral and cross-target testable. The
// helper maps these to the real Win32 enums when it actually writes.
// -----------------------------------------------------------------------

/// <summary>Registry root hive. Neutral mirror of Microsoft.Win32.RegistryHive.</summary>
public enum RegistryHiveKind
{
    LocalMachine = 0,
    CurrentUser = 1,
}

/// <summary>Registry value kind. Neutral mirror of Microsoft.Win32.RegistryValueKind
/// restricted to the kinds DrDXT writes.</summary>
public enum RegistryValueType
{
    DWord = 0,
    QWord = 1,
    String = 2,
    /// <summary>REG_MULTI_SZ. La valeur transportee est une seule chaine dont les LIGNES
    /// sont les entrees (une entree par ligne). Ajoute pour `PagingFiles`, la seule feuille
    /// multi-chaines que DrDXT ecrit — et elle reste soumise a l'allowlist feuille-exacte :
    /// etendre le TYPE n'etend pas la SURFACE, seule une nouvelle feuille le ferait.</summary>
    MultiString = 3,
}

/// <summary>The closed set of service-control actions the helper will accept.</summary>
public enum ServiceAction
{
    Install = 0,
    Start = 1,
    Stop = 2,
    Query = 3,
}

/// <summary>The closed set of AMD ADLX tuning operations DrDXT applies.</summary>
public enum AdlxTuningKind
{
    AntiLag = 0,
    Undervolt = 1,
    PowerLimit = 2,
    /// <summary>A2b (07/10/2026) : FRTC, le plafond d'images du pilote AMD. IntValue &gt; 0 = allumé à cette cible ;
    /// &lt; 0 = éteint, cible rendue à -IntValue (l'inverse exact d'un essai) ; 0 refusé.</summary>
    FrameRateTarget = 3,
    /// <summary>A2c (08/10/2026) : la Vsync du pilote AMD (IADLX3DWaitForVerticalRefresh). IntValue = le mode
    /// ADLX_WAIT_FOR_VERTICAL_REFRESH_MODE : 0 toujours coupée, 1 coupée sauf avis du jeu, 2 activée sauf avis du jeu,
    /// 3 toujours activée.</summary>
    VerticalRefresh = 4,
}

/// <summary>
/// M3 lot 2 — ask Windows to send the NEXT boot into the firmware setup UI, then restart.
///
/// This is <c>shutdown /r /fw</c>: a DOCUMENTED Windows facility that sets the UEFI-spec
/// <c>OsIndications</c> bit 0 for us. DrDXT therefore writes NO firmware variable of its own —
/// the difference between using the spec and hand-rolling NVRAM, and it matters as much to an
/// antivirus as it does to the product rule. Writing the <c>Setup</c> varstore stays FORBIDDEN
/// (docs/M3-BIOS-ecriture-preuves-2026-08-25.md); this op is not a way around that, it is the
/// one firmware gesture the specification actually provides for.
///
/// The op takes only a delay: there is deliberately no "run a command" shape here, so the
/// arguments are fixed by the type and the caller cannot express anything else.
/// </summary>
/// <param name="DelaySeconds">
/// Seconds before the restart, so the UI can show what is about to happen and the user can back
/// out. Bounded by the allowlist — an unbounded delay would let a caller park a reboot far in the
/// future, which the user would experience as a spontaneous restart with no visible cause.
/// </param>
public sealed record RebootToFirmwareSetupOp(int DelaySeconds) : PrivilegedOp;

/// <summary>
/// M3 lot 2 — cancel a pending firmware boot (<c>shutdown /a /fw</c>).
///
/// The undo half, and the reason the trip is safe to offer at all: a request that cannot be
/// withdrawn is not a request, it is a commitment made on the user's behalf.
/// </summary>
public sealed record CancelPendingFirmwareBootOp : PrivilegedOp;

/// <summary>
/// Lit une variable firmware EFI (<c>GetFirmwareEnvironmentVariableExW</c>), en session élevée.
///
/// À quoi ça sert : aujourd'hui, quand le client revient d'un redémarrage dans le BIOS, l'app ne
/// Sait pas s'il a fait le réglage — elle l'infère (<c>XmpInferencer</c> s'étiquette lui-même
/// « probably »). Lire la variable met fin au silence après reboot, ce qu'attend la boucle de
/// vérification.
///
/// Lecture seule, et c'est structurel : il n'existe aucune op d'écriture firmware dans
/// l'énumération fermée. Écrire au mauvais offset peut rendre une carte non démarrable, sans
/// annulation possible (le seul recours serait physique), et l'offset dépend du couple
/// (modèle × version de BIOS) — aucune API ne le publie.
///
/// Le nom et le GUID sont validés feuille-exacte par l'allowlist du helper : seul l'espace des
/// variables EFI GLOBALES est permis, ainsi que les noms fermés des varstores « Setup » (AMI) et
/// « CpuSetup » (Intel), en lecture seulement (L3.1). Aucune opération n'écrit dans le firmware.
/// </summary>
public sealed record ReadFirmwareVariableOp(string Name, string VendorGuid) : PrivilegedOp;

/// <summary>
/// 24/09/2026 — plafonne l'ENVOI de tout le trafic de la machine, sauf celui du jeu, par deux
/// politiques QoS Windows en memoire (ActiveStore : elles disparaissent au redemarrage).
///
/// Mesure sur la machine de reference (upload sature ~720 Mbit/s, A/B alterne x3, 120 pings) :
/// ping p99 48 -> 18 ms avec le plafond. Au repos : rien a gagner. Source de l'API :
/// learn.microsoft.com/powershell/module/netqos/new-netqospolicy. L'exemption du jeu l'emporte
/// sur la politique generale (mesure le 24/09 : 343 -> 839 Mbit/s pour l'exe exempte).
/// </summary>
public sealed record ApplyUploadCapOp(string GameExeName, long CapBitsPerSecond) : PrivilegedOp;

/// <summary>L'inverse exact de <see cref="ApplyUploadCapOp"/> : retire les deux politiques.</summary>
public sealed record RemoveUploadCapOp : PrivilegedOp;

/// <summary>28/09/2026 : retient la protection du ping pour le service de démarrage, sous %ProgramData%\DrDxt.
/// Même borne que <see cref="ApplyUploadCapOp"/> : un nom d'exe nu, un plafond dans la plage.</summary>
public sealed record RetainServiceUploadCapOp(string GameExeName, long CapBitsPerSecond) : PrivilegedOp;

/// <summary>L'inverse de <see cref="RetainServiceUploadCapOp"/> : le service ne porte plus rien.</summary>
public sealed record ForgetServiceUploadCapOp : PrivilegedOp;

/// <summary>
/// 24/09/2026 — retire un element du magasin BCD de l'entree de demarrage courante (`{current}`).
///
/// Le raisonnement est celui de `BcdInterpretation` : `bcdedit /enum` ne liste que les elements
/// PRESENTS, donc en lire un prouve que quelqu'un l'a ecrit. Le retirer rend la decision a Windows,
/// sans avoir a connaitre sa valeur par defaut. Ensemble ferme d'elements (allowlist), aucun ne
/// peut empecher le demarrage : `bootstatuspolicy` ne decide que de l'ecran de recuperation.
/// </summary>
public sealed record DeleteBcdValueOp(string Element) : PrivilegedOp;

/// <summary>L'inverse exact de <see cref="DeleteBcdValueOp"/> : repose la valeur d'origine capturee,
/// sur le meme ensemble ferme d'elements et de valeurs.</summary>
public sealed record SetBcdValueOp(string Element, string Value) : PrivilegedOp;

/// <summary>
/// 25/09/2026 — l'etat du magasin de composants de Windows, tel que `DISM /CheckHealth` le rend :
/// sain, reparable ou non reparable (Microsoft, « Repair a Windows Image »). Lecture seule : aucun
/// parametre, rien n'est repare. DISM exige les droits administrateur, d'ou le helper.
/// Machine de reference le 25/09 : « The component store is repairable ».
/// </summary>
public sealed record ReadComponentStoreHealthOp : PrivilegedOp;

/// <summary>
/// 11/09/2026 — inscrire au manifeste signe. L'App n'a ni la cle ni le droit d'ecrire la racine
/// admin : elle envoie les ENTREES, le helper eleve les valide (identifiants du catalogue, valeurs
/// bornees, ce qui serait EXECUTE doit passer l'allowlist), les fusionne au manifeste existant
/// (premiere origine gagne), signe avec la cle de la machine et ecrit atomiquement.
/// L'App ne tient jamais la cle privee.
/// 06/10/2026 : <c>Retirer</c> ote ces identifiants des deux listes (levier annule). Choix ecrit : le helper ne
/// verifie pas que la valeur d'origine a ete remise avant ; un appelant authentifie (la vraie App, meme compte) peut
/// donc effacer une origine, et la desinstallation rendra moins. Ce n'est pas une escalade (rien n'est execute, et ce
/// meme appelant peut deja ecrire ces reglages par l'allowlist), mais une origine HKLM n'est plus immuable.
/// </summary>
/// <param name="Tweaks">Corrections au redemarrage : le reconciliateur les reaffirme.</param>
/// <param name="RevertOnly">Origines des leviers gardes : seule la desinstallation les rend.</param>
/// <param name="RepartirDUnManifesteVide">La voie de reprise, explicite : quand le manifeste existant
/// est illisible (ou la cle inutilisable), il est mis de cote (jamais efface) et un manifeste neuf
/// commence. Refuse sur un manifeste lisible : on ne jette jamais des origines qu'on sait relire.</param>
public sealed record CommitManifestOp(
    IReadOnlyList<AppliedTweak> Tweaks,
    IReadOnlyList<AppliedTweak> RevertOnly,
    bool RepartirDUnManifesteVide = false,
    IReadOnlyList<string>? Retirer = null) : PrivilegedOp;

/// <summary>Documented Windows power-plan GUIDs (the only plans the allowlist accepts).</summary>
public static class KnownPowerPlans
{
    public static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Balanced        = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid Ultimate        = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static bool IsKnown(Guid guid) =>
        guid == HighPerformance || guid == Balanced || guid == Ultimate;
}
