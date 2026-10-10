using DrDxt.Core;

namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// Real <see cref="IPresentMonSession"/> built on the PresentMonAPI2 frame-query model:
/// pmOpenSession → pmStartTrackingProcess → pmRegisterFrameQuery → pmConsumeFrames (parse blob)
/// → pmFreeFrameQuery → pmStopTrackingProcess → pmCloseSession.
///
/// Honesty / degraded contract (CRITICAL): never throws toward the caller. Every
/// non-success <see cref="PM_STATUS"/> and every P/Invoke exception is converted to a
/// <see cref="Result"/> failure, so the scanner can fall back to detection-only mode.
/// Native cleanup always runs in a finally, each step in its own try/catch.
/// </summary>
public sealed class PresentMonSession : IPresentMonSession
{
    // Buffer capacity, in frames, for one pmConsumeFrames poll. Sized so a high-FPS
    // game cannot overflow a single poll between our soft 2ms polls.
    private const uint MaxFramesPerPoll = 4096;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(2);
    // Mesuré : 1 à 2 s entre pmStartTrackingProcess et la première image. 5 s de marge.
    private static readonly TimeSpan AttentePremiereImage = TimeSpan.FromSeconds(5);

    private readonly IPresentMonApi _api;
    private readonly IMonotonicClock _clock;

    /// <inheritdoc/>
    public string? LastUnavailableDetail { get; private set; }

    // internal: the api seam is internal, and a public ctor would leak it (CS0051).
    // DI constructs this via an explicit factory in AddDrDxtMeasurement; tests use
    // it directly through InternalsVisibleTo.
    internal PresentMonSession(IPresentMonApi api, IMonotonicClock clock, Func<long>? qpcMaintenant = null,
        long? qpcParSeconde = null)
    {
        _api = api;
        _clock = clock;
        _qpcMaintenant = qpcMaintenant ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _qpcParSeconde = qpcParSeconde ?? System.Diagnostics.Stopwatch.Frequency;
    }

    // L'horloge QPC (Stopwatch sous Windows), celle des instants que PresentMon donne par image.
    private readonly Func<long> _qpcMaintenant;
    private readonly long _qpcParSeconde;

    /// <summary>Le retard de livraison des images par le service, au-delà duquel la fenêtre se ferme quand même.
    /// Mesuré : 1 à 2 s ; marge de 3 s.</summary>
    internal static readonly TimeSpan MargeDeLivraison = TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public Result<bool> IsServiceAvailable()
    {
        try
        {
            var rc = _api.OpenSession(out var session);
            if (rc == PM_STATUS.PM_STATUS_SUCCESS)
            {
                // Service answered: close the probe session and report available.
                try { _api.CloseSession(session); } catch { /* best-effort */ }
                LastUnavailableDetail = null;
                return Result<bool>.Ok(true);
            }
            // DLL loaded but service refused → unavailable. Expected state, not an error.
            // On garde le code brut : c'est lui qui distingue « service arrêté » d'un
            // PIPE_ERROR causé par un autre PresentMon installé sur la machine.
            LastUnavailableDetail = $"PM_STATUS={(int)rc} ({rc})";
            return Result<bool>.Ok(false);
        }
        catch (DllNotFoundException)
        {
            // PresentMonAPI2.dll absent — normal on CI / a dev box without PresentMon.
            LastUnavailableDetail = "PresentMonAPI2.dll introuvable";
            return Result<bool>.Ok(false);
        }
        catch (EntryPointNotFoundException)
        {
            // DLL present but wrong version / incompatible ABI.
            LastUnavailableDetail = "PresentMonAPI2.dll présente mais ABI incompatible";
            return Result<bool>.Ok(false);
        }
        catch (Exception ex)
        {
            // Any other failure (incl. SEH from a broken DLL) → "no service", never throw.
            LastUnavailableDetail = ex.GetType().Name;
            return Result<bool>.Ok(false);
        }
    }

