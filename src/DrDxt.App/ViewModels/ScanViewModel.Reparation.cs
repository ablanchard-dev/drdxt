using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;
using Geste = DrDxt.App.Platform.CorruptionDuMagasin.Geste;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

/// <summary>La carte Agir : la réparation des fichiers de Windows et la pause du maintien.</summary>
public sealed partial class ScanViewModel
{
    public bool ReparationVisible => _reparationARelancer;
    public bool ReparationAOuvrir => _reparationARelancer && !ReparationLancee;
    partial void OnReparationLanceeChanged(bool value) => OnPropertyChanged(nameof(ReparationAOuvrir));
    public string LabelReparationTitre => _localize(_geste switch { Geste.Reinstaller => "Reparation.Windows.Reinstallation.Titre", _ => "Reparation.Windows.Titre" });
    public string LabelReparationDescription => _localize(_geste switch { Geste.Reinstaller => "Reparation.Windows.Reinstallation.Description", _ => "Reparation.Windows.Description" });

    // La réparation a échoué faute de source. Relancer RestoreHealth tournerait en rond : le geste devient la
    // réinstallation de Microsoft (CorruptionDuMagasin), jamais un nettoyage irréversible.
    private Geste _geste;

    /// <summary>Lu dans CBS.log et WinSxS : l'appli pose la vraie lecture au démarrage, les tests un faux. Faux quand
    /// rien n'est lisible : le bouton garde la réparation.</summary>
    internal Func<Geste> CorruptionSansSource { get; set; } = () => Geste.Reparer;

    private void PasserAuGeste(Geste geste)
    {
        _geste = geste;
        OnPropertyChanged(nameof(LabelReparationTitre));
        OnPropertyChanged(nameof(LabelReparationDescription));
    }

    private Geste SansSource()
    {
        try { return CorruptionSansSource(); }
        catch (Exception) { return Geste.Reparer; }
    }
    public string LabelReparationMaintenir => _localize("Reparation.Windows.Maintenir");
    public bool CarteWindowsVisible => BcdVisible || ReparationVisible || PauseMaintienVisible;

    // ── La carte « Agir » du domaine windows : la pause du maintien du service de demarrage ──
    // Le fichier de pause est sous %ProgramData%\DrDxt : le helper l'ecrit, l'App le relit apres chaque geste.
    // La fenetre s'affiche avant ce branchement : le setter previent la carte.
    internal Func<bool>? ServiceInstalle
    {
        get => _serviceInstalle;
        set { _serviceInstalle = value; OnPropertyChanged(nameof(PauseMaintienVisible)); OnPropertyChanged(nameof(CarteWindowsVisible)); }
    }
    private Func<bool>? _serviceInstalle;
    internal Func<DrDxt.Reconciler.EtatDeLaPause>? LirePauseDuMaintien
    {
        get => _lirePauseDuMaintien;
        set { _lirePauseDuMaintien = value; SignalerPauseMaintien(); }
    }
    private Func<DrDxt.Reconciler.EtatDeLaPause>? _lirePauseDuMaintien;

    [ObservableProperty] private string? _erreurPauseMaintien;
    [ObservableProperty] private bool _pauseMaintienEnCours;

    public bool PauseMaintienVisible => _helper is not null && ServiceInstalle?.Invoke() == true;
    public bool MaintienEnPause => LirePauseDuMaintien?.Invoke() == DrDxt.Reconciler.EtatDeLaPause.EnPause;
    // Un fichier de pause qui n'est pas aux administrateurs : le service s'arrete (l'echec sur), l'App dit le doute.
    public bool PauseIncertaine => LirePauseDuMaintien?.Invoke() == DrDxt.Reconciler.EtatDeLaPause.NonFiable;
    public string LabelPauseMaintienTitre => _localize("Maintien.Pause.Titre");
    public string LabelPauseMaintienDescription => _localize("Maintien.Pause.Description");
    public bool PauseMaintienEtatVisible => MaintienEnPause || PauseIncertaine;
    public string LabelPauseMaintienEtat => _localize(PauseIncertaine ? "Maintien.Pause.Incertaine" : "Maintien.Pause.EnPause");
    public string LabelPauseMaintienBascule => _localize(PauseIncertaine ? "Maintien.Pause.Remettre"
        : MaintienEnPause ? "Maintien.Pause.Reprendre" : "Maintien.Pause.Poser");
    public string LabelPauseMaintienRetirer => _localize("Maintien.Pause.Retirer");

