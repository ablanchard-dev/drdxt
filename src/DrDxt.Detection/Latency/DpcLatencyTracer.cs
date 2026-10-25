using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Latency;

/// <summary>Ce qu'un pilote coute, en temps passe dans ses DPC et ses ISR.</summary>
/// <param name="Module">Le fichier du pilote (ex. <c>nvlddmkm.sys</c>), ou <c>?</c> si l'adresse
/// ne tombe dans aucun module connu.</param>
/// <param name="Evenements">Nombre de DPC/ISR attribues a ce module.</param>
/// <param name="TotalMs">Temps cumule, en millisecondes.</param>
/// <param name="MaxMs">La plus longue occurrence, en millisecondes. C'est elle qui fait la
/// micro-saccade : un pilote qui prend 8 ms d'un coup rate une image entiere a 144 Hz, meme si son
/// total sur 10 s est ridicule.</param>
/// <param name="AuDelaDeUneMs">Occurrences au-dela de 1 ms : le seuil au-dela duquel une trame de
/// jeu est en danger.</param>
public sealed record DpcCost(
    string Module, int Evenements, double TotalMs, double MaxMs, int AuDelaDeUneMs);

/// <summary>Ce que la trace a pu faire.</summary>
public enum DpcOutcome { Ok, DroitsInsuffisants, AucunEvenement, Echec }

/// <summary>Ce que la trace a rendu, avec le code Win32 quand il y en a un et avec le detail des
/// opcodes vus.
///
/// `ParOpcode` n'est pas un ornement de mise au point. Une session ETW qui demarre, tourne et
/// ne livre rien ressemble exactement a une machine saine ou aucun pilote ne coute rien. Sans
/// l'histogramme brut, « 0 DPC couteux » est indistinguable de « je n'ai rien su lire ».</summary>
public sealed record DpcTraceResult(
    DpcOutcome Issue,
    int CodeWin32,
    IReadOnlyList<DpcCost> Couts,
    IReadOnlyDictionary<byte, int> ParOpcode,
    int EvenementsTotal,
    double DureeObserveeSecondes,
    int RejetesTropCourts,
    int RejetesDeltaInvalide)
{
    /// <summary>Temps cumule en DPC/ISR rapporte a la duree observee, en pourcentage d'un seul cœur.
    ///
    /// Ce n'est pas une part de machine, et la difference est un facteur 28 sur ce PC. Les DPC
    /// tombent sur tous les processeurs a la fois : leur somme peut depasser 100 % sans que la
    /// machine soit saturee. Affiche sous le nom « part CPU », ce nombre se lirait mal : 8,62 %
    /// deviendrait « 8,6 % de la machine » alors que la vraie part est 0,31 %. Le nom porterait
    /// l'erreur, pas le calcul.</summary>
    public double EquivalentCoeurPercent =>
        DureeObserveeSecondes > 0 ? Couts.Sum(c => c.TotalMs) / (DureeObserveeSecondes * 1000.0) * 100.0 : 0;

    /// <summary>La meme chose ramenee a la machine entiere.</summary>
    public double PartMachinePercent => EquivalentCoeurPercent / Math.Max(1, Environment.ProcessorCount);
}