    /// <inheritdoc/>
    public Task<Result<FrameCapture>> CaptureAsync(int targetPid, TimeSpan duration, CancellationToken ct)
        => Task.Run(() => CaptureCore(targetPid, duration, ct), CancellationToken.None);

    private async Task<Result<FrameCapture>> CaptureCore(int targetPid, TimeSpan duration, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail, "Capture annulée avant démarrage.");

        if (targetPid <= 0)
            return Result<FrameCapture>.Fail(ErrorCodes.PidInvalid, $"PID invalide : {targetPid}.");

        var pid = (uint)targetPid;
        var sessionOpen = false;
        var session = nint.Zero;
        var tracking = false;
        var queryRegistered = false;
        var query = nint.Zero;

        try
        {
            var openRc = _api.OpenSession(out session);
            if (openRc != PM_STATUS.PM_STATUS_SUCCESS)
                return Result<FrameCapture>.Fail(ErrorCodes.PmonUnavailable,
                    $"pmOpenSession a retourné {openRc}: service PresentMon indisponible.");
            sessionOpen = true;

            // 07/10/2026 (J3) : le service PresentMon est partagé. Un autre client qui suit déjà le jeu laisse un arriéré
            // d'images que notre premier pmConsumeFrames rend aussitôt (mesuré le 06/10 : 9,9 s d'images pour 6 s de
            // capture). L'instant QPC d'avant le suivi sert de borne : toute image démarrée avant est écartée.
            var debutQpc = _qpcMaintenant();
            var trackRc = _api.StartTracking(session, pid);
            if (trackRc == PM_STATUS.PM_STATUS_INVALID_PID)
                return Result<FrameCapture>.Fail(ErrorCodes.PidInvalid,
                    $"pmStartTrackingProcess : PID {targetPid} invalide.");
            if (trackRc != PM_STATUS.PM_STATUS_SUCCESS)
                return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail,
                    $"pmStartTrackingProcess a retourné {trackRc} pour PID {targetPid}.");
            tracking = true;

