using System.Text.RegularExpressions;

namespace DrDxt.Infrastructure.Ops;

// -----------------------------------------------------------------------
// T14/T15 — the SHARED anonymization pipeline (RGPD minimisation, cahier §8).
//
// Every string that could leave the machine (a crash payload T14, a diagnostics
// zip entry T15) is run through this first. It is intentionally conservative —
// it OVER-redacts rather than risk a leak — and TOTAL: it never throws (a regex
// timeout or odd input degrades to "return what we have so far", never an
// exception, because a scrubber that throws inside a crash reporter is the worst
// possible failure mode).
//
// What is masked, in order (each rule independent, all idempotent):
//   1. user paths           C:\Users\<name>\...  → C:\Users\<redacted>\...
//                           (any drive, \ or /, case-insensitive)
//   2. emails               foo@bar.com          → <redacted-email>
//   3. labelled fingerprints fingerprint: <hex>  → fingerprint: <redacted-fp>
//   4. labelled license keys (license)key=<tok>  → <redacted-key>
//   5. dashed key tokens    XXXX-XXXX-...-XXXX    → <redacted-key>
//   6. long bare hex blobs  32+ hex chars        → <redacted-fp>
//
// The username TAIL (AppData\..., file name, :line N) is KEPT — it is not PII and
// is what makes a stack trace useful. Only the username segment is masked.
// -----------------------------------------------------------------------

/// <summary>The shared scrubber seam. Deterministic, idempotent, total (never throws).</summary>
public interface ITextScrubber
{
    /// <summary>Returns an anonymized copy of <paramref name="text"/> with user paths,
    /// emails, license keys and raw fingerprints masked. <c>null</c> → empty string.
    /// Never throws.</summary>
    string Scrub(string? text);
}

/// <summary>Regex-based implementation of <see cref="ITextScrubber"/>. All patterns are
/// compiled once with a bounded match timeout so a pathological input degrades gracefully
/// instead of hanging or throwing.</summary>
public sealed class TextScrubber : ITextScrubber
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // 1. User path: <drive>:\Users\<username>\  (or C:/Users/<username>/). We capture the
    //    leading "<drive>:<sep>Users<sep>" and replace only the <username> segment so the
    //    tail (AppData\..., file, :line N) is preserved.
    private static readonly Regex UserPath = new(
        @"(?<head>[A-Za-z]:[\\/]Users[\\/])(?<user>[^\\/]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);

    // 2. Email — masked before key/hex rules so an email's local part is never half-eaten.
    private static readonly Regex Email = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled, MatchTimeout);

    // 3. Labelled fingerprint: fingerprint / fp / machineFp = / : <hex...>.
    private static readonly Regex LabelledFingerprint = new(
        @"(?<label>\b(?:fingerprint|machine[_\s]?fp|fp)\b\s*[:=]\s*)(?<val>[A-Fa-f0-9]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);

    // 4. Labelled license key: (license)key = / : <token of alnum/dash, 8+ chars>.
    private static readonly Regex LabelledKey = new(
        @"(?<label>\b(?:license[_\s]?key|licence[_\s]?key|key)\b\s*[:=]\s*)(?<val>[A-Za-z0-9\-]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);

    // 5. Dashed key token: 3+ dash-separated groups of 3+ alnum (Keygen/UUID-style key),
    //    e.g. A1B2C3D4-E5F6-7890-ABCD-EF1234567890.
    // 25/09/2026 (rapport réel) : sans l'exigence d'un chiffre, toute suite de mots à tirets était prise
    // pour une clé — la source Microsoft « multimedia-class-scheduler-service » sortait en
    // « multimedia-<redacted-key> », et la preuve disparaissait du rapport. Une clé aléatoire (GUID,
    // Keygen) porte toujours au moins un chiffre ; des mots, non.
    // 25/09/2026 (rapport réel, suite) : le paquet Store « 9NVMNJCR03XV-9426MICRO-STARINTERNATION »
    // était masqué. Segments bornés à 12 caractères, le plus long d'un GUID (une clé Windows en a 5).
    // Limite connue : une clé dont un segment dépasse 12 caractères passerait en clair.
    private static readonly Regex DashedKey = new(
        @"\b(?=[A-Za-z0-9-]*\d)[A-Za-z0-9]{3,12}(?:-[A-Za-z0-9]{3,12}){2,}\b",
        RegexOptions.Compiled, MatchTimeout);

    // 6. Long bare hex blob (32+ hex chars) — a raw fingerprint / digest with no label.
    private static readonly Regex BareHex = new(
        @"\b[A-Fa-f0-9]{32,}\b",
        RegexOptions.Compiled, MatchTimeout);

    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var s = text;
        try
        {
            s = UserPath.Replace(s, "${head}<redacted>");
            s = Email.Replace(s, "<redacted-email>");
            s = LabelledFingerprint.Replace(s, "${label}<redacted-fp>");
            s = LabelledKey.Replace(s, "${label}<redacted-key>");
            s = DashedKey.Replace(s, "<redacted-key>");
            s = BareHex.Replace(s, "<redacted-fp>");
            return s;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input hit the bounded timeout mid-pipeline. Return what we have
            // scrubbed so far — never throw out of the scrubber (a throwing scrubber inside
            // a crash reporter is the worst case). The partial result has already had the
            // earlier (higher-priority) rules applied.
            return s;
        }
    }
}
