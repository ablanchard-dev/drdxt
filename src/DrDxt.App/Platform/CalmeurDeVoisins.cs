using System.Diagnostics;

namespace DrDxt.App.Platform;

/// <summary>Ce que le calmeur demande au système : lire et poser la priorité d'un processus.</summary>
public interface IPrioritesDeProcessus
{
    /// <summary>La priorité actuelle, ou <c>null</c> si le processus est fermé ou protégé.</summary>
    ProcessPriorityClass? Lire(int pid);

    /// <summary>Pose la priorité ; faux si le processus est fermé ou protégé (rien n'est forcé).</summary>
    bool Poser(int pid, ProcessPriorityClass priorite);
}

/// <summary>
/// 03/10/2026 (boucle 2D2P, P3.2) : l'idée de ProBalance (Process Lasso, doc Bitsum) sans le reste. Pendant une partie, un
/// programme d'arrière-plan qui accapare le processeur passe en priorité « en dessous de la normale » ; il garde du
/// processeur, il passe seulement après le jeu. Sa priorité d'origine lui est rendue à la fin de la partie.
///
/// Garde-fous : seulement s'il dépasse le seuil à deux lectures de suite (un pic d'une lecture ne suffit pas) ; jamais
/// le jeu ni la fenêtre au premier plan ; jamais un processus du système ou de sécurité ; jamais un processus déjà
/// abaissé par quelqu'un d'autre (seule une priorité « normale » est touchée). Coupé par défaut : rien ne se passe sans
/// l'accord du joueur. Limite assumée : si DrDXT est tué en pleine partie, le voisin garde sa priorité basse jusqu'à son
/// prochain redémarrage (Windows ne la mémorise pas).
/// </summary>
public sealed class CalmeurDeVoisins
{
    /// <summary>Un quart de cœur tenu sur deux lectures : assez pour peser sur un jeu limité par le processeur.</summary>
    public const double SeuilPourcentDUnCoeur = 25;

