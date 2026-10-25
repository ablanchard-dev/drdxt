using System.Runtime.InteropServices;

namespace DrDxt.Detection.Gpu.Adlx;

// Le ventilateur de la carte dédiée en écriture (IGPUManualFanTuning.h). La séquence est celle de l'exemple officiel
// ManualFanTuning (Samples/CPP/GPUTuning/ManualFanTuning) : partir des états vides, y poser vitesse et température,
// IsValidFanTuningStates, puis SetFanTuningStates. Une vitesse fixe garde les températures de la courbe en place et met la
// même vitesse à chaque état ; le Zero RPM, qui arrêterait les ventilateurs au repos, est coupé. Le retour remet les états
// et le Zero RPM par défaut de la carte (IADLXManualFanTuning1). Chaque écriture est relue ; un écart se dit. ADLX_RESET_NEEDED
// (« one click auto tuning » actif) n'enchaîne aucune remise à l'usine : elle remettrait aussi fréquence et puissance.
public sealed partial class AdlxNative
{
    /// <summary>Pose la même vitesse (en %) à chaque état de la courbe de la carte dédiée et coupe le Zero RPM. Rend
    /// <c>null</c> si posé et relu, sinon la raison ; ne lève jamais.</summary>
    public string? PoserVentilateurFixe(int pourcent)
    {
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize did not succeed.";
        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, pTs = IntPtr.Zero, pFan = IntPtr.Zero, pActuels = IntPtr.Zero,
            pVides = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu) ?? VentilateurManuel(pGpu, out pTs, out pFan);
            if (raison is not null) return raison;

            var plages = Slot<GetTwoRangesDelegate>(pFan, AdlxVtableSlots.Fan_GetTuningRanges);
            if (plages == null || plages(pFan, out var vitesses, out _) != 0) return "GetFanTuningRanges failed.";
            if (pourcent < vitesses.minValue || pourcent > vitesses.maxValue)
                return $"Fan speed {pourcent} % is outside the card's range [{vitesses.minValue}, {vitesses.maxValue}] %.";

            var lire = Slot<GetOutPtrDelegate>(pFan, AdlxVtableSlots.Fan_GetTuningStates);
            if (lire == null || lire(pFan, out pActuels) != 0 || pActuels == IntPtr.Zero) return "GetFanTuningStates failed.";
            var actuels = CollectFanPoints(pActuels);
            var vides = Slot<GetOutPtrDelegate>(pFan, AdlxVtableSlots.Fan_GetEmptyTuningStates);
            if (vides == null || vides(pFan, out pVides) != 0 || pVides == IntPtr.Zero) return "GetEmptyFanTuningStates failed.";
            if (Etats(pVides) is not { } etats || etats.Count != actuels.Count || etats.Count == 0)
            {
                if (Etats(pVides) is { } e) LibererTous(e);
                return "The empty fan states do not match the current curve.";
            }
            try
            {
                for (var i = 0; i < etats.Count; i++)
                {
                    var vitesse = Slot<SetIntDelegate>(etats[i], AdlxVtableSlots.FanState_SetFanSpeed);
                    var temperature = Slot<SetIntDelegate>(etats[i], AdlxVtableSlots.FanState_SetTemperature);
                    if (vitesse == null || temperature == null || vitesse(etats[i], pourcent) != 0
                        || temperature(etats[i], actuels[i].TempC) != 0)
                        return $"Fan state {i} refused its speed or temperature.";
                }
            }
            finally { LibererTous(etats); }

            if (PoserEtats(pFan, pVides) is { } refus) return refus;
            if (PoserZeroRpm(pFan, actif: false, seulementSiActif: true) is { } refusZero) return refusZero;

