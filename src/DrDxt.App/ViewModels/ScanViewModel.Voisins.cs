using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Platform;

namespace DrDxt.App.ViewModels;

// « Faire passer les programmes gourmands après le jeu ». Coupé par défaut ; le clic du
// joueur est le consentement, gardé entre deux lancements comme « Surveiller mes parties ». Pendant une partie, une lecture
// des voisins toutes les 5 s (CalmeurDeVoisins) ; à la fin de la partie, à l'arrêt et à la fermeture de DrDXT, chaque
// programme calmé retrouve sa priorité.
public partial class ScanViewModel
{
    [ObservableProperty] private bool _calmerVoisinsActif;
    [ObservableProperty] private string? _voisinsCalmes;
    private CancellationTokenSource? _calmerVoisins;
    // Les calmeurs des boucles en cours : éteint puis rallumé pendant une lecture, l'ancien tourne encore et garde ce qu'il a
    // abaissé.
    private readonly HashSet<CalmeurDeVoisins> _calmeurs = [];

    public string LabelCalmerTitre => _localize("Voisins.Calmer.Titre");
    public string LabelCalmerDescription => _localize("Voisins.Calmer.Description");
    public string LabelCalmerDescriptionCourte => CarteDeLaMachine.TitreEtSuite(LabelCalmerDescription).Titre;
    public string LabelCalmerDescriptionSuite => CarteDeLaMachine.TitreEtSuite(LabelCalmerDescription).Suite;
    public string LabelCalmerBascule => _localize("Voisins.Calmer.Libelle");   // L'état est dans l'interrupteur

    /// <summary>Le choix du joueur, gardé entre deux lancements. <c>null</c> = rien n'est gardé (les tests).</summary>
    internal DrDxt.Application.Network.VeillePingMemorisee? MemoireCalmer { get; set; }

    /// <summary>Le système qui pose les priorités. Remplaçable par les tests : rien n'est posé sur leur machine.</summary>
    internal IPrioritesDeProcessus Priorites { get; set; } = new PrioritesWindows();

    internal TimeSpan PasCalmer { get; set; } = TimeSpan.FromSeconds(5);
    internal Func<int> PidPremierPlan { get; set; } = PremierPlanWindows;

    partial void OnCalmerVoisinsActifChanged(bool value)
    {
        OnPropertyChanged(nameof(PropositionCalmer));
        OnPropertyChanged(nameof(CalmeurPropose));
    }

    /// <summary>Le clic du joueur est daté au journal des changements (« voisins.calmer:on/off ») :
    /// la bascule se lit sur la carte des parties et se juge sur les parties d'avant et d'après, comme HAGS.
    /// La commande tourne tant que le calmeur est allumé : sans exécutions simultanées, le bouton « Arrêter » restait grisé
    /// pendant tout ce temps, et le calmeur ne s'arrêtait pas depuis l'écran.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task BasculerCalmerVoisins()
    {
        try
        {
            if (CalmerVoisinsActif)
            {
                EteindreCalmeur();
                NoterCalmeur(false);
                return;
            }
            NoterCalmeur(true);
            await AllumerCalmeurAsync();
        }
        // L'interrupteur se coche tout seul au clic ; une erreur avant le changement d'état le laissait faux.
        finally { OnPropertyChanged(nameof(CalmerVoisinsActif)); }
    }

    private async Task AllumerCalmeurAsync()
    {
        _calmerVoisins = new CancellationTokenSource();
        CalmerVoisinsActif = true;
        MemoireCalmer?.Allumer();
        await CalmerPendantLesPartiesAsync(_calmerVoisins.Token);
    }

    private void EteindreCalmeur()
    {
        _calmerVoisins?.Cancel();
        CalmerVoisinsActif = false;
        MemoireCalmer?.Eteindre();
    }

    internal const string CleCalmeur = "voisins.calmer";

