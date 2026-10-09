using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// Ce qui tourne a cote du jeu — 11/09/2026, demande produit.
//
// Ce qui a declenche ce code. Un scan a affiche « 164 FPS » pendant qu'on regardait l'ecran de
// DrDXT, le jeu en arriere-plan. Le meme soir, en partie reelle avec nos agents qui compilaient, il
// mesurait 79,5 FPS. Un FPS moyen seul ne veut rien dire : il depend de ce qui tournait a cote. Sur
// son i7-14700F, Warzone est limite par le processeur (cpuApp 96 %, GPU busy 66 %) — chaque coeur
// pris par OBS, un navigateur ou un compilateur est un coeur pris au jeu.
//
// L'ecran de scan dit donc, avant de montrer un chiffre, ce qui tournait a cote : nom lisible,
// part de processeur (en pourcentage d'UN coeur, comme le Gestionnaire des taches) et part de GPU.
//
// Lecture seule, et honnete sur ses limites :
//   * le processeur se mesure par DIFFERENCE de temps processeur sur une fenetre courte — c'est ce
//     que fait le Gestionnaire des taches, sans compteur de performance ni ETW ;
//   * le GPU se lit dans la categorie « GPU Engine » des compteurs de performance de Windows, la
//     meme source que l'onglet Performances. Absente (pilote, service desactive) ⇒ pas de part GPU,
//     et on ne l'invente pas ;
//   * un processus qu'on ne peut pas ouvrir (protege, anti-triche, autre session) est simplement
//     absent de la liste. La liste n'est donc jamais garantie COMPLETE ; elle ne ment pas sur ce
//     qu'elle montre.
// -----------------------------------------------------------------------

/// <param name="Nom">Le nom lisible du processus (sans .exe).</param>
/// <param name="ProcesseurPourcent">Part de temps processeur pendant la fenetre, en % d'UN coeur.</param>
/// <param name="GpuPourcent">Part de GPU, ou <c>null</c> quand les compteurs ne repondent pas.</param>
/// <param name="Pid">03/10/2026 (P3.2) : le processus lu, pour pouvoir le calmer ; 0 quand il n'est pas connu.</param>
public sealed record VoisinDuJeu(string Nom, double ProcesseurPourcent, double? GpuPourcent, int Pid = 0);

public interface ILecteurDeVoisins
{
    /// <summary>Les processus qui consomment notablement pendant une courte fenetre, hors le jeu
    /// (<paramref name="pidDuJeu"/>) et hors DrDXT lui-meme. Ne jette jamais. 03/10/2026 (D8.6, revue n° 3) :
    /// une liste vide disait à la fois « rien vu » et « pas pu regarder », et l'écran devait l'avouer entre parenthèses.
    /// Désormais <c>null</c> = pas pu regarder ; liste vide = regardé, rien au-dessus du seuil.</summary>
    IReadOnlyList<VoisinDuJeu>? Lire(int? pidDuJeu, CancellationToken ct);

    /// <summary>04/10/2026 : par programme (processus d'un même nom additionnés avant le seuil), sans coupe ; pour le
    /// relevé des parties. Par défaut, la lecture par processus.</summary>
    IReadOnlyList<VoisinDuJeu>? LireParNom(int? pidDuJeu, CancellationToken ct) => Lire(pidDuJeu, ct);
}

