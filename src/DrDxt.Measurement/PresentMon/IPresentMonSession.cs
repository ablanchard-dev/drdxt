using DrDxt.Core;

namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// Frontière de capture PresentMon (service-based). Renvoie les frametimes (ms) d'un PID.
/// Cette interface est le seul point de contact entre BenchmarkRunner et PresentMon :
/// elle peut être mockée en tests pour valider l'orchestration sans service réel.
/// </summary>
public interface IPresentMonSession
{
    /// <summary>
    /// Vérifie si le service PresentMon est disponible sur cette machine.
    /// Ne lève jamais d'exception — retourne Ok(false) si absent, Ok(true) si présent,
    /// Fail(PmonUnavailable) uniquement si la vérification elle-même plante.
    /// </summary>
    Result<bool> IsServiceAvailable();

    /// <summary>
    /// Cause exacte de la dernière indisponibilité constatée par <see cref="IsServiceAvailable"/>
    /// (code PM_STATUS, DLL absente, ABI incompatible…), ou <c>null</c> si le service répond.
    /// Un booléen ne dit pas quoi réparer : « service arrêté » et « un autre PresentMon d'une
    /// version différente occupe le tube » demandent deux gestes opposés.
    /// </summary>
    string? LastUnavailableDetail => null;

    /// <summary>
    /// Capture les séries par-frame (frametimes + latence render-present, ms) du processus
    /// <paramref name="targetPid"/> pendant <paramref name="duration"/>.
    /// Retourne Fail(PmonUnavailable) si le service est absent, Fail(NoFrames) si aucune frame captée,
    /// Fail(CaptureFail) sur tout autre problème. Ne lève jamais d'exception.
    /// </summary>
    Task<Result<FrameCapture>> CaptureAsync(int targetPid, TimeSpan duration, CancellationToken ct);
}