    // Incertaine, la bascule repose la pause : le geste qui fait le moins.
    [RelayCommand]
    private Task BasculerPauseMaintien() => PoserPauseMaintienAsync(PauseIncertaine || !MaintienEnPause);

    [RelayCommand]
    private Task RetirerPauseMaintien() => PoserPauseMaintienAsync(false);

    private async Task PoserPauseMaintienAsync(bool enPause)
    {
        if (PauseMaintienEnCours || _helper is null) return;
        PauseMaintienEnCours = true;
        ErreurPauseMaintien = null;
        try
        {
            var r = await _helper.ExecuteAsync(new SetMaintenancePauseOp(enPause), CancellationToken.None);
            if (!r.IsOk)
                ErreurPauseMaintien = RaisonLisible.Echec(r, _localize("Maintien.Pause.Echec"), _localize);
        }
        finally
        {
            PauseMaintienEnCours = false;
            SignalerPauseMaintien();
        }
    }

    private void SignalerPauseMaintien()
    {
        OnPropertyChanged(nameof(MaintienEnPause));
        OnPropertyChanged(nameof(PauseIncertaine));
        OnPropertyChanged(nameof(PauseMaintienEtatVisible));
        OnPropertyChanged(nameof(LabelPauseMaintienEtat));
        OnPropertyChanged(nameof(LabelPauseMaintienBascule));
    }

    /// <summary>« en cours » tant que la console est ouverte, puis ce que la relecture de
    /// Windows trouve à sa fermeture : sans elle, rien ne reviendrait dans DrDXT et le joueur resterait sans réponse.</summary>
    [ObservableProperty] private string? _etatReparation;
    internal Task? FinDeReparation { get; private set; }

    /// <summary>Le lanceur : ShellExecute « runas » par defaut ; rend la fin de la console, <c>null</c> si rien
    /// n'a ete lance. Une couture pour les tests.</summary>
    internal Func<System.Diagnostics.ProcessStartInfo, Task?> LancerConsole { get; set; } = psi =>
    {
        var p = System.Diagnostics.Process.Start(psi);
        return p is null ? null : p.WaitForExitAsync().ContinueWith(_ => p.Dispose(), TaskScheduler.Default);
    };

    // ── La preuve du repos de la carte, conclue au lancement ──────
    /// <summary>Ce que la mesure du repos a décidé, dit sur la page Carte graphique. <c>null</c> = rien n'attendait.</summary>
    [ObservableProperty] private string? _verdictReposCarte;

    private async Task AttendreFinDeReparation(Task console)
    {
        await console;
        RepairFinding? relu = null;
        try { relu = await Task.Run(RelireSanteWindows); }
        catch (Exception) { /* une relecture ratee se dit « non relu », jamais en silence */ }
        var geste = relu?.Statut == RepairStatus.Suspicious ? SansSource() : Geste.Reparer;
        var cle = relu?.Statut switch
        {
            RepairStatus.ExpectedDefault => "Reparation.Windows.Terminee.Sain",
            RepairStatus.Suspicious when geste == Geste.Reinstaller => "Reparation.Windows.Terminee.Reinstaller",
            RepairStatus.Suspicious => "Reparation.Windows.Terminee.Abime",
            _ => "Reparation.Windows.Terminee.NonRelu",
        };
        EtatReparation = _localize(cle);
        PasserAuGeste(geste);
        // Encore abime ou non relu : la carte se rouvre, le joueur peut relancer.
        if (relu?.Statut != RepairStatus.ExpectedDefault) ReparationLancee = false;
    }

