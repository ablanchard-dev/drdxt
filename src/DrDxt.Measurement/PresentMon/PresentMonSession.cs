using DrDxt.Core;

namespace DrDxt.Measurement.PresentMon;

/// <summary>
/// Le vrai <see cref="IPresentMonSession"/>, construit sur le modèle de requête d'images de PresentMonAPI2 :
/// pmOpenSession → pmStartTrackingProcess → pmRegisterFrameQuery → pmConsumeFrames (parse blob)
/// → pmFreeFrameQuery → pmStopTrackingProcess → pmCloseSession.
///
/// Ne lève jamais : chaque <see cref="PM_STATUS"/> non réussi et chaque exception P/Invoke devient un échec
/// <see cref="Result"/> (le scanner passe en détection seule). Nettoyage natif dans un finally, chaque étape isolée.
/// </summary>
public sealed class PresentMonSession : IPresentMonSession
{
    // Capacité du tampon, en images, pour une interrogation pmConsumeFrames. Dimensionnée pour qu'un
    // jeu à FPS élevés ne puisse pas la déborder entre deux interrogations : le délai demandé est de 2 ms, mais
    // l'horloge de Windows l'arrondit à environ 15,6 ms, et une interrogation qui rend des images est suivie aussitôt
    // de la suivante.
    private const uint MaxFramesPerPoll = 4096;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(2);
    // Mesuré : 1 à 2 s entre pmStartTrackingProcess et la première image. 5 s de marge.
    private static readonly TimeSpan AttentePremiereImage = TimeSpan.FromSeconds(5);

    private readonly IPresentMonApi _api;
    private readonly IMonotonicClock _clock;

    /// <inheritdoc/>
    public string? LastUnavailableDetail { get; private set; }

