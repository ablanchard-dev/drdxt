namespace DrDxt.Infrastructure.Updates;

// -----------------------------------------------------------------------
// M5 / T12-T13 — update error codes. Module-local, namespaced so literals are
// never scattered (master plan §10 convention, mirroring LicensingErrorCodes /
// ReconcilerErrorCodes). These are CODES (machine-readable), never UI text.
//
// The cardinal rule for the whole update path: a package update is verified
// (signature then hash) before anything is executed or any file is replaced.
// Jamais fail-open — every uncertainty keeps the CURRENT installed version.
// -----------------------------------------------------------------------
public static class UpdateErrorCodes
{
    // T12 — The Ed25519 signature over the canonical update MANIFEST did not verify:
    // tampered signature, tampered manifest fields (url/sha256/version), absent/empty
    // signature, signed by the wrong key, or a corrupt embedded update public key.
    // Fail-clean: the download is REFUSED — we do not even fetch the payload, because
    // an untrusted manifest's url/sha256 are not worth acting on. Never "ok by default".
    public const string SignatureInvalid = "UPDATE_SIG_INVALID";

    // T12 — The SHA-256 of the DOWNLOADED payload does not equal the sha256 carried in
    // the (already signature-verified) manifest. The payload is never executed / staged
    // / extracted. We only ever trust a hash that came from a SIGNED manifest — so this
    // check runs strictly after SignatureInvalid passed.
    public const string HashMismatch = "UPDATE_HASH_MISMATCH";

    // T12 — The manifest's version is LOWER than the installed version, outside a beta
    // channel. Refused: an update must never silently roll the user back (a downgrade is
    // the classic vector to re-introduce a fixed vulnerability). Beta channels may opt in.
    public const string Downgrade = "UPDATE_DOWNGRADE";

    // T13 — The update feed could not be reached (no network / transport failure). This
    // is NON-BLOCKING: the app keeps running on the CURRENT version. Deliberately distinct
    // from SignatureInvalid — an unreachable feed never downgrades trust, it just defers.
    public const string Offline = "UPDATE_OFFLINE";

    // T13 — The elevated binary-replacement seam is not wired live yet (deferred, like the
    // M2 fail-clean stubs). The app/profiles channel never needs it; the app channel
    // surfaces this instead of silently doing nothing or writing without elevation. The
    // staged payload was verified and is intact — only the privileged swap is deferred.
    public const string ReplacerUnavailable = "UPDATE_REPLACER_UNAVAILABLE";

    // LU — Aucun serveur de mise a jour n'est configure (ou son url n'est pas une url https
    // absolue). L'updater n'est pas monte : le produit reste sur sa version installee et ne tente
    // rien. Deliberement distinct d'Offline — « pas configure » se corrige en configurant, tandis
    // qu'« hors ligne » envoie chercher une panne de reseau qui n'existe pas. C'est l'etat reel du
    // produit tant qu'aucune url de deploiement n'existe.
    public const string NotConfigured = "UPDATE_NOT_CONFIGURED";

    // T13 — A verified payload could not be staged/extracted on disk (IO failure, disk
    // full, permission). The PREVIOUS version/pack is left intact (clean transaction —
    // never a half-applied state). Fail-clean, never throws across the boundary.
    public const string StageFailed = "UPDATE_STAGE_FAILED";
}
