using System.Runtime.InteropServices;


namespace DrDxt.Detection.Gpu.Adlx;

/// <summary>Écritures ADLX : fréquence, limite de puissance, retour à l'usine, Anti-Lag, FRTC et VSync, toujours GET puis SET.</summary>
public sealed partial class AdlxNative
{
    // ── TrySetGfx ─────────────────────────────────────────────────────────

    public bool TrySetGfx(int gpuIndex, int newMaxFreqMhz, int newVoltMv, out AdlxGfxReads original)
    {
        original = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pGfxBase   = IntPtr.Zero;
        IntPtr pGfxV2     = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return false;

            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null
                || getTuningSvc(_pSystem, out pTuningSvc) != 0
                || pTuningSvc == IntPtr.Zero) return false;

            // IsSupportedManualGFXTuning (emplacement 8) : seul RDNA gère la v2 + la tension
            var isSupported = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsSupportedManualGfx);
            if (isSupported == null) return false;
            if (isSupported(pTuningSvc, pGpu, out var gfxOk) != 0 || gfxOk == 0)
                return false;

            // GetManualGFXTuning (slot 14)
            var getManual = Slot<GetManualDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_GetManualGfx);
            if (getManual == null
                || getManual(pTuningSvc, pGpu, out pGfxBase) != 0
                || pGfxBase == IntPtr.Zero) return false;

            // QI vers IADLXManualGraphicsTuning2 (requis pour la tension + l'écriture)
            var qi = Slot<QueryIfDelegate>(pGfxBase, AdlxVtableSlots.QueryInterface);
            if (qi == null
                || qi(pGfxBase, "IADLXManualGraphicsTuning2", out pGfxV2) != 0
                || pGfxV2 == IntPtr.Zero) return false;

            // Lire d'abord l'état actuel (GET puis SET) : capture aussi les plages pour la validation
            if (!ReadGfxV2(pGfxV2, out original)) return false;

            // Valider les valeurs proposées contre les plages matérielles avant toute écriture
            if (newMaxFreqMhz < original.MaxFreqRange.Min || newMaxFreqMhz > original.MaxFreqRange.Max)
                return false;
            if (newVoltMv < original.VoltRange.Min || newVoltMv > original.VoltRange.Max)
                return false;

            // Écrire MaxFreq (emplacement 8) ; la tension (emplacement 11) seulement si elle change vraiment
            return SetGfxV2(pGfxV2, newMaxFreqMhz, newVoltMv, original.MaxFreqMhz, original.VoltMv);
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

    /// <summary>Écrit MaxFreq (emplacement 8) et, seulement si elle change, la tension (emplacement 11)
    /// sur un pointeur IADLXManualGraphicsTuning2 vivant. L'écriture de tension est sautée quand
    /// <paramref name="newVoltMv"/> égale <paramref name="originalVoltMv"/> : RDNA4 (RX 9000) n'a plus
    /// de contrôle manuel de tension, SetGPUVoltage y échouerait ; comme le harnais ne change jamais la
    /// tension (décalage 0), on n'y touche pas. Si SetVolt échoue après un SetMaxFreq réussi, MaxFreq
    /// est remise au mieux à <paramref name="originalMaxFreqMhz"/>.</summary>
    private static bool SetGfxV2(IntPtr pT, int newMaxFreqMhz, int newVoltMv,
        int originalMaxFreqMhz, int originalVoltMv)
    {
        try
        {
            // SetGPUMaxFrequency : emplacement de vtable 8 d'IADLXManualGraphicsTuning2
            var setMaxFreq = Slot<SetIntDelegate>(pT, AdlxVtableSlots.Gfx2_SetMaxFreq);
            if (setMaxFreq == null) return false;
            if (setMaxFreq(pT, newMaxFreqMhz) != 0) return false;

            // SetGPUVoltage : seulement quand la tension change vraiment. Sauter une écriture de tension sans
            // effet est juste, et c'est ce qui garde RDNA4 (sans tension manuelle) fonctionnel.
            if (newVoltMv != originalVoltMv)
            {
                var setVolt = Slot<SetIntDelegate>(pT, AdlxVtableSlots.Gfx2_SetVolt);
                if (setVolt == null || setVolt(pT, newVoltMv) != 0)
                {
                    // Retour arrière d'une écriture partielle, au mieux : son code retour est ignoré. S'il rate (exception ou
                    // code non nul), la nouvelle fréquence reste posée et l'appel rend false. Le seul appelant
                    // (ADLXTuningActions.SetUndervolt) repasse la tension qu'il vient de lire : cette branche n'est prise que
                    // si la tension change entre ses deux lectures, et sur false il relit, rend et dit lui-même l'écriture
                    // partielle.
                    try { setMaxFreq(pT, originalMaxFreqMhz); } catch { }
                    return false;
                }
            }
            return true;
        }
        catch { return false; }
    }

    // ── TrySetPowerLimit ──────────────────────────────────────────────────────

    public bool TrySetPowerLimit(int gpuIndex, int newPowerLimitPct, out AdlxPowerReads original)
    {
        original = default;
        if (!IsAvailable || gpuIndex < 0) return false;

        IntPtr pGpuList   = IntPtr.Zero;
        IntPtr pGpu       = IntPtr.Zero;
        IntPtr pTuningSvc = IntPtr.Zero;
        IntPtr pPowerTune = IntPtr.Zero;
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

            // Lire l'état actuel (GET puis SET)
            var getRange = Slot<GetRangeDelegate>(pPowerTune, AdlxVtableSlots.Power_GetLimitRange);
            var getPct   = Slot<GetIntDelegate>(pPowerTune, AdlxVtableSlots.Power_GetLimit);
            if (getRange == null || getPct == null) return false;
            if (getRange(pPowerTune, out var pwrRange) != 0) return false;
            if (getPct(pPowerTune,   out var currentPct) != 0) return false;

            original = new AdlxPowerReads
            {
                PowerLimitPct = currentPct,
                PowerRange    = ToPublic(pwrRange),
            };

            // Valider avant d'écrire
            if (newPowerLimitPct < original.PowerRange.Min || newPowerLimitPct > original.PowerRange.Max)
                return false;

            // Écrire la limite de puissance : emplacement de vtable 5 (IADLXManualPowerTuning)
            var setPct = Slot<SetIntDelegate>(pPowerTune, AdlxVtableSlots.Power_SetLimit);
            if (setPct == null) return false;
            return setPct(pPowerTune, newPowerLimitPct) == 0;
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

    // ── EstALUsine ───────────────────────────────────────────────────────
    // IADLXGPUTuningServices::IsAtFactory(pGPU, adlx_bool*), entrée 4 de IGPUTuning.h (ADLX) : relire la remise à l'usine,
    // dont le code de retour seul ne prouve rien. null si non lu.
    public bool? EstALUsine(int gpuIndex)
    {
        if (!IsAvailable || gpuIndex < 0) return null;
        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, pTuningSvc = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return null;
            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null || getTuningSvc(_pSystem, out pTuningSvc) != 0 || pTuningSvc == IntPtr.Zero) return null;
            var lire = Slot<IsSupportedDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_IsAtFactory);
            if (lire == null || lire(pTuningSvc, pGpu, out var aLUsine) != 0) return null;
            return aLUsine != 0;
        }
        catch (Exception) { return null; }
        finally
        {
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu != IntPtr.Zero) Release(pGpu);
            if (pGpuList != IntPtr.Zero) Release(pGpuList);
        }
    }

    // ── RemettreReglageUsine ─────────────────────────────────────────────
    // IADLXGPUTuningServices::ResetToFactory(pGPU), entrée 5 de IGPUTuning.h (ADLX d9f04a9b). Le seul retour vers le défaut
    // du pilote quand une sous-tension gardée au redémarrage ne se rend plus dans la bande : tout le réglage de la carte
    // repart à l'usine, jamais une fréquence choisie par l'appelant. Rend null si fait.
    public string? RemettreReglageUsine(int gpuIndex)
    {
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize did not succeed.";
        if (gpuIndex < 0) return "Invalid GPU index.";

        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, pTuningSvc = IntPtr.Zero;
        try
        {
            if (!GetGpuAtIndex(gpuIndex, out pGpuList, out pGpu)) return "GPU not found.";
            var getTuningSvc = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTuningSvc == null || getTuningSvc(_pSystem, out pTuningSvc) != 0 || pTuningSvc == IntPtr.Zero)
                return "IADLXGPUTuningServices absent.";
            var reset = Slot<ResetDelegate>(pTuningSvc, AdlxVtableSlots.Tuning_ResetToFactory);
            if (reset == null) return "IADLXGPUTuningServices.ResetToFactory introuvable.";
            var res = reset(pTuningSvc, pGpu);
            return res == 0 ? null : $"IADLXGPUTuningServices.ResetToFactory a rendu {res}.";
        }
        catch (Exception ex) { return $"ADLX reset interrupted: {ex.GetType().Name}."; }
        finally
        {
            if (pTuningSvc != IntPtr.Zero) Release(pTuningSvc);
            if (pGpu != IntPtr.Zero) Release(pGpu);
            if (pGpuList != IntPtr.Zero) Release(pGpuList);
        }
    }

    // ── EcrireAntiLag (A2, 07/10/2026) ──────────────────────────────────
    // Anti-Lag classique (IADLX3DAntiLag.SetEnabled, entrée 5 de I3DSettings.h), sur la même carte dédiée que
    // LireReglages. Le niveau (Anti-Lag Next, IADLX3DAntiLag1) n'est jamais touché. Rend null si écrit.
    public string? EcrireAntiLag(bool actif)
    {
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize did not succeed.";

        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, p3D = IntPtr.Zero, pAntiLag = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return raison;
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return "IADLX3DSettingsServices absent.";
            var get = Slot<GetManualDelegate>(p3D, AdlxVtableSlots.Settings3D_GetAntiLag);
            if (get == null || get(p3D, pGpu, out pAntiLag) != 0 || pAntiLag == IntPtr.Zero) return "IADLX3DAntiLag absent.";
            if (Etat(pAntiLag).Supporte != true) return "Anti-Lag is not supported by this card.";
            var set = Slot<SetBoolDelegate>(pAntiLag, AdlxVtableSlots.AntiLag_SetEnabled);
            if (set == null) return "IADLX3DAntiLag.SetEnabled introuvable.";
            var res = set(pAntiLag, actif ? (byte)1 : (byte)0);
            return res == 0 ? null : $"IADLX3DAntiLag.SetEnabled a rendu {res}.";
        }
        catch (Exception ex) { return $"ADLX write interrupted: {ex.GetType().Name}."; }
        finally
        {
            Release(pAntiLag);
            Release(p3D);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // ── EcrireFrtc (A2b, 07/10/2026) ────────────────────────────────────
    // IADLX3DFrameRateTargetControl (I3DSettings.h) : 5 GetFPSRange, 7 SetEnabled, 8 SetFPS. La cible d'abord,
    // dans la plage que le pilote donne, puis l'état. Même carte dédiée que LireReglages.
    public string? EcrireFrtc(int code)
    {
        if (code == 0) return "FRTC: 0 is neither a target nor a state.";
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize did not succeed.";

        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, p3D = IntPtr.Zero, pFrtc = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return raison;
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return "IADLX3DSettingsServices absent.";
            var get = Slot<GetManualDelegate>(p3D, AdlxVtableSlots.Settings3D_GetFrameRateTargetControl);
            if (get == null || get(p3D, pGpu, out pFrtc) != 0 || pFrtc == IntPtr.Zero) return "IADLX3DFrameRateTargetControl absent.";
            if (Etat(pFrtc).Supporte != true) return "FRTC is not supported by this card.";

            var fps = Math.Abs(code);
            var plage = Slot<GetRangeDelegate>(pFrtc, AdlxVtableSlots.Frtc_GetFpsRange);
            if (plage == null || plage(pFrtc, out var r) != 0) return "FRTC : plage d'images illisible.";
            if (fps < r.minValue || fps > r.maxValue) return $"FRTC: {fps} fps outside the driver's range ({r.minValue}-{r.maxValue}).";

            var setFps = Slot<SetIntDelegate>(pFrtc, AdlxVtableSlots.Frtc_SetFps);
            var setOn = Slot<SetBoolDelegate>(pFrtc, AdlxVtableSlots.Frtc_SetEnabled);
            if (setFps == null || setOn == null) return "FRTC: write entry points not found.";
            var res = setFps(pFrtc, fps);
            if (res != 0) return $"IADLX3DFrameRateTargetControl.SetFPS a rendu {res}.";
            res = setOn(pFrtc, code > 0 ? (byte)1 : (byte)0);
            return res == 0 ? null : $"IADLX3DFrameRateTargetControl.SetEnabled a rendu {res}.";
        }
        catch (Exception ex) { return $"ADLX write interrupted: {ex.GetType().Name}."; }
        finally
        {
            Release(pFrtc);
            Release(p3D);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // La carte dédiée (IADLXGPU::Type == GPUTYPE_DISCRETE). Sur un Ryzen 7800X3D + RX 9070 XT, l'index 0 est
    // la puce intégrée (relevé du harnais, 29/06). Une seule carte au type illisible ou indéfini : c'est elle. Une seule carte DITE
    // intégrée (un Ryzen à puce graphique avec une RTX) n'est pas la carte du jeu : rien. Plusieurs sans dédiée dite : non lu,
    // plutôt que lire la mauvaise. Rend null si trouvée, sinon la raison.
    /// <summary>Pur : l'index de la carte à lire ou écrire, d'après le type lu de chaque carte (null : non lu).</summary>
    internal static int? IndexDeLaCarte(IReadOnlyList<int?> types)
    {
        for (var i = 0; i < types.Count; i++)
            if (types[i] == AdlxGpuTypeDiscrete) return i;
        return types.Count == 1 && types[0] != AdlxGpuTypeIntegrated ? 0 : null;
    }

    private string? CarteDediee(out IntPtr pGpuList, out IntPtr pGpu)
    {
        pGpuList = IntPtr.Zero;
        pGpu     = IntPtr.Zero;
        var getGpus = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUs);
        if (getGpus == null || getGpus(_pSystem, out pGpuList) != 0 || pGpuList == IntPtr.Zero)
            return "IADLXSystem.GetGPUs returned no list.";
        var sizeFn = Slot<SizeDelegate>(pGpuList, AdlxVtableSlots.GpuList_Size);
        var atFn   = Slot<AtIndexDelegate>(pGpuList, AdlxVtableSlots.GpuList_At);
        if (sizeFn == null || atFn == null) return "IADLXGPUList illisible.";
        uint n = sizeFn(pGpuList);
        if (n == 0) return "ADLX lists no AMD card.";

        var types = new List<int?>((int)n);
        for (uint i = 0; i < n; i++)
        {
            int? lu = null;
            if (atFn(pGpuList, i, out var p) == 0 && p != IntPtr.Zero)
            {
                var type = Slot<GetIntDelegate>(p, AdlxVtableSlots.Gpu_Type);
                if (type != null && type(p, out var t) == 0) lu = t;
                Release(p);
            }
            types.Add(lu);
        }
        if (IndexDeLaCarte(types) is { } index && atFn(pGpuList, (uint)index, out var carte) == 0 && carte != IntPtr.Zero)
        {
            pGpu = carte;
            return null;
        }
        return n == 1 ? "ADLX lists only an integrated AMD chip: not the game's card."
            : $"ADLX lists {n} cards and reports none as discrete.";
    }

    // SAM : IADLXGPUTuningServices → QI IADLXGPUTuningServices1 → GetSmartAccessMemory → IsSupported / IsEnabled.
    // Non pris en charge = null : « la plateforme ne peut pas » n'est pas « éteint, à allumer ».
    private bool? LireSam(IntPtr pGpu)
    {
        IntPtr pTs = IntPtr.Zero, pTs1 = IntPtr.Zero, pSam = IntPtr.Zero;
        try
        {
            var getTs = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
            if (getTs == null || getTs(_pSystem, out pTs) != 0 || pTs == IntPtr.Zero) return null;
            var qi = Slot<QueryIfDelegate>(pTs, AdlxVtableSlots.QueryInterface);
            if (qi == null || qi(pTs, "IADLXGPUTuningServices1", out pTs1) != 0 || pTs1 == IntPtr.Zero) return null;
            var getSam = Slot<GetManualDelegate>(pTs1, AdlxVtableSlots.Tuning1_GetSmartAccessMemory);
            if (getSam == null || getSam(pTs1, pGpu, out pSam) != 0 || pSam == IntPtr.Zero) return null;
            var (supporte, actif) = Etat(pSam);
            return supporte == true ? actif : null;
        }
        catch { return null; }
        finally { Release(pSam); Release(pTs1); Release(pTs); }
    }

    // Anti-Lag, Chill, FRTC : IADLX3DSettingsServices. Une limite d'images (FRTC, Chill) que la carte ne prend pas en
    // charge est lue éteinte : elle ne limite rien. Anti-Lag non pris en charge reste non lu, comme SAM : « éteint »
    // ferait proposer de l'allumer, et la carte refuserait.
    private (bool? AntiLag, bool? FrtcActif, int? FrtcFps, int? FrtcReglee, bool? ChillActif, int? ChillMin, int? ChillMax) Lire3D(IntPtr pGpu)
    {
        IntPtr p3D = IntPtr.Zero;
        try
        {
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return default;

            bool? antiLag = Fonction(p3D, AdlxVtableSlots.Settings3D_GetAntiLag, pGpu, _ => { }, nonGereeEteinte: false);
            int? frtcFps = null;
            bool? frtc = Fonction(p3D, AdlxVtableSlots.Settings3D_GetFrameRateTargetControl, pGpu,
                p => frtcFps = Entier(p, AdlxVtableSlots.Frtc_GetFps));
            int? chillMin = null, chillMax = null;
            bool? chill = Fonction(p3D, AdlxVtableSlots.Settings3D_GetChill, pGpu, p =>
            {
                chillMin = Entier(p, AdlxVtableSlots.Chill_GetMinFps);
                chillMax = Entier(p, AdlxVtableSlots.Chill_GetMaxFps);
            });
            return (antiLag, frtc, frtc == true ? frtcFps : null, frtcFps, chill, chill == true ? chillMin : null, chill == true ? chillMax : null);
        }
        catch { return default; }
        finally { Release(p3D); }
    }

    // Le mode Vsync du pilote et Enhanced Sync. Un mode non lu reste null, jamais « coupée ».
    private (int? Mode, bool? EnhancedSync) LireVsync(IntPtr pGpu)
    {
        IntPtr p3D = IntPtr.Zero;
        try
        {
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return default;
            int? mode = null;
            var vsync = Fonction(p3D, AdlxVtableSlots.Settings3D_GetWaitForVerticalRefresh, pGpu,
                p => mode = Entier(p, AdlxVtableSlots.Wfvr_GetMode));
            var enhanced = Fonction(p3D, AdlxVtableSlots.Settings3D_GetEnhancedSync, pGpu, _ => { });
            return (vsync is null ? null : mode, enhanced);
        }
        catch { return default; }
        finally { Release(p3D); }
    }

    // ── EcrireVsync (A2c, 08/10/2026) ───────────────────────────────────
    // IADLX3DWaitForVerticalRefresh.SetMode (entrée 6 de I3DSettings.h), même carte dédiée que LireReglages.
    public string? EcrireVsync(int mode)
    {
        if (mode is < 0 or > 3) return $"Vsync: unknown mode {mode} (0 to 3).";
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize did not succeed.";

        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, p3D = IntPtr.Zero, pVsync = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu);
            if (raison is not null) return raison;
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return "IADLX3DSettingsServices absent.";
            var get = Slot<GetManualDelegate>(p3D, AdlxVtableSlots.Settings3D_GetWaitForVerticalRefresh);
            if (get == null || get(p3D, pGpu, out pVsync) != 0 || pVsync == IntPtr.Zero) return "IADLX3DWaitForVerticalRefresh absent.";
            if (Etat(pVsync).Supporte != true) return "Driver Vsync is not supported by this card.";
            var set = Slot<SetIntDelegate>(pVsync, AdlxVtableSlots.Wfvr_SetMode);
            if (set == null) return "IADLX3DWaitForVerticalRefresh.SetMode introuvable.";
            var res = set(pVsync, mode);
            return res == 0 ? null : $"IADLX3DWaitForVerticalRefresh.SetMode a rendu {res}.";
        }
        catch (Exception ex) { return $"ADLX write interrupted: {ex.GetType().Name}."; }
        finally
        {
            Release(pVsync);
            Release(p3D);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // Obtient l'interface d'une fonction 3D pour la carte, lit son état et, si elle est prise en charge, ses valeurs
    // (A2b : aussi éteinte, pour rendre la cible FRTC exacte ; l'appelant ne garde que celles d'une fonction allumée).
    private static bool? Fonction(IntPtr p3D, int slot, IntPtr pGpu, Action<IntPtr> lireValeurs, bool nonGereeEteinte = true)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            var get = Slot<GetManualDelegate>(p3D, slot);
            if (get == null || get(p3D, pGpu, out p) != 0 || p == IntPtr.Zero) return null;
            var (supporte, actif) = Etat(p);
            if (supporte == false) return nonGereeEteinte ? false : null;
            if (supporte == true) lireValeurs(p);
            return actif;
        }
        catch { return null; }
        finally { Release(p); }
    }

    // IsSupported (slot 3) puis IsEnabled (slot 4), tête commune de SAM, Anti-Lag, Chill et FRTC.
    private static (bool? Supporte, bool? Actif) Etat(IntPtr p)
    {
        var sup = Slot<GetBoolDelegate>(p, AdlxVtableSlots.Feature_IsSupported);
        if (sup == null || sup(p, out var s) != 0) return (null, null);
        if (s == 0) return (false, null);
        var en = Slot<GetBoolDelegate>(p, AdlxVtableSlots.Feature_IsEnabled);
        if (en == null || en(p, out var e) != 0) return (true, null);
        return (true, e != 0);
    }

    private static int? Entier(IntPtr p, int slot)
    {
        var get = Slot<GetIntDelegate>(p, slot);
        return get != null && get(p, out var v) == 0 ? v : null;
    }

    // Boost et Image Sharpening de la carte, RSR et AFMF du système : lus seulement, aucune entrée Set* n'est appelée.
    // Non pris en charge = null (Fonction et Etat), jamais « éteint ».
    private (bool? Boost, bool? Ris, bool? Rsr, bool? Afmf) LireRadeon(IntPtr pGpu)
    {
        IntPtr p3D = IntPtr.Zero;
        try
        {
            var get3D = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_Get3DSettingsServices);
            if (get3D == null || get3D(_pSystem, out p3D) != 0 || p3D == IntPtr.Zero) return default;
            var boost = Fonction(p3D, AdlxVtableSlots.Settings3D_GetBoost, pGpu, _ => { }, nonGereeEteinte: false);
            var ris = Fonction(p3D, AdlxVtableSlots.Settings3D_GetImageSharpening, pGpu, _ => { }, nonGereeEteinte: false);
            var rsr = Globale(p3D, AdlxVtableSlots.Settings3D_GetRadeonSuperResolution);
            bool? afmf = null;
            IntPtr p3D1 = IntPtr.Zero;
            try
            {
                var qi = Slot<QueryIfDelegate>(p3D, AdlxVtableSlots.QueryInterface);
                if (qi != null && qi(p3D, "IADLX3DSettingsServices1", out p3D1) == 0 && p3D1 != IntPtr.Zero)
                    afmf = Globale(p3D1, AdlxVtableSlots.Settings3D1_GetAmdFluidMotionFrames);
            }
            finally { Release(p3D1); }
            return (boost, ris, rsr, afmf);
        }
        catch { return default; }
        finally { Release(p3D); }
    }

    // Une fonction 3D du système (sans carte) : son état si elle est prise en charge, sinon null.
    private static bool? Globale(IntPtr pServices, int slot)
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            var get = Slot<GetOutPtrDelegate>(pServices, slot);
            if (get == null || get(pServices, out p) != 0 || p == IntPtr.Zero) return null;
            var (supporte, actif) = Etat(p);
            return supporte == true ? actif : null;
        }
        catch { return null; }
        finally { Release(p); }
    }

    // (pThis, out adlx_bool) : IsSupported / IsEnabled
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBoolDelegate(IntPtr pThis, out sbyte val);

    // (pThis, pGpu) : ResetToFactory
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResetDelegate(IntPtr pThis, IntPtr pGpu);

    // (pThis, val) : sert aux écritures SetGPUMaxFrequency (emplacement 8),
    // SetGPUVoltage (emplacement 11), SetPowerLimit (emplacement 5)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetIntDelegate(IntPtr pThis, int val);

    // (pThis, adlx_bool) : IADLX3DAntiLag.SetEnabled ; adlx_bool est un octet (ADLXDefines.h).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetBoolDelegate(IntPtr pThis, byte val);
}
