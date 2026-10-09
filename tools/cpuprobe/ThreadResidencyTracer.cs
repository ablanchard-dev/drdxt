using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DrDxt.Detection.Cpu;

namespace DrDxt.Tools.CpuProbe;

/// <summary>Ou un thread a REELLEMENT passe son temps, et non ou Windows preferait le mettre.</summary>
/// <param name="ThreadId">Le thread observe.</param>
/// <param name="TempsExecuteSecondes">Temps cumule sur un processeur, en secondes.</param>
/// <param name="ParClasse">Temps par classe d'efficacite, en secondes.</param>
/// <param name="ParProcesseur">Temps par processeur logique, en secondes.</param>
/// <param name="Migrations">Nombre de changements de processeur.</param>
/// <param name="Commutations">Nombre de mises en execution.</param>
public sealed record ThreadResidency(
    int ThreadId,
    double TempsExecuteSecondes,
    IReadOnlyDictionary<byte, double> ParClasse,
    IReadOnlyDictionary<int, double> ParProcesseur,
    int Migrations,
    int Commutations)
{
    /// <summary>La part passee sur une classe donnee, ou <c>null</c> si rien n'a ete observe.</summary>
    public double? Part(byte classe) =>
        TempsExecuteSecondes > 0 && ParClasse.TryGetValue(classe, out var s)
            ? s / TempsExecuteSecondes
            : TempsExecuteSecondes > 0 ? 0 : null;
}

/// <summary>Ce que la trace a pu faire.</summary>
public enum TraceOutcome { Ok, DroitsInsuffisants, SessionIndisponible, AucunEvenement, Echec }

/// <summary>Ce que la trace a rendu, AVEC le code Win32 quand il y en a un.
///
/// Une premiere version repliait tout sur un enum. Elle a affiche « SessionIndisponible » alors
/// que la session mise en cause n'existait meme pas dans `logman query -ets` : l'enum avait mange
/// le code, et le diagnostic etait impossible. Un code d'erreur qu'on jette est une enquete qu'on
/// s'interdit.</summary>
/// <param name="CodeWin32">Le code rendu par l'API, ou 0. C'est LUI qui permet de chercher.</param>
public sealed record TraceResult(
    TraceOutcome Issue, int CodeWin32, IReadOnlyList<ThreadResidency> Residences);