    // internal : la couture d'API est interne, et un constructeur public la ferait fuir (CS0051).
    // La DI le construit par une fabrique explicite dans AddDrDxtMeasurement ; les tests l'utilisent
    // directement grâce à InternalsVisibleTo.
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
                // Le service a répondu : fermer la session de sonde et le déclarer disponible.
                try { _api.CloseSession(session); } catch { /* best-effort */ }
                LastUnavailableDetail = null;
                return Result<bool>.Ok(true);
            }
            // DLL chargée mais service refusé → indisponible. État attendu, pas une erreur.
            // On garde le code brut : c'est lui qui distingue « service arrêté » d'un
            // PIPE_ERROR causé par un autre PresentMon installé sur la machine.
            LastUnavailableDetail = $"PM_STATUS={(int)rc} ({rc})";
            return Result<bool>.Ok(false);
        }
        catch (DllNotFoundException)
        {
            // PresentMonAPI2.dll absente : normal en CI / sur une machine de dev sans PresentMon.
            LastUnavailableDetail = "PresentMonAPI2.dll introuvable";
            return Result<bool>.Ok(false);
        }
        catch (EntryPointNotFoundException)
        {
            // DLL présente mais mauvaise version / ABI incompatible.
            LastUnavailableDetail = Docteur.T("PM.AbiIncompatible");
            return Result<bool>.Ok(false);
        }
        catch (Exception ex)
        {
            // Tout autre échec (y compris une SEH d'une DLL cassée) → « pas de service », jamais d'exception.
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
            return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail, Docteur.T("PM.AnnuleeAvant"));

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
                    Docteur.T("PM.OpenSession", openRc));
            sessionOpen = true;

            // Le service PresentMon est partagé. Un autre client qui suit déjà le jeu laisse un arriéré
            // d'images que notre premier pmConsumeFrames rend aussitôt (mesuré le 06/10 : 9,9 s d'images pour 6 s de
            // capture). L'instant QPC d'avant le suivi sert de borne : toute image démarrée avant est écartée.
            var debutQpc = _qpcMaintenant();
            var trackRc = _api.StartTracking(session, pid);
            if (trackRc == PM_STATUS.PM_STATUS_INVALID_PID)
                return Result<FrameCapture>.Fail(ErrorCodes.PidInvalid,
                    $"pmStartTrackingProcess : PID {targetPid} invalide.");
            if (trackRc != PM_STATUS.PM_STATUS_SUCCESS)
                return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail,
                    Docteur.T("PM.Tracking", trackRc, targetPid));
            tracking = true;

            // Les onze series par frame, brutes (aucune agregation cote service), dont les grandeurs que demande le
            // classifieur de goulot. L'ordre de ce tableau est le contrat : chaque `dataOffset` est lu par index juste apres.
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
                // La latence d'affichage (début de l'image → écran), pour juger les leviers de latence.
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_DISPLAY_LATENCY, stat = PM_STAT.PM_STAT_NONE },
                // La latence PC (entrée → écran), livrée seulement quand le jeu pose les marqueurs de latence.
                new PM_QUERY_ELEMENT { metric = PM_METRIC.PM_METRIC_PC_LATENCY, stat = PM_STAT.PM_STAT_NONE },
            };

            var regRc = _api.RegisterFrameQuery(session, elements, out query, out var blobSize);
            if (regRc != PM_STATUS.PM_STATUS_SUCCESS)
                return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail,
                    Docteur.T("PM.Register", regRc));
            queryRegistered = true;

            if (blobSize == 0)
                return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail,
                    Docteur.T("PM.BlobNul"));

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
            // L'instant de chaque image, sur l'horloge QPC (celle de Stopwatch). Taille autre que 8 : pas de filtre.
            var debutImageOffset = (int)elements[8].dataOffset;
            var filtrerArriere = elements[8].dataSize == 8;
            // Lue à la taille annoncée seulement : une taille autre que 8 = série absente, jamais un chiffre inventé.
            var latenceAffichageOffset = (int)elements[9].dataOffset;
            var latenceAffichageLue = elements[9].dataSize == 8;
            var latencePcOffset = (int)elements[10].dataOffset;
            var latencePcLue = elements[10].dataSize == 8;
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
            var latencePc = new List<double>();

            // La durée part de la première image, pas de pmStartTrackingProcess. Mesure du 06/10/2026 (scène D3D11, service
            // Intel 2.5.1, `tools/scene`) : les fournisseurs ETW (DXGI, D3D9, DxgKrnl) ne s'activent qu'au suivi, première
            // image 1 à 2 s plus tard (capture d'1 s : aucune image ; de 3 s : une seconde couverte). Attente bornée : un
            // processus qui ne présente rien reste « aucune image », AttentePremiereImage de plus.
            _clock.Restart();
            var enAttente = true;
            // La durée se mesure sur les instants des images, pas sur l'horloge de DrDXT : le service livre avec 1 à 2 s de
            // retard (mesuré : 844 images au lieu de ~1 200 pour 6 s à 200 FPS avec un 2e client). Arrêt à la première image
            // hors fenêtre ; l'horloge reste un filet, avec une marge de livraison.
            long? finQpc = null;
            // Si l'horloge des images n'est pas celle de DrDXT, tout serait écarté en silence ; le compte
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
                        Docteur.T("PM.Consume", consumeRc));

                if (framesRead == 0)
                {
                    await Task.Delay(PollDelay, ct).ConfigureAwait(false); // interrogation douce : ne pas faire tourner le CPU à vide
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
                    // La durée part de la première image gardée : un arriéré seul ne lance pas le chronomètre.
                    if (enAttente) { enAttente = false; _clock.Restart(); }
                    frames.Add(BitConverter.ToDouble(buffer, frameBase + frametimeOffset));
                    // Latence rendu-présentation (seulement la part présentation→affichage, pas la latence système).
                    latencies.Add(BitConverter.ToDouble(buffer, frameBase + latencyOffset));
                    // GPU busy (ms de travail du GPU sur l'image) : GpuBusy/FrameTime ⇒ limité par le GPU ou par le CPU.
                    gpuBusy.Add(BitConverter.ToDouble(buffer, frameBase + gpuBusyOffset));
                    // Les quatre grandeurs du classifieur, meme blob, meme frame : les series restent paralleles pour les
                    // ratios par frame en aval.
                    gpuWait.Add(BitConverter.ToDouble(buffer, frameBase + gpuWaitOffset));
                    cpuBusy.Add(BitConverter.ToDouble(buffer, frameBase + cpuBusyOffset));
                    presentApi.Add(BitConverter.ToDouble(buffer, frameBase + presentApiOffset));
                    animationError.Add(BitConverter.ToDouble(buffer, frameBase + animationErrorOffset));
                    if (latenceAffichageLue) latenceAffichage.Add(BitConverter.ToDouble(buffer, frameBase + latenceAffichageOffset));
                    if (latencePcLue) latencePc.Add(BitConverter.ToDouble(buffer, frameBase + latencePcOffset));
                    if (presentModeSize == 4) presentModes.Add(BitConverter.ToInt32(buffer, frameBase + presentModeOffset));
                    else if (presentModeSize == 8) presentModes.Add((int)BitConverter.ToInt64(buffer, frameBase + presentModeOffset));
                }
            }

            if (frames.Count == 0)
                return Result<FrameCapture>.Fail(ErrorCodes.NoFrames, ecartees > 0
                    ? Docteur.T("PM.AucuneEcartees", ecartees, premierEcarte, debutQpc)
                    : Docteur.T("PM.Aucune"));

            return Result<FrameCapture>.Ok(
                new FrameCapture(frames, latencies, gpuBusy, gpuWait, cpuBusy, presentApi, animationError, presentModes,
                    DisplayLatencyMs: latenceAffichageLue ? latenceAffichage : null, PcLatencyMs: latencePcLue ? latencePc : null));
        }
        catch (OperationCanceledException)
        {
            return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail, Docteur.T("PM.Annulee"));
        }
        catch (DllNotFoundException)
        {
            return Result<FrameCapture>.Fail(ErrorCodes.PmonUnavailable,
                Docteur.T("PM.Introuvable"));
        }
        catch (EntryPointNotFoundException ex)
        {
            return Result<FrameCapture>.Fail(ErrorCodes.PmonUnavailable,
                Docteur.T("PM.PointEntree", ex.Message));
        }
        catch (Exception ex)
        {
            return Result<FrameCapture>.Fail(ErrorCodes.CaptureFail, ex.Message);
        }
        finally
        {
            // Toujours libérer les ressources natives, chaque étape isolée pour qu'un échec ne masque pas les autres.
            if (queryRegistered) { try { _api.FreeFrameQuery(query); } catch { /* best-effort */ } }
            if (tracking) { try { _api.StopTracking(session, pid); } catch { /* best-effort */ } }
            if (sessionOpen) { try { _api.CloseSession(session); } catch { /* best-effort */ } }
        }
    }
}
