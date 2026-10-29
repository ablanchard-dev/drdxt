using System.Runtime.InteropServices;


namespace DrDxt.Detection.Gpu.Adlx;

/// <summary>Sondes ADLX : le chemin des réglages graphiques, les réglages 3D lus et FreeSync.</summary>
public sealed partial class AdlxNative
{
    // ── ProbeGfxPath (diagnostics) ────────────────────────────────────────
    // Reprend le parcours de TryReadGfx mais note l'issue de chaque étape, pour qu'une barrière sautée
    // dise exactement où ADLX s'est arrêté. Lecture seule ; ne lève jamais.
    // Utilise les mêmes constantes AdlxVtableSlots que le vrai chemin (pas de dérive d'emplacement).

    public IReadOnlyList<GpuProbeStep> ProbeGfxPath(int gpuIndex)
    {
        var steps = new List<GpuProbeStep>();

        if (!IsAvailable)   // déclenche EnsureInitialized → remplit _initDetail en cas d'échec
        {
            steps.Add(new GpuProbeStep("adlx-init", false,
                _initDetail ?? "ADLXInitialize did not succeed (no AMD GPU / Adrenalin driver)."));
            return steps;
        }
        steps.Add(new GpuProbeStep("adlx-init", true, $"ADLXInitialize ok (result={_initResult})."));

        if (gpuIndex < 0)
        {
            steps.Add(new GpuProbeStep("gpu-index", false, $"Invalid GPU index {gpuIndex}."));
            return steps;
        }

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pGfxBase   = IntPtr.Zero;
        IntPtr pGfxV2     = IntPtr.Zero;
        try
        {
            var getGpus = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUs);
            if (getGpus == null || getGpus(_pSystem, out pGpuList) != 0 || pGpuList == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("get-gpus", false, "IADLXSystem.GetGPUs returned no list."));
                return steps;
            }
            steps.Add(new GpuProbeStep("get-gpus", true));

            var sizeFn = Slot<SizeDelegate>(pGpuList, AdlxVtableSlots.GpuList_Size);
            uint count = sizeFn?.Invoke(pGpuList) ?? 0u;
            if (sizeFn == null || gpuIndex >= (int)count)
            {
                steps.Add(new GpuProbeStep("gpu-count", false,
                    $"{count} GPU(s) listed; requested index {gpuIndex} is out of range."));
                return steps;
            }
            steps.Add(new GpuProbeStep("gpu-count", true, $"{count} GPU(s) listed; using index {gpuIndex}."));