/// <summary>
/// 07/10/2026 (revue F-27) : seul le pid du jeu était écarté. Ses enfants et son lanceur (RiotClientServices,
/// EpicWebHelper, steamwebhelper, GameOverlayUI, Battle.net) passaient pour des voisins : au-dessus d'un quart de cœur, le
/// calmeur les proposait et les passait sous la normale, au risque de ralentir le jeu lui-même. La famille du jeu est
/// écartée de la lecture : ce dont la chaîne de parents remonte au jeu, ce qui l'a lancé (jusqu'en haut), et ce que son
/// lanceur direct fait tourner à côté, sauf si ce lanceur est un processus de Windows (l'explorateur lance tout).
/// Limite : un pid parent peut avoir été réattribué après la fin de son processus ; on écarte alors trop, jamais trop peu.
/// </summary>
public static class FamilleDuJeu
{
    /// <param name="processus">Chaque processus : son parent et son nom (sans « .exe »).</param>
    public static IReadOnlySet<int> De(int pidDuJeu, IReadOnlyDictionary<int, (int Parent, string Nom)> processus)
    {
        var famille = new HashSet<int> { pidDuJeu };
        // Ce qui l'a lancé, jusqu'en haut ; une boucle de parents (pids réattribués) s'arrête au premier déjà vu.
        for (var pid = pidDuJeu; processus.TryGetValue(pid, out var p) && p.Parent > 4 && famille.Add(p.Parent);) pid = p.Parent;
        // Ce qui descend du jeu, et de son lanceur direct quand ce n'est pas un processus de Windows.
        var racines = new List<int> { pidDuJeu };
        if (processus.TryGetValue(pidDuJeu, out var jeu) && processus.TryGetValue(jeu.Parent, out var lanceur)
            && !CalmeurDeVoisins.Protege(lanceur.Nom))
            racines.Add(jeu.Parent);
        var enfants = processus.ToLookup(e => e.Value.Parent, e => e.Key);
        var vus = new HashSet<int>(racines);
        var file = new Queue<int>(racines);
        while (file.TryDequeue(out var parent))
            foreach (var enfant in enfants[parent])
                if (vus.Add(enfant)) { famille.Add(enfant); file.Enqueue(enfant); }
        return famille;
    }
}

/// <summary>Le defaut sur toute plateforme et en test : ne regarde rien.</summary>
public sealed class AucunVoisin : ILecteurDeVoisins
{
    public IReadOnlyList<VoisinDuJeu>? Lire(int? pidDuJeu, CancellationToken ct) => null;
}

[SupportedOSPlatform("windows")]
public sealed class VoisinsWin32 : ILecteurDeVoisins
{
    /// <summary>Le seuil : au-dela, le voisin est assez gros pour peser sur une mesure. 5 % d'UN
    /// coeur, ou 5 % du GPU — le chiffre retenu.</summary>
    public const double SeuilPourcent = 5.0;

    /// <summary>La fenetre de mesure. Assez longue pour que la difference de temps processeur soit
    /// stable, assez courte pour ne pas faire attendre le scan.</summary>
    public static readonly TimeSpan Fenetre = TimeSpan.FromMilliseconds(600);

    /// <summary>Combien de voisins on montre. Une liste COURTE : au-dela, personne ne la lit.</summary>
    public const int MaxVoisins = 5;

    // 04/10/2026, revue (P2) : le service de capture de DrDXT tourne juste avant chaque lecture en partie.
    private static readonly string[] LeNotre = { "DrDxt.App", "DrDxt.ElevatedHelper", "DrDxt.ReconcilerHost", "PresentMonService" };

    private readonly Func<IReadOnlyDictionary<int, (int Parent, string Nom)>> _processus;

    /// <param name="processus">Revue F-27 : la table des processus et de leurs parents ; par défaut, un instantané
    /// Toolhelp de Windows.</param>
    public VoisinsWin32(Func<IReadOnlyDictionary<int, (int Parent, string Nom)>>? processus = null)
        => _processus = processus ?? InstantaneToolhelp;

    public IReadOnlyList<VoisinDuJeu>? Lire(int? pidDuJeu, CancellationToken ct)
        => Mesurer(pidDuJeu, ct)?
            .Where(v => v.ProcesseurPourcent >= SeuilPourcent || v.GpuPourcent >= SeuilPourcent)
            .OrderByDescending(v => Math.Max(v.ProcesseurPourcent, v.GpuPourcent ?? 0))
            .Take(MaxVoisins)
            .ToList();

    public IReadOnlyList<VoisinDuJeu>? LireParNom(int? pidDuJeu, CancellationToken ct)
        => Mesurer(pidDuJeu, ct) is { } lus ? ParNom(lus) : null;

