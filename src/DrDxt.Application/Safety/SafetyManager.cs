using DrDxt.Core;

namespace DrDxt.Application.Safety;

// -----------------------------------------------------------------------
// SafetyManager — the M2 reversibility gate (master plan §6, §8.1, §16).
//
// B1 EnsureRestorePointAsync: Nothing is ever applied without a verified System
//    Restore point. If restore is off and cannot be enabled (or the user
//    declines) we BLOCK — we never silently proceed. The point is created
//    through the elevated helper via a typed CreateRestorePointOp, then verified
//    to actually exist (GPO/SKU can silently no-op the create). Throttled to one
//    point per session/batch so we never spam.
//
// B2 RevertAsync: reconstruct the inverse typed PrivilegedOp from the snapshot's
//    typed OriginalValueJson (the codec wire form, carrying the real captured
//    original value) and send it back through the elevated client. The generic
//    low-level path covers registry / power-plan reverts; tweak-specific reverts
//    (e.g. a GPU V/F curve) live in ITweak.RevertAsync (Phase E).
//
// Every external boundary is wrapped → Result.Fail. NO exception ever escapes.
// -----------------------------------------------------------------------
public sealed class SafetyManager : ISafetyManager
{
    /// <summary>06/10/2026 (plan 4.5b) : une création sans point relu est retentée une fois. VSS échoue par
    /// intermittence (8193) et réussit à l'essai suivant (mesuré deux fois sur ma machine).</summary>
    public const int Creations = 2;

    /// <summary>14/09/2026 — la fenetre ou Windows refuse de creer un nouveau point : 24 h par defaut
    /// (Microsoft Learn, SRSetRestorePointW, cle SystemRestorePointCreationFrequency absente). Un point de
    /// cette fenetre est accepte et DIT, plutot que de forcer la frequence a 0 : cela evincerait les points
    /// plus anciens de l'utilisateur (audit adverse du projet, non publie, point M9). Au-dela : on bloque.</summary>
    public static readonly TimeSpan FenetreDuRefusDeWindows = TimeSpan.FromHours(24);

    public DateTime? PointReutiliseUtc { get; private set; }

    private readonly IRestorePointService _restore;
    private readonly IConsentPrompt _consent;
    private readonly IElevatedClient _elevated;

    // Throttle: once a verified point exists for this session/batch, we don't
    // create another (master plan B1: "one point per session/batch").
    private bool _pointEnsured;

    /// <summary>06/10/2026 (plan 4.5) : lectures de la liste des points avant de conclure qu'aucun n'existe.</summary>
    public const int LecturesDuPoint = 8;

    /// <summary>**NON CHIFFRÉ** au-delà de la mesure : sur ma machine, un point WMI est listé dès la fin de l'appel
    /// (9 à 19 s) ; 7 attentes de 3 s laissent 21 s de plus avant de dire « pas de point ».</summary>
    internal static readonly TimeSpan EntreDeuxLectures = TimeSpan.FromSeconds(3);

    private readonly Func<TimeSpan, Task> _attendre;

    public SafetyManager(IRestorePointService restore, IConsentPrompt consent, IElevatedClient elevated,
        Func<TimeSpan, Task>? attendre = null)
    {
        _restore = restore;
        _consent = consent;
        _elevated = elevated;
        _attendre = attendre ?? (d => Task.Delay(d));
    }

    public async Task<Result> EnsureRestorePointAsync()
    {
        if (_pointEnsured)
            return Result.Ok();

        try
        {
            if (!_restore.IsEnabled())
            {
                // Restore is off: we may proceed only by enabling it with explicit
                // consent. No consent → declined; consent but cannot enable → disabled.
                if (!_consent.AskEnableRestore())
                    return Result.Fail(ErrorCodes.RestoreDeclined,
                        "System Restore is disabled and the user declined enabling it.");

                if (!_restore.TryEnable())
                    return Result.Fail(ErrorCodes.RestoreDisabled,
                        "System Restore could not be enabled (GPO/SKU/locked).");
            }

            // 06/10/2026 (plan 4.5b, le mainteneur : « pas un point à chaque fois qu'on optimise ») : un point de moins de
            // 24 h, de DrDXT ou de Windows, protège déjà ; on le réutilise et l'écran le dit.
            var demandeUtc = DateTime.UtcNow;
            if (_restore.DernierPointDepuis(demandeUtc - FenetreDuRefusDeWindows) is { } recent)
            {
                PointReutiliseUtc = recent;
                _pointEnsured = true;
                return Result.Ok();
            }

            // Create the point through the typed, allowlisted elevated path, under DrDXT's fixed name.
            // Le retour de Windows ne prouve pas le point (plan 4.5) : la liste est relue avant de conclure.
            // Revue du 06/10 (P1) : l'échec VSS intermittent remonte en refus ; il est retenté lui aussi.
            DateTime? trouve = null;
            string? dernierRefus = null;
            for (var essai = 0; essai < Creations && trouve is null; essai++)
            {
                var create = await _elevated
                    .ExecuteAsync(new CreateRestorePointOp(CreateRestorePointOp.NomDrDxt), CancellationToken.None)
                    .ConfigureAwait(false);
                if (!create.IsOk)
                {
                    dernierRefus = create.Error?.Message ?? create.Error?.Code ?? "?";
                    continue;
                }
                dernierRefus = null;

                for (var i = 0; i < LecturesDuPoint && trouve is null; i++)
                {
                    if (i > 0) await _attendre(EntreDeuxLectures).ConfigureAwait(false);
                    trouve = _restore.DernierPointDepuis(demandeUtc - FenetreDuRefusDeWindows);
                }
            }
            if (trouve is not { } point)
                return dernierRefus is not null
                    ? Result.Fail(ErrorCodes.RestoreFailed, dernierRefus)
                    : Result.Fail(ErrorCodes.RestoreNotVerified, $"no restore point read back after {Creations} attempts");
            PointReutiliseUtc = point < demandeUtc ? point : null;

            _pointEnsured = true;
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.RestoreFailed, $"Restore-point boundary error: {ex.Message}");
        }
    }

    public async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        // Defensive validation of the incoming JSON (master plan §9.6): a malformed
        // or unknown-typed snapshot must never throw and must never run an arbitrary
        // op — fail clean with SAFETY.SNAPSHOT_INVALID.
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        Result<PrivilegedOp> decoded;
        try
        {
            decoded = PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SnapshotInvalid, $"Snapshot decode error: {ex.Message}");
        }

        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid,
                "Snapshot original value is not a known typed privileged op.");

        try
        {
            // Send the captured-original op back through the elevated client to
            // restore the exact prior value. Surface the helper's result verbatim.
            return await _elevated.ExecuteAsync(decoded.Value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }
}
