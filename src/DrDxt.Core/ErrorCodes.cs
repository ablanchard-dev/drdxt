namespace DrDxt.Core;

// REQUIRED — literal codes must never be scattered elsewhere.
public static class ErrorCodes
{
    public const string DetectionFailed = "DETECTION_FAILED";
    public const string BenchmarkFailed = "BENCHMARK_FAILED";
    public const string RestorePointBlocked = "RESTORE_POINT_BLOCKED";
    public const string ElevationDenied = "ELEVATION_DENIED";
    // 08/10/2026 (revue n° 10) : le joueur a refusé l'invite Windows. Repris de HelperLauncher pour que
    // l'Application le reconnaisse sans dépendre du helper.
    public const string ElevationDeclined = "ELEVATION_DECLINED";
    // 08/10/2026 (revue n° 10) : une écriture d'un levier a été refusée, ET le retour arrière des écritures déjà
    // faites aussi : une partie du réglage est encore en place. L'appelant garde son journal (RetourArriere).
    public const string TweakPartiallyApplied = "TWEAK.PARTIALLY_APPLIED";
    // §9.13 handshake: a peer connected but the handshake itself was malformed,
    // truncated, garbage, or carried an unsupported protocol marker — i.e. a
    // transport/protocol fault, not an authorization denial. Distinct from
    // ElevationDenied (which is reserved for a well-formed peer that failed
    // caller authorization). Both are fail-closed (no key minted).
    public const string HandshakeProtocolError = "HANDSHAKE_PROTOCOL_ERROR";
    // On-demand launch: the elevated helper was launched (or believed up) but its
    // pipe never accepted a real connect+handshake within the startup deadline.
    // Distinct from ELEVATION_DECLINED (user cancelled UAC) — this is a launched
    // helper that did not come up in time. Clean, retryable on a later op.
    public const string HelperUnreachable = "HELPER_UNREACHABLE";
    public const string SignatureInvalid = "SIGNATURE_INVALID";
    public const string ManifestTampered = "MANIFEST_TAMPERED";
    // 11/09/2026 — le manifeste existant ne se relit pas (altere, signe par une autre cle, illisible) :
    // le commit est refuse, rien n'est ecrit, et l'App propose la reprise explicite. Jamais un
    // ecrasement silencieux : il effacerait les seules origines que la desinstallation sait rendre.
    public const string ManifestUnreadable = "MANIFEST_UNREADABLE";
    // 11/09/2026 — la cle de signature de la machine existe mais est inutilisable (illisible, ACL non
    // fiable, ou c'est la cle de dev). Refus nomme ; la meme reprise explicite la met de cote.
    public const string ManifestKeyUnusable = "MANIFEST_KEY_UNUSABLE";
    // 11/09/2026 — le manifeste signe est ecrit, mais la reference de derive (build, pilote) n'a pas pu etre
    // posee dans le fichier du reconciliateur : la 1re passe notifiera au lieu de reappliquer. Dit, jamais tu.
    public const string ManifestReferenceNotSeeded = "MANIFEST_REFERENCE_NOT_SEEDED";
    public const string GuideLinkTampered = "GUIDE_LINK_TAMPERED";
    public const string UpdateExpired = "UPDATE_EXPIRED";
    public const string OpNotAllowed = "OP_NOT_ALLOWED";
    public const string IoFailure = "IO_FAILURE";

    // M1 — Detection codes
    public const string WmiFail          = "DET.WMI_FAIL";
    public const string DxgiFail         = "DET.DXGI_FAIL";
    public const string NvapiUnavailable = "DET.NVAPI_UNAVAIL";
    public const string AdlxUnavailable  = "DET.ADLX_UNAVAIL";
    public const string NoGpu            = "DET.NO_GPU";
    public const string PowerFail        = "DET.POWER_FAIL";
    public const string RegistryFail     = "DET.REG_FAIL";
    public const string FirmwareFail     = "DET.FIRMWARE_FAIL";

    // M1 — Measurement codes
    public const string PmonUnavailable  = "MEAS.PMON_UNAVAIL";
    public const string CaptureFail      = "MEAS.CAPTURE_FAIL";
    public const string NoFrames         = "MEAS.NO_FRAMES";
    public const string PidInvalid       = "MEAS.PID_INVALID";

    // M1.5 — PresentMon spine additions
    // Live process but 0 frames captured = the game's anti-cheat blocks ETW (distinct from no service).
    public const string NoPresentingFrames   = "MEAS.NO_PRESENTING_FRAMES";
    // Service binary not installed (one-time elevated install needed) ≠ runtime PMON unavailable.
    public const string ServiceNotInstalled = "MEAS.SERVICE_NOT_INSTALLED";