    /// <summary>Les processus d'un même nom additionnés, puis le seuil ; aucune coupe. Pid 0 : un programme, pas un processus.</summary>
    public static IReadOnlyList<VoisinDuJeu> ParNom(IReadOnlyList<VoisinDuJeu> lus)
        => lus.GroupBy(v => v.Nom, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VoisinDuJeu(g.Key, Math.Round(g.Sum(v => v.ProcesseurPourcent), 1),
                g.All(v => v.GpuPourcent is null) ? null : Math.Round(g.Sum(v => v.GpuPourcent ?? 0), 1)))
            .Where(v => v.ProcesseurPourcent >= SeuilPourcent || v.GpuPourcent >= SeuilPourcent)
            .OrderByDescending(v => Math.Max(v.ProcesseurPourcent, v.GpuPourcent ?? 0))
            .ToList();

    /// <summary>Chaque processus lu sur la fenêtre, sans seuil ; <c>null</c> = pas pu regarder.</summary>
    private List<VoisinDuJeu>? Mesurer(int? pidDuJeu, CancellationToken ct)
    {
        try
        {
            // Revue F-27 : la famille du jeu (ses enfants, son lanceur) n'est pas un voisin. Table illisible : le jeu seul.
            IReadOnlySet<int> famille = pidDuJeu is { } jeu ? FamilleDuJeu.De(jeu, LireProcessus()) : new HashSet<int>();
            var debut = Echantillon(famille, ct);
            if (debut.Count == 0) return null;
            var gpuDebut = OccupationGpu();

            var chrono = Stopwatch.StartNew();
            ct.WaitHandle.WaitOne(Fenetre);
            chrono.Stop();
            if (chrono.Elapsed <= TimeSpan.Zero) return null;

            var fin = Echantillon(famille, ct);
            var gpu = PartsGpu(gpuDebut, OccupationGpu(), chrono.Elapsed);

            return fin
                .Select(f =>
                {
                    if (!debut.TryGetValue(f.Key, out var avant)) return null;
                    var pourcent = (f.Value.Temps - avant.Temps).TotalMilliseconds
                                   / chrono.Elapsed.TotalMilliseconds * 100.0;
                    gpu.TryGetValue(f.Key, out var partGpu);
                    return new VoisinDuJeu(f.Value.Nom, Math.Round(pourcent, 1),
                        gpu.Count == 0 ? null : Math.Round(partGpu, 1), f.Key);
                })
                .OfType<VoisinDuJeu>()
                .ToList();
        }
        catch
        {
            // Jamais d'exception vers le scan : au pire on ne sait pas, et on le dit (null).
            return null;
        }
    }

    private IReadOnlyDictionary<int, (int Parent, string Nom)> LireProcessus()
    {
        try { return _processus(); }
        catch { return new Dictionary<int, (int, string)>(); }
    }

    private static Dictionary<int, (string Nom, TimeSpan Temps)> Echantillon(IReadOnlySet<int> famille, CancellationToken ct)
    {
        var moi = Environment.ProcessId;
        var resultat = new Dictionary<int, (string, TimeSpan)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (p.Id == moi || famille.Contains(p.Id) || p.Id <= 4) continue;
                if (LeNotre.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                resultat[p.Id] = (p.ProcessName, p.TotalProcessorTime);
            }
            catch
            {
                // Processus protege, deja termine, acces refuse : il n'est pas dans la liste.
            }
            finally
            {
                p.Dispose();
            }
        }