    private static readonly HashSet<string> Intouchables = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "audiodg", "explorer", "MsMpEng", "MpDefenderCoreService", "SecurityHealthService", "fontdrvhost", "ctfmon",
        "DrDxt.App", "DrDxt.ElevatedHelper", "DrDxt.ReconcilerHost",
        // 08/10/2026 (vu sur capture : WmiPrvSE proposé) : les hôtes de Windows qui travaillent en arrière-plan. Limite : la
        // liste est par nom (les voisins relevés ne portent pas leur chemin) ; un hôte absent d'ici n'est pas reconnu.
        "WmiPrvSE", "WmiApSrv", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "TiWorker", "TrustedInstaller",
        "MoUsoCoreWorker", "usocoreworker", "wuauclt", "msiexec", "spoolsv", "sppsvc", "RuntimeBroker", "conhost", "dllhost",
        "taskhostw", "sihost", "WUDFHost", "backgroundTaskHost", "CompPkgSrv", "SgrmBroker", "NisSrv", "LsaIso",
        "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "SearchHost",
        // 07/10/2026 (L-VOISINS) : les antitriches connus. Toucher au processus d'un antitriche, même sa seule priorité, peut
        // passer pour une manipulation ; un nom absent de cette liste reste protégé par le mot « anticheat » (Protege).
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "vgc", "vgtray", "EAAntiCheat.GameService",
        "EAAntiCheat.GameServiceLauncher", "FACEIT", "FACEITService", "PnkBstrA", "PnkBstrB", "GameMon", "GameMon64",
    };

    /// <summary>Jamais touché, quoi qu'en dise le joueur : le système, la sécurité, DrDXT, les antitriches.</summary>
    public static bool Protege(string nom)
        => Intouchables.Contains(nom) || nom.Contains("anticheat", StringComparison.OrdinalIgnoreCase);

    /// <summary>07/10/2026 (L-VOISINS) : les programmes à proposer au calmeur, lus dans les parties relevées : ceux qui y ont
    /// tenu plus d'un quart de cœur sur deux relevés (<see cref="DrDxt.Application.Parties.VoisinEnPartie"/>, même seuil
    /// que le calmeur), hors protégés et hors ce que le joueur a exclu. <c>null</c> = aucune partie dont les voisins ont été
    /// lus : rien à dire, jamais une proposition inventée. Vide = lus, aucun à calmer.</summary>
    public static IReadOnlyList<string>? AProposer(IEnumerable<DrDxt.Application.Parties.PartieRelevee> parties, IEnumerable<string> exclus)
    {
        var lues = parties.Where(p => p.Voisins is not null).ToList();
        if (lues.Count == 0) return null;
        var sauf = new HashSet<string>(exclus, StringComparer.OrdinalIgnoreCase);
        return lues.SelectMany(p => p.Voisins!).OrderByDescending(v => v.PourcentMax).Select(v => v.Nom)
            .Where(n => !Protege(n) && !sauf.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private readonly IPrioritesDeProcessus _systeme;
    private readonly Dictionary<int, ProcessPriorityClass> _abaisses = new();
    private HashSet<int> _auDessusALaDerniereLecture = new();
    private readonly HashSet<string> _exclus;

    /// <param name="exclus">07/10/2026 (L-VOISINS) : les programmes que le joueur a exclus ; jamais touchés.</param>
    public CalmeurDeVoisins(IPrioritesDeProcessus systeme, IEnumerable<string>? exclus = null)
    {
        _systeme = systeme;
        _exclus = new HashSet<string>(exclus ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Les processus calmés en ce moment.</summary>
    public IReadOnlyCollection<int> Abaisses => _abaisses.Keys;

    /// <summary>Une lecture des voisins pendant la partie. Rend les noms des programmes calmés à cette lecture.</summary>
    public IReadOnlyList<string> Lecture(IEnumerable<VoisinDuJeu> vus, int pidDuJeu, int pidPremierPlan)
    {
        var calmes = new List<string>();
        var auDessus = new HashSet<int>();
        foreach (var v in vus)
        {
            if (v.Pid <= 4 || v.Pid == pidDuJeu || v.Pid == pidPremierPlan || Protege(v.Nom) || _exclus.Contains(v.Nom)) continue;
            if (v.ProcesseurPourcent < SeuilPourcentDUnCoeur) continue;
            auDessus.Add(v.Pid);
            if (!_auDessusALaDerniereLecture.Contains(v.Pid) || _abaisses.ContainsKey(v.Pid)) continue;
            if (_systeme.Lire(v.Pid) != ProcessPriorityClass.Normal) continue;
            if (_systeme.Poser(v.Pid, ProcessPriorityClass.BelowNormal))
            {
                _abaisses[v.Pid] = ProcessPriorityClass.Normal;
                calmes.Add(v.Nom);
            }
        }
        _auDessusALaDerniereLecture = auDessus;
        return calmes;
    }

    /// <summary>Fin de partie : chaque programme calmé retrouve sa priorité d'origine, s'il tourne encore et si personne
    /// ne l'a changée entre-temps.</summary>
    public void Rendre()
    {
        foreach (var (pid, origine) in _abaisses)
            if (_systeme.Lire(pid) == ProcessPriorityClass.BelowNormal)
                _systeme.Poser(pid, origine);
        _abaisses.Clear();
        _auDessusALaDerniereLecture.Clear();
    }
}

/// <summary>Le vrai système, par <see cref="Process"/>.</summary>
public sealed class PrioritesWindows : IPrioritesDeProcessus
{
    public ProcessPriorityClass? Lire(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.PriorityClass; }
        catch { return null; }
    }

    public bool Poser(int pid, ProcessPriorityClass priorite)
    {
        try { using var p = Process.GetProcessById(pid); p.PriorityClass = priorite; return true; }
        catch { return false; }
    }
}