/// <summary>
/// La latence DPC/ISR, par ETW : « quel pilote de cette machine coute des micro-saccades ».
///
/// ══ pourquoi cette grandeur plutot qu'une liste de reglages ══
/// Un pic DPC ne coute pas des FPS moyens : il coute une trame ratee. C'est la difference entre
/// une moyenne qui ne bouge pas et un 1 % low qui s'effondre, exactement ce qu'un joueur ressent
/// sans savoir le nommer. Et c'est specifique au materiel : la mesure nomme le pilote fautif au
/// lieu de reciter des reglages copies d'un site a l'autre.
///
/// ══ ce qui est reutilise ══
/// La plomberie (session privee en mode `SYSTEM_LOGGER`, activation par `EnableTraceEx2`, boucle
/// `ProcessTrace` bornee sur un thread de fond, arret garanti par `finally`) est celle du traceur
/// de residence des threads (`tools/cpuprobe`), dont l'en-tete detaille les pieges evites.
///
/// Une session noyau ne cohabite pas avec n'importe quoi : si `xperf`, WPR ou un antivirus en
/// tient une, cette methode rend un echec plutot que de casser l'existante.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpcLatencyTracer
{
    private const string NomSession = "DrDxtDpc";

    /// <summary>GUID propre a cette session. Poser `SystemTraceControlGuid` ici rend
    /// ERROR_INVALID_PARAMETER (87) : la doc l'interdit en une phrase.</summary>
    private static readonly Guid GuidSessionPrivee = new("2b8f4d17-0c93-4a6e-b5d1-7e42a9c60f38");

    /// <summary>`SystemInterruptProviderGuid`, table officielle des System Providers. Mot-cle
    /// `SYSTEM_INTERRUPT_KW_DPC` ↔ `PERF_DPC` / `EVENT_TRACE_FLAG_DPC`.</summary>
    private static readonly Guid SystemInterruptProviderGuid = new("d4bbee17-b545-4888-858b-744169015b25");

    /// <summary>`PerfInfoGuid` : c'est sous ce GUID que les evenements sortent, pas sous celui du
    /// System Provider. La doc le dit : « the outputted events are not marked as being emitted from
    /// the individual system providers ».</summary>
    private static readonly Guid PerfInfoGuid = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");

    // Opcodes PerfInfo. Un DPC arrive a la fin de son execution, son `InitialTime` porte le debut.
    // La table officielle (Microsoft Learn, « PerfInfo class ») ne liste que 46, 51, 52 et 66 a 69 : aucun
    // evenement propre aux ISR des interruptions MSI n'y est documente, donc aucun opcode n'est ajoute sur la foi d'un
    // nom vu ailleurs. Un ISR MSI non compte reste une limite connue de cette mesure.
    private const byte OpcodeThreadedDpc = 66;
    private const byte OpcodeIsr = 67;
    private const byte OpcodeDpc = 68;
    private const byte OpcodeTimerDpc = 69;

    private const uint EVENT_TRACE_FLAG_DPC = 0x00000020;
    private const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_VERBOSE = 5;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_ALREADY_EXISTS = 183;

    /// <summary>Les decalages d'interop ETW que ce traceur utilise, publies pour etre compares aux
    /// en-tetes du SDK par `EtwInteropLayoutTests`.
    ///
    /// La garde porte sur ce code-ci, le seul d'interop ETW reellement expedie (appele par le scan) :
    /// seize octets d'ecart rendent zero evenement, et rien ne leve.
    /// Lus sur la structure elle-meme : un decalage change dans <see cref="Natif.EVENT_TRACE_LOGFILE"/> fait rougir la garde.</summary>
    public static IReadOnlyDictionary<string, int> DecalagesUtilises => new Dictionary<string, int>
    {
        ["EVENT_TRACE_LOGFILEW.size"] = Marshal.SizeOf<Natif.EVENT_TRACE_LOGFILE>(),
        ["LoggerName"] = Decalage(nameof(Natif.EVENT_TRACE_LOGFILE.LoggerName)),
        ["ProcessTraceMode"] = Decalage(nameof(Natif.EVENT_TRACE_LOGFILE.ProcessTraceMode)),
        ["PerfFreq"] = Decalage(nameof(Natif.EVENT_TRACE_LOGFILE.PerfFreq)),
        ["EventRecordCallback"] = Decalage(nameof(Natif.EVENT_TRACE_LOGFILE.EventRecordCallback)),
    };

    private static int Decalage(string champ) => (int)Marshal.OffsetOf<Natif.EVENT_TRACE_LOGFILE>(champ);

    private EventRecordCallback? _rappel;
    /// <summary>Une durée de la trace en millisecondes. La session est en temps système (ClientContext = 2) : horodatages et
    /// InitialTime sont en unités de 100 ns, quelle que soit la fréquence du compteur de performance de la machine.</summary>
    internal static double DureeMs(long ticks) => ticks / 10_000.0;
    private int _evenements;
    private int _codeProcessTrace;

    private readonly Dictionary<byte, int> _parOpcode = new();
    private int _rejetesTropCourts;
    private int _rejetesDeltaInvalide;
    private readonly Dictionary<string, Accumulateur> _parModule = new(StringComparer.OrdinalIgnoreCase);
    private ulong[] _basesTriees = Array.Empty<ulong>();
    private uint[] _taillesTriees = Array.Empty<uint>();
    private string[] _nomsTries = Array.Empty<string>();

    private sealed class Accumulateur
    {
        public int Evenements;
        public double TotalMs;
        public double MaxMs;
        public int AuDelaDeUneMs;
    }

    /// <summary>Observe les DPC et les ISR de toute la machine pendant <paramref name="duree"/>.
    /// Lecture pure : rien n'est applique, rien n'est ecrit.</summary>
    public DpcTraceResult Observer(TimeSpan duree)
    {
        ChargerCarteDesPilotes();

        var taille = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 2 * 1024;
        var proprietes = Marshal.AllocHGlobal(taille);
        var debut = DateTime.UtcNow;
        // Libérés par le `finally` sur tous les chemins, retour anticipé ou exception compris.
        var nomSession = IntPtr.Zero;
        var poignee = unchecked((ulong)-1);
        try
        {
            Remplir(proprietes, taille);

            var code = StartTrace(out var session, NomSession, proprietes);
            if (code == ERROR_ALREADY_EXISTS)
            {
                // Notre nom, donc notre session restee ouverte apres une interruption : on la ferme
                // et on recommence. Uniquement la notre.
                var menage = Marshal.AllocHGlobal(taille);
                try { Remplir(menage, taille); ControlTrace(0, NomSession, menage, EVENT_TRACE_CONTROL_STOP); }
                finally { Marshal.FreeHGlobal(menage); }
                code = StartTrace(out session, NomSession, proprietes);
            }
            if (code == ERROR_ACCESS_DENIED)
                return Echec(DpcOutcome.DroitsInsuffisants, code, 0);
            if (code != ERROR_SUCCESS)
                return Echec(DpcOutcome.Echec, code, 0);

            // Le delegue vit dans un champ : en variable locale, le ramasse-miettes pourrait le
            // collecter pendant que le noyau detient encore son adresse.
            _rappel = SurEvenement;
            nomSession = Marshal.StringToHGlobalUni(NomSession);

            // `EnableFlags` seul ne livre rien a une session privee (mesure du 08/09/2026) : depuis
            // Windows 10 build 20348 le fournisseur systeme s'active par `EnableTraceEx2`.
            // Simplification voulue : mot-cle a ulong.MaxValue ; les valeurs numeriques des `SYSTEM_INTERRUPT_KW_*`
            // ne sont pas publiees dans la table, et un masque faux ne leve rien : il rend une trace
            // vide, le pire mode d'echec. On prend tout le fournisseur et on trie sur l'opcode.
            // Le code rendu est lu : ignore, un refus ici donnerait une trace vide, rangee en
            // « aucun evenement » avec le code de ProcessTrace, et la vraie cause serait perdue. Le
            // `finally` arrete la session.
            var activation = EnableTraceEx2(session, ref Unsafe(SystemInterruptProviderGuid),
                EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_VERBOSE, ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (activation != ERROR_SUCCESS)
                return Echec(activation == ERROR_ACCESS_DENIED ? DpcOutcome.DroitsInsuffisants : DpcOutcome.Echec, activation, 0);

            var journal = new EVENT_TRACE_LOGFILE
            {
                LoggerName = nomSession,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_rappel),
            };

            poignee = OpenTrace(ref journal);
            if (poignee == unchecked((ulong)-1))
                return Echec(DpcOutcome.Echec, Marshal.GetLastWin32Error(), 0);


            // `ProcessTrace` peut ne jamais rendre la main : thread de fond, attente bornee, et
            // c'est le `finally` qui garantit l'arret. Un outil de diagnostic qui laisse une trace
            // noyau derriere lui abime la machine qu'il examine.
            var poignees = new[] { poignee };
            var consommateur = new Thread(() =>
            {
                try { _codeProcessTrace = ProcessTrace(poignees, 1, IntPtr.Zero, IntPtr.Zero); }
                catch { /* session coupee sous ses pieds : fonctionnement voulu */ }
            }) { IsBackground = true };
            consommateur.Start();

            debut = DateTime.UtcNow;
            Thread.Sleep(duree);
            var observee = (DateTime.UtcNow - debut).TotalSeconds;

            var arretApres = Marshal.AllocHGlobal(taille);
            try { Remplir(arretApres, taille); ControlTrace(0, NomSession, arretApres, EVENT_TRACE_CONTROL_STOP); }
            finally { Marshal.FreeHGlobal(arretApres); }

            consommateur.Join(TimeSpan.FromSeconds(3));
            CloseTrace(poignee);
            poignee = unchecked((ulong)-1);

            if (_evenements == 0)
                return Echec(DpcOutcome.AucunEvenement, _codeProcessTrace, observee);

            var couts = _parModule
                .Select(kv => new DpcCost(kv.Key, kv.Value.Evenements, kv.Value.TotalMs,
                    kv.Value.MaxMs, kv.Value.AuDelaDeUneMs))
                .OrderByDescending(c => c.MaxMs)
                .ToList();

            return new DpcTraceResult(DpcOutcome.Ok, 0, couts, _parOpcode, _evenements, observee,
                _rejetesTropCourts, _rejetesDeltaInvalide);
        }
        catch
        {
            return Echec(DpcOutcome.Echec, Marshal.GetLastWin32Error(), (DateTime.UtcNow - debut).TotalSeconds);
        }
        finally
        {
            try
            {
                var arret = Marshal.AllocHGlobal(taille);
                try { Remplir(arret, taille); ControlTrace(0, NomSession, arret, EVENT_TRACE_CONTROL_STOP); }
                finally { Marshal.FreeHGlobal(arret); }
            }
            catch { /* deja fermee : rien a rattraper */ }
            if (poignee != unchecked((ulong)-1)) CloseTrace(poignee);
            if (nomSession != IntPtr.Zero) Marshal.FreeHGlobal(nomSession);
            Marshal.FreeHGlobal(proprietes);
        }
    }

    private DpcTraceResult Echec(DpcOutcome issue, int code, double duree) =>
        new(issue, code, Array.Empty<DpcCost>(), _parOpcode, _evenements, duree,
            _rejetesTropCourts, _rejetesDeltaInvalide);

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
                // 2 = temps systeme, et ce chiffre est le coeur de la mesure.
                // Avec `ClientContext = 1` (QPC), la valeur qu'utilise le tracer de commutations,
                // ou elle est juste, la trace livre ses evenements, les routines sont bonnes, et
                // aucune duree ne sort : `InitialTime` arrive en ticks QPC depuis le demarrage
                // (~1,5e10) pendant que l'en-tete de l'evenement, lui, est converti en FILETIME
                // (~1,34e17). La soustraction est alors toujours negative, la garde jette tout, et
                // l'outil affiche un tableau vide qui ressemble trait pour trait a « cette machine
                // n'a aucun DPC couteux ». Mesure du 09/09/2026, sur 45 000 evenements recus.
                // En temps systeme les deux sont dans la meme unite de 100 ns : la soustraction a
                // enfin un sens.
                ClientContext = 2,
            },
            LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE,
            EnableFlags = EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT,
            LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>(),
            LogFileNameOffset = 0,
        };
        Marshal.StructureToPtr(p, memoire, false);
    }

    private void SurEvenement(ref EVENT_RECORD evenement)
    {
        _evenements++;
        if (evenement.EventHeader.ProviderId != PerfInfoGuid) return;

        var opcode = evenement.EventHeader.EventDescriptor.Opcode;
        if (opcode != OpcodeDpc && opcode != OpcodeIsr
            && opcode != OpcodeThreadedDpc && opcode != OpcodeTimerDpc) return;

        _parOpcode[opcode] = _parOpcode.GetValueOrDefault(opcode) + 1;

        // Disposition commune aux quatre : InitialTime (8) puis Routine (8). L'ISR ajoute derriere
        // ReturnValue/Vector, qu'on ne lit pas.
        if (evenement.UserDataLength < 16 || evenement.UserData == IntPtr.Zero)
        {
            _rejetesTropCourts++;
            return;
        }

        var debut = Marshal.ReadInt64(evenement.UserData, 0);
        var routine = (ulong)Marshal.ReadInt64(evenement.UserData, 8);

        var ticks = evenement.EventHeader.TimeStamp - debut;
        // Un delta negatif ou absurde vient d'un trou de trace ou d'une disposition mal lue : on ne
        // l'invente pas en le ramenant a zero, on le jette.
        if (ticks <= 0) { _rejetesDeltaInvalide++; return; }
        var ms = DureeMs(ticks);
        if (ms > 1000) { _rejetesDeltaInvalide++; return; }

        var module = Attribuer(routine);
        if (!_parModule.TryGetValue(module, out var acc))
            _parModule[module] = acc = new Accumulateur();

        acc.Evenements++;
        acc.TotalMs += ms;
        if (ms > acc.MaxMs) acc.MaxMs = ms;
        if (ms >= 1.0) acc.AuDelaDeUneMs++;
    }

    /// <summary>L'adresse d'une routine → le fichier du pilote qui la contient : base ≤ adresse &lt; base + taille.
    ///
    /// `EnumDeviceDrivers` ne donne pas la taille des modules : elle est lue dans l'en-tête PE du fichier
    /// (`SizeOfImage`, la taille de l'image chargée en mémoire, doc Microsoft « PE Format »). Une adresse entre la fin
    /// d'un module et le base du suivant rend `?` au lieu du nom du précédent. Taille illisible (fichier introuvable) :
    /// `?` aussi, jamais le plus grand base ≤ adresse, qui donnerait le temps d'un pilote inconnu à son voisin. Mesuré le
    /// 16/10/2026 sur la machine de référence : 3 pilotes sur 244 sans taille lisible, les copies de vidage `dump_*`.</summary>
    private string Attribuer(ulong routine) => Attribuer(routine, _basesTriees, _taillesTriees, _nomsTries);

    /// <summary>Logique pure, exposee pour etre testable sans session noyau. Une taille à 0 veut dire « non lue ».</summary>
    public static string Attribuer(ulong routine, ulong[] basesTriees, uint[] tailles, string[] nomsTries)
    {
        if (basesTriees.Length == 0 || routine < basesTriees[0]) return "?";

        var lo = 0;
        var hi = basesTriees.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (basesTriees[mid] <= routine) lo = mid; else hi = mid - 1;
        }
        return tailles[lo] == 0 || routine - basesTriees[lo] >= tailles[lo] ? "?" : nomsTries[lo];
    }

    /// <summary>`SizeOfImage` du fichier d'un pilote, ou 0 si illisible. Le chemin rendu par
    /// `GetDeviceDriverFileName` est un chemin noyau (`\SystemRoot\…`, `\??\C:\…`) : ramené à un chemin Win32.</summary>
    internal static uint TailleImage(string cheminNoyau)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var chemin = cheminNoyau.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ? windows + cheminNoyau[@"\SystemRoot".Length..]
            : cheminNoyau.StartsWith(@"\??\", StringComparison.Ordinal) ? cheminNoyau[4..]
            : cheminNoyau.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase) ? windows + cheminNoyau[@"\Windows".Length..]
            : cheminNoyau;
        try
        {
            using var fichier = File.OpenRead(chemin);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fichier);
            return (uint)(pe.PEHeaders.PEHeader?.SizeOfImage ?? 0);
        }
        catch (Exception) { return 0; }
    }

    private void ChargerCarteDesPilotes()
    {
        var bases = new IntPtr[2048];
        if (!Natif.EnumDeviceDrivers(bases, (uint)(bases.Length * IntPtr.Size), out var octets) || octets == 0)
            return;

        var nombre = (int)(octets / IntPtr.Size);
        var paires = new List<(ulong Base, uint Taille, string Nom)>(nombre);
        var tampon = new StringBuilder(260);
        for (var i = 0; i < nombre && i < bases.Length; i++)
        {
            tampon.Clear();
            var lu = Natif.GetDeviceDriverBaseName(bases[i], tampon, tampon.Capacity);
            var nom = lu > 0 ? tampon.ToString() : "?";
            tampon.Clear();
            var taille = Natif.GetDeviceDriverFileName(bases[i], tampon, tampon.Capacity) > 0 ? TailleImage(tampon.ToString()) : 0;
            paires.Add(((ulong)bases[i].ToInt64(), taille, nom));
        }

        paires.Sort((a, b) => a.Base.CompareTo(b.Base));
        _basesTriees = paires.Select(p => p.Base).ToArray();
        _taillesTriees = paires.Select(p => p.Taille).ToArray();
        _nomsTries = paires.Select(p => p.Nom).ToArray();
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

    private static ref Guid Unsafe(Guid valeur)
    {
        _guidTemporaire = valeur;
        return ref _guidTemporaire;
    }

    private static Guid _guidTemporaire;
}