        return resultat;
    }

    /// <summary>Le temps CUMULE d'occupation de chaque moteur GPU, en centaines de nanosecondes, lu d'UN
    /// Seul coup dans « GPU Engine » (la source de l'onglet Performances). Les instances s'appellent
    /// « pid_1234_luid_..._engtype_3D » : une par moteur et par processus, gardees separees (voir
    /// <see cref="PartsGpu"/>). Categorie absente ⇒ dictionnaire vide ⇒ aucune part GPU affichee.
    ///
    /// 11/09/2026 — mesure, pas gout. Cette methode ouvrait un <c>PerformanceCounter</c> par
    /// instance : 637 objets sur cette machine, <b>552 ms de processeur</b> par scan (banc
    /// `tools/voisinscout`), soit presque un cœur entier pendant que le joueur JOUE — exactement le
    /// voisin qu'on lui reproche. Et ces 552 ms ne produisaient rien : le compteur est un cumul, un
    /// seul <c>NextValue()</c> sur un compteur neuf rend toujours <b>0</b>, donc chaque voisin
    /// affichait « 0 % de carte graphique », y compris Firefox et le gestionnaire de fenetres.
    /// <c>ReadCategory()</c> lit les 637 instances en <b>1,4 ms</b>, et deux lectures encadrant la
    /// fenetre donnent une vraie part. 400 fois moins cher, et juste au lieu de faux.</summary>
    private static Dictionary<string, long> OccupationGpu()
    {
        var cumul = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            if (!System.Diagnostics.PerformanceCounterCategory.Exists("GPU Engine")) return cumul;
            var lot = new System.Diagnostics.PerformanceCounterCategory("GPU Engine").ReadCategory();
            foreach (System.Diagnostics.InstanceData donnee in lot["Utilization Percentage"].Values)
            {
                cumul[donnee.InstanceName] = donnee.RawValue;
            }
        }
        catch
        {
            cumul.Clear(); // compteurs indisponibles : on ne devine pas une part GPU.
        }

        return cumul;
    }

    /// <summary>La part de GPU par PID sur la fenetre : pour chaque moteur, la difference des deux
    /// cumuls rapportee a la duree, puis, par PID, le moteur le PLUS OCCUPE. Un moteur absent de l'une
    /// des deux lectures n'a pas de part : on ne devine pas.
    ///
    /// 26/09/2026 : les moteurs d'un meme PID etaient SOMMES. Le Gestionnaire des taches, dont ce chiffre
    /// reprend la source et le sens, montre le moteur le plus occupe (« the utilization of the busiest
    /// engine », devblogs.microsoft.com/directx/gpus-in-the-task-manager). Une somme gonfle la part de
    /// tout processus qui occupe plusieurs moteurs a la fois (rendu 3D et encodage video d'un logiciel
    /// de diffusion, rendu et calcul asynchrone d'un jeu), jusqu'a depasser 100 %.</summary>
    internal static Dictionary<int, double> PartsGpu(
        Dictionary<string, long> debut, Dictionary<string, long> fin, TimeSpan duree)
    {
        var parts = new Dictionary<int, double>();
        if (debut.Count == 0 || fin.Count == 0 || duree <= TimeSpan.Zero) return parts;
        foreach (var (moteur, apres) in fin)
        {
            if (!debut.TryGetValue(moteur, out var avant)) continue;
            var morceaux = moteur.Split('_');
            if (morceaux.Length < 2 || !morceaux[0].Equals("pid", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(morceaux[1], out var pid)) continue;
            // Les cumuls sont en centaines de nanosecondes, comme les ticks d'un TimeSpan.
            var part = Math.Max(0, apres - avant) / (double)duree.Ticks * 100.0;
            parts[pid] = parts.TryGetValue(pid, out var deja) ? Math.Max(deja, part) : part;
        }

        return parts;
    }

    /// <summary>Chaque processus et son parent, d'un instantané Toolhelp (CreateToolhelp32Snapshot, Process32FirstW /
    /// Process32NextW, Microsoft Learn). Lecture seule, sans droits. Échec : table vide.</summary>
    private static IReadOnlyDictionary<int, (int Parent, string Nom)> InstantaneToolhelp()
    {
        var table = new Dictionary<int, (int, string)>();
        var h = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return table;
        try
        {
            var e = new ProcessEntry32W { dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            for (var ok = Process32FirstW(h, ref e); ok; ok = Process32NextW(h, ref e))
                table[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, System.IO.Path.GetFileNameWithoutExtension(e.szExeFile));
        }
        finally { CloseHandle(h); }
        return table;
    }

    private const uint SnapProcess = 0x00000002; // TH32CS_SNAPPROCESS

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
