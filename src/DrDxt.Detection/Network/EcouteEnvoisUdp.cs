using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Network;

/// <summary>
/// Écoute, le temps d'une fenêtre, les envois UDP publiés par
/// Microsoft-Windows-Kernel-Network (événement 42, voir <see cref="EnvoisUdp"/>) et les cumule par programme et par
/// destination. Lecture pure : rien n'est écrit, aucun paquet n'est envoyé. Session privée en temps réel, arrêtée dans
/// tous les cas (même modèle que <c>DpcLatencyTracer</c>). Rend <c>null</c> quand l'écoute n'a pas pu se faire : « pas pu
/// regarder » ne se confond jamais avec « rien envoyé ».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EcouteEnvoisUdp
{
    private const string NomSession = "DrDxtEnvoisUdp";
    private static readonly Guid GuidSession = new("6c1f2e9a-41b7-4d8e-9a35-0f7b2c4d8e61");

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_VERBOSE = 5;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_ALREADY_EXISTS = 183;

    private EventRecordCallback? _rappel;
    private readonly Dictionary<(int Pid, uint Adresse, int Port), long> _cumul = new();
    private int? _pidVise;
    // Mode « qui occupe la montée » : TCP et UDP, IPv4 et IPv6, cumulés par programme seulement.
    private Dictionary<int, long>? _parProcessus;

    // Une seule session de ce nom à la fois dans le processus : deux écoutes en même temps (la route du scan et la montée)
    // se fermeraient l'une l'autre par ERROR_ALREADY_EXISTS.
    private static readonly object Verrou = new();

    /// <summary>Les octets envoyés par chaque programme pendant la fenêtre, en TCP et en UDP, sur IPv4 et IPv6 (événements
    /// 10, 26, 42, 58 de <see cref="EnvoisUdp.Fournisseur"/>). <c>null</c> si la session n'a pas pu s'ouvrir.</summary>
    public IReadOnlyDictionary<int, long>? OctetsParProcessus(TimeSpan duree)
    {
        _parProcessus = new Dictionary<int, long>();
        return Ecouter(duree) is null ? null : _parProcessus;
    }

    /// <summary>Les envois de la fenêtre, cumulés par programme et destination ; seulement ceux de <paramref name="pid"/>
    /// s'il est donné. <c>null</c> si la session n'a pas pu s'ouvrir.</summary>
    public IReadOnlyList<EnvoiUdp>? Ecouter(TimeSpan duree, int? pid = null)
    {
        lock (Verrou) return EcouterSeul(duree, pid);
    }

    private IReadOnlyList<EnvoiUdp>? EcouterSeul(TimeSpan duree, int? pid)
    {
        _pidVise = pid;
        var taille = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 2 * 1024;
        var proprietes = Marshal.AllocHGlobal(taille);
        try
        {
            Remplir(proprietes, taille);
            var code = StartTrace(out var session, NomSession, proprietes);
            if (code == ERROR_ALREADY_EXISTS)
            {
                // Notre nom, donc notre session restée ouverte après une interruption : on la ferme et on recommence.
                Arreter(taille);
                Remplir(proprietes, taille);
                code = StartTrace(out session, NomSession, proprietes);
            }
            if (code != ERROR_SUCCESS) return null;

            var fournisseur = EnvoisUdp.Fournisseur;
            if (EnableTraceEx2(session, ref fournisseur, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE,
                    _parProcessus is null ? EnvoisUdp.MotCleIpv4 : EnvoisUdp.MotCleIpv4 | EnvoisUdp.MotCleIpv6, 0, 0, IntPtr.Zero) != ERROR_SUCCESS) return null;

            // Le délégué vit dans un champ : en variable locale, le ramasse-miettes pourrait le collecter pendant que le
            // noyau détient encore son adresse.
            _rappel = SurEvenement;
            var nom = Marshal.StringToHGlobalUni(NomSession);
            // Fermée par le `finally` aussi quand une exception sort entre l'ouverture et la fin.
            var poignee = unchecked((ulong)-1);
            try
            {
                var journal = new EVENT_TRACE_LOGFILE
                {
                    LoggerName = nom,
                    ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                    EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_rappel),
                };
                poignee = OpenTrace(ref journal);
                if (poignee == unchecked((ulong)-1)) return null;

                var poignees = new[] { poignee };
                var consommateur = new Thread(() =>
                {
                    try { ProcessTrace(poignees, 1, IntPtr.Zero, IntPtr.Zero); }
                    catch { /* session coupée sous ses pieds : fonctionnement voulu */ }
                }) { IsBackground = true };
                consommateur.Start();
                Thread.Sleep(duree);
                Arreter(taille);
                consommateur.Join(TimeSpan.FromSeconds(3));
            }
            finally
            {
                if (poignee != unchecked((ulong)-1)) CloseTrace(poignee);
                Marshal.FreeHGlobal(nom);
            }

            lock (_cumul)
                return _cumul.Select(kv => new EnvoiUdp(kv.Key.Pid, (int)Math.Min(kv.Value, int.MaxValue),
                    new IPAddress(kv.Key.Adresse), kv.Key.Port)).ToList();
        }
        catch { return null; }
        finally
        {
            try { Arreter(taille); } catch { /* déjà fermée */ }
            Marshal.FreeHGlobal(proprietes);
        }
    }

    private void SurEvenement(ref EVENT_RECORD e)
    {
        if (_parProcessus is { } parPid)
        {
            if (e.EventHeader.ProviderId != EnvoisUdp.Fournisseur || !EnvoisUdp.EvenementsEnvoi.Contains(e.EventHeader.EventDescriptor.Id)
                || e.UserData == IntPtr.Zero || e.UserDataLength < 8) return;
            var tete = new byte[8];
            Marshal.Copy(e.UserData, tete, 0, 8);
            var (pid, taille) = EnvoisUdp.PidEtTaille(tete);
            lock (parPid) parPid[pid] = parPid.GetValueOrDefault(pid) + taille;
            return;
        }
        if (e.EventHeader.ProviderId != EnvoisUdp.Fournisseur || e.EventHeader.EventDescriptor.Id != EnvoisUdp.EvenementEnvoiIpv4) return;
        if (e.UserData == IntPtr.Zero || e.UserDataLength < 20) return;
        var octets = new byte[20];
        Marshal.Copy(e.UserData, octets, 0, 20);
        if (EnvoisUdp.Decoder(octets) is not { } envoi) return;
        if (_pidVise is { } p && envoi.Pid != p) return;
        var cle = (envoi.Pid, BitConverter.ToUInt32(octets, 8), envoi.PortDestination);
        lock (_cumul) _cumul[cle] = _cumul.GetValueOrDefault(cle) + envoi.Octets;
    }

    private static void Arreter(int taille)
    {
        var arret = Marshal.AllocHGlobal(taille);
        try { Remplir(arret, taille); ControlTrace(0, NomSession, arret, EVENT_TRACE_CONTROL_STOP); }
        finally { Marshal.FreeHGlobal(arret); }
    }

    private static void Remplir(IntPtr memoire, int taille)
    {
        for (var i = 0; i < taille; i++) Marshal.WriteByte(memoire, i, 0);
        Marshal.StructureToPtr(new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER { BufferSize = (uint)taille, Guid = GuidSession, Flags = WNODE_FLAG_TRACED_GUID, ClientContext = 2 },
            LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
            LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>(),
        }, memoire, false);
    }

    private delegate void EventRecordCallback(ref EVENT_RECORD eventRecord);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize; public uint ProviderId; public ulong HistoricalContext;
        public long TimeStamp; public Guid Guid; public uint ClientContext; public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize; public uint MinimumBuffers; public uint MaximumBuffers;
        public uint MaximumFileSize; public uint LogFileMode; public uint FlushTimer;
        public uint EnableFlags; public int AgeLimit; public uint NumberOfBuffers;
        public uint FreeBuffers; public uint EventsLost; public uint BuffersWritten;
        public uint LogBuffersLost; public uint RealTimeBuffersLost; public IntPtr LoggerThreadId;
        public uint LogFileNameOffset; public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id; public byte Version; public byte Channel; public byte Level;
        public byte Opcode; public ushort Task; public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_HEADER
    {
        public ushort Size; public ushort HeaderType; public ushort Flags; public ushort EventProperty;
        public uint ThreadId; public uint ProcessId; public long TimeStamp; public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor; public ulong ProcessorTime; public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ETW_BUFFER_CONTEXT { public byte ProcessorIndex; public byte Alignment; public ushort LoggerId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader; public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount; public ushort UserDataLength;
        public IntPtr ExtendedData; public IntPtr UserData; public IntPtr UserContext;
    }

}
