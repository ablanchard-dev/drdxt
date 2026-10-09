using DrDxt.Core;
using System.Runtime.InteropServices;

// 25/09/2026 — pour tout l'assembly. L'App peut tourner en administrateur (relance elevee, session
// admin) : une DLL cherchee par son seul nom qui manque a cote de l'App serait cherchee dans le PATH,
// dont des dossiers sont modifiables par l'utilisateur (meme faille que le helper, fermee le 25/09/2026).
// PresentMonAPI2.dll est livree a cote de l'assembly ; les autres DLL chargees ici vivent dans System32.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]

namespace DrDxt.Measurement.PresentMon;

// ============================================================
// Real P/Invoke layer on PresentMonAPI2.dll (v2.5.1, MIT ; third_party/presentmon/VERSION.txt).
//
// This is the only un-unit-tested boundary in the spine: its correctness is
// confirmed by a manual in-game smoke test (third_party/presentmon/SMOKE-TEST.md).
// All capture/parse logic lives in PresentMonSession and IS unit-tested through
// the IPresentMonApi seam with canned blobs.
//
// Signatures confirmed against third_party/presentmon/PresentMonAPI.h:
//   PM_STATUS pmOpenSessionWithPipe(PM_SESSION_HANDLE* pHandle, const char* controlPipeName);
//   PM_STATUS pmCloseSession(PM_SESSION_HANDLE handle);
//   PM_STATUS pmStartTrackingProcess(PM_SESSION_HANDLE handle, uint32_t pid);
//   PM_STATUS pmStopTrackingProcess(PM_SESSION_HANDLE handle, uint32_t pid);
//   PM_STATUS pmRegisterFrameQuery(PM_SESSION_HANDLE, PM_FRAME_QUERY_HANDLE*, PM_QUERY_ELEMENT*, uint64_t, uint32_t*);
//   PM_STATUS pmConsumeFrames(PM_FRAME_QUERY_HANDLE, uint32_t, uint8_t*, uint32_t*);
//   PM_STATUS pmFreeFrameQuery(PM_FRAME_QUERY_HANDLE);
// Opaque handles (PM_SESSION_HANDLE / PM_FRAME_QUERY_HANDLE) marshal as nint.
// ============================================================
internal sealed class PresentMonApi2 : IPresentMonApi
{
    private const string Dll = "PresentMonAPI2.dll";

    // `pmOpenSession(out h)` — la variante sans argument — n'est plus liee, volontairement.
    // Elle ouvre `\\.\pipe\sharedpresentmonsvcnamedpipe`, le tube par defaut, celui que le service
    // Intel (2.5.1, demarrage automatique) possede deja sur cette machine : mesure du 07/09/2026,
    // les deux services Running et une seule paire de tubes. Le client parlait donc au service
    // rival, d'une autre version -- l'explication du PM_STATUS_PIPE_ERROR 3 fois sur 5.
    //
    // `const char* controlPipeName` : ANSI, qui est deja le defaut de marshalling de DllImport
    // pour `string` ; on l'ecrit quand meme, parce qu'un defaut implicite n'est pas un contrat.
    [DllImport(Dll, CharSet = CharSet.Ansi)]
    private static extern PM_STATUS pmOpenSessionWithPipe(out nint pHandle, string controlPipeName);

    [DllImport(Dll)]
    private static extern PM_STATUS pmCloseSession(nint handle);

    [DllImport(Dll)]
    private static extern PM_STATUS pmStartTrackingProcess(nint handle, uint pid);

    [DllImport(Dll)]
    private static extern PM_STATUS pmStopTrackingProcess(nint handle, uint pid);

    [DllImport(Dll)]
    private static extern PM_STATUS pmRegisterFrameQuery(
        nint session, out nint pHandle, [In, Out] PM_QUERY_ELEMENT[] elements, ulong numElements, out uint pBlobSize);

    [DllImport(Dll)]
    private static extern PM_STATUS pmConsumeFrames(
        nint handle, uint processId, [In, Out] byte[] pBlobs, ref uint pNumFramesToRead);

    [DllImport(Dll)]
    private static extern PM_STATUS pmFreeFrameQuery(nint handle);

    // Le nom du tube n'est pas un choix d'appelant : c'est le contrat entre le service que DrDXT
    // installe et le client qui le consomme. Il vit dans Core pour que les deux cotes ne puissent
    // pas deriver l'un sans l'autre.
    public PM_STATUS OpenSession(out nint session)
        => pmOpenSessionWithPipe(out session, PresentMonNaming.ControlPipe);

    public PM_STATUS CloseSession(nint session) => pmCloseSession(session);

    public PM_STATUS StartTracking(nint session, uint pid) => pmStartTrackingProcess(session, pid);

    public PM_STATUS StopTracking(nint session, uint pid) => pmStopTrackingProcess(session, pid);

    public PM_STATUS RegisterFrameQuery(nint session, PM_QUERY_ELEMENT[] elements, out nint query, out uint blobSize)
        => pmRegisterFrameQuery(session, out query, elements, (ulong)elements.Length, out blobSize);

    public PM_STATUS ConsumeFrames(nint query, uint pid, byte[] blobs, uint maxFrames, out uint framesRead)
    {
        // pNumFramesToRead is in/out: pass buffer capacity (in frames), receive count actually read.
        framesRead = maxFrames;
        return pmConsumeFrames(query, pid, blobs, ref framesRead);
    }

    public PM_STATUS FreeFrameQuery(nint query) => pmFreeFrameQuery(query);
}