    // NS-9 — Network latency instrument.
    // The measurement request itself is unusable (no host, probe count <= 0, timeout <= 0)
    // → refuse before sending anything, never guess a default target.
    public const string MeasureInvalidInput = "MEAS.INVALID_INPUT";
    // Not one probe was sent (cancelled before the first) → this is NO measurement, which is
    // Not the same as 100% packet loss. Never publish a loss figure we did not observe.
    public const string MeasureNoSamples = "MEAS.NO_SAMPLES";
    // The network adapter's own counters could not be read (interface absent, or it vanished
    // mid-read). This is NO reading, which is not the same as "zero errors" — an adapter we
    // cannot interrogate must never be reported as healthy.
    public const string MeasureNicUnreadable = "MEAS.NIC_UNREADABLE";

    // M1 — Scan codes
    public const string NoBaseline       = "SCAN.NO_BASELINE";

    // M1 — Storage detection
    public const string StorageFail      = "DET.STORAGE_FAIL";

    // M3 — BIOS Doctor (foundation). The HardwareInventory/SettingsState handed to
    // the advisor is internally incoherent (e.g. empty Memory while inferring XMP)
    // → refuse cleanly, never throw, never emit a fabricated firmware Finding.
    public const string BiosInputInvalid = "BIOS_INPUT_INVALID";

    // M3 — BIOS Doctor (guide content). The guide-library data is corrupt: malformed
    // JSON, an unknown vendor/setting enum, a blank exact-setting-name (the safety
    // net), or an injection marker in a value → refuse cleanly (defensive §9.6),
    // never render unvalidated external data, never throw.
    public const string BiosTemplateInvalid = "BIOS_TEMPLATE_INVALID";

    // M3 — BIOS Doctor (closed-loop verification). The pending-change marker on disk
    // is corrupt: malformed JSON, missing/empty targets, an unknown setting enum, or
    // an out-of-range timestamp → the loop refuses to read it as a real pending change
    // (fail-clean, never throw). Distinct from "absent" (no marker = nothing pending,
    // an Ok no-op). A corrupt marker is a fault we surface, never silently trust.
    public const string BiosPendingChangeCorrupt = "BIOS_PENDING_CHANGE_CORRUPT";

    // M3 — BIOS Doctor (closed-loop verification). Reading/writing the pending-change
    // marker file hit a real I/O fault (the directory, the temp file, the move) →
    // fail-clean via Result, the boundary wrapped, never an unhandled exception.
    public const string BiosPendingChangeIo = "BIOS_PENDING_CHANGE_IO";

    // LM — Mesure memoire. La reference sur disque est illisible (JSON malforme, identifiant de
    // machine vide) : on refuse de s'en servir plutot que de comparer un apres a n'importe quoi.
    // Distinct de "absente", qui n'est pas une erreur : rien n'a encore ete mesure.
    public const string MemoryBaselineCorrupt = "MEMORY_BASELINE_CORRUPT";

    // LM — Mesure memoire. Ecrire ou lire le fichier de reference a bute sur une vraie panne
    // d'entree/sortie : echec propre par Result, jamais d'exception qui remonte.
    public const string MemoryBaselineIo = "MEMORY_BASELINE_IO";

    // M2 Phase A — Elevated-helper allowlist / signed-IPC fail-clean codes.
    // The op was not in the closed, leaf-exact allowlist (unknown type, blacklisted
    // target, or out-of-range field) → refused before execution, never run.
    public const string NotAllowlisted   = "HELPER.NOT_ALLOWLISTED";
    // The typed privileged request failed its transport-integrity (HMAC) check →
    // refused fail-clean, op never executed. (Distinct from the legacy
    // SignatureInvalid="SIGNATURE_INVALID" used by the M0 envelope path.)
    public const string HelperSignatureInvalid = "HELPER.SIGNATURE_INVALID";
    // An allowlisted, signed op was dispatched but the privileged action itself
    // failed at execution time (e.g. the underlying SDK/registry write errored).
    public const string HelperOpFailed   = "HELPER.OP_FAILED";