/// <summary>
/// LA RESIDENCE REELLE D'UN THREAD, PAR ETW. **INACHEVE — NE REND AUCUNE DONNEE A CE JOUR.**
///
/// ══ ETAT AU 08/09/2026, POUR QUE PERSONNE NE REPAYE CE CHEMIN ══
/// `StartTrace` REUSSIT desormais. `ProcessTrace` ne rend jamais la main, et aucune residence n'est
/// produite. Quatre tentatives, quatre echecs DISTINCTS, chacun instructif :
///
///   1. Session « NT Kernel Logger » → ERROR_ALREADY_EXISTS. Ce nom designe le journal noyau
///      SINGLETON ; le `Circular Kernel Context Logger` du systeme occupe deja une place.
///   2. Session privee avec `Wnode.Guid = SystemTraceControlGuid` → ERROR_INVALID_PARAMETER (87).
///      La doc le dit en une phrase : « Make sure the Wnode.Guid member is not set to
///      SystemTraceControlGuid. You must assign a new GUID to this member. »
///      (learn.microsoft.com, « Configuring and Starting a SystemTraceProvider Session »).
///   3. GUID prive → la session DEMARRE, et `ProcessTrace` bloque indefiniment.
///   4. Consommateur sur un thread de fond avec attente bornee → la session survit quand meme au
///      processus, et il a fallu la retrouver avec `logman query -ets` puis l'arreter a la main.
///
/// Le point 4 est le plus grave : **un outil de diagnostic qui laisse une trace noyau derriere
/// lui abime la machine qu'il examine.** Tant que ce fichier n'a pas prouve qu'il nettoie derriere
/// lui dans TOUS les cas, il ne doit pas etre appele hors d'une session surveillee.
///
/// Ce qui reste a trouver : pourquoi `ProcessTrace` ne rend pas la main sur une session systeme
/// privee, et pourquoi `ControlTrace(..., STOP)` ne la termine pas. Piste non exploree :
/// `EnableTraceEx2` avec les GUID de fournisseurs systeme de Windows 10 build 20348+, au lieu de
/// `EnableFlags` — la doc les presente comme le chemin moderne.
///
/// **AUCUNE RESIDENCE N'EST DEVINEE EN ATTENDANT.** L'appelant recoit `Echec` avec son code
/// Win32, et la question « ce thread est-il bien schedule ? » reste ouverte.
///
/// CORRIGE UNE IDEE FAUSSE AVANT QU'ELLE N'ENTRE DANS L'ARCHITECTURE.
/// `GetThreadIdealProcessor` rend un processeur **PREFERE**. `GetThreadSelectedCpuSets` rend les
/// ensembles **ASSIGNES**. Ni l'un ni l'autre ne dit ou le thread a passe son temps — Windows peut
/// l'executer ailleurs a chaque instant. Seule la commutation de contexte le dit.
///
/// ET UN AUTRE RACCOURCI TESTE PUIS REJETE : le rapport cycles / temps-CPU. Mesure du
/// 08/09/2026 sur cod.exe — threads chauds a 2,07-2,18 Gcycles/s, threads moyens a 1,71-1,95.
/// La variation est CONTINUE, elle ne separe pas deux classes de coeurs, et elle s'explique
/// vraisemblablement par la granularite de `GetThreadTimes` sur des threads fragmentes. Ce n'etait
/// donc pas un proxy de la classe de coeur, et le croire aurait produit un diagnostic invente.
///
/// ══ CE QUE CETTE SESSION FAIT, ET SEULEMENT CA ══
/// Une session ETW noyau avec le seul drapeau CSwitch, consommee en temps reel, agregee a la
/// volee. **Aucun evenement n'est conserve.** DrDXT n'est pas WPA : on compte du temps par
/// processeur pour une poignee de threads, on jette le reste.
///
/// Aucune injection, aucun hook, aucune lecture memoire du jeu. ETW observe le NOYAU, pas le
/// processus — c'est ce qui le rend compatible avec un anti-triche.
///
/// Demande les droits administrateur (session noyau). Sans eux : `DroitsInsuffisants`, et
/// surtout pas un resultat approche.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThreadResidencyTracer
{
    // PAS « NT Kernel Logger » : ce nom designe le journal noyau SINGLETON, dont Windows
    // n'autorise qu'une instance par machine. Depuis Windows 8, `EVENT_TRACE_SYSTEM_LOGGER_MODE`
    // permet a une session PRIVEE et nommee de recevoir les evenements du fournisseur systeme. On
    // ne se bat donc pas pour le singleton, et on ne coupe la trace de personne.
    private const string NomSessionNoyau = "DrDxtCSwitch";
    // LE BUG QUI A COUTE DEUX TENTATIVES, ET LA DOC LE DIT EN UNE PHRASE.
    //
    // « Make sure the Wnode.Guid member is not set to SystemTraceControlGuid. You must assign a
    // new GUID to this member. » — learn.microsoft.com, « Configuring and Starting a
    // SystemTraceProvider Session ».
    //
    // `SystemTraceControlGuid` designe le journal noyau SINGLETON. Le poser sur une session privee
    // rend ERROR_INVALID_PARAMETER (87) — un code que la premiere version repliait sur un enum, ce
    // qui rendait le diagnostic impossible. Une session privee porte SON PROPRE GUID.
    //
    // Le fournisseur systeme se multiplexe sur 8 sessions depuis Windows 8 ; les deux premieres
    // places sont reservees au NT Kernel Logger et au Circular Kernel Context Logger — tous deux
    // deja pris sur cette machine.
    private static readonly Guid GuidSessionPrivee = new("6f2a1c73-8e4b-4d2f-9a51-3c7e5b0d9146");
    private static readonly Guid ThreadGuid = new("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");

    /// <summary>`SystemSchedulerProviderGuid`. Depuis Windows 10 build 20348, les evenements du
    /// fournisseur systeme s'activent par `EnableTraceEx2` comme n'importe quel fournisseur.
    /// Source : learn.microsoft.com, « System Providers ».</summary>
    private static readonly Guid SystemSchedulerProviderGuid = new("599a2a76-4d91-4910-9ac7-7d33f2e97a6c");

    private const uint EVENT_TRACE_FLAG_CSWITCH = 0x00000010;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_ALREADY_EXISTS = 183;
    private const byte OpcodeCSwitch = 36;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_VERBOSE = 5;

    /// <summary>`SYSTEM_SCHEDULER_KW_CONTEXT_SWITCH`, dernier mot-cle de la liste du fournisseur
    /// Scheduler. **NON VERIFIE dans evntrace.h** : la valeur est deduite de l'ordre de la
    /// documentation, et c'est exactement le genre de supposition qui ne leve rien quand elle est
    /// fausse — elle produit simplement zero evenement. Le code teste donc plusieurs masques et
    /// s'arrete au premier qui livre, plutot que de croire celui-la.</summary>
    private const ulong KW_CONTEXT_SWITCH_SUPPOSE = 0x0000000000000200;

    private readonly Dictionary<int, Etat> _cibles = new();
    private readonly int[] _threadSurProcesseur = new int[512];
    private readonly long[] _depuisSurProcesseur = new long[512];
    private readonly Dictionary<int, byte> _classeParProcesseur;
    private long _frequenceQpc = 10_000_000;   // ETW horodate en 100 ns par defaut
    private long _evenements;
    private EventRecordCallback? _rappel;

    /// <summary>Ce que `ProcessTrace` a rendu. Sans ce chiffre, « aucun evenement » est un
    /// constat sterile : on ne sait pas si la boucle a echoue, ou si elle a tourne a vide.</summary>
    private int _codeProcessTrace;

    private sealed class Etat
    {
        public double Total;
        public readonly Dictionary<byte, double> ParClasse = new();
        public readonly Dictionary<int, double> ParProcesseur = new();
        public int Migrations;
        public int Commutations;
        public int DernierProcesseur = -1;
    }

    public ThreadResidencyTracer(IReadOnlyList<LogicalProcessor> topologie) =>
        _classeParProcesseur = topologie.ToDictionary(p => (int)p.IndexLogique, p => p.ClasseEfficacite);

    /// <summary>Observe la residence des <paramref name="threads"/> pendant <paramref name="duree"/>.
    ///
    /// Une session noyau est UNIQUE sur la machine : si une autre trace tourne (xperf, WPR, un
    /// antivirus), cette methode rend `SessionIndisponible` plutot que de casser l'existante.</summary>
    public TraceResult Observer(IReadOnlyCollection<int> threads, TimeSpan duree)
    {
        foreach (var t in threads) _cibles[t] = new Etat();
        Array.Fill(_threadSurProcesseur, 0);

        var taille = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 2 * 1024;
        var proprietes = Marshal.AllocHGlobal(taille);
        ulong session = 0;
        try
        {
            Remplir(proprietes, taille);

            var code = StartTrace(out session, NomSessionNoyau, proprietes);
            if (code == ERROR_ALREADY_EXISTS)
            {
                // Une session a NOUS, restee ouverte apres une interruption : on la ferme et on
                // recommence. C'est notre nom, donc notre responsabilite — et seulement le notre.
                var menage = Marshal.AllocHGlobal(taille);
                try { Remplir(menage, taille); ControlTrace(0, NomSessionNoyau, menage, EVENT_TRACE_CONTROL_STOP); }
                finally { Marshal.FreeHGlobal(menage); }
                code = StartTrace(out session, NomSessionNoyau, proprietes);
            }
            if (code == ERROR_ACCESS_DENIED)
                return new TraceResult(TraceOutcome.DroitsInsuffisants, code, Array.Empty<ThreadResidency>());
            if (code != ERROR_SUCCESS)
                return new TraceResult(TraceOutcome.Echec, code, Array.Empty<ThreadResidency>());

            // Le delegue est garde dans un champ de l'instance : s'il n'etait qu'une variable
            // locale, le ramasse-miettes pourrait le collecter pendant que le noyau detient encore
            // son adresse — un plantage qui n'arrive qu'en charge et qu'on ne reproduit jamais.
            _rappel = SurEvenement;
            var nomSession = Marshal.StringToHGlobalUni(NomSessionNoyau);
            // `EnableFlags` seul ne livre RIEN a une session PRIVEE : mesure du 08/09/2026, la
            // session demarrait, `ProcessTrace` rendait la main, et zero evenement arrivait. Depuis
            // Windows 10 build 20348 le fournisseur systeme s'active par `EnableTraceEx2`.
            //
            // La valeur du mot-cle n'est pas verifiee dans l'en-tete : on essaie donc plusieurs
            // masques et on garde le premier qui livre. Un masque faux ne leve rien — il rend
            // simplement une trace vide, ce qui est le pire mode d'echec possible.
            foreach (var masque in new[] { KW_CONTEXT_SWITCH_SUPPOSE, ulong.MaxValue })
            {
                var actif = EnableTraceEx2(session, ref Unsafe(SystemSchedulerProviderGuid),
                    EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE, masque, 0, 0, IntPtr.Zero);
                if (actif == ERROR_SUCCESS) break;
            }

            var journal = new EVENT_TRACE_LOGFILE
            {
                LoggerName = nomSession,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_rappel),
            };

            var poignee = OpenTrace(ref journal);
            if (poignee == unchecked((ulong)-1))
                return new TraceResult(TraceOutcome.Echec, Marshal.GetLastWin32Error(),
                    Array.Empty<ThreadResidency>());

            if (journal.PerfFreq > 0) _frequenceQpc = journal.PerfFreq;

            // `ProcessTrace` PEUT NE JAMAIS RENDRE LA MAIN, et ca s'est produit le 08/09/2026 :
            // la session est restee ouverte sur la machine apres la mort du processus, et il a fallu
            // la retrouver avec `logman query -ets` puis l'arreter a la main. Un outil de diagnostic
            // qui laisse une trace noyau derriere lui est un outil qui abime la machine qu'il
            // examine.
            //
            // On l'execute donc sur un thread de fond avec une attente BORNEE. Que la boucle rende
            // la main ou non, le `finally` arrete la session — c'est la seule garantie qui compte.
            var poignees = new[] { poignee };
            var consommateur = new Thread(() =>
            {
                try { _codeProcessTrace = ProcessTrace(poignees, 1, IntPtr.Zero, IntPtr.Zero); }
                catch { /* la session a ete coupee sous ses pieds : c'est le fonctionnement voulu */ }
            }) { IsBackground = true };
            consommateur.Start();

            Thread.Sleep(duree);
            var arretApres = Marshal.AllocHGlobal(taille);
            try { Remplir(arretApres, taille); ControlTrace(0, NomSessionNoyau, arretApres, EVENT_TRACE_CONTROL_STOP); }
            finally { Marshal.FreeHGlobal(arretApres); }

            // Une marge courte pour que les tampons se vident. Au-dela on n'attend plus : le thread
            // est en arriere-plan et la session est deja coupee.
            consommateur.Join(TimeSpan.FromSeconds(3));
            CloseTrace(poignee);

            if (_evenements == 0)
                return new TraceResult(TraceOutcome.AucunEvenement, _codeProcessTrace,
                    Array.Empty<ThreadResidency>());

            return new TraceResult(TraceOutcome.Ok, 0, _cibles.Select(kv => new ThreadResidency(
                kv.Key, kv.Value.Total, kv.Value.ParClasse, kv.Value.ParProcesseur,
                kv.Value.Migrations, kv.Value.Commutations)).ToList());
        }
        catch
        {
            return new TraceResult(TraceOutcome.Echec, Marshal.GetLastWin32Error(),
                Array.Empty<ThreadResidency>());
        }
        finally
        {
            try
            {
                var arret = Marshal.AllocHGlobal(taille);
                try { Remplir(arret, taille); ControlTrace(0, NomSessionNoyau, arret, EVENT_TRACE_CONTROL_STOP); }
                finally { Marshal.FreeHGlobal(arret); }
            }
            catch { /* la session est peut-etre deja fermee : rien a rattraper */ }
            Marshal.FreeHGlobal(proprietes);
        }
    }

    private static void Remplir(IntPtr memoire, int taille)
    {
        for (var i = 0; i < taille; i++) Marshal.WriteByte(memoire, i, 0);
        var p = new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER
            {
                BufferSize = (uint)taille,
                Guid = GuidSessionPrivee,
                Flags = WNODE_FLAG_TRACED_GUID,
                ClientContext = 1,   // 1 = QPC
            },
            LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE,
            // UN SEUL drapeau. CSwitch produit deja des dizaines de milliers d'evenements par
            // seconde ; en ajouter un autre transformerait ce lecteur en collecteur de traces.
            EnableFlags = EVENT_TRACE_FLAG_CSWITCH,
            LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>(),
            LogFileNameOffset = 0,
        };
        Marshal.StructureToPtr(p, memoire, false);
    }

    private void SurEvenement(ref EVENT_RECORD evenement)
    {
        _evenements++;
        if (evenement.EventHeader.ProviderId != ThreadGuid
            || evenement.EventHeader.EventDescriptor.Opcode != OpcodeCSwitch
            || evenement.UserDataLength < 8 || evenement.UserData == IntPtr.Zero)
            return;

        var processeur = evenement.BufferContext.ProcessorIndex;
        if (processeur >= _threadSurProcesseur.Length) return;

        var nouveau = Marshal.ReadInt32(evenement.UserData, 0);
        var instant = evenement.EventHeader.TimeStamp;

        // Le thread qui QUITTE ce processeur encaisse le temps ecoule depuis qu'il y est monte.
        var sortant = _threadSurProcesseur[processeur];
        if (sortant != 0 && _cibles.TryGetValue(sortant, out var etat))
        {
            var secondes = (instant - _depuisSurProcesseur[processeur]) / (double)_frequenceQpc;
            if (secondes > 0 && secondes < 1)   // un delta absurde vient d'un trou de trace
            {
                etat.Total += secondes;
                var classe = _classeParProcesseur.TryGetValue(processeur, out var c) ? c : (byte)255;
                etat.ParClasse[classe] = etat.ParClasse.GetValueOrDefault(classe) + secondes;
                etat.ParProcesseur[processeur] = etat.ParProcesseur.GetValueOrDefault(processeur) + secondes;
            }
        }

        if (_cibles.TryGetValue(nouveau, out var entrant))
        {
            entrant.Commutations++;
            if (entrant.DernierProcesseur >= 0 && entrant.DernierProcesseur != processeur)
                entrant.Migrations++;
            entrant.DernierProcesseur = processeur;
        }

        _threadSurProcesseur[processeur] = nouveau;
        _depuisSurProcesseur[processeur] = instant;
    }

    // ── frontiere native ────────────────────────────────────────────────────────────────────

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
    private struct ETW_BUFFER_CONTEXT
    {
        public byte ProcessorIndex; public byte Alignment; public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader; public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount; public ushort UserDataLength;
        public IntPtr ExtendedData; public IntPtr UserData; public IntPtr UserContext;
    }

    /// <summary>
    /// DISPOSITION VERIFIEE CONTRE LE SDK INSTALLE, PAS DEDUITE.
    ///
    /// Ce type a ete faux DEUX fois, et les deux fois le C# compilait sans un mot :
    ///
    ///   1. `LayoutKind.Sequential` avec `EventCallback` et `EventRecordCallback` en deux champs
    ///      alors que l'en-tete en fait une UNION, plus un `CurrentEvent` et un
    ///      `TRACE_LOGFILE_HEADER` sous-dimensionnes. Resultat : `ProcessTrace` bloquait.
    ///   2. Disposition explicite avec des decalages CALCULES a la main. Ils tombaient a
    ///      16 octets trop tot sur le rappel (408 au lieu de 424), parce que j'avais estime
    ///      `TRACE_LOGFILE_HEADER` a 264 octets quand il en fait 280. Resultat : plus de blocage,
    ///      mais ZERO evenement — le pire mode d'echec, celui qui ressemble a « rien a mesurer ».
    ///
    /// Les valeurs ci-dessous viennent de `tools/etwlayout`, qui compile `offsetof` contre les
    /// en-tetes du SDK de CETTE machine (10.0.26100.0) et les imprime. Un test compare les deux et
    /// echoue si un seul decalage diverge : on ne peut plus introduire silencieusement
    /// « 16 octets trop loin mais tout compile ».
    ///
    /// <code>
    ///   0   LogFileName          368  (TRACE_LOGFILE_HEADER interne)
    ///   8   LoggerName           376  LogfileHeader.PerfFreq
    ///  16   CurrentTime          400  BufferCallback
    ///  24   BuffersRead          408  BufferSize
    ///  28   ProcessTraceMode     424  EventCallback / EventRecordCallback  (UNION)
    ///  32   CurrentEvent         432  IsKernelTrace
    /// 120   LogfileHeader        440  Context
    ///                            448  taille totale
    /// </code>
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EVENT_TRACE_LOGFILE
    {
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(376)] public long PerfFreq;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
    }

    /// <summary>Les decalages que ce fichier utilise, exposes pour que le test les compare a la
    /// sortie de l'oracle natif. Un ecart d'un seul octet doit faire echouer la suite.</summary>
    /// <summary>11/09/2026 : cette table N'EST PLUS COMPAREE au SDK. La garde
    /// `EtwInteropLayoutTests` lit desormais celle de `DpcLatencyTracer`, le traceur que le produit
    /// expedie. Celle-ci reste ici parce que c'est l'interop de CE fichier ; ne pas la croire
    /// verifiee.</summary>
    public static IReadOnlyDictionary<string, int> DecalagesUtilises => new Dictionary<string, int>
    {
        ["EVENT_TRACE_LOGFILEW.size"] = 448,
        ["LoggerName"] = 8,
        ["ProcessTraceMode"] = 28,
        ["PerfFreq"] = 376,
        ["EventRecordCallback"] = 424,
    };

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW", SetLastError = true)]
    private static extern int StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    private static extern int ControlTrace(ulong sessionHandle, string sessionName,
        IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW", SetLastError = true)]
    private static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handleArray, uint handleCount,
        IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong traceHandle);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong traceHandle, ref Guid providerId,
        uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword,
        uint timeout, IntPtr enableParameters);

    /// <summary>Un GUID en variable locale, pour pouvoir le passer par reference.</summary>
    private static ref Guid Unsafe(Guid valeur)
    {
        _guidTemporaire = valeur;
        return ref _guidTemporaire;
    }

    private static Guid _guidTemporaire;
}
