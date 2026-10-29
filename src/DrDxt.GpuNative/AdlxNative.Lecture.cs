using System.Runtime.InteropServices;


namespace DrDxt.Detection.Gpu.Adlx;

/// <summary>Lectures ADLX : réglages graphiques, puissance, ventilateurs, et l'accès à un GPU par index.</summary>
public sealed partial class AdlxNative
{
    // ── TryReadGfx ────────────────────────────────────────────────────────

    public bool TryReadGfx(int gpuIndex, out AdlxGfxReads reads)
    {
        reads = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pGfxBase   = IntPtr.Zero;
        IntPtr pGfxV2     = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            // GetGPUTuningServices (IADLXSystem slot 8)
            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualGFXTuning (slot 8)
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualGfx);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var gfxOk) != 0 || gfxOk == 0)
                return false;

            // GetManualGFXTuning (slot 14) → IADLXManualGraphicsTuning1*
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualGfx);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pGfxBase) != 0
                || pGfxBase == IntPtr.Zero) return false;

            // Tenter un QI vers IADLXManualGraphicsTuning2 (RDNA) ; la base est Tuning1 (emplacement 2 = QI).
            var qi = Slot<QueryIfDelegate>(pGfxBase, AdlxVtableSlots.QueryInterface);
            if (qi != null
                && qi(pGfxBase, "IADLXManualGraphicsTuning2", out pGfxV2) == 0
                && pGfxV2 != IntPtr.Zero)
            {
                return ReadGfxV2(pGfxV2, out reads);
            }

            // Chemin Vega / plus ancien : Tuning1 n'expose pas les API de lecture de décalage / tension dont
            // se sert notre réglage. Le dire honnêtement « non lisible » (false) plutôt qu'une fausse valeur
            // zéro : un 0 laisserait passer la barrière sur une carte qu'on ne peut pas vraiment régler. La
            // barrière saute alors proprement (jamais une fausse lecture).
            return false;
        }
        catch { return false; }
        finally
        {
            if (pGfxV2     != IntPtr.Zero) Release(pGfxV2);
            if (pGfxBase   != IntPtr.Zero) Release(pGfxBase);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    // La fréquence est essentielle ; la tension est facultative. RDNA4 (RX 9000) n'a plus de contrôle
    // manuel de tension (comme NVIDIA Blackwell) : GetGPUVoltage y échoue alors que les lectures de
    // fréquence réussissent. Exiger la tension ferait passer pour « non prise en charge » une carte
    // parfaitement réglable. Donc : échouer seulement si une lecture de fréquence échoue ; sans
    // tension, la mettre à 0 / plage vide et rendre quand même les données de fréquence.
    private static bool ReadGfxV2(IntPtr pT, out AdlxGfxReads reads)
    {
        reads = default;
        try
        {
            var getMinRange  = Slot<GetRangeDelegate>(pT, AdlxVtableSlots.Gfx2_GetMinFreqRange);
            var getMinFreq   = Slot<GetIntDelegate>(pT, AdlxVtableSlots.Gfx2_GetMinFreq);
            var getMaxRange  = Slot<GetRangeDelegate>(pT, AdlxVtableSlots.Gfx2_GetMaxFreqRange);
            var getMaxFreq   = Slot<GetIntDelegate>(pT, AdlxVtableSlots.Gfx2_GetMaxFreq);
            var getVoltRange = Slot<GetRangeDelegate>(pT, AdlxVtableSlots.Gfx2_GetVoltRange);
            var getVolt      = Slot<GetIntDelegate>(pT, AdlxVtableSlots.Gfx2_GetVolt);

            // Seules les lectures de fréquence max sont essentielles : c'est le bouton qu'on règle.
            // RDNA4 (RX 9000) en abandonne plusieurs autres (fréquence min et tension) : les traiter toutes
            // comme facultatives ; une carte qui expose MaxFreq + sa plage est réglable.
            if (getMaxRange == null || getMaxFreq == null) return false;
            AdlxIntRangeNative maxR = default; int maxFreq = 0;
            if (getMaxRange(pT, out maxR)   != 0) return false;
            if (getMaxFreq(pT, out maxFreq) != 0) return false;

            // Lectures facultatives : 0 / vide par défaut quand le pilote ne les expose pas.
            AdlxIntRangeNative minR = default; int minFreq = 0;
            if (getMinRange != null && getMinRange(pT, out var mr2) == 0) minR = mr2;
            if (getMinFreq  != null && getMinFreq(pT,  out var mf2) == 0) minFreq = mf2;

            AdlxIntRangeNative voltR = default; int volt = 0;
            if (getVoltRange != null && getVolt != null
             && getVoltRange(pT, out var vr2) == 0 && getVolt(pT, out var v2) == 0)
            { voltR = vr2; volt = v2; }

            reads = new AdlxGfxReads
            {
                VoltMv       = volt,
                VoltRange    = ToPublic(voltR),
                MaxFreqMhz   = maxFreq,
                MaxFreqRange = ToPublic(maxR),
                MinFreqMhz   = minFreq,
                MinFreqRange = ToPublic(minR),
            };
            return true;
        }
        catch { return false; }
    }

    // ── TryReadPower ──────────────────────────────────────────────────────

    public bool TryReadPower(int gpuIndex, out AdlxPowerReads reads)
    {
        reads = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList    = IntPtr.Zero;
        IntPtr pGpu        = IntPtr.Zero;
        IntPtr pTuningSvc  = IntPtr.Zero;
        IntPtr pPowerTune  = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualPowerTuning (slot 11)
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualPower);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var pwrOk) != 0 || pwrOk == 0)
                return false;

            // GetManualPowerTuning (slot 17)
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualPower);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pPowerTune) != 0
                || pPowerTune == IntPtr.Zero) return false;

            var getRange = Slot<GetRangeDelegate>(pPowerTune, AdlxVtableSlots.Power_GetLimitRange);
            var getPct   = Slot<GetIntDelegate>(pPowerTune, AdlxVtableSlots.Power_GetLimit);
            if (getRange == null || getPct == null) return false;

            if (getRange(pPowerTune, out var pwrRange) != 0) return false;
            if (getPct(pPowerTune,   out var pct)      != 0) return false;

            reads = new AdlxPowerReads
            {
                PowerLimitPct = pct,
                PowerRange    = ToPublic(pwrRange),
            };
            return true;
        }
        catch { return false; }
        finally
        {
            if (pPowerTune != IntPtr.Zero) Release(pPowerTune);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    // ── TryReadFan ────────────────────────────────────────────────────────

    public bool TryReadFan(int gpuIndex, out AdlxFanReads reads)
    {
        reads = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pFanTune   = IntPtr.Zero;
        IntPtr pStateList = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualFanTuning (slot 10)
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualFan);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var fanOk) != 0 || fanOk == 0)
                return false;

            // GetManualFanTuning (slot 16)
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualFan);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pFanTune) != 0
                || pFanTune == IntPtr.Zero) return false;

            // GetFanTuningRanges (emplacement 3) : speedRange d'abord, puis tempRange
            var getRanges = Slot<GetTwoRangesDelegate>(pFanTune, AdlxVtableSlots.Fan_GetTuningRanges);
            if (getRanges == null) return false;
            if (getRanges(pFanTune, out var speedRange, out var tempRange) != 0)
                return false;

            // GetFanTuningStates (slot 4) → IADLXManualFanTuningStateList*
            var getStates = Slot<GetOutPtrDelegate>(pFanTune, AdlxVtableSlots.Fan_GetTuningStates);
            List<AdlxFanPoint> points = new();
            if (getStates != null
                && getStates(pFanTune, out pStateList) == 0
                && pStateList != IntPtr.Zero)
            {
                points = CollectFanPoints(pStateList);
            }

            reads = new AdlxFanReads
            {
                Points     = points,
                TempRange  = ToPublic(tempRange),
                SpeedRange = ToPublic(speedRange),
            };
            return true;
        }
        catch { return false; }
        finally
        {
            if (pStateList != IntPtr.Zero) Release(pStateList);
            if (pFanTune   != IntPtr.Zero) Release(pFanTune);
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu       != IntPtr.Zero) Release(pGpu);
            if (pGpuList   != IntPtr.Zero) Release(pGpuList);
        }
    }

    private static List<AdlxFanPoint> CollectFanPoints(IntPtr pStateList)
    {
        var pts = new List<AdlxFanPoint>();
        try
        {
            var sizeFn  = Slot<SizeDelegate>(pStateList, AdlxVtableSlots.FanStateList_Size);
            var atState = Slot<AtIndexDelegate>(pStateList, AdlxVtableSlots.FanStateList_At);
            if (sizeFn == null || atState == null) return pts;

            uint count = sizeFn(pStateList);
            for (uint i = 0; i < count; i++)
            {
                IntPtr pState = IntPtr.Zero;
                try
                {
                    if (atState(pStateList, i, out pState) != 0 || pState == IntPtr.Zero)
                        continue;

                    var getSpeed = Slot<GetIntDelegate>(pState, AdlxVtableSlots.FanState_GetFanSpeed);
                    var getTemp  = Slot<GetIntDelegate>(pState, AdlxVtableSlots.FanState_GetTemperature);
                    if (getSpeed == null || getTemp == null) continue;

                    if (getSpeed(pState, out var speed) != 0) continue;
                    if (getTemp(pState,  out var temp)  != 0) continue;

                    pts.Add(new AdlxFanPoint { TempC = temp, SpeedPct = speed });
                }
                finally
                {
                    if (pState != IntPtr.Zero) Release(pState);
                }
            }
        }
        catch { /* return what we managed to collect */ }
        return pts;
    }

    // ── Shared: GetGPUs + At_GPUList ─────────────────────────────────────

    private bool GetGpuAtIndex(int gpuIndex, out IntPtr pGpuList, out IntPtr pGpu)
    {
        pGpuList = IntPtr.Zero;
        pGpu     = IntPtr.Zero;
        try
        {
            // IADLXSystem slot 1 = GetGPUs
            var getGpus = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUs);
            if (getGpus == null
                || getGpus(_pSystem, out pGpuList) != 0
                || pGpuList == IntPtr.Zero) return false;

            // IADLXGPUList slot 3 = Size
            var sizeFn = Slot<SizeDelegate>(pGpuList, AdlxVtableSlots.GpuList_Size);
            if (sizeFn == null || gpuIndex >= (int)sizeFn(pGpuList)) return false;

            // IADLXGPUList slot 11 = At_GPUList
            var atFn = Slot<AtIndexDelegate>(pGpuList, AdlxVtableSlots.GpuList_At);
            if (atFn == null
                || atFn(pGpuList, (uint)gpuIndex, out pGpu) != 0
                || pGpu == IntPtr.Zero) return false;

            return true;
        }
        catch { return false; }
    }

    // ── Reflets des structs natives ─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlxIntRangeNative
    {
        public int minValue;
        public int maxValue;
        public int step;
    }

    private static AdlxIntRange ToPublic(AdlxIntRangeNative n) =>
        new() { Min = n.minValue, Max = n.maxValue, Step = n.step };

    // (pThis, out speedRange, out tempRange) : GetFanTuningRanges
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTwoRangesDelegate(
        IntPtr pThis, out AdlxIntRangeNative r1, out AdlxIntRangeNative r2);
}