    // M2 Phase B — Safety / rollback (ISafetyManager).
    // System Restore is OFF and cannot be enabled (GPO/SKU/locked) → block, never
    // proceed without a verified restore point.
    public const string RestoreDisabled  = "SAFETY.RESTORE_DISABLED";
    // System Restore is OFF and the user explicitly declined enabling it → block.
    public const string RestoreDeclined  = "SAFETY.RESTORE_DECLINED";
    // Creating (or verifying) the restore point failed → block, nothing applied.
    public const string RestoreFailed     = "SAFETY.RESTORE_FAILED";
    // 06/10/2026 : Windows a accepté la demande, mais aucun point n'a pu être relu ensuite (pas créé, ou liste illisible).
    public const string RestoreNotVerified = "SAFETY.RESTORE_NOT_VERIFIED";
    // The TweakSnapshot's OriginalValueJson is malformed / not a known typed op →
    // refuse to revert fail-clean (never throw, never run an unknown op).
    public const string SnapshotInvalid   = "SAFETY.SNAPSHOT_INVALID";

    // M2 Phase D — Tweak applicability / SDK availability (ITweak).
    // The tweak is out of its hardware gating or its requested value is out of
    // bounds → it does not apply to this machine (skip cleanly, never write).
    public const string TweakNotApplicable = "TWEAK.NOT_APPLICABLE";
    // The vendor SDK / registry-read path needed to capture the original value or
    // apply the change is unavailable → fail clean (never throw), nothing applied.
    public const string TweakSdkUnavailable = "TWEAK.SDK_UNAVAILABLE";
    // 14/09/2026 — l'executable n'a aucun profil pilote a lui (NVAPI_EXECUTABLE_NOT_FOUND) : le pilote
    // lui applique le global. Distinct d'un profil qui existe mais ne se lit pas (TweakSdkUnavailable) :
    // seul ce cas-ci autorise a se rabattre sur le profil global.
    public const string DrsNoGameProfile = "TWEAK.DRS_NO_GAME_PROFILE";
    // An AMD GPU is present on this machine but the ADLX tuning SDK is not available
    // in this build. Distinct from TweakSdkUnavailable (generic) and TweakNotApplicable
    // (hardware gating) — this is an honest "coming soon" for AMD GPU tuning.
    // §22.3: never silently skip, never fake success, always say why.
    public const string GpuAmdTuningUnavailable = "TWEAK.GPU_AMD_TUNING_UNAVAILABLE";
    // The tweak needs elevation but is being applied without an elevated client.
    public const string TweakElevationRequired = "TWEAK.ELEVATION_REQUIRED";
    // A GPU-tuning tweak (undervolt / power-limit) was applied but its internal
    // stability test detected a hang / artifact / crash → it reverted itself
    // Immediately and returns this. We never return Ok while the GPU is unstable.
    public const string TweakUnstable = "TWEAK.UNSTABLE";
    // The GPU tuning path could not be PROVEN writable on this specific card+driver:
    // the pre-apply capability probe (identity write of the captured original + read-back)
    // either failed or read back a different value. Doctrine §6.0 of the north-star plan:
    // we do not own 2015-2026 hardware, so support is proven per device by probe, never
    // assumed from an architecture name. Fail-closed → the lever is refused honestly and
    // Nothing is tuned. Distinct from TweakSdkUnavailable (no SDK at all) and from
    // TweakNotApplicable (hardware gating / out-of-bounds value).
    public const string TweakUnsupportedOnThisGpu = "TWEAK.UNSUPPORTED_ON_THIS_GPU";

    // M6 — Per-chip optimization profiles.
    // The HardwareInventory handed to the profile matcher is missing/incoherent
    // (e.g. null) → refuse cleanly (fail-closed), never guess a profile, never throw.
    // The matcher still returns the safe generic baseline for a recognized-but-
    // unmatched chip; this code is reserved for an UNUSABLE input.
    public const string ProfileInputInvalid = "PROFILE.INPUT_INVALID";

    // M2 Phase C — Transactional tweak engine (ITweakEngine).
    // A step of the batch failed at apply time → the whole already-applied batch
    // was rolled back (LIFO). Never a half-applied state.
    public const string BatchReverted     = "ENGINE.BATCH_REVERTED";
    // The re-benchmark measured a regression beyond the combined margins → the
    // whole batch was auto-reverted (LIFO). We never keep a measured loss.
    public const string RegressionReverted = "ENGINE.REGRESSION_REVERTED";
    // The re-benchmark showed no measurable gain beyond the combined margins.
    // This is a VALID, KEPT state (not a regression) — surfaced honestly, not reverted.
    public const string NoMeasurableGain  = "ENGINE.NO_MEASURABLE_GAIN";
}