            // Les 8 series par frame, brutes (aucune agregation cote service).
            //
            // L1.1 : les quatre dernieres sont nouvelles. Le classifieur de goulot demande
            // 8 grandeurs ; le produit n'en calculait qu'une, parce que ces champs existaient
            // cote PresentMon et n'etaient tout simplement pas demandes ici. L'ordre de ce
            // tableau est le contrat : chaque `dataOffset` est lu par index juste apres.
            var elements = new[]
            {
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_BETWEEN_PRESENTS, stat = PM_STAT.PM_STAT_NONE },
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_RENDER_PRESENT_LATENCY, stat = PM_STAT.PM_STAT_NONE },
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_GPU_BUSY, stat = PM_STAT.PM_STAT_NONE },
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_GPU_WAIT, stat = PM_STAT.PM_STAT_NONE },
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_CPU_BUSY, stat = PM_STAT.PM_STAT_NONE },
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_IN_PRESENT_API, stat = PM_STAT.PM_STAT_NONE },
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_ANIMATION_ERROR, stat = PM_STAT.PM_STAT_NONE },
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_PRESENT_MODE, stat = PM_STAT.PM_STAT_NONE },
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_CPU_START_QPC, stat = PM_STAT.PM_STAT_NONE },
                // B4 (10/10/2026) : la latence d'affichage (début de l'image → écran), pour juger les leviers de latence.
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_DISPLAY_LATENCY, stat = PM_STAT.PM_STAT_NONE },
            };

            var regRc = _api.RegisterFrameQuery(session, elements, out query, out var blobSize);
            if (regRc != PM_STATUS.PM_STATUS_SUCCESS)
                return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail,
                    $"pmRegisterFrameQuery a retourné {regRc}.");
            queryRegistered = true;

            if (blobSize == 0)
                return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail,
                    "pmRegisterFrameQuery a renvoyé un blobSize nul.");

            var frametimeOffset = (int)elements[0].dataOffset;
            var latencyOffset = (int)elements[1].dataOffset;
            var gpuBusyOffset = (int)elements[2].dataOffset;
            var gpuWaitOffset = (int)elements[3].dataOffset;
            var cpuBusyOffset = (int)elements[4].dataOffset;
            var presentApiOffset = (int)elements[5].dataOffset;
            var animationErrorOffset = (int)elements[6].dataOffset;
            // Un enum : lu à la taille que le service annonce, jamais supposée. Taille inconnue ⇒ série vide.
            var presentModeOffset = (int)elements[7].dataOffset;
            var presentModeSize = elements[7].dataSize;
            // L'instant de chaque image, sur l'horloge QPC (celle de Stopwatch). Taille autre que 8 : pas de filtre, comme avant.
            var debutImageOffset = (int)elements[8].dataOffset;
            var filtrerArriere = elements[8].dataSize == 8;
            // Lue à la taille annoncée seulement : une taille autre que 8 = série absente, jamais un chiffre inventé.
            var latenceAffichageOffset = (int)elements[9].dataOffset;
            var latenceAffichageLue = elements[9].dataSize == 8;
            var buffer = new byte[checked((int)blobSize * (int)MaxFramesPerPoll)];
            var frames = new List<double>();
            var latencies = new List<double>();
            var gpuBusy = new List<double>();
            var gpuWait = new List<double>();
            var cpuBusy = new List<double>();
            var presentApi = new List<double>();
            var animationError = new List<double>();
            var presentModes = new List<int>();
            var latenceAffichage = new List<double>();

            // La durée se compte à partir de la PREMIÈRE image, pas du pmStartTrackingProcess.
            // Mesure du 06/10/2026 (scène D3D11, service Intel 2.5.1, `tools/scene`) : le service
            // n'active ses fournisseurs ETW (DXGI, D3D9, DxgKrnl) qu'au début du suivi, et la
            // première image arrive 1 à 2 s plus tard. Capture d'1 s : « aucune image » ; de 3 s :
            // une seule seconde couverte. L'attente est bornée : un processus qui ne présente rien
            // reste « aucune image », avec AttentePremiereImage de plus.
            _clock.Restart();
            var enAttente = true;
            // 07/10/2026 (J3) : quand chaque image porte son instant, la durée se mesure sur ces instants, pas sur l'horloge
            // de DrDXT : le service livre les images avec 1 à 2 s de retard (mesuré : 844 images au lieu de ~1 200 pour 6 s
            // à 200 FPS avec un 2e client). La capture s'arrête à la première image hors fenêtre ; l'horloge ne reste qu'un
            // filet, avec une marge de livraison.
            long? finQpc = null;
            // Revue F-34 : si l'horloge des images n'est pas celle de DrDXT, tout serait écarté en silence ; le compte
            // et le premier instant lu vont dans le message d'échec.
            var ecartees = 0;
            long? premierEcarte = null;
            var fenetreFinie = false;
            while (!fenetreFinie && _clock.Elapsed < (enAttente ? AttentePremiereImage + duration
                       : duration + (filtrerArriere ? MargeDeLivraison : TimeSpan.Zero)) && !ct.IsCancellationRequested)
            {
                var consumeRc = _api.ConsumeFrames(query, pid, buffer, MaxFramesPerPoll, out var framesRead);
                if (consumeRc != PM_STATUS.PM_STATUS_SUCCESS)
                    return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail,
                        $"pmConsumeFrames a retourné {consumeRc}.");

                if (framesRead == 0)
                {
                    await Task.Delay(PollDelay, ct).ConfigureAwait(false); // soft poll — don't spin the CPU
                    continue;
                }

                for (var i = 0; i < framesRead; i++)
                {
                    var frameBase = (int)i * (int)blobSize;
                    if (filtrerArriere && BitConverter.ToUInt64(buffer, frameBase + debutImageOffset) is var qpc && qpc != 0)
                    {
                        if ((long)qpc < debutQpc) { ecartees++; premierEcarte ??= (long)qpc; continue; }
                        finQpc ??= (long)qpc + (long)(duration.TotalSeconds * _qpcParSeconde);
                        if ((long)qpc >= finQpc) { fenetreFinie = true; break; }
                    }
                    // La durée part de la première image GARDÉE : un arriéré seul ne lance pas le chronomètre.
                    if (enAttente) { enAttente = false; _clock.Restart(); }
                    frames.Add(BitConverter.ToDouble(buffer, frameBase + frametimeOffset));
                    // Render-present latency (present→display portion only — not system latency).
                    latencies.Add(BitConverter.ToDouble(buffer, frameBase + latencyOffset));
                    // GPU busy (ms the GPU worked on the frame) — GpuBusy/FrameTime ⇒ GPU- vs CPU-bound.
                    gpuBusy.Add(BitConverter.ToDouble(buffer, frameBase + gpuBusyOffset));
                    // L1.1 — les quatre grandeurs qui manquaient au classifieur. Meme lecture
                    // brute, meme blob, meme frame : les series restent PARALLELES, ce dont
                    // depend tout ratio par frame calcule en aval.
                    gpuWait.Add(BitConverter.ToDouble(buffer, frameBase + gpuWaitOffset));
                    cpuBusy.Add(BitConverter.ToDouble(buffer, frameBase + cpuBusyOffset));
                    presentApi.Add(BitConverter.ToDouble(buffer, frameBase + presentApiOffset));
                    animationError.Add(BitConverter.ToDouble(buffer, frameBase + animationErrorOffset));
                    if (latenceAffichageLue) latenceAffichage.Add(BitConverter.ToDouble(buffer, frameBase + latenceAffichageOffset));
                    if (presentModeSize == 4) presentModes.Add(BitConverter.ToInt32(buffer, frameBase + presentModeOffset));
                    else if (presentModeSize == 8) presentModes.Add((int)BitConverter.ToInt64(buffer, frameBase + presentModeOffset));
                }
            }

            if (frames.Count == 0)
                return Result<FrameCapture>.Fail(ErrorCodes.NoFrames, ecartees > 0
                    ? $"Aucune frame capturée : {ecartees} image(s) écartée(s) comme antérieures au suivi (premier instant lu "
                      + $"{premierEcarte}, borne {debutQpc}). Si cela se répète, l'horloge des images n'est pas celle de DrDXT."
                    : "Aucune frame capturée (jeu hors premier plan, anti-cheat, ou jeu non lancé ?).");

            return Result<FrameCapture>.Ok(
                new FrameCapture(frames, latencies, gpuBusy, gpuWait, cpuBusy, presentApi, animationError, presentModes,
                    DisplayLatencyMs: latenceAffichageLue ? latenceAffichage : null));
        }
        catch (OperationCanceledException)
        {
            return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail, "Capture annulée.");
        }
        catch (DllNotFoundException)
        {
            return Result<FrameCapture>.Fail(ErrorCodes.PmonUnavailable,
                "PresentMonAPI2.dll introuvable: PresentMon non installé.");
        }
        catch (EntryPointNotFoundException ex)
        {
            return Result<FrameCapture>.Fail(ErrorCodes.PmonUnavailable,
                $"Point d'entrée PresentMon introuvable (mauvaise version DLL ?) : {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail, ex.Message);
        }
        finally
        {
            // Always release native resources, each step isolated so one failure can't mask the others.
            if (queryRegistered) { try { _api.FreeFrameQuery(query); } catch { /* best-effort */ } }
            if (tracking) { try { _api.StopTracking(session, pid); } catch { /* best-effort */ } }
            if (sessionOpen) { try { _api.CloseSession(session); } catch { /* best-effort */ } }
        }
    }
}
