using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.GpuNative.NvApi;

// -----------------------------------------------------------------------
// NvApiNative : l'unique vraie frontière P/Invoke vers nvapi64.dll.
//
// Architecture
// ────────────
// nvapi64.dll n'exporte que nvapi_QueryInterface(id) → pointeur de fonction ; tout le reste
// s'atteint par QueryInterface(id) → GetDelegateForFunctionPointer. NvApiEntry possède ce
// mécanisme d'entrée (+ NvAPI_Initialize, EnumPhysicalGPUs, la règle d'or MakeVersion) ;
// NvApiNative le compose et porte les ids de fonction rétro-ingéniés et la recette d'écriture
// GET puis SET. C'est le miroir NVIDIA d'AdlxNative : une seule frontière avec les lectures (non
// élevées) et les écritures (chemin élevé), pour que les ids de fonction et la recette
// d'écriture, coûteux à valider en réel sur une vraie RTX 4060, aient exactement une copie,
// jamais un doublon divergent.
//
// Surfaces
// ────────
//   Lectures  : TryReadCoreOffsetMhz (GetPstates20), non élevée.
//               TryReadPowerLimitPct (ClientPowerPoliciesGetStatus).
//   Écritures : TrySetCoreClockOffset (Get→SetPstates20, écriture minimale, conditionnée au bit modifiable).
//               TrySetPowerLimitPercent (GetInfo→bornage→GetStatus→SetStatus).
//               TrySetDrsGlobalSetting (la séquence de session DRS publique et sûre).
//
// Règles d'or de sécurité (recette) :
//   #1 mot de version = Marshal.SizeOf(struct) | (1 << 16) à l'exécution (NvApiEntry.MakeVersion) :
//      une taille incorrecte devient l'IncompatibleStructVersion sûr du pilote, pas une écriture.
//   #2 GET puis SET : lire toute la struct actuelle, ne modifier sur place que le seul champ visé,
//      la réécrire ; jamais une struct d'écriture construite à zéro (qui enverrait du déchet).
//   #3 statut 0 = Ok ; tout non nul → false, jamais de suite.
//
// Contrat d'échec propre (identique à AdlxNative) : sur une machine AMD / sans NVIDIA, nvapi64.dll
// est absente → IsAvailable vaut false et chaque Try* rend false ; un id de fonction privée
// manquant, une taille de struct incorrecte ou toute panne d'interop rendent false. Ne lève jamais
// vers l'appelant : un catch-all garde chaque chemin.
//
// La règle prudente de bornes des valeurs (plages de décalage / de puissance, reprises de
// GpuSafeBounds) n'est pas imposée ici : elle vit dans le NvApiTuningMath du helper et est vérifiée
// par les orchestrateurs d'écriture (NvApiTuningActions) avant d'atteindre cette frontière,
// exactement comme AdlxTuningMath est placé devant AdlxNative. Cette frontière n'est que recette +
// bornage du pilote.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class NvApiNative : INvApiNative
{
    // --- ids de fonction de réglage (recette). GET = documentés ; écritures = privées (classe Afterburner). ---
    private const uint Id_GetPstates20 = 0x6FF81213;                 // documented
    private const uint Id_SetPstates20 = 0x0F4DAE6B;                 // privé
    private const uint Id_ClientPowerPoliciesGetInfo = 0x34206D86;   // privé
    private const uint Id_ClientPowerPoliciesGetStatus = 0x70916171; // privé
    private const uint Id_ClientPowerPoliciesSetStatus = 0xAD95F5ED; // privé

    // --- ids de fonction DRS (table de la recette, tous dans l'en-tête public de NVIDIA). ---
    private const uint Id_DRS_CreateSession = 0x0694D52E;
    private const uint Id_DRS_LoadSettings = 0x375DBD6B;
    private const uint Id_DRS_GetBaseProfile = 0xDA8466A0;
    // Offset verifie contre NvAPIWrapper/FunctionId.cs et nvidiaProfileInspector.
    private const uint Id_DRS_FindApplicationByName = 0xEEE566B2;
    private const uint Id_DRS_GetSetting = 0x73BF8338;    // NvAPI_DRS_GetSetting : vérifié contre NvAPIWrapper FunctionId.cs + jNizM NvAPI_IDs.txt
    private const uint Id_DRS_SetSetting = 0x577DD202;
    private const uint Id_DRS_SaveSettings = 0xFCBC7E14;
    private const uint Id_DRS_DestroySession = 0xDAD9CFF8;

    // VRR, lecture : identifiants verifies contre deux sources, jNizM NvAPI_IDs.txt et le fichier
    // officiel NVIDIA nvapi_interface.h : { "NvAPI_DISP_GetDisplayIdByDisplayName", 0xae457190 },
    // { "NvAPI_Disp_GetVRRInfo", 0xdf8fda57 }. (NvAPIWrapper n'a pas GetVRRInfo.)
    private const uint Id_DISP_GetDisplayIdByDisplayName = 0xAE457190;
    private const uint Id_Disp_GetVRRInfo = 0xDF8FDA57;

    private const uint GraphicsClockDomain = 0;  // PublicClockDomain GRAPHICS
    // PublicClockDomain MEMORY. NV_GPU_PUBLIC_CLOCK_ID (NvAPIWrapper PublicClockId) : le voisin
    // documenté de GRAPHICS=0 dans le même enum : GRAPHICS=0, MEMORY=4, PROCESSOR=7, VIDEO=8. Épinglé
    // par le test de gel mémoire de NvApiFinderTests pour attraper toute dérive.
    private const uint MemoryClockDomain = 4;
    private const uint PstateP0 = 0;             // PerformanceStateId P0

    // Tailles des tampons de chaîne + de valeur de NVDRS_SETTING (NvApiDriverSettings.h).
    private const int NvapiUnicodeStringMax = 2048;   // settingName: wchar[2048].
    private const int NvapiSettingMaxValues = 4100;   // blobs de valeur prédéfinie / actuelle.
    private const uint NvdrsSettingV1Version = 1;
    private const uint NvdrsApplicationV4Version = 4;
    private const uint NvdrsSettingDwordType = 0;     // NVDRS_DWORD_TYPE.

    private readonly NvApiEntry _entry;

    public NvApiNative() : this(new NvApiEntry()) { }

    internal NvApiNative(NvApiEntry entry) => _entry = entry;

    public bool IsAvailable => _entry.IsAvailable;

    private IntPtr ResolveHandle(int gpuIndex)
    {
        var gpus = _entry.EnumPhysicalGpus();
        return gpuIndex < gpus.Count ? gpus[gpuIndex] : IntPtr.Zero;
    }

    // ── Lectures ─────────────────────────────────────────────────────────────

    public bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz)
    {
        offsetMhz = 0;
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var get = _entry.GetDelegate<GetPstates20Delegate>(Id_GetPstates20);
            if (get is null) return false;

            var info = NewPstates20Info();
            if (get(handle, ref info) != (int)NvApiStatus.Ok) return false;

            if (!TryFindP0GraphicsClockIndex(info, out var p, out var c)) return false;

            offsetMhz = NvApiConvert.KhzToMhz(info.Pstates[p].Clocks[c].FreqDeltaKhz.Value);
            return true;
        }
        catch { return false; }
    }

    public bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz)
    {
        // Même GET documenté que la lecture du cœur (une lecture sûre, pas une écriture privée), qui
        // trouve le delta du domaine d'horloge P0/MEMORY au lieu de GRAPHICS. L'application de
        // l'undervolt lit les deux décalages pour capturer un retour fidèle : c'est donc réel ; un placebo
        // à échec propre ici empêcherait l'undervolt NVIDIA de s'appliquer.
        offsetMhz = 0;
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var get = _entry.GetDelegate<GetPstates20Delegate>(Id_GetPstates20);
            if (get is null) return false;

            var info = NewPstates20Info();
            if (get(handle, ref info) != (int)NvApiStatus.Ok) return false;

            if (!TryFindP0MemoryClockIndex(info, out var p, out var c)) return false;

            offsetMhz = NvApiConvert.KhzToMhz(info.Pstates[p].Clocks[c].FreqDeltaKhz.Value);
            return true;
        }
        catch { return false; }
    }

    public bool TryReadPowerLimitPct(int gpuIndex, out int powerLimitPct)
    {
        powerLimitPct = 0;
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var getStatus = _entry.GetDelegate<PowerStatusDelegate>(Id_ClientPowerPoliciesGetStatus);
            if (getStatus is null) return false;

            var status = NewPowerStatus();
            if (getStatus(handle, ref status) != (int)NvApiStatus.Ok) return false;
            if (status.Count == 0 || status.Entries is null || status.Entries.Length == 0) return false;

            // Note : le % est entier ici ; une cible d'origine fractionnaire (par exemple 87,5 % = 87_500 PCM)
            // se capture à 88 → le retour est à ±0,5 % (négligeable). La lecture du décalage cœur est exacte.
            powerLimitPct = NvApiConvert.PcmToPercent(status.Entries[0].PowerTargetPcm);
            return true;
        }
        catch { return false; }
    }

    /// <summary>`NV_GET_VRR_INFO_V1` : version, mot de bits, reservedEx[4] = 24 octets. La version se
    /// calcule sur la taille reelle (regle d'or #1) : une divergence donne l'erreur sure du pilote.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NvGetVrrInfoV1
    {
        public uint Version;
        public uint Bits;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] ReservedEx;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDisplayIdByDisplayNameDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string displayName, out uint displayId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVrrInfoDelegate(uint displayId, ref NvGetVrrInfoV1 info);

    public bool TryReadVrr(string displayName, out VrrReading reading)
    {
        reading = default;
        if (!IsAvailable || string.IsNullOrWhiteSpace(displayName)) return false;
        try
        {
            var parNom = _entry.GetDelegate<GetDisplayIdByDisplayNameDelegate>(Id_DISP_GetDisplayIdByDisplayName);
            var lireVrr = _entry.GetDelegate<GetVrrInfoDelegate>(Id_Disp_GetVRRInfo);
            if (parNom is null || lireVrr is null) return false;

            // « The display must be active to retrieve the displayId » (doc NVIDIA) : un ecran
            // pilote par une autre carte ou inactif rend une erreur, donc false : non lu.
            if (parNom(displayName, out var displayId) != (int)NvApiStatus.Ok) return false;

            var info = new NvGetVrrInfoV1
            {
                Version = NvApiEntry.MakeVersion<NvGetVrrInfoV1>(1),
                ReservedEx = new uint[4],
            };
            if (lireVrr(displayId, ref info) != (int)NvApiStatus.Ok) return false;

            reading = VrrReading.Decoder(info.Bits);
            return true;
        }
        catch { return false; }
    }

    // ── Écritures ────────────────────────────────────────────────────────────

    public bool TrySetCoreClockOffset(int gpuIndex, int offsetMhz)
    {
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var get = _entry.GetDelegate<GetPstates20Delegate>(Id_GetPstates20);
            var set = _entry.GetDelegate<SetPstates20Delegate>(Id_SetPstates20);
            if (get is null || set is null) return false;

            // GET puis SET (règle d'or n°2) : lire toute la struct actuelle, ne modifier sur place que le delta
            // de fréquence P0/Graphics, réécrire la même struct. Jamais d'écriture construite à zéro (elle
            // enverrait des ClockType / drapeaux / plages déchet). Une taille de struct incorrecte est rejetée
            // ici (IncompatibleStructVersion sûr) avant toute écriture.
            var current = NewPstates20Info();
            if (get(handle, ref current) != (int)NvApiStatus.Ok) return false;

            if (!TryFindP0GraphicsClockIndex(current, out var p, out var c)) return false;

            // Le bit modifiable de l'entrée d'horloge est la barrière qui fait foi pour chaque horloge
            // (NvAPIWrapper IPerformanceStates20ClockEntry.IsEditable = bit0 de _Flags). Les drapeaux
            // modifiables au niveau info / pstate ne conditionnent exprès rien ici : s'en servir risquerait un
            // faux négatif qui refuserait une écriture valide, et le bit de la feuille suffit.
            if ((current.Pstates[p].Clocks[c].Flags & 1u) == 0u) return false;

            // Écriture minimale, la convention SetPstates20 (ce qu'envoie MSI Afterburner) : soumettre
            // seulement le delta d'horloge P0/Graphics, pas toute la struct lue. Renvoyer toute la struct (avec
            // ses entrées de tension de base) est rejeté par le pilote avec NotSupported (-104) : les
            // modifications de tension sont restreintes (prouvé en réel sur une RTX 4060). On copie le vrai
            // ClockType + la plage de delta de l'entrée lue pour qu'aucun champ ne soit du déchet, on ne
            // change que la valeur du delta de fréquence, et on ne touche aucune tension (NumBaseVoltages = 0).
            var write = NewPstates20Info();
            write.NumPstates = 1;
            write.NumClocks = 1;
            write.NumBaseVoltages = 0;
            write.Pstates[0].StateId = PstateP0;
            write.Pstates[0].Clocks[0].DomainId = GraphicsClockDomain;
            write.Pstates[0].Clocks[0].ClockType = current.Pstates[p].Clocks[c].ClockType;
            write.Pstates[0].Clocks[0].FreqDeltaKhz = current.Pstates[p].Clocks[c].FreqDeltaKhz;
            write.Pstates[0].Clocks[0].FreqDeltaKhz.Value = NvApiConvert.MhzToKhz(offsetMhz);

            return set(handle, ref write) == (int)NvApiStatus.Ok;
        }
        catch { return false; }
    }

    public bool TrySetMemoryClockOffset(int gpuIndex, int offsetMhz)
    {
        // Miroir exact de TrySetCoreClockOffset pour le domaine P0/MEMORY (4) : même recette GET puis SET,
        // même convention d'écriture minimale à une entrée (ce qu'envoie Afterburner pour un décalage
        // mémoire), même barrière du bit modifiable. La lecture mémoire sur cette struct est déjà prouvée
        // en réel (la capture de l'undervolt s'en sert) ; seul le domaine de l'écriture diffère du chemin
        // cœur validé en réel.
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var get = _entry.GetDelegate<GetPstates20Delegate>(Id_GetPstates20);
            var set = _entry.GetDelegate<SetPstates20Delegate>(Id_SetPstates20);
            if (get is null || set is null) return false;

            var current = NewPstates20Info();
            if (get(handle, ref current) != (int)NvApiStatus.Ok) return false;

            if (!TryFindP0MemoryClockIndex(current, out var p, out var c)) return false;

            // Le bit modifiable de l'entrée d'horloge est la barrière qui fait foi (comme pour le cœur).
            if ((current.Pstates[p].Clocks[c].Flags & 1u) == 0u) return false;

            var write = NewPstates20Info();
            write.NumPstates = 1;
            write.NumClocks = 1;
            write.NumBaseVoltages = 0;
            write.Pstates[0].StateId = PstateP0;
            write.Pstates[0].Clocks[0].DomainId = MemoryClockDomain;
            write.Pstates[0].Clocks[0].ClockType = current.Pstates[p].Clocks[c].ClockType;
            write.Pstates[0].Clocks[0].FreqDeltaKhz = current.Pstates[p].Clocks[c].FreqDeltaKhz;
            write.Pstates[0].Clocks[0].FreqDeltaKhz.Value = NvApiConvert.MhzToKhz(offsetMhz);

            return set(handle, ref write) == (int)NvApiStatus.Ok;
        }
        catch { return false; }
    }

    public bool TrySetPowerLimitPercent(int gpuIndex, float pct)
    {
        if (!IsAvailable || gpuIndex < 0) return false;
        try
        {
            var handle = ResolveHandle(gpuIndex);
            if (handle == IntPtr.Zero) return false;

            var getInfo = _entry.GetDelegate<PowerInfoDelegate>(Id_ClientPowerPoliciesGetInfo);
            var getStatus = _entry.GetDelegate<PowerStatusDelegate>(Id_ClientPowerPoliciesGetStatus);
            var setStatus = _entry.GetDelegate<PowerStatusDelegate>(Id_ClientPowerPoliciesSetStatus);
            if (getInfo is null || getStatus is null || setStatus is null) return false;

            // GET info → les bornes PCM [min, max] valides du pilote pour ce GPU.
            var info = NewPowerInfo();
            if (getInfo(handle, ref info) != (int)NvApiStatus.Ok) return false;
            if (info.Count == 0 || info.Entries is null || info.Entries.Length == 0) return false;

            var min = info.Entries[0].MinPowerPcm;
            var max = info.Entries[0].MaxPowerPcm;
            if (max < min || max == 0) return false;

            var clamped = NvApiConvert.ClampPcm(NvApiConvert.PercentToPcm(pct), min, max);

            // GET status → ne modifier sur place que la cible de puissance d'entry[0] → SET (règle d'or n°2).
            var status = NewPowerStatus();
            if (getStatus(handle, ref status) != (int)NvApiStatus.Ok) return false;
            if (status.Count == 0 || status.Entries is null || status.Entries.Length == 0) return false;

            status.Entries[0].PowerTargetPcm = clamped;
            return setStatus(handle, ref status) == (int)NvApiStatus.Ok;
        }
        catch { return false; }
    }

    // ── DRS (écriture publique et sûre d'un réglage du pilote) ──────────────────────────────

    public bool TrySetDrsGlobalSetting(uint settingId, uint value)
    {
        if (!IsAvailable) return false;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var getBaseProfile = _entry.GetDelegate<GetBaseProfileDelegate>(Id_DRS_GetBaseProfile);
            var setSetting = _entry.GetDelegate<SetSettingDelegate>(Id_DRS_SetSetting);
            var saveSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_SaveSettings);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || getBaseProfile is null
                || setSetting is null || saveSettings is null || destroySession is null)
            {
                return false;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return false;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return false;

                if (getBaseProfile(session, out var profile) != (int)NvApiStatus.Ok || profile == IntPtr.Zero)
                    return false;

                // Construire la struct du réglage. Préallouer chaque ByValArray (règle d'or n°2)
                // et calculer le mot de version à l'exécution (règle d'or n°1).
                var setting = new NVDRS_SETTING_V1
                {
                    version = NvApiEntry.MakeVersion<NVDRS_SETTING_V1>(NvdrsSettingV1Version),
                    settingName = string.Empty,
                    settingId = settingId,
                    settingType = NvdrsSettingDwordType,
                    settingLocation = 0,
                    isCurrentPredefined = 0,
                    isPredefinedValid = 0,
                    predefinedValue = new byte[NvapiSettingMaxValues],
                    currentValue = new byte[NvapiSettingMaxValues],
                };
                // Le DWORD vit dans les 4 premiers octets de currentValue (recette §C).
                BitConverter.TryWriteBytes(setting.currentValue, value);

                if (setSetting(session, profile, ref setting) != (int)NvApiStatus.Ok) return false;
                if (saveSettings(session) != (int)NvApiStatus.Ok) return false;

                return true;
            }
            finally
            {
                // Toujours détruire la session, même sur un échec en pleine séquence.
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return false; }
    }

    // ── DRS (ecriture par application, L2) ─────────────────────────────────
    //
    // Meme sequence que l'ecriture globale, a un appel pres : FindApplicationByName remplace
    // GetBaseProfile. Cet appel-la change tout : la hierarchie du driver est
    // Application > Global courant > Base (PG-12072-001 p.2), donc une ecriture globale peut ne
    // jamais atteindre un jeu qui possede son propre profil, et le produit annoncerait un
    // reglage applique que le jeu ne verrait pas.
    public bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(applicationPath)) return false;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var findApp = _entry.GetDelegate<FindApplicationByNameDelegate>(Id_DRS_FindApplicationByName);
            var setSetting = _entry.GetDelegate<SetSettingDelegate>(Id_DRS_SetSetting);
            var saveSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_SaveSettings);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || findApp is null
                || setSetting is null || saveSettings is null || destroySession is null)
            {
                return false;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return false;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return false;

                // Pre-allouer chaque ByValTStr + mot de version calcule au runtime (regles d'or #1 et #2).
                var app = new NVDRS_APPLICATION_V4
                {
                    version = NvApiEntry.MakeVersion<NVDRS_APPLICATION_V4>(NvdrsApplicationV4Version),
                    isPredefined = 0,
                    appName = string.Empty,
                    userFriendlyName = string.Empty,
                    launcher = string.Empty,
                    fileInFolder = string.Empty,
                    bits = 0,
                    commandLine = string.Empty,
                };

                // NVAPI_EXECUTABLE_NOT_FOUND quand l'exe n'a pas de profil a lui. On rend false
                // et on n'en cree pas : fabriquer un profil ecrirait une entree permanente dans
                // la base du driver pour un jeu peut-etre jamais lance, et obligerait le revert a
                // savoir supprimer un profil au lieu de restaurer une valeur. Il n'y a pas non
                // plus de repli silencieux sur le profil de base : ce serait ecrire ailleurs que
                // la ou l'appelant a demande.
                if (findApp(session, applicationPath, out var profile, ref app) != (int)NvApiStatus.Ok
                    || profile == IntPtr.Zero)
                {
                    return false;
                }

                var setting = new NVDRS_SETTING_V1
                {
                    version = NvApiEntry.MakeVersion<NVDRS_SETTING_V1>(NvdrsSettingV1Version),
                    settingName = string.Empty,
                    settingId = settingId,
                    settingType = NvdrsSettingDwordType,
                    settingLocation = 0,
                    isCurrentPredefined = 0,
                    isPredefinedValid = 0,
                    predefinedValue = new byte[NvapiSettingMaxValues],
                    currentValue = new byte[NvapiSettingMaxValues],
                };
                // Le DWORD vit dans les 4 premiers octets de currentValue (recette §C).
                BitConverter.TryWriteBytes(setting.currentValue, value);

                if (setSetting(session, profile, ref setting) != (int)NvApiStatus.Ok) return false;
                // NVIDIA documente que SaveSettings ecrase ce qu'un autre processus a modifie
                // entre-temps, sans notification (PG-12072-001 p.11). D'ou une session qui ne
                // contient rien d'autre que cette ecriture.
                if (saveSettings(session) != (int)NvApiStatus.Ok) return false;

                return true;
            }
            finally
            {
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return false; }
    }

    // ── DRS (suppression par application) ────
    //
    // L'inverse d'une ecriture au profil d'un jeu quand ce profil ne possedait pas le reglage.
    // `NvAPI_DRS_DeleteProfileSetting(hSession, hProfile, settingId)` : « deletes a setting or sets
    // it back to predefined value » (docs.nvidia.com/nvapi, groupe drsapi). Identifiant verifie le
    // 10/09/2026 dans NvAPIWrapper `FunctionId.cs` et jNizM `NvAPI_IDs.txt` (temoins GetSetting
    // 0x73BF8338 / SetSetting 0x577DD202, egaux aux constantes ci-dessus).
    // Jamais NvAPI_DRS_RestoreProfileDefault : il touche le profil entier et peut le supprimer.
    private const uint Id_DRS_DeleteProfileSetting = 0xE4A26362;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeleteProfileSettingDelegate(IntPtr session, IntPtr profile, uint settingId);

    public bool TryDeleteDrsSettingForApplication(string applicationPath, uint settingId)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(applicationPath)) return false;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var findApp = _entry.GetDelegate<FindApplicationByNameDelegate>(Id_DRS_FindApplicationByName);
            var deleteSetting = _entry.GetDelegate<DeleteProfileSettingDelegate>(Id_DRS_DeleteProfileSetting);
            var saveSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_SaveSettings);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || findApp is null
                || deleteSetting is null || saveSettings is null || destroySession is null)
            {
                return false;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return false;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return false;

                var app = new NVDRS_APPLICATION_V4
                {
                    version = NvApiEntry.MakeVersion<NVDRS_APPLICATION_V4>(NvdrsApplicationV4Version),
                    isPredefined = 0,
                    appName = string.Empty,
                    userFriendlyName = string.Empty,
                    launcher = string.Empty,
                    fileInFolder = string.Empty,
                    bits = 0,
                    commandLine = string.Empty,
                };

                // Pas de profil a lui : rien a supprimer, et surtout pas de repli sur la base.
                if (findApp(session, applicationPath, out var profile, ref app) != (int)NvApiStatus.Ok
                    || profile == IntPtr.Zero)
                {
                    return false;
                }

                if (deleteSetting(session, profile, settingId) != (int)NvApiStatus.Ok) return false;
                // Session qui ne contient rien d'autre que cette suppression (PG-12072-001 p.11 :
                // SaveSettings ecrase ce qu'un autre processus a modifie entre-temps).
                if (saveSettings(session) != (int)NvApiStatus.Ok) return false;

                return true;
            }
            finally
            {
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return false; }
    }

    // ── DRS (lecture publique et sûre d'un réglage du pilote ; non élevée) ─────────────────
    public bool TryGetDrsGlobalSetting(uint settingId, out uint value)
    {
        // Délègue pour que les deux surcharges ne puissent pas diverger : une séquence de session, une recette.
        var ok = TryReadDrsGlobalSetting(settingId, out var reading);
        value = ok ? reading.Value : 0;
        return ok;
    }

    public bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading)
        => LireDrsAuJeu(applicationPath, settingId, out reading) == INvApiNative.LectureAuJeu.Lu;

    public INvApiNative.LectureAuJeu LireDrsAuJeu(string applicationPath, uint settingId, out DrsReading reading)
    {
        const INvApiNative.LectureAuJeu Illisible = INvApiNative.LectureAuJeu.Illisible;
        reading = default;
        if (!IsAvailable || string.IsNullOrWhiteSpace(applicationPath)) return Illisible;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var findApp = _entry.GetDelegate<FindApplicationByNameDelegate>(Id_DRS_FindApplicationByName);
            var getSetting = _entry.GetDelegate<GetSettingDelegate>(Id_DRS_GetSetting);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || findApp is null
                || getSetting is null || destroySession is null)
            {
                return Illisible;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return Illisible;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return Illisible;

                // Préallouer chaque ByValTStr + mot de version à l'exécution (règles d'or n°1 et n°2).
                var app = new NVDRS_APPLICATION_V4
                {
                    version = NvApiEntry.MakeVersion<NVDRS_APPLICATION_V4>(NvdrsApplicationV4Version),
                    isPredefined = 0,
                    appName = string.Empty,
                    userFriendlyName = string.Empty,
                    launcher = string.Empty,
                    fileInFolder = string.Empty,
                    bits = 0,
                    commandLine = string.Empty,
                };

                // NVAPI_EXECUTABLE_NOT_FOUND quand l'exe n'a aucun profil, et NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS
                // pour un nom de fichier nu. Les deux sont un « pas de réponse » honnête -> false.
                // Mesuré le 10/09/2026 sur cod.exe : une fois le profil existant, GetSetting se rabat bien sur le
                // global : un réglage que le profil ne possède pas revient avec la valeur qu'applique le pilote et
                // settingLocation = GLOBAL_PROFILE. La provenance est gardée dans la lecture pour que les
                // appelants distinguent « le jeu le possède » de « le jeu en hérite ». Ne jamais lire la valeur sans elle.
                // Seul EXECUTABLE_NOT_FOUND veut dire « pas de profil, le pilote applique
                // le global ». Tout autre echec (chemin ambigu compris) reste Illisible : jamais un repli devine.
                var trouve = findApp(session, applicationPath, out var profile, ref app);
                if (trouve == (int)NvApiStatus.ExecutableNotFound)
                    return INvApiNative.LectureAuJeu.PasDeProfil;
                if (trouve != (int)NvApiStatus.Ok || profile == IntPtr.Zero)
                    return Illisible;

                var setting = new NVDRS_SETTING_V1
                {
                    version = NvApiEntry.MakeVersion<NVDRS_SETTING_V1>(NvdrsSettingV1Version),
                    settingName = string.Empty,
                    settingId = settingId,
                    settingType = NvdrsSettingDwordType,
                    settingLocation = 0,
                    isCurrentPredefined = 0,
                    isPredefinedValid = 0,
                    predefinedValue = new byte[NvapiSettingMaxValues],
                    currentValue = new byte[NvapiSettingMaxValues],
                };

                if (getSetting(session, profile, settingId, ref setting) != (int)NvApiStatus.Ok)
                    return Illisible;

                reading = new DrsReading(
                    BitConverter.ToUInt32(setting.currentValue, 0),
                    setting.settingLocation,
                    setting.isCurrentPredefined,
                    setting.isPredefinedValid);
                return INvApiNative.LectureAuJeu.Lu;
            }
            finally
            {
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return Illisible; }
    }

    public bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading)
        => LireDrsGlobal(settingId, out reading) == (int)NvApiStatus.Ok;

    public bool DrsGlobalAuDefaut(uint settingId)
        => LireDrsGlobal(settingId, out _) == (int)NvApiStatus.SettingNotFound;

    // Le statut de GetSetting au profil de base, ou Error si une étape d'avant tombe : seul un SettingNotFound de
    // GetSetting lui-même dit « au défaut du pilote ».
    private int LireDrsGlobal(uint settingId, out DrsReading reading)
    {
        const int Echec = (int)NvApiStatus.Error;
        reading = default;
        if (!IsAvailable) return Echec;
        try
        {
            var createSession = _entry.GetDelegate<CreateSessionDelegate>(Id_DRS_CreateSession);
            var loadSettings = _entry.GetDelegate<SessionDelegate>(Id_DRS_LoadSettings);
            var getBaseProfile = _entry.GetDelegate<GetBaseProfileDelegate>(Id_DRS_GetBaseProfile);
            var getSetting = _entry.GetDelegate<GetSettingDelegate>(Id_DRS_GetSetting);
            var destroySession = _entry.GetDelegate<SessionDelegate>(Id_DRS_DestroySession);

            if (createSession is null || loadSettings is null || getBaseProfile is null
                || getSetting is null || destroySession is null)
            {
                return Echec;
            }

            if (createSession(out var session) != (int)NvApiStatus.Ok || session == IntPtr.Zero)
                return Echec;

            try
            {
                if (loadSettings(session) != (int)NvApiStatus.Ok) return Echec;

                if (getBaseProfile(session, out var profile) != (int)NvApiStatus.Ok || profile == IntPtr.Zero)
                    return Echec;

                // Préallouer chaque ByValArray (règle d'or n°2) + mot de version à l'exécution (n°1).
                var setting = new NVDRS_SETTING_V1
                {
                    version = NvApiEntry.MakeVersion<NVDRS_SETTING_V1>(NvdrsSettingV1Version),
                    settingName = string.Empty,
                    settingId = settingId,
                    settingType = NvdrsSettingDwordType,
                    settingLocation = 0,
                    isCurrentPredefined = 0,
                    isPredefinedValid = 0,
                    predefinedValue = new byte[NvapiSettingMaxValues],
                    currentValue = new byte[NvapiSettingMaxValues],
                };

                // GetSetting remplit `setting`. Un réglage absent du profil rend NVAPI_SETTING_NOT_FOUND : un « pas de valeur
                // posée » honnête, jamais un 0 fabriqué (l'état vrai est « au défaut du pilote », dit par DrsGlobalAuDefaut).
                var statut = getSetting(session, profile, settingId, ref setting);
                if (statut != (int)NvApiStatus.Ok)
                    return statut;

                // Le DWORD vit dans les 4 premiers octets de currentValue (recette §C). Les champs de provenance
                // reviennent avec la même lecture : les garder ne coûte rien et c'est le seul moyen de distinguer
                // une valeur livrée par le pilote d'un choix de l'utilisateur.
                reading = new DrsReading(
                    BitConverter.ToUInt32(setting.currentValue, 0),
                    setting.settingLocation,
                    setting.isCurrentPredefined,
                    setting.isPredefinedValid);
                return (int)NvApiStatus.Ok;
            }
            finally
            {
                try { destroySession(session); } catch { /* best-effort cleanup */ }
            }
        }
        catch { return Echec; }
    }

    // ── Chercheurs d'entrée d'horloge P0 ──────────────────────────────────────────────

    /// <summary>Trouve les indices de l'entrée d'horloge P0/Graphics dans une struct pstates lue, en
    /// respectant les NumPstates/NumClocks annoncés par le pilote. Interne pour les tests unitaires ;
    /// le chemin d'écriture modifie info.Pstates[p].Clocks[c] sur place avec ces indices.</summary>
    internal static bool TryFindP0GraphicsClockIndex(NvGpuPerfPstates20InfoV1 info, out int pstateIndex, out int clockIndex) =>
        TryFindP0ClockIndex(info, GraphicsClockDomain, out pstateIndex, out clockIndex);

    /// <summary>Trouve les indices de l'entrée d'horloge P0/MEMORY dans une struct pstates lue, en
    /// respectant les NumPstates/NumClocks annoncés par le pilote. Reprend le chercheur graphique avec
    /// le PublicClockDomain MEMORY (4) ; sert à la vraie lecture du décalage mémoire. Interne pour les
    /// tests unitaires.</summary>
    internal static bool TryFindP0MemoryClockIndex(NvGpuPerfPstates20InfoV1 info, out int pstateIndex, out int clockIndex) =>
        TryFindP0ClockIndex(info, MemoryClockDomain, out pstateIndex, out clockIndex);

    /// <summary>Chercheur partagé d'entrée d'horloge P0 : trouve l'entrée d'horloge du premier pstate
    /// P0 dont le DomainId égale <paramref name="domainId"/>, en respectant les comptes annoncés par le
    /// pilote. Une seule implémentation, pour que les chercheurs graphique et mémoire ne divergent pas.</summary>
    private static bool TryFindP0ClockIndex(NvGpuPerfPstates20InfoV1 info, uint domainId, out int pstateIndex, out int clockIndex)
    {
        var pstateCount = Math.Min((int)info.NumPstates, info.Pstates?.Length ?? 0);
        for (var p = 0; p < pstateCount; p++)
        {
            if (info.Pstates![p].StateId != PstateP0)
                continue;
            var clocks = info.Pstates[p].Clocks;
            var clockCount = Math.Min((int)info.NumClocks, clocks?.Length ?? 0);
            for (var c = 0; c < clockCount; c++)
            {
                if (clocks![c].DomainId == domainId)
                {
                    pstateIndex = p;
                    clockIndex = c;
                    return true;
                }
            }
        }
        pstateIndex = -1;
        clockIndex = -1;
        return false;
    }

    // ── Fabriques de structs : préallouer chaque ByValArray (règle d'or), poser la version ──

    private static NvGpuPerfPstates20InfoV1 NewPstates20Info()
    {
        var info = new NvGpuPerfPstates20InfoV1
        {
            Version = NvApiEntry.MakeVersion<NvGpuPerfPstates20InfoV1>(1),
            Pstates = new NvGpuPerformanceState20[NvApiTuningConstants.MaxPstates],
        };
        for (var i = 0; i < info.Pstates.Length; i++)
        {
            info.Pstates[i] = new NvGpuPerformanceState20
            {
                Clocks = new NvGpuPstate20ClockEntryV1[NvApiTuningConstants.MaxClocks],
                BaseVoltages = new NvGpuPstate20BaseVoltageEntryV1[NvApiTuningConstants.MaxBaseVoltages],
            };
        }
        return info;
    }

    private static NvGpuClientPowerPoliciesStatusV1 NewPowerStatus() => new()
    {
        Version = NvApiEntry.MakeVersion<NvGpuClientPowerPoliciesStatusV1>(1),
        Entries = new NvGpuPowerPolicyStatusEntry[NvApiTuningConstants.MaxPowerEntries],
    };

    private static NvGpuClientPowerPoliciesInfoV1 NewPowerInfo() => new()
    {
        Version = NvApiEntry.MakeVersion<NvGpuClientPowerPoliciesInfoV1>(1),
        Entries = new NvGpuPowerPolicyInfoEntry[NvApiTuningConstants.MaxPowerEntries],
    };

    // ── délégués (tous Cdecl, la convention de nvapi64) ───────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetPstates20Delegate(IntPtr gpuHandle, ref NvGpuPerfPstates20InfoV1 info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetPstates20Delegate(IntPtr gpuHandle, ref NvGpuPerfPstates20InfoV1 info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerStatusDelegate(IntPtr gpuHandle, ref NvGpuClientPowerPoliciesStatusV1 status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PowerInfoDelegate(IntPtr gpuHandle, ref NvGpuClientPowerPoliciesInfoV1 info);

    // --- délégués DRS ---
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateSessionDelegate(out IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionDelegate(IntPtr session);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBaseProfileDelegate(IntPtr session, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSettingDelegate(IntPtr session, IntPtr profile, ref NVDRS_SETTING_V1 setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingDelegate(IntPtr session, IntPtr profile, uint settingId, ref NVDRS_SETTING_V1 setting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int FindApplicationByNameDelegate(
        IntPtr session,
        [MarshalAs(UnmanagedType.LPWStr)] string applicationName,
        out IntPtr profile,
        ref NVDRS_APPLICATION_V4 application);

    // NVDRS_APPLICATION_V4 (NvApiDriverSettings.h). Pack = 8, chaque NvAPI_UnicodeString est un tampon
    // fixe de 2048 wchar (règle d'or n°2) : un pointeur ici corromprait la disposition.
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct NVDRS_APPLICATION_V4
    {
        public uint version;
        public uint isPredefined;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string appName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string userFriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string launcher;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string fileInFolder;
        // bitvector1: isMetro:1, isCommandLine:1, reserved:30.
        public uint bits;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string commandLine;
    }

    // NVDRS_SETTING_V1 (NvApiDriverSettings.h). Pack = 8 (règle d'or n°2).
    [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
    private struct NVDRS_SETTING_V1
    {
        public uint version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NvapiUnicodeStringMax)]
        public string settingName;
        public uint settingId;
        public uint settingType;       // NVDRS_SETTING_TYPE (0 = DWORD).
        public uint settingLocation;   // NVDRS_SETTING_LOCATION (0 = current profile).
        public uint isCurrentPredefined;
        public uint isPredefinedValid;
        // L'union NVDRS_SETTING_VALUES, exprimée en blobs de valeur bruts ; le DWORD est dans les 4 premiers
        // octets de currentValue. Préallouée à SizeConst (règle d'or n°2).
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvapiSettingMaxValues)]
        public byte[] predefinedValue;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NvapiSettingMaxValues)]
        public byte[] currentValue;
    }
}