            Release(pActuels);
            pActuels = IntPtr.Zero;
            if (lire(pFan, out pActuels) != 0 || pActuels == IntPtr.Zero) return "The fan states were not read back.";
            return CollectFanPoints(pActuels) is { Count: > 0 } relus && relus.All(p => p.SpeedPct == pourcent)
                ? null : "The fan speed read back is not the one written.";
        }
        catch (Exception ex) { return $"ADLX fan write interrupted: {ex.GetType().Name}."; }
        finally
        {
            Release(pVides);
            Release(pActuels);
            Release(pFan);
            Release(pTs);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    /// <summary>Remet la courbe et le Zero RPM par défaut de la carte dédiée (IADLXManualFanTuning1). Rend <c>null</c> si
    /// remis et relu, sinon la raison ; ne lève jamais.</summary>
    public string? RendreVentilateurAuto()
    {
        if (!IsAvailable) return _initDetail ?? "ADLXInitialize did not succeed.";
        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero, pTs = IntPtr.Zero, pFan = IntPtr.Zero, pFan1 = IntPtr.Zero,
            pDefaut = IntPtr.Zero, pRelus = IntPtr.Zero;
        try
        {
            var raison = CarteDediee(out pGpuList, out pGpu) ?? VentilateurManuel(pGpu, out pTs, out pFan);
            if (raison is not null) return raison;
            var qi = Slot<QueryIfDelegate>(pFan, AdlxVtableSlots.QueryInterface);
            if (qi == null || qi(pFan, "IADLXManualFanTuning1", out pFan1) != 0 || pFan1 == IntPtr.Zero)
                return "IADLXManualFanTuning1 absent: the card's default curve is not readable.";
            var defaut = Slot<GetOutPtrDelegate>(pFan1, AdlxVtableSlots.Fan1_GetDefaultTuningStates);
            if (defaut == null || defaut(pFan1, out pDefaut) != 0 || pDefaut == IntPtr.Zero) return "GetDefaultFanTuningStates failed.";
            var attendus = CollectFanPoints(pDefaut);
            if (attendus.Count == 0) return "The card's default curve is empty.";
            if (PoserEtats(pFan, pDefaut) is { } refus) return refus;

            var zeroDefaut = Slot<GetBoolDelegate>(pFan1, AdlxVtableSlots.Fan1_GetDefaultZeroRpmState);
            if (zeroDefaut != null && zeroDefaut(pFan1, out var z) == 0 && PoserZeroRpm(pFan, z != 0, seulementSiActif: false) is { } refusZero)
                return refusZero;

            var lire = Slot<GetOutPtrDelegate>(pFan, AdlxVtableSlots.Fan_GetTuningStates);
            if (lire == null || lire(pFan, out pRelus) != 0 || pRelus == IntPtr.Zero) return "The fan states were not read back.";
            return CollectFanPoints(pRelus).SequenceEqual(attendus) ? null : "The fan curve read back is not the card's default.";
        }
        catch (Exception ex) { return $"ADLX fan restore interrupted: {ex.GetType().Name}."; }
        finally
        {
            Release(pRelus);
            Release(pDefaut);
            Release(pFan1);
            Release(pFan);
            Release(pTs);
            Release(pGpu);
            Release(pGpuList);
        }
    }

    /// <summary>La température de la carte dédiée (IADLXGPUMetrics), en °C ; null si elle ne se lit pas.</summary>
    public double? LireTemperatureCarte()
    {
        if (!IsAvailable) return null;
        IntPtr pGpuList = IntPtr.Zero, pGpu = IntPtr.Zero;
        try { return CarteDediee(out pGpuList, out pGpu) is null ? LireTemperature(pGpu) : null; }
        catch { return null; }
        finally
        {
            Release(pGpu);
            Release(pGpuList);
        }
    }

    // GetGPUTuningServices → IsSupportedManualFanTuning → GetManualFanTuning, sur la carte donnée.
    private string? VentilateurManuel(IntPtr pGpu, out IntPtr pTs, out IntPtr pFan)
    {
        pTs = IntPtr.Zero;
        pFan = IntPtr.Zero;
        var getTs = Slot<GetOutPtrDelegate>(_pSystem, AdlxVtableSlots.System_GetGPUTuningServices);
        if (getTs == null || getTs(_pSystem, out pTs) != 0 || pTs == IntPtr.Zero) return "IADLXGPUTuningServices absent.";
        var supporte = Slot<IsSupportedDelegate>(pTs, AdlxVtableSlots.Tuning_IsSupportedManualFan);
        if (supporte == null || supporte(pTs, pGpu, out var ok) != 0 || ok == 0) return "Manual fan tuning is not supported by this card.";
        var get = Slot<GetManualDelegate>(pTs, AdlxVtableSlots.Tuning_GetManualFan);
        return get == null || get(pTs, pGpu, out pFan) != 0 || pFan == IntPtr.Zero ? "IADLXManualFanTuning absent." : null;
    }

    // IsValidFanTuningStates (errorIndex -1 = valide) puis SetFanTuningStates.
    private static string? PoserEtats(IntPtr pFan, IntPtr pEtats)
    {
        var valider = Slot<IsValidStatesDelegate>(pFan, AdlxVtableSlots.Fan_IsValidTuningStates);
        var poser = Slot<SetPtrDelegate>(pFan, AdlxVtableSlots.Fan_SetTuningStates);
        if (valider == null || poser == null) return "IADLXManualFanTuning write entries absent.";
        if (valider(pFan, pEtats, out var erreur) != 0 || erreur != -1) return $"The fan states are not valid (state {erreur}).";
        var res = poser(pFan, pEtats);
        return res == 0 ? null : $"SetFanTuningStates returned {res}.";
    }

    // Le Zero RPM, seulement s'il est pris en charge ; seulementSiActif : n'écrire que pour couper un Zero RPM allumé.
    private static string? PoserZeroRpm(IntPtr pFan, bool actif, bool seulementSiActif)
    {
        var supporte = Slot<GetBoolDelegate>(pFan, AdlxVtableSlots.Fan_IsSupportedZeroRpm);
        if (supporte == null || supporte(pFan, out var s) != 0 || s == 0) return null;
        if (seulementSiActif)
        {
            var lire = Slot<GetBoolDelegate>(pFan, AdlxVtableSlots.Fan_GetZeroRpmState);
            if (lire == null || lire(pFan, out var on) != 0) return "GetZeroRPMState failed.";
            if (on == 0) return null;
        }
        var poser = Slot<SetBoolDelegate>(pFan, AdlxVtableSlots.Fan_SetZeroRpmState);
        if (poser == null) return "SetZeroRPMState absent.";
        var res = poser(pFan, (byte)(actif ? 1 : 0));
        return res == 0 ? null : $"SetZeroRPMState returned {res}.";
    }

    // Les états d'une liste, chacun à libérer ; null si la liste ne se lit pas.
    private static List<IntPtr>? Etats(IntPtr pListe)
    {
        var taille = Slot<SizeDelegate>(pListe, AdlxVtableSlots.FanStateList_Size);
        var a = Slot<AtIndexDelegate>(pListe, AdlxVtableSlots.FanStateList_At);
        if (taille == null || a == null) return null;
        var etats = new List<IntPtr>();
        for (uint i = 0; i < taille(pListe); i++)
        {
            if (a(pListe, i, out var p) != 0 || p == IntPtr.Zero)
            {
                LibererTous(etats);
                return null;
            }
            etats.Add(p);
        }
        return etats;
    }

    private static void LibererTous(List<IntPtr> objets)
    {
        foreach (var p in objets) Release(p);
        objets.Clear();
    }

    // (pThis, pStates, out errorIndex) : IsValidFanTuningStates
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsValidStatesDelegate(IntPtr pThis, IntPtr pStates, out int errorIndex);

    // (pThis, pStates) : SetFanTuningStates
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPtrDelegate(IntPtr pThis, IntPtr pStates);
}