            var atFn = Slot<AtIndexDelegate>(pGpuList, AdlxVtableSlots.GpuList_At);
            if (atFn == null || atFn(pGpuList, (uint)gpuIndex, out pGpu) != 0 || pGpu == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("get-gpu", false, "IADLXGPUList.At_GPUList returned no GPU."));
                return steps;
            }
            steps.Add(new GpuProbeStep("get-gpu", true));

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null || getTuningSvc(_pSystem, out pTuningSvc) != 0 || pTuningSvc == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("tuning-services", false, "GetGPUTuningServices failed."));
                return steps;
            }
            steps.Add(new GpuProbeStep("tuning-services", true));

            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualGfx);
            if (isSupported == null || isSupported(pTuningSvc, pGpu, out var gfxOk) != 0)
            {
                steps.Add(new GpuProbeStep("manual-gfx-supported", false, "IsSupportedManualGFXTuning call failed."));
                return steps;
            }
            if (gfxOk == 0)
            {
                steps.Add(new GpuProbeStep("manual-gfx-supported", false,
                    "Card reports manual GFX tuning UNSUPPORTED for this GPU/driver."));
                return steps;
            }
            steps.Add(new GpuProbeStep("manual-gfx-supported", true, "supported=yes"));

            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualGfx);
            if (getManual == null || getManual(pTuningSvc, pGpu, out pGfxBase) != 0 || pGfxBase == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("get-manual-gfx", false, "GetManualGFXTuning returned no interface."));
                return steps;
            }
            steps.Add(new GpuProbeStep("get-manual-gfx", true));

            var qi = Slot<QueryIfDelegate>(pGfxBase, AdlxVtableSlots.QueryInterface);
            if (qi == null || qi(pGfxBase, "IADLXManualGraphicsTuning2", out pGfxV2) != 0 || pGfxV2 == IntPtr.Zero)
            {
                steps.Add(new GpuProbeStep("qi-tuning2", false,
                    "No IADLXManualGraphicsTuning2: likely Vega/older; frequency/voltage offset tuning unavailable."));
                return steps;
            }
            steps.Add(new GpuProbeStep("qi-tuning2", true));

            // Lectures fines : dire quel Get* échoue. RDNA4 (RX 9000) garde le réglage de fréquence max mais
            // abandonne la fréquence min et la tension ; seuls MaxFreq + sa plage sont requis.
            var getMinRange  = Slot<GetRangeDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetMinFreqRange);
            var getMinFreq   = Slot<GetIntDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetMinFreq);
            var getMaxRange  = Slot<GetRangeDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetMaxFreqRange);
            var getMaxFreq   = Slot<GetIntDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetMaxFreq);
            var getVoltRange = Slot<GetRangeDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetVoltRange);
            var getVolt      = Slot<GetIntDelegate>(pGfxV2, AdlxVtableSlots.Gfx2_GetVolt);

            int tmpI = 0; AdlxIntRangeNative tmpR = default;
            bool maxFreqOk   = getMaxFreq   != null && getMaxFreq(pGfxV2,   out tmpI) == 0; int maxFreqVal = tmpI;
            bool maxRangeOk  = getMaxRange  != null && getMaxRange(pGfxV2,  out tmpR) == 0; AdlxIntRangeNative maxRangeVal = tmpR;
            bool minFreqOk   = getMinFreq   != null && getMinFreq(pGfxV2,   out tmpI) == 0;
            bool minRangeOk  = getMinRange  != null && getMinRange(pGfxV2,  out tmpR) == 0;
            bool voltOk      = getVolt      != null && getVolt(pGfxV2,      out tmpI) == 0;
            bool voltRangeOk = getVoltRange != null && getVoltRange(pGfxV2, out tmpR) == 0;

            steps.Add(new GpuProbeStep("read-maxfreq",       maxFreqOk,   maxFreqOk  ? $"max={maxFreqVal} MHz" : "GetGPUMaxFrequency error"));
            steps.Add(new GpuProbeStep("read-maxfreq-range", maxRangeOk,  maxRangeOk ? $"range=[{maxRangeVal.minValue},{maxRangeVal.maxValue}] MHz" : "GetGPUMaxFrequencyRange error"));
            steps.Add(new GpuProbeStep("read-minfreq",       minFreqOk,   minFreqOk  ? "ok" : "GetGPUMinFrequency error (RDNA4 drops this: OPTIONAL)"));
            steps.Add(new GpuProbeStep("read-minfreq-range", minRangeOk,  minRangeOk ? "ok" : "GetGPUMinFrequencyRange error (RDNA4 drops this: OPTIONAL)"));
            steps.Add(new GpuProbeStep("read-voltage",       voltOk,      voltOk     ? "ok" : "GetGPUVoltage error (RDNA4 drops this: OPTIONAL)"));
            steps.Add(new GpuProbeStep("read-voltage-range", voltRangeOk, voltRangeOk? "ok" : "GetGPUVoltageRange error (RDNA4 drops this: OPTIONAL)"));
            steps.Add(new GpuProbeStep("gfx-tunable", maxFreqOk && maxRangeOk,
                (maxFreqOk && maxRangeOk)
                    ? "FREQUENCY tuning available (only MaxFreq required): this GPU is tunable"
                    : "MaxFreq read failed: cannot tune this GPU"));
            return steps;
        }
        catch (Exception ex)
        {
            steps.Add(new GpuProbeStep("probe-exception", false, $"{ex.GetType().Name}: {ex.Message}"));
            return steps;
        }
        finally
        {
            if (pGfxV2     != IntPtr.Zero) Release(pGfxV2);
            if (pGfxBase   != IntPtr.Zero) Release(pGfxBase);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    // ── LireReglages (A1, lecture seule) ──────────────────────────────────
    // Chaque valeur est lue à part : une entrée qui rend une erreur ADLX laisse sa valeur à null et
    // n'empêche pas les autres. Aucune entrée Set* n'est appelée ici.

    private const int AdlxGpuTypeDiscrete = 2; // ADLX_GPU_TYPE.GPUTYPE_DISCRETE (ADLXDefines.h)
    private const int AdlxGpuTypeIntegrated = 1; // ADLX_GPU_TYPE.GPUTYPE_INTEGRATED

    public AdlxReglagesLus LireReglages()
    {
        if (!IsAvailable)
            return AdlxReglagesLus.RienLu(_initDetail ?? "ADLXInitialize did not succeed.");

        IntPtr pGpuList = IntPtr.Zero;
        IntPtr pGpu     = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return AdlxReglagesLus.RienLu(raison);

            var (antiLag, frtcActif, frtcFps, frtcReglee, chillActif, chillMin, chillMax) = Lire3D(pGpu);
            var (vsyncMode, enhancedSync) = LireVsync(pGpu);
            var (boost, ris, rsr, afmf) = LireRadeon(pGpu);
            return new AdlxReglagesLus(
                Sam: LireSam(pGpu),
                AntiLag: antiLag,
                FrtcActif: frtcActif,
                FrtcFps: frtcFps,
                ChillActif: chillActif,
                ChillMinFps: chillMin,
                ChillMaxFps: chillMax,
                TemperatureC: LireTemperature(pGpu),
                FrtcFpsReglee: frtcReglee,
                VsyncMode: vsyncMode,
                EnhancedSyncActif: enhancedSync,
                BoostActif: boost,
                ImageSharpeningActif: ris,
                RsrActif: rsr,
                AfmfActif: afmf);
        }
        catch (Exception ex) { return AdlxReglagesLus.RienLu($"ADLX read interrupted: {ex.GetType().Name}."); }
        finally
        {
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // ── LireFreeSync (A-FS1, 08/10/2026, lecture seule) ─────────────────
    // IADLXSystem.GetDisplaysServices (3) → GetDisplays (4) → pour chaque écran : Name (6) et GetFreeSync (9), puis
    // IsSupported / IsEnabled. Le nom appartient à ADLX : il est copié, jamais libéré ici.
    public IReadOnlyList<AdlxEcranFreeSync> LireFreeSync()
    {
        var ecrans = new List<AdlxEcranFreeSync>();
        if (!IsAvailable) return ecrans;

        IntPtr pSvc = IntPtr.Zero, pListe = IntPtr.Zero;
        try
        {
            var getSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetDisplaysServices);
            if (getSvc == null || getSvc(_pSystem, out pSvc) != 0 || pSvc == IntPtr.Zero) return ecrans;
            var getListe = Slot<GetOutPtrDelegate>(pSvc, AdlxVtableSlots.DisplayServices_GetDisplays);
            if (getListe == null || getListe(pSvc, out pListe) != 0 || pListe == IntPtr.Zero) return ecrans;
            var taille = Slot<SizeDelegate>(pListe, AdlxVtableSlots.DisplayList_Size);
            var at = Slot<AtIndexDelegate>(pListe, AdlxVtableSlots.DisplayList_At);
            var getFs = Slot<GetManualDelegate>(pSvc, AdlxVtableSlots.DisplayServices_GetFreeSync);
            if (taille == null || at == null || getFs == null) return ecrans;

            for (uint i = 0, n = taille(pListe); i < n; i++)
            {
                IntPtr pEcran = IntPtr.Zero, pFs = IntPtr.Zero;
                try
                {
                    if (at(pListe, i, out pEcran) != 0 || pEcran == IntPtr.Zero) continue;
                    var getNom = Slot<GetOutPtrDelegate>(pEcran, AdlxVtableSlots.Display_Name);
                    var nom = getNom != null && getNom(pEcran, out var pNom) == 0 && pNom != IntPtr.Zero
                        ? Marshal.PtrToStringAnsi(pNom) : null;
                    var (sup, actif) = getFs(pSvc, pEcran, out pFs) == 0 && pFs != IntPtr.Zero
                        ? Etat(pFs) : ((bool?)null, (bool?)null);
                    ecrans.Add(new AdlxEcranFreeSync(nom ?? "", sup, actif));
                }
                finally
                {
                    Release(pFs);
                    Release(pEcran);
                }
            }
            return ecrans;
        }
        catch { return ecrans; }
        finally
        {
            Release(pListe);
            Release(pSvc);
        }
    }
}