    private void NoterCalmeur(bool allume)
    {
        if (JournalChangements is null) return;
        JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(Horloge(),
            string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Changement.Reglage"),
                _localize("Reglage.voisins.calmer"), _localize(allume ? "Reglage.Eteint" : "Reglage.Allume"),
                _localize(allume ? "Reglage.Allume" : "Reglage.Eteint")),
            null, CleCalmeur + (allume ? ":on" : ":off")));
        Afficher(MemoireParties?.Lire() ?? []);
    }

    /// <summary>Le calmeur est un levier de performance (il rend du processeur au jeu), pas de
    /// fiabilité : si sa dernière bascule est « allumé » et qu'elle est jugée « moins bien en jeu » sur les parties (le même
    /// bilan borné que l'écran), il s'éteint seul, et l'arrêt est daté à son tour.</summary>
    internal IReadOnlyList<string> RetirerLeCalmeurSIlCoute(IReadOnlyList<DrDxt.Application.Parties.Changement> journal,
        IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> parties)
    {
        if (!CalmerVoisinsActif) return [];
        var derniere = journal.Where(c => c.Cle?.StartsWith(CleCalmeur + ":", StringComparison.Ordinal) == true)
            .OrderByDescending(c => c.Quand).FirstOrDefault();
        if (derniere?.Cle != CleCalmeur + ":on") return [];
        if (BilansBornes(journal, parties).FirstOrDefault(x => x.C == derniere).B?.Verdict
            != DrDxt.Application.Parties.VerdictEnJeu.Pire) return [];
        EteindreCalmeur();
        NoterCalmeur(false);
        return [string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Changement.Retire"), derniere.Libelle)];
    }

    /// <summary>Au lancement : rallume le choix que le joueur avait laissé allumé (sans le dater une seconde fois).</summary>
    public void RallumerCalmerVoisins()
    {
        if (MemoireCalmer?.EstAllumee() == true && !CalmerVoisinsActif)
            _ = AllumerCalmeurAsync();
    }

    // ── Proposé d'office, et la liste d'exclusion du joueur ─────────────────────────────
    private IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> _partiesPourProposition = [];

    /// <summary>Ce que les parties relevées disent des voisins : non lus, aucun, ou les programmes proposés.</summary>
    public string PropositionCalmer
    {
        get
        {
            var noms = CalmeurDeVoisins.AProposer(_partiesPourProposition, Exclus());
            // Aucune partie relevée : « allume le relevé » seulement s'il est éteint ; allumé (le cas par défaut), rien n'est
            // encore lu.
            if (noms is null) return _localize(MemoireParties is { } m && !m.EstAllume() ? "Voisins.Proposition.NonLu" : "Voisins.Proposition.PasEncore");
            if (noms.Count == 0) return _localize("Voisins.Proposition.Aucun");
            return string.Format(System.Globalization.CultureInfo.CurrentCulture,
                _localize(CalmerVoisinsActif ? "Voisins.Proposition.Actif" : "Voisins.Proposition"), string.Join(", ", noms));
        }
    }

    /// <summary>Vrai quand les parties nomment un voisin à calmer et que le calmeur est coupé : la carte le met en avant.</summary>
    public bool CalmeurPropose => !CalmerVoisinsActif && CalmeurDeVoisins.AProposer(_partiesPourProposition, Exclus()) is { Count: > 0 };

    private void ProposerDepuis(IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> parties)
    {
        _partiesPourProposition = parties;
        OnPropertyChanged(nameof(PropositionCalmer));
        OnPropertyChanged(nameof(CalmeurPropose));
    }

    /// <summary>Les programmes que le joueur exclut, séparés par des virgules ; gardés entre deux lancements.</summary>
    [ObservableProperty] private string _voisinsExclus = "";

    /// <summary>Le fichier de la liste d'exclusion. <c>null</c> = rien n'est gardé (les tests).</summary>
    internal DrDxt.Core.FichierJsonAtomique? FichierExclus { get; set; }

    public string LabelExclusTitre => _localize("Voisins.Exclus.Titre");
    public string LabelExclusProteges => _localize("Voisins.Exclus.Proteges");

    internal IReadOnlyList<string> Exclus()
        => VoisinsExclus.Split([',', ';', '\n'],StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).ToList();

    /// <summary>Au lancement : la liste gardée. Un fichier absent ou illisible : liste vide, rien d'exclu d'office.</summary>
    internal void ChargerExclus()
    {
        if (FichierExclus?.Read() is not { IsOk: true, Value: { } texte }) return;
        _chargementExclus = true;
        try { VoisinsExclus = texte.Trim(); }
        finally { _chargementExclus = false; }
    }

    private bool _chargementExclus;

    partial void OnVoisinsExclusChanged(string value)
    {
        if (!_chargementExclus) FichierExclus?.WriteAtomic(value);
        ProposerDepuis(_partiesPourProposition);
    }

    /// <summary>À la fermeture de DrDXT : rien ne reste abaissé derrière lui, quelle que soit la boucle qui l'a abaissé. Rendu
    /// sur place, sans attendre les boucles : leur suite passe par le fil d'interface, celui-là même qui ferme. Un calmeur
    /// rendu repart de zéro : il lui faut deux lectures pour abaisser à nouveau.</summary>
    public void RendreLesPriorites()
    {
        CalmeurDeVoisins[] tous;
        lock (_calmeurs) tous = [.. _calmeurs];
        foreach (var calmeur in tous) calmeur.Rendre();
    }

    /// <summary>Ce processus est-il un jeu possible (ni navigateur, ni lecteur, ni Windows) ? Le filtre F-5
    /// de la veille des parties ; injectable pour les tests.</summary>
    internal Func<int, bool> EstUnJeu { get; set; } =
        pid => NomExeDuProcessus(pid) is { } exe && DrDxt.Core.NomExeDeJeu.EstUnJeuPossible(exe);

    internal async Task CalmerPendantLesPartiesAsync(CancellationToken ct)
    {
        var calmeur = new CalmeurDeVoisins(Priorites, Exclus());
        lock (_calmeurs) _calmeurs.Add(calmeur);
        var enPartie = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Le même filtre que la veille des parties ; une vidéo en plein écran n'est pas une
                // partie, et les programmes à côté (un encodage, un téléchargement) n'ont pas à passer sous la normale.
                var pid = _gameLocator.LocateRunningGamePid() is { } trouve && trouve > 0 && EstUnJeu(trouve) ? trouve : 0;
                if (pid > 0)
                {
                    enPartie = true;
                    var vus = await Task.Run(() => _voisins.Lire(pid, ct), ct).ConfigureAwait(true);
                    if (vus is not null && calmeur.Lecture(vus, pid, PidPremierPlan()) is { Count: > 0 } calmes)
                        VoisinsCalmes = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                            _localize("Voisins.Calmer.Fait"), string.Join(", ", calmes));
                }
                else if (enPartie)
                {
                    enPartie = false;
                    calmeur.Rendre();
                }
                await Task.Delay(PasCalmer, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { /* arrêt demandé */ }
        catch (Exception)
        {
            // Sans ceci, une erreur imprévue arrêterait la boucle en silence, l'interrupteur resté sur « allumé ». Le choix du
            // joueur reste noté : le prochain lancement le rallume.
            CalmerVoisinsActif = false;
            DireAuDemarrage(_localize("Voisins.Calmer.ArreteSurErreur"));
        }
        finally
        {
            calmeur.Rendre();
            lock (_calmeurs) _calmeurs.Remove(calmeur);
        }
    }

    private static int PremierPlanWindows()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // ── Ce qui tournait a cote, et la marge ──────────────────────────────────────
    //
    // Un scan a affiche « 164 FPS » pendant qu'on regardait l'ecran de DrDXT, le jeu en
    // arriere-plan. Le meme soir, en partie reelle avec nos compilations sur son processeur : 79,5.
    // Un FPS moyen seul est trompeur. L'ecran montre donc, a cote du chiffre, sa marge d'erreur (que
    // BenchmarkResult calcule deja) et la liste courte de ce qui consommait en meme temps.

    /// <summary>Ce qui tournait a cote du jeu pendant la mesure, une ligne lisible par voisin. Liste
    /// Vide = on n'a rien vu au-dessus du seuil, ou on n'a pas su regarder. <see cref="VoisinsConnus"/>
    /// dit lequel des deux, pour que l'ecran ne fasse jamais passer « on ne sait pas » pour « rien ».</summary>
    public ObservableCollection<string> VoisinsDuJeu { get; } = new();

    /// <summary>Vrai des qu'au moins un voisin notable a ete vu. Faux = rien a montrer.</summary>
    [ObservableProperty]
    private bool _voisinsConnus;

    /// <summary>La phrase d'en-tete de la liste, ou la phrase qui dit qu'on n'a rien vu de notable.</summary>
    [ObservableProperty]
    private string? _voisinsResume;

    public string LabelVoisins => _localize("Scan.Voisins.Header");
    public string LabelVoisinsTitre => _localize("Scan.Voisins.Titre");
    public string LabelConsigneScene => _localize("Scan.Scene.Consigne");

    /// <summary>Le FPS moyen avec sa marge, dans le meme champ : « 148 ± 3,2 ». Jamais un chiffre
    /// seul : c'est la demande produit, et c'est la seule forme honnete d'une mesure.</summary>
    [ObservableProperty]
    private string _avgFpsAvecMarge = Dash;

    /// <summary>Le 1 % low avec sa marge (`P1LowMarginOfError`) : il varie plus d'une passe à l'autre que la
    /// moyenne, un chiffre seul tromperait. Même règle que le FPS moyen.</summary>
    [ObservableProperty]
    private string _p1LowAvecMarge = Dash;

    private void PeuplerLesVoisins(IReadOnlyList<DrDxt.App.Platform.VoisinDuJeu>? vus)
    {
        VoisinsDuJeu.Clear();
        // « pas pu lire » a sa propre phrase, au lieu d'une parenthèse sous « rien vu ».
        if (vus is null)
        {
            VoisinsConnus = false;
            VoisinsResume = _localize("Scan.Voisins.NonLus");
            return;
        }
        foreach (var v in vus)
            VoisinsDuJeu.Add(v.GpuPourcent is { } gpu
                ? string.Format(CultureInfo.CurrentUICulture, _localize("Scan.Voisins.LigneGpu"), v.Nom,
                    v.ProcesseurPourcent, gpu)
                : string.Format(CultureInfo.CurrentUICulture, _localize("Scan.Voisins.Ligne"), v.Nom,
                    v.ProcesseurPourcent));

        VoisinsConnus = VoisinsDuJeu.Count > 0;
        // Sans jeu lu, la liste ne dit pas « à côté du jeu ».
        var avecJeu = _gamePid != 0;
        VoisinsResume = VoisinsConnus
            ? _localize(avecJeu ? "Scan.Voisins.Header" : "Scan.Voisins.HeaderSansJeu")
            : _localize(avecJeu ? "Scan.Voisins.Aucun" : "Scan.Voisins.AucunSansJeu");
    }
}
