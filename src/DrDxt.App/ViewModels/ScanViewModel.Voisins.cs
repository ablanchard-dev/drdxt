using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Platform;

namespace DrDxt.App.ViewModels;

// 03/10/2026 (boucle 2D2P, P3.2b) : « Faire passer les programmes gourmands après le jeu ». Coupé par défaut ; le clic du
// joueur est le consentement, gardé entre deux lancements comme « Surveiller mes parties ». Pendant une partie, une lecture
// des voisins toutes les 5 s (CalmeurDeVoisins) ; à la fin de la partie, à l'arrêt et à la fermeture de DrDXT, chaque
// programme calmé retrouve sa priorité.
public partial class ScanViewModel
{
    [ObservableProperty] private bool _calmerVoisinsActif;
    [ObservableProperty] private string? _voisinsCalmes;
    private CancellationTokenSource? _calmerVoisins;
    private CalmeurDeVoisins? _calmeur;

    public string LabelCalmerTitre => _localize("Voisins.Calmer.Titre");
    public string LabelCalmerDescription => _localize("Voisins.Calmer.Description");
    public string LabelCalmerDescriptionCourte => CarteDeLaMachine.TitreEtSuite(LabelCalmerDescription).Titre;
    public string LabelCalmerDescriptionSuite => CarteDeLaMachine.TitreEtSuite(LabelCalmerDescription).Suite;
    public string LabelCalmerBascule => _localize("Voisins.Calmer.Libelle");   // 08/10/2026 (défaut 10) : l'état est dans l'interrupteur

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

    /// <summary>07/10/2026 (L-VOISINS) : le clic du joueur est daté au journal des changements (« voisins.calmer:on/off ») :
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
        // 08/10/2026 (revue) : l'interrupteur se coche tout seul au clic ; une erreur avant le changement d'état le laissait faux.
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

    /// <summary>07/10/2026 (L-VOISINS) : le calmeur est un levier de performance (il rend du processeur au jeu), pas de
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

    // ── 07/10/2026 (L-VOISINS) : proposé d'office, et la liste d'exclusion du joueur ─────────────────────────────
    private IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> _partiesPourProposition = [];

    /// <summary>Ce que les parties relevées disent des voisins : non lus, aucun, ou les programmes proposés.</summary>
    public string PropositionCalmer
    {
        get
        {
            var noms = CalmeurDeVoisins.AProposer(_partiesPourProposition, Exclus());
            if (noms is null) return _localize("Voisins.Proposition.NonLu");
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

    /// <summary>À la fermeture de DrDXT : rien ne reste abaissé derrière lui.</summary>
    public void RendreLesPriorites() => _calmeur?.Rendre();

    /// <summary>Revue F-26 : ce processus est-il un jeu possible (ni navigateur, ni lecteur, ni Windows) ? Le filtre F-5
    /// de la veille des parties ; injectable pour les tests.</summary>
    internal Func<int, bool> EstUnJeu { get; set; } =
        pid => NomExeDuProcessus(pid) is { } exe && DrDxt.Core.NomExeDeJeu.EstUnJeuPossible(exe);

    internal async Task CalmerPendantLesPartiesAsync(CancellationToken ct)
    {
        _calmeur = new CalmeurDeVoisins(Priorites, Exclus());
        var enPartie = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Revue F-26 : le même filtre que la veille des parties (F-5) ; une vidéo en plein écran n'est pas une
                // partie, et les programmes à côté (un encodage, un téléchargement) n'ont pas à passer sous la normale.
                var pid = _gameLocator.LocateRunningGamePid() is { } trouve && trouve > 0 && EstUnJeu(trouve) ? trouve : 0;
                if (pid > 0)
                {
                    enPartie = true;
                    var vus = await Task.Run(() => _voisins.Lire(pid, ct), ct).ConfigureAwait(true);
                    if (vus is not null && _calmeur.Lecture(vus, pid, PidPremierPlan()) is { Count: > 0 } calmes)
                        VoisinsCalmes = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                            _localize("Voisins.Calmer.Fait"), string.Join(", ", calmes));
                }
                else if (enPartie)
                {
                    enPartie = false;
                    _calmeur.Rendre();
                }
                await Task.Delay(PasCalmer, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { /* arrêt demandé */ }
        finally { _calmeur.Rendre(); }
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
}