    /// <summary>La commande de la console. Chemins absolus tires de l'API, jamais du PATH ni de
    /// $env:SystemRoot (tous deux reglables par l'utilisateur, et la console est elevee) ; -NoProfile
    /// pour ne pas executer en administrateur le profil PowerShell de l'utilisateur.
    /// <para>Installée, la console passe par le lanceur natif (environnement reconstruit,
    /// commande fixe écrite dans le lanceur) ; sans lanceur à côté de l'App (build de développement), elle s'ouvre seule.</para></summary>
    internal static System.Diagnostics.ProcessStartInfo ConsoleDeReparation(string system32, string? lanceur = null)
    {
        if (lanceur is not null && File.Exists(lanceur))
        {
            var parLanceur = new System.Diagnostics.ProcessStartInfo(lanceur) { UseShellExecute = true, Verb = "runas" };
            parLanceur.ArgumentList.Add(DrDxt.Lanceur.EnvironnementPropre.ReparerWindows);
            return parLanceur;
        }
        var (fichier, arguments) = DrDxt.Lanceur.EnvironnementPropre.ConsoleDeReparation(system32);
        return new System.Diagnostics.ProcessStartInfo(fichier, arguments) { UseShellExecute = true, Verb = "runas" };
    }

    private void ArmerReparation(RepairTriage triage)
    {
        _reparationARelancer = triage.Attention.Any(x => x.Domaine == "STABILITE" && x.Nom == "magasin de composants"
                                                          && x.Statut == RepairStatus.Suspicious);
        PasserAuGeste(_reparationARelancer ? SansSource() : Geste.Reparer);
        OnPropertyChanged(nameof(ReparationVisible));
        OnPropertyChanged(nameof(ReparationAOuvrir));
        OnPropertyChanged(nameof(CarteWindowsVisible));
    }

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private void LancerReparation()
    {
        if (!_reparationARelancer || ReparationLancee) return;
        ErreurReparation = null;
        try
        {
            if (_geste == Geste.Reinstaller)
            {
                // La page Récupération de Windows : « Réinstaller maintenant » garde fichiers et applications. Rien à
                // attendre ici, Windows mène la suite et redémarre.
                LancerConsole(new System.Diagnostics.ProcessStartInfo("ms-settings:recovery") { UseShellExecute = true });
                EtatReparation = _localize("Reparation.Windows.Reinstallation.Ouverte");
                return;
            }
            var console = LancerConsole(ConsoleDeReparation(Environment.GetFolderPath(Environment.SpecialFolder.System),
                Path.Combine(AppContext.BaseDirectory, DrDxt.App.Platform.RelanceElevee.Lanceur)));
            ReparationLancee = console is not null;
            if (console is null)
            {
                EtatReparation = _localize("Reparation.Windows.NonLancee");
                return;
            }
            EtatReparation = _localize("Reparation.Windows.Lancee");
            FinDeReparation = AttendreFinDeReparation(console);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED : le joueur a refuse la fenetre UAC. Ce n'est pas une panne.
            ErreurReparation = _localize("Reparation.Windows.Refusee");
        }
        catch (Exception ex)
        {
            ErreurReparation = $"{_localize("Reparation.Windows.Echec")}{Docteur.DeuxPoints}{RaisonLisible.Dire(ex, _localize)}";
        }
    }

    /// <summary>Le choix du joueur, garde entre deux lancements. <c>null</c> = rien n'est
    /// garde (les tests ; l'appli pose le fichier reel au demarrage).</summary>
    internal DrDxt.Application.Network.PlafondEnvoiMemorise? MemoirePlafond { get; set; }
}
