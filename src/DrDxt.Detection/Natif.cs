using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DrDxt.Detection;

/// <summary>
/// Les fonctions natives de Windows que plusieurs lecteurs de DrDxt.Detection appellent, déclarées une seule fois, aux noms
/// de paramètres et de champs du Windows SDK (cfgmgr32.h, devpropdef.h, fileapi.h, ioapiset.h, hidpi.h, winuser.h,
/// handleapi.h, processthreadsapi.h, evntrace.h, winreg.h, wingdi.h, psapi.h) : une signature corrigée l'est partout. Rien ici n'écrit sur la machine ; CreateFileW est ouverte sans droit d'écriture par
/// chacun de ses appelants (gardé par LesLecteursNeTouchentRienTests).
/// </summary>
internal static class Natif
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, int BufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
        byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_Interface_List_SizeW(out uint pulLen, ref Guid InterfaceClassGuid, string? pDeviceID,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern int CM_Get_Device_Interface_ListW(ref Guid InterfaceClassGuid, string? pDeviceID, char[] Buffer,
        uint BufferLen, uint ulFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(IntPtr PreparsedData, byte[] Capabilities);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemCpuSetInformation(IntPtr Information, uint BufferLength, out uint ReturnedLength,
        IntPtr Process, uint Flags);

    // ── ETW (evntrace.h), pour les deux traceurs de DrDXT : la latence DPC et les envois UDP ──────────────

    /// <summary>EVENT_TRACE_LOGFILEW, réduite aux champs que DrDXT lit et pose. Disposition vérifiée contre le SDK par
    /// <c>tools/etwlayout</c>, pas déduite : deux versions fausses ont compilé sans un mot, l'une bloquant ProcessTrace,
    /// l'autre rendant zéro événement. Les décalages sont publiés par DpcLatencyTracer.DecalagesUtilises et comparés au
    /// SDK par EtwInteropLayoutTests.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    internal struct EVENT_TRACE_LOGFILE
    {
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(376)] public long PerfFreq;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW", SetLastError = true)]
    internal static extern int StartTrace(out ulong TraceHandle, string InstanceName, IntPtr Properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    internal static extern int ControlTrace(ulong TraceHandle, string InstanceName, IntPtr Properties, uint ControlCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW", SetLastError = true)]
    internal static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE Logfile);

    [DllImport("advapi32.dll")]
    internal static extern int ProcessTrace(ulong[] HandleArray, uint HandleCount, IntPtr StartTime, IntPtr EndTime);

    [DllImport("advapi32.dll")]
    internal static extern int CloseTrace(ulong TraceHandle);

    [DllImport("advapi32.dll")]
    internal static extern int EnableTraceEx2(ulong TraceHandle, ref Guid ProviderId, uint ControlCode, byte Level,
        ulong MatchAnyKeyword, ulong MatchAllKeyword, uint Timeout, IntPtr EnableParameters);

    // ── Registre (winreg.h) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>La date de dernière écriture d'une clé (lpftLastWriteTime, un FILETIME), seule information que .NET
    /// n'expose pas. Les autres sorties sont laissées à null.</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
    internal static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass,
        IntPtr lpReserved, IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues,
        IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

    // RIDI_DEVICENAME : le nom de l'appareil, en caractères.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, char[]? pData, ref uint pcbSize);

    // ── DisplayConfig (wingdi.h, winuser.h) : les chemins actifs et ce que chaque sortie envoie. Les tableaux restent en
    // octets, découpés aux tailles du SDK (DISPLAYCONFIG_PATH_INFO 72, DISPLAYCONFIG_MODE_INFO 64) par CheminsDAffichage.

    // ── psapi.h : les pilotes chargés en mémoire (trace DPC, constat RTCore). ──

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDeviceDrivers(IntPtr[] lpImageBase, uint cb, out uint lpcbNeeded);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDeviceDriverBaseNameW")]
    internal static extern int GetDeviceDriverBaseName(IntPtr ImageBase, System.Text.StringBuilder lpFilename, int nSize);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDeviceDriverFileNameW")]
    internal static extern int GetDeviceDriverFileName(IntPtr ImageBase, System.Text.StringBuilder lpFilename, int nSize);

    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, byte[] pathArray,
        ref uint numModeInfoArrayElements, byte[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(byte[] requestPacket);
}
