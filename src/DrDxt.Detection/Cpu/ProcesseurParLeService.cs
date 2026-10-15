using System.IO.Pipes;
using System.Runtime.InteropServices;
using DrDxt.Core;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// L'état du processeur demandé au service DrDXT, quand l'App n'a pas les droits de le lire elle-même.
/// Le serveur est authentifié : le PID du processus qui sert le tube doit être celui du service (lu au gestionnaire de
/// services). Sinon (service arrêté, tube squatté par un programme quelconque), rien n'est cru : « non lu ».
/// Lecture périmée (plus de 10 s) ou illisible : « non lu », jamais une vieille température.
/// </summary>
public static class ProcesseurParLeService
{
    public static readonly TimeSpan Attente = TimeSpan.FromMilliseconds(300);

    public static EtatCpu? Lire() => LireTout()?.Etat;

    /// <summary>Toute la lecture servie (état et limites), ou null.</summary>
    public static EtatCpuServi? LireTout()
    {
        // Un échec (PID qui ne colle pas, tube muet) se mémorise 2 s, le temps de ne pas attendre 300 ms à
        // chaque seconde ; plus long, il priverait le levier et le test de refroidissement de lecture alors que le service
        // tournait. Service absent : le PID nul (relu toutes les 30 s) coupe court, sans attente.
        lock (Verrou) if (DateTimeOffset.Now < _pauseJusqua) return null;
        var lu = LireTout(EtatCpuServi.NomDuTube, PidDuService, () => DateTimeOffset.Now);
        if (lu is null)
            lock (Verrou)
            {
                _pauseJusqua = DateTimeOffset.Now + TimeSpan.FromSeconds(2);
                _pidLuLe = DateTimeOffset.MinValue; // le PID se relit au prochain essai (service redémarré)
            }
        return lu;
    }

    private static DateTimeOffset _pauseJusqua = DateTimeOffset.MinValue;

    /// <summary>Coutures de test : le nom du tube, le PID attendu du serveur, l'horloge.</summary>
    internal static EtatCpu? Lire(string nomDuTube, Func<uint?> pidAttendu, Func<DateTimeOffset> horloge)
        => LireTout(nomDuTube, pidAttendu, horloge)?.Etat;

    internal static EtatCpuServi? LireTout(string nomDuTube, Func<uint?> pidAttendu, Func<DateTimeOffset> horloge)
    {
        try
        {
            // Service absent (case décochée à l'installation) ou arrêté : on n'attend pas un tube qui ne viendra pas.
            if (pidAttendu() is not { } attendu) return null;
            // Identification seulement : un serveur usurpateur n'obtient jamais de quoi agir au nom de l'utilisateur.
            using var tube = new NamedPipeClientStream(".", nomDuTube, PipeDirection.In, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            tube.Connect(Attente);
            if (!GetNamedPipeServerProcessId(tube.SafePipeHandle, out var pidServeur)) return null;
            if (attendu != pidServeur) return null;
            using var lecteur = new StreamReader(tube);
            // Un service qui accepte et ne répond plus ne fige pas l'échantillonneur de la partie.
            using var borne = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            return EtatCpuServi.Lire(lecteur.ReadLineAsync(borne.Token).AsTask().GetAwaiter().GetResult(), horloge());
        }
        catch (Exception) { return null; } // pas de service, délai dépassé, tube cassé : non lu
    }

    private static DateTimeOffset _pidLuLe = DateTimeOffset.MinValue;
    private static uint? _pidLu;
    private static readonly object Verrou = new();

    /// <summary>Le PID du service DrDXT, relu toutes les 30 s au plus (le relevé demande chaque seconde) ; null s'il ne tourne pas.</summary>
    internal static uint? PidDuService()
    {
        lock (Verrou)
        {
            if (DateTimeOffset.Now - _pidLuLe < TimeSpan.FromSeconds(30)) return _pidLu;
            _pidLu = LirePid(EtatCpuServi.NomDuService);
            _pidLuLe = DateTimeOffset.Now;
            return _pidLu;
        }
    }

    private static uint? LirePid(string service)
    {
        var scm = OpenSCManagerW(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm == 0) return null;
        try
        {
            var svc = OpenServiceW(scm, service, 0x0004 /* SERVICE_QUERY_STATUS */);
            if (svc == 0) return null;
            try
            {
                var tampon = new byte[36]; // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(svc, 0 /* SC_STATUS_PROCESS_INFO */, tampon, (uint)tampon.Length, out _)) return null;
                var etat = BitConverter.ToUInt32(tampon, 4);   // dwCurrentState
                var pid = BitConverter.ToUInt32(tampon, 28);   // dwProcessId
                return etat == 4 /* SERVICE_RUNNING */ && pid != 0 ? pid : null;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle tube, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenSCManagerW(string? machine, string? baseDeDonnees, uint acces);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenServiceW(nint scm, string nom, uint acces);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(nint service, int niveau, byte[] tampon, uint taille, out uint requis);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(nint h);
}
