using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Profiles;
using DrDxt.Core;
using Geste = DrDxt.App.Platform.CorruptionDuMagasin.Geste;
using DrDxt.Detection.Hardware;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

// Les cartes « Agir » des domaines memoire, reseau et windows : les trois gestes que le joueur
// declenche depuis la carte de sa machine. Meme classe, fichier a part : le reste du VM est le scan.
public sealed partial class ScanViewModel
{
    /// <summary>Vrai quand un scan en jeu a mesure le bufferbloat : le mode peut s'allumer.</summary>
    public bool PlafondEnvoiDisponible => _plafondEnvoiMesure > 0 && _exeJeuMesure is not null && _helper is not null;
    public bool PlafondEnvoiActif => _modePlafond is not null || _porteeParLeService;

    // Un seul poseur. Le service de demarrage en marche porte la protection (il relit le reglage que
    // le helper vient d'ecrire) : l'App n'arme pas son propre mode, sinon, l'App fermee en pleine partie, son bail
    // expire et retire le plafond que le service croit encore pose. L'installeur livre l'App et le service de la
    // meme version : un service en marche sait porter la protection. <c>null</c> = pas de service (les tests).
    // La fenetre s'affiche avant ce branchement : le setter previent la carte, qui garderait sinon le texte lu a l'affichage.
    internal Func<bool>? ServiceDemarrageActif
    {
        get => _serviceDemarrageActif;
        set { _serviceDemarrageActif = value; OnPropertyChanged(nameof(LabelPingDescription)); OnPropertyChanged(nameof(LabelPingDescriptionCourte)); OnPropertyChanged(nameof(LabelPingDescriptionSuite)); }
    }
    private Func<bool>? _serviceDemarrageActif;
    private bool _porteeParLeService;

    /// <summary>Ce que la mesure en jeu retient : le plafond conseille (0 = pas de bufferbloat) et le
    /// jeu observe. Seul point d'entree, pour le scan comme pour les tests.</summary>
    internal void RetenirMesureEnvoi(long plafondBps, string? exe)
    {
        _plafondEnvoiMesure = plafondBps;
        _exeJeuMesure = exe;
        SignalerPlafondEnvoi();
    }

    /// <summary>Allume le plafond d'envoi automatique : pose au lancement du jeu,
    /// retrait a sa fermeture, bail du helper comme filet si l'appli meurt.</summary>
    public async Task<Result> ActiverPlafondEnvoiAsync()
    {
        // Le mode n'est retenu qu'apres l'attente du helper (UAC). Un second appel dans
        // cet intervalle (rallumage au lancement puis clic) ouvrait un second mode : en eteignant,
        // seule sa boucle s'arretait. Il attend maintenant l'activation en cours et en rend l'issue.
        if (_activationPlafond is { IsCompleted: false } enCours) return await enCours;
        var activation = ActiverPlafondEnvoiUneFoisAsync();
        _activationPlafond = activation;
        return await activation;
    }

    private Task<Result>? _activationPlafond;

    /// <summary>Les jeux dont le service porte le plafond (la règle du helper) ; couture de test : un jeu vivant de test.</summary>
    internal Func<string?, bool> JeuPorteParLeService { get; set; } = NomExeDeJeu.EstUnJeuConnu;

    private async Task<Result> ActiverPlafondEnvoiUneFoisAsync()
    {
        if (!PlafondEnvoiDisponible)
            return Result.Fail(ErrorCodes.TweakNotApplicable, Docteur.T("App.PasDeBufferbloat"));
        if (_modePlafond is not null || _porteeParLeService) return Result.Ok();
        // Le service ne porte que le plafond d'un jeu reconnu par son nom (le helper refuse les autres) : un autre jeu garde
        // la protection de l'App, comme sans service.
        if (ServiceDemarrageActif?.Invoke() == true && JeuPorteParLeService(_exeJeuMesure))
        {
            // Le service pose un plafond fixe. DrDXT ouvert, l'ajustement tourne encore et lui
            // passe chaque nouveau plafond ; il le repose au battement suivant.
            _porteeParLeService = true;
            var exe = _exeJeuMesure!;
            var plafond = _plafondEnvoiMesure;
            _battementPlafond = new CancellationTokenSource();
            // Jamais par _helper, qui se lance a la demande : une invite UAC en pleine partie, sans geste du joueur.
            if (HelperSansLancer is { } sansLancer)
                LancerAjustement(
                    () => DrDxt.Application.Network.PlafondEnvoiPendantLeJeu.ProcessusVivant(exe),
                    () => plafond,
                    async (apres, ct) =>
                    {
                        if (!(await sansLancer.ExecuteAsync(new RetainServiceUploadCapOp(exe, apres), ct)).IsOk)
                            return false;
                        plafond = apres;
                        return true;
                    },
                    _battementPlafond.Token);
            return Result.Ok();
        }

        var mode = new DrDxt.Application.Network.PlafondEnvoiPendantLeJeu(
            _helper!, JeuVivant,
            _exeJeuMesure!, _plafondEnvoiMesure);
        var demarre = await mode.DemarrerAsync(CancellationToken.None);
        if (!demarre.IsOk) return demarre;

        _modePlafond = mode;
        _battementPlafond = new CancellationTokenSource();
        var ct = _battementPlafond.Token;
        _ = Task.Run(async () =>
        {
            using var minuterie = new PeriodicTimer(TourDuPlafond);
            try
            {
                do
                {
                    await mode.TickAsync(ct);
                    DireLeFiletDuPlafond(mode.SansFilet);
                }
                while (await minuterie.WaitForNextTickAsync(ct));
            }
            catch (OperationCanceledException) { /* le mode a ete eteint */ }
        }, ct);

        LancerAjustement(() => mode.PlafondPose, () => mode.PlafondBps,
            async (apres, c) => (await mode.ChangerPlafondAsync(apres, c)).IsOk, ct);
        return Result.Ok();
    }

    /// <summary>Le battement ne relance plus le helper (aucune invite sans geste) : injoignable, le plafond ne se pose pas,
    /// et sans ce texte le joueur ne saurait pas pourquoi. Il part de lui-même dès qu'un battement rejoint le helper ; une
    /// autre erreur de la carte n'est jamais effacée par lui.</summary>
    private void DireLeFiletDuPlafond(bool sansFilet)
    {
        var texte = _localize("Ping.Protection.SansFilet");
        if (sansFilet) ErreurPlafondEnvoi = texte;
        else if (ErreurPlafondEnvoi == texte) ErreurPlafondEnvoi = null;
    }

    // La boucle fermee (AjustementDuPlafond) : plafond en place, une fenetre du
    // trafic reel, le meme juge que le scan, et le plafond suit. Jamais au-dessus de la capacite
    // vue au scan (plafond / FractionPlafond).
    private void LancerAjustement(Func<bool> pose, Func<long> actuel,
        Func<long, CancellationToken, Task<bool>> changer, CancellationToken ct)
    {
        if (CollecteurFenetre is { } collecter)
        {
            var max = (long)(_plafondEnvoiMesure / DrDxt.Measurement.Network.Bufferbloat.FractionPlafond);
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (!pose()) { await Task.Delay(TourDuPlafond, ct); continue; }
                        var fenetre = await collecter(FenetreAjustement, ct);
                        if (!pose()) continue;
                        var avant = actuel();
                        var apres = DrDxt.Measurement.Network.AjustementDuPlafond.Suivant(avant, max, fenetre);
                        if (apres != avant && await changer(apres, ct))
                        {
                            // La trace qui rend l'essai reel lisible : chaque changement, avec
                            // les deux p90 qui l'ont decide (journal de diagnostic, bouton d'export).
                            var v = DrDxt.Measurement.Network.Bufferbloat.Juger(fenetre);
                            JournalPlafond?.Change(IdJournalPlafond,
                                FormattableString.Invariant($"{avant / 1e6:0.#} Mbit/s"),
                                FormattableString.Invariant($"{apres / 1e6:0.#} Mbit/s (ping p90 repos {v.PingReposP90:0} ms, en charge {v.PingChargeP90:0} ms)"),
                                "DrDXT");
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch
                    {
                        // L'ajustement est un plus : sur un echec, le plafond en place reste, et on
                        // reessaie apres une pause plutot que de tourner a vide.
                        try { await Task.Delay(TourDuPlafond, ct); } catch (OperationCanceledException) { return; }
                    }
                }
            }, ct);
        }
    }

    /// <summary>Le releve envoi + ping pendant la partie (celui du scan, passif). <c>null</c> = plafond
    /// fixe, sans boucle fermee (les tests ; l'appli le pose au demarrage).</summary>
    internal Func<TimeSpan, CancellationToken, Task<IReadOnlyList<DrDxt.Measurement.Network.EchantillonEnvoi>>>? CollecteurFenetre { get; set; }

    /// <summary>Le tour de la boucle du plafond d'envoi (5 s). Réglable pour les tests : un tour fixe de 5 s faisait
    /// dépasser au test du plafond toute attente raisonnable sur un runner GitHub chargé (10 s, puis 30 s : 31 s mesurés).</summary>
    internal TimeSpan TourDuPlafond { get; set; } = TimeSpan.FromSeconds(5);

    internal TimeSpan FenetreAjustement { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Un client qui ne lance jamais le helper (pas d'invite UAC sans geste du joueur). Il
    /// porte l'ajustement quand le service tient la protection ; <c>null</c> = pas d'ajustement dans ce mode.</summary>
    internal IElevatedClient? HelperSansLancer { get; set; }

    /// <summary>Le service a-t-il deja une protection retenue sous %ProgramData% ? <c>null</c> = on
    /// ne sait pas (les tests), traite comme oui : rien n'est transmis au lancement.</summary>
    internal Func<bool>? ServicePorteUnRetenu { get; set; }

    /// <summary>Le jeu est-il vivant, pour poser le plafond. Couture : le test
    /// s'appuyait sur une vraie recherche de processus (le sien), la seule partie qu'il ne tenait pas sur un runner chargé.</summary>
    internal Func<string, bool> JeuVivant { get; set; } = DrDxt.Application.Network.PlafondEnvoiPendantLeJeu.ProcessusVivant;

    public const string IdJournalPlafond = "network.upload-cap";

    // ── La veille du ping ─────────────────────────────────────────────────────
    // Sans scan en partie saturee, la protection restait grisee. La veille s'allume sans mesure : elle
    // juge les vraies parties (VeilleDuPing), et n'allume la protection que sur un verdict detecte.
    [ObservableProperty] private bool _veillePingActive;
    private CancellationTokenSource? _veillePing;

    /// <summary>Proposee tant que rien n'a ete mesure : c'est l'autre chemin vers la protection.</summary>
    public bool VeillePingProposee => PlafondEnvoiIndisponible && CollecteurFenetre is not null && _helper is not null;
    public string LabelVeilleBascule => _localize("Ping.Veille.Libelle");   // L'état est dans l'interrupteur
    public string LabelVeilleDescription => _localize(VeillePingActive ? "Ping.Veille.EnCours" : "Ping.Veille.Description");

    internal TimeSpan FenetreVeille { get; set; } = TimeSpan.FromSeconds(60);
    internal Func<TimeSpan, CancellationToken, Task>? AttenteVeille { get; set; }

    /// <summary>Le jeu en cours, pour la veille : le programme en plein ecran, s'il peut etre un jeu
    /// (pas un navigateur, un lecteur ou un binaire de Windows). Remplacable par les tests.</summary>
    internal Func<string?>? JeuPourLaVeille { get; set; }

    private string? JeuEnPleinEcran()
    {
        if (_gameLocator.LocateRunningGamePid() is not { } pid || pid <= 0) return null;
        var exe = NomExeDuProcessus(pid);
        return exe is not null && DrDxt.Application.Network.PlafondEnvoiMemorise.EstUnJeuPossible(exe) ? exe : null;
    }

    partial void OnVeillePingActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(LabelVeilleDescription));
        OnPropertyChanged(nameof(LabelVeilleDescriptionCourte));
        OnPropertyChanged(nameof(LabelVeilleDescriptionSuite));
    }

    // Sans exécutions concurrentes, la commande restait « en cours » tant que la veille attendait un
    // verdict (parfois jamais) : l'interrupteur, grisé, ne pouvait plus l'éteindre. Calmer et Relevé l'avaient déjà.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task BasculerVeillePing()
    {
        try
        {
            if (VeillePingActive)
            {
                _veillePing?.Cancel();
                VeillePingActive = false;
                MemoireVeille?.Eteindre();
                return;
            }
            if (!VeillePingProposee) return;
            _veillePing = new CancellationTokenSource();
            VeillePingActive = true;
            MemoireVeille?.Allumer();
            await SurveillerLesPartiesAsync(_veillePing.Token);
        }
        // L'interrupteur se coche tout seul au clic : si l'état n'a pas bougé (sortie anticipée, erreur), on le lui redit.
        finally { OnPropertyChanged(nameof(VeillePingActive)); }
    }

    /// <summary>Le choix du joueur, garde entre deux lancements. <c>null</c> = rien n'est garde (les tests ;
    /// l'appli pose le fichier reel au demarrage).</summary>
    internal DrDxt.Application.Network.VeillePingMemorisee? MemoireVeille { get; set; }

    /// <summary>Au lancement : relance la veille que le joueur avait laissee allumee. Sans attente : elle
    /// dure jusqu'a un verdict, ou jusqu'a ce qu'il l'arrete.</summary>
    public void RallumerVeillePing()
    {
        if (MemoireVeille?.EstAllumee() == true && !VeillePingActive && VeillePingProposee)
            _ = BasculerVeillePingCommand.ExecuteAsync(null);
    }

    /// <summary>Veille jusqu'a un bufferbloat vu en partie, puis retient le plafond et allume la
    /// protection, comme si le joueur avait clique apres un scan en jeu. Arret : rien n'est pose.</summary>
    internal async Task SurveillerLesPartiesAsync(CancellationToken ct)
    {
        var veille = new DrDxt.Application.Network.VeilleDuPing(JeuPourLaVeille ?? JeuEnPleinEcran, CollecteurFenetre!, FenetreVeille, AttenteVeille);
        (string Exe, DrDxt.Measurement.Network.VerdictBufferbloat Verdict) trouve;
        try { trouve = await veille.AttendreUnBufferbloatAsync(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            VeillePingActive = false;
            ErreurPlafondEnvoi = $"{_localize("Ping.Protection.Echec")}{Docteur.DeuxPoints}{RaisonLisible.Dire(ex, _localize)}";
            return;
        }

        VeillePingActive = false;
        // Le plafond trouve prend le relais (sa propre memoire le rallume) : la veille a fini son travail.
        MemoireVeille?.Eteindre();
        RetenirMesureEnvoi(trouve.Verdict.PlafondConseilleBps, trouve.Exe);
        JournalPlafond?.Change(IdJournalPlafond, "veille",
            FormattableString.Invariant($"{trouve.Verdict.PlafondConseilleBps / 1e6:0.#} Mbit/s for {trouve.Exe} (idle ping p90 {trouve.Verdict.PingReposP90:0} ms, en charge {trouve.Verdict.PingChargeP90:0} ms)"),
            "DrDXT");
        // La veille trouve le défaut en pleine partie : aucune invite Windows ne s'y ouvre (SansGesteDuJoueur).
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        if (await ActiverEtRetenirAsync(trouve.Exe, trouve.Verdict.PlafondConseilleBps) is { IsOk: false } echec)
            ErreurPlafondEnvoi = RaisonLisible.Echec(echec, _localize("Ping.Protection.Echec"), _localize);
        SignalerPlafondEnvoi();
    }

    /// <summary>Ou chaque changement de plafond est ecrit. <c>null</c> = pas de trace (les tests qui
    /// ne la verifient pas ; l'appli pose le journal de diagnostic au demarrage).</summary>
    internal DrDxt.Core.IDiagnosticsLog? JournalPlafond { get; set; }

    // Le nom de l'exe tel que la QoS l'attend (nom nu). Un processus deja parti ou protege : null,
    // et le mode ne s'allume pas plutot que de plafonner un autre programme.
    private static string? NomExeDuProcessus(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch { return null; }
    }

    /// <summary>Le chemin complet de l'exe (pour écarter tout programme de Windows, à toute profondeur) ; null si
    /// le processus est parti (la règle des noms suffit alors). Lu avec les droits les plus faibles, sans ouvrir la mémoire
    /// du processus : un jeu protégé par un anti-triche le refuserait, et le surveille.</summary>
    private static string? CheminDuProcessus(int pid) => pid > 0 ? DrDxt.App.Platform.Win32GameLocator.CheminDe((uint)pid) : null;

    // Le dernier test memoire en session, rendu dans la sante au prochain audit.
    private RepairFinding? _constatTestMemoire;

    // ── La carte « Agir » du domaine latence : ce que la souris et la manette envoient vraiment ──────────
    /// <summary>La mesure des entrées (cinq secondes d'écoute, puis le constat) ; l'appli la pose, null = carte cachée.</summary>
    internal Func<CancellationToken, RepairFinding>? MesureDesEntrees { get; set; }
    public bool MesureDesEntreesDisponible => MesureDesEntrees is not null;
    private RepairFinding? _constatCadence;
    [ObservableProperty] private bool _cadenceEnCours;
    [ObservableProperty] private string? _resultatCadence;
    public bool CadencePrete => !CadenceEnCours;
    partial void OnCadenceEnCoursChanged(bool value) { OnPropertyChanged(nameof(CadencePrete)); OperationLongueChangee(); }
    public string LabelCapsuleCadence => _localize("Agir.Capsule.Cadence");
    public string LabelCadenceTitre => _localize(SansManette ? "Entrees.Cadence.Titre.SansManette" : "Entrees.Cadence.Titre");
    public string LabelCadenceDescription => _localize(SansManette ? "Entrees.Cadence.Description.SansManette" : "Entrees.Cadence.Description");
    public string LabelCadenceMaintenir => _localize(SansManette ? "Entrees.Cadence.Maintenir.SansManette" : "Entrees.Cadence.Maintenir");
    public string LabelCadenceEnCours => _localize(SansManette ? "Entrees.Cadence.EnCours.SansManette" : "Entrees.Cadence.EnCours");

    /// <summary>Une manette est-elle branchée ? Posé au lancement ; null = non lu, le texte nomme alors la manette.</summary>
    internal Func<bool>? ManetteBranchee { get; set; }

    // « bouge la souris et la manette » ne se dit qu'à qui en a une, ou quand on ne sait pas.
    private bool SansManette => ManetteBranchee?.Invoke() == false;

    /// <summary>Le maintien du noyau est la confirmation ; l'écoute tourne hors du fil d'interface.</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task MesurerCadence()
    {
        if (CadenceEnCours || MesureDesEntrees is not { } mesure) return;
        CadenceEnCours = true;
        ResultatCadence = null;
        try
        {
            _constatCadence = await Task.Run(() => mesure(CancellationToken.None));
            ResultatCadence = _constatCadence.Explication;
        }
        catch (Exception ex) { ResultatCadence = RaisonLisible.Dire(ex, _localize); }
        finally { CadenceEnCours = false; }
    }

    /// <summary>Le point d'étape du test mémoire, dans les données de DrDXT ; null = aucun (les tests qui ne le vérifient
    /// pas).</summary>
    internal string? FichierRepriseTestMemoire { get; set; }

    /// <summary>Le jugement d'un test resté sans fin (l'appli lit le point d'étape et les journaux) ; null = rien lu.</summary>
    internal Func<RepairFinding?>? JugerRepriseTestMemoire { get; set; }

    // ── La carte « Agir » du domaine memoire ──────────────────
    public static readonly TimeSpan DureeTestMemoire = TimeSpan.FromMinutes(5);
    // Coutures de test : cinq minutes et un quart de la memoire ne se testent pas en suite. L'ecran
    // ne les touche jamais ; null = la valeur du produit.
    internal TimeSpan DureeDuTestMemoire { get; set; } = DureeTestMemoire;
    internal long? VolumeDuTestMemoire { get; set; }
    private CancellationTokenSource? _arretTestMemoire;

    [ObservableProperty] private bool _testMemoireEnCours;
    [ObservableProperty] private double _progressionTestMemoire;
    [ObservableProperty] private string _tempsRestantTestMemoire = string.Empty;
    [ObservableProperty] private string? _resultatTestMemoire;
    [ObservableProperty] private bool _testMemoireEnDefaut;

    public bool TestMemoireATermine => ResultatTestMemoire is not null && !TestMemoireEnCours;
    partial void OnResultatTestMemoireChanged(string? value) => OnPropertyChanged(nameof(TestMemoireATermine));
    partial void OnTestMemoireEnCoursChanged(bool value) { OnPropertyChanged(nameof(TestMemoireATermine)); OperationLongueChangee(); }

    public string LabelCarteAgir => _localize("Carte.Agir");
    public string LabelTestMemoireTitre => _localize("Memoire.Test.Titre");
    public string LabelTestMemoireDescription => _localize("Memoire.Test.Description");
    public string LabelTestMemoireMaintenir => _localize("Memoire.Test.Maintenir");
    public string LabelTestMemoireEnCours => _localize("Memoire.Test.EnCours");
    public string LabelTestMemoireArreter => _localize("Memoire.Test.Arreter");
    public string LabelTestMemoireRelancer => _localize("Memoire.Test.Relancer");

    /// <summary>Lance le test. Le maintien du noyau est la confirmation ; l'interface n'est jamais
    /// figee (le test tourne hors du fil d'interface) et l'arret se prend entre deux passes.</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task LancerTestMemoire()
    {
        if (TestMemoireEnCours) return;
        _arretTestMemoire = new CancellationTokenSource();
        TestMemoireEnCours = true;
        ResultatTestMemoire = null;
        ProgressionTestMemoire = 0;
        var debut = DateTime.UtcNow;
        var minuterie = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        minuterie.Tick += (_, _) =>
        {
            var ecoule = DateTime.UtcNow - debut;
            ProgressionTestMemoire = Math.Min(1, ecoule / DureeDuTestMemoire);
            var reste = DureeDuTestMemoire - ecoule;
            TempsRestantTestMemoire = reste > TimeSpan.Zero
                ? $"{(int)reste.TotalMinutes}:{reste.Seconds:00} {_localize("Memoire.Test.Restant")}"
                : string.Empty;
        };
        minuterie.Start();
        try
        {
            var r = await TesterMemoireEnSessionAsync(DureeDuTestMemoire, VolumeDuTestMemoire, _arretTestMemoire.Token);
            TestMemoireEnDefaut = r.Erreurs > 0;
            ResultatTestMemoire = r.Phrase;
        }
        catch (Exception ex)
        {
            TestMemoireEnDefaut = false;
            ResultatTestMemoire = RaisonLisible.Dire(ex, _localize);
        }
        finally
        {
            minuterie.Stop();
            ProgressionTestMemoire = 1;
            TempsRestantTestMemoire = string.Empty;
            TestMemoireEnCours = false;
        }
    }

    [RelayCommand]
    private void ArreterTestMemoire() => _arretTestMemoire?.Cancel();

    /// <summary>Teste la RAM sans redemarrer (motifs ecrits puis relus sur tous les coeurs). Le volume
    /// par defaut est le quart de la memoire, borne a la moitie de la memoire libre : assez pour charger le
    /// controleur, pas assez pour faire paginer la machine. Zero erreur n'est jamais « sain » : c'est « observe ».</summary>
    public async Task<DrDxt.Measurement.Memory.ResultatTestMemoire> TesterMemoireEnSessionAsync(
        TimeSpan duree, long? octets = null, CancellationToken ct = default)
    {
        var volume = octets ?? DrDxt.Measurement.Memory.TestMemoireEnSession.Volume(
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, DrDxt.Measurement.CommitPressureReader.LibrePhysique());
        // Le point d'étape : posé avant, relevé à chaque passe, effacé à la fin normale (arrêt du joueur compris). Resté
        // en place, il dit au lancement suivant que la machine s'est arrêtée pendant le test.
        var debut = DateTimeOffset.Now;
        void Point(int passes)
        {
            if (FichierRepriseTestMemoire is { } f)
                DrDxt.Measurement.Memory.RepriseDuTestMemoire.Poser(f, new(debut, DateTimeOffset.Now, passes));
        }
        Point(0);
        var r = await Task.Run(() => DrDxt.Measurement.Memory.TestMemoireEnSession.Lancer(
            volume, duree, Environment.ProcessorCount, ct, Point));
        if (FichierRepriseTestMemoire is { } fini) DrDxt.Measurement.Memory.RepriseDuTestMemoire.Effacer(fini);
        _constatTestMemoire = DrDxt.Measurement.Memory.TestMemoireEnSession.Constat(r);
        return r;
    }

    // ── La carte « Agir » du domaine reseau ─────────────────────────────────────────────────
    [ObservableProperty] private string? _erreurPlafondEnvoi;
    [ObservableProperty] private bool _plafondEnvoiEnChangement;

    public bool PlafondEnvoiIndisponible => !PlafondEnvoiDisponible;
    public string LabelPingTitre => _localize("Ping.Protection.Titre");
    // Le texte suit celui qui pose le plafond. « N'agit que DrDXT ouvert » est faux quand le service la porte.
    public string LabelPingDescription => _localize(ServiceDemarrageActif?.Invoke() == true
        ? "Ping.Protection.Description.Service" : "Ping.Protection.Description");
    public string LabelPingIndisponible => _localize("Ping.Protection.Indisponible");

    // Trois paragraphes d'un bloc faisaient un mur de texte dans « Agir » ; la
    // première phrase reste, la suite s'ouvre sous le « ? », comme pour un constat.
    public string LabelPingDescriptionCourte => CarteDeLaMachine.TitreEtSuite(LabelPingDescription).Titre;
    public string LabelPingDescriptionSuite => CarteDeLaMachine.TitreEtSuite(LabelPingDescription).Suite;
    public string LabelVeilleDescriptionCourte => CarteDeLaMachine.TitreEtSuite(LabelVeilleDescription).Titre;
    public string LabelVeilleDescriptionSuite => CarteDeLaMachine.TitreEtSuite(LabelVeilleDescription).Suite;
    // Ce que la bascule appliquera, dit avant le clic : le plafond conseillé par la mesure en jeu.
    public string TextePlafondEnvoi => PlafondEnvoiDisponible
        ? string.Format(CultureInfo.CurrentCulture, _localize("Ping.Protection.Plafond"), _plafondEnvoiMesure / 1_000_000.0,
            _exeJeuMesure ?? "?")
        : string.Empty;
    public string LabelPingBascule => _localize(PlafondEnvoiActif ? "Ping.Protection.Desactiver" : "Ping.Protection.Activer");
    public string LabelPingEtat => _localize(PlafondEnvoiActif ? "Ping.Protection.Active" : "Ping.Protection.Inactive");

    private void SignalerPlafondEnvoi()
    {
        OnPropertyChanged(nameof(PlafondEnvoiDisponible));
        OnPropertyChanged(nameof(PlafondEnvoiIndisponible));
        OnPropertyChanged(nameof(TextePlafondEnvoi));
        OnPropertyChanged(nameof(PlafondEnvoiActif));
        OnPropertyChanged(nameof(LabelPingBascule));
        OnPropertyChanged(nameof(LabelPingEtat));
        OnPropertyChanged(nameof(VeillePingProposee));
        OnPropertyChanged(nameof(LabelPingDescription));
        OnPropertyChanged(nameof(LabelPingDescriptionCourte));
        OnPropertyChanged(nameof(LabelPingDescriptionSuite));
    }

    /// <summary>Allume ou eteint la protection. Un refus (UAC decline, helper absent) se dit : la
    /// bascule ne pretend jamais un etat qu'elle n'a pas obtenu.</summary>
    [RelayCommand]
    private async Task BasculerPlafondEnvoi()
    {
        if (PlafondEnvoiEnChangement) return;
        PlafondEnvoiEnChangement = true;
        ErreurPlafondEnvoi = null;
        try
        {
            if (PlafondEnvoiActif)
            {
                // Service en marche, c'est lui qui pose : un refus d'oublier le laisserait plafonner a chaque partie.
                if (await OublierPourLeServiceAsync() is { IsOk: false } refus && _porteeParLeService)
                {
                    ErreurPlafondEnvoi = RaisonLisible.Echec(refus, _localize("Ping.Protection.Echec"), _localize);
                    return;
                }
                var retrait = await DesactiverPlafondEnvoiAsync();
                MemoirePlafond?.Oublier();
                if (!retrait.IsOk)
                    ErreurPlafondEnvoi = string.Format(CultureInfo.CurrentCulture, _localize("Ping.Protection.RetraitDiffere"),
                        RaisonLisible.Dire(retrait, _localize));
            }
            else if (await ActiverEtRetenirAsync(_exeJeuMesure!, _plafondEnvoiMesure) is { IsOk: false } echec)
                ErreurPlafondEnvoi = RaisonLisible.Echec(echec, _localize("Ping.Protection.Echec"), _localize);
        }
        catch (Exception ex) { ErreurPlafondEnvoi = RaisonLisible.Dire(ex, _localize); }
        finally
        {
            PlafondEnvoiEnChangement = false;
            SignalerPlafondEnvoi();
        }
    }

    // ── La carte « Agir » du domaine windows : rendre l'écran de recuperation ─────────────────
    // L'element et la valeur lus par l'audit. `_bcdRendu` garde exactement ce qui a ete retire :
    // l'annulation repose cette valeur-la, pas une valeur supposee.
    private (string Element, string Valeur)? _bcdARendre;
    private (string Element, string Valeur)? _bcdRendu;

    // La carte promet « Réversible » : la valeur retirée est gardée sur disque jusqu'à l'annulation, écrite avant le
    // helper. Null (tests) : rien n'est écrit et le retrait ne vit que pour la session.
    internal DrDxt.Application.Safety.GardeDuBcdRetire? GardeBcd { get; set; }

    [ObservableProperty] private string? _erreurBcd;
    [ObservableProperty] private bool _bcdEnCours;

    public bool BcdARendre => _bcdARendre is not null && _bcdRendu is null;
    public bool BcdRendu => _bcdRendu is not null;
    public bool BcdVisible => BcdARendre || BcdRendu;
    public string LabelBcdTitre => _localize("Bcd.Rendre.Titre");
    public string LabelBcdDescription => _localize("Bcd.Rendre.Description");
    public string LabelBcdMaintenir => _localize("Bcd.Rendre.Maintenir");
    public string LabelBcdFait => _localize("Bcd.Rendre.Fait");
    public string LabelBcdAnnuler => _localize("Bcd.Rendre.Annuler");

    private void ArmerBcd(RepairTriage triage)
    {
        var f = triage.Attention.FirstOrDefault(x => x.Domaine == "BCD" && x.Nom == DrDxt.Application.Safety.GardeDuBcdRetire.Element
                                                     && !string.IsNullOrWhiteSpace(x.ValeurCourante));
        _bcdARendre = f is null ? null : (f.Nom, f.ValeurCourante!);
        // L'audit ne voit plus le reglage : s'il a ete retire par DrDXT lors d'une session precedente,
        // la carte le redit et garde son Annuler. S'il est revenu, le fichier est perime et ignore.
        if (f is null && _bcdRendu is null)
            _bcdRendu = GardeBcd?.Lire();
        SignalerBcd();
    }

    private void SignalerBcd()
    {
        OnPropertyChanged(nameof(BcdARendre));
        OnPropertyChanged(nameof(BcdRendu));
        OnPropertyChanged(nameof(BcdVisible));
        OnPropertyChanged(nameof(CarteWindowsVisible));
    }

    /// <summary>Retire l'element par le helper (UAC a la demande) ; le helper relit apres ecriture.</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task RendreBcd()
    {
        if (BcdEnCours || _bcdARendre is not { } cible || _helper is null) return;
        BcdEnCours = true;
        ErreurBcd = null;
        try
        {
            // La garde d'abord : bcdedit écrit tout de suite, une coupure entre les deux laisserait un retrait sans annulation.
            if (GardeBcd is { } garde && !garde.Ecrire(cible.Element, cible.Valeur))
            {
                ErreurBcd = _localize("Bcd.Rendre.GardeImpossible");
                return;
            }
            var r = await _helper.ExecuteAsync(new DeleteBcdValueOp(cible.Element), CancellationToken.None);
            if (r.IsOk)
            {
                _bcdRendu = cible;
                // Le constat restait sur la page à côté de la carte « rendu » (même
                // défaut que la remesure processeur). Retiré de la page, gardé pour l'annulation.
                _constatBcdRetire = RepairFindings.FirstOrDefault(v => v.DomaineCle == "BCD" && v.NomCle == cible.Element);
                if (_constatBcdRetire is not null) { RepairFindings.Remove(_constatBcdRetire); ReconstruireCarteSurLeDomaineOuvert(); }
            }
            else
            {
                // Un refus n'a rien écrit : la garde ferait proposer un faux Annuler. Sans réponse du helper après l'envoi,
                // bcdedit a pu retirer la valeur : la garde reste, et la session suivante propose l'Annuler si l'audit ne la
                // voit plus.
                if (r.Error?.Code is not (ErrorCodes.HelperNoReply or ErrorCodes.HelperNoReplyJournalisee)) GardeBcd?.Effacer();
                ErreurBcd = RaisonLisible.Echec(r, _localize("Bcd.Rendre.Echec"), _localize);
            }
        }
        finally { BcdEnCours = false; SignalerBcd(); }
    }

    private RepairFindingViewModel? _constatBcdRetire;

    /// <summary>La carte est construite au scan : un geste qui change ce qu'elle montre la refait, sur le
    /// domaine que le joueur regardait.</summary>
    private void ReconstruireCarteSurLeDomaineOuvert()
    {
        var ouvert = DomaineSelectionne?.Cle;
        if (Domaines.Count > 0) ConstruireCarte();
        if (Domaines.FirstOrDefault(d => d.Cle == ouvert) is { } meme) DomaineSelectionne = meme;
    }

    /// <summary>Repose exactement la valeur retiree.</summary>
    [RelayCommand]
    private async Task AnnulerBcd()
    {
        if (BcdEnCours || _bcdRendu is not { } rendu || _helper is null) return;
        BcdEnCours = true;
        ErreurBcd = null;
        try
        {
            var r = await _helper.ExecuteAsync(new SetBcdValueOp(rendu.Element, rendu.Valeur), CancellationToken.None);
            if (r.IsOk)
            {
                _bcdRendu = null;
                GardeBcd?.Effacer();
                if (_constatBcdRetire is not null) { RepairFindings.Add(_constatBcdRetire); _constatBcdRetire = null; ReconstruireCarteSurLeDomaineOuvert(); }
            }
            else ErreurBcd = RaisonLisible.Echec(r, _localize("Bcd.Rendre.Echec"), _localize);
        }
        finally { BcdEnCours = false; SignalerBcd(); }
    }

    // ── La carte « Agir » du domaine processeur : remesurer a la demande ────────────────────────
    // La reference se mesure au premier scan puis se relit : sans ce bouton, elle ne se
    // referait jamais, meme devenue fausse (une reference ancienne peut ignorer la limite de puissance).
    [ObservableProperty] private bool _remesureCpuEnCours;
    [ObservableProperty] private bool _remesureCpuFaite;
    [ObservableProperty] private string? _erreurRemesureCpu;

    public bool RemesureCpuVisible => _cpuBaseline is not null && _machineId is not null
        && (CpuSustainedLabel is not null || CpuSauteJeu);

    // Le scan capture le jeu, donc le parcours normal le lance avant de scanner. Sans mesure
    // jeu lancé, ce joueur n'avait ni ligne, ni raison, ni bouton : la carte s'affiche et dit pourquoi.
    [ObservableProperty] private bool _cpuSauteJeu;
    partial void OnCpuSauteJeuChanged(bool value) => OnPropertyChanged(nameof(RemesureCpuVisible));
    // Une information, pas une erreur : rien n'est à corriger sur la machine, elle a sa ligne neutre.
    public string LabelCpuSauteJeu => _localize("Cpu.Remesure.SauteJeu");
    public string LabelRemesureCpuTitre => _localize("Cpu.Remesure.Titre");
    public string LabelRemesureCpuDescription => _localize("Cpu.Remesure.Description");
    // La ligne qui reste à l'écran ; le texte entier passe en info-bulle.
    public string LabelRemesureCpuDescriptionCourte => _localize("Cpu.Remesure.Description.Court");
    public string LabelRemesureCpuMaintenir => _localize("Cpu.Remesure.Maintenir");
    public string LabelRemesureCpuEnCours => _localize("Cpu.Remesure.EnCours");
    public string LabelRemesureCpuFaite => _localize("Cpu.Remesure.Faite");

    // La phrase change avec chaque mesure : le guide de la limite de puissance la suit.
    partial void OnCpuSustainedLabelChanged(string? value)
    {
        OnPropertyChanged(nameof(RemesureCpuVisible));
        OnPropertyChanged(nameof(LignesLimiteDePuissance));
        OnPropertyChanged(nameof(ALimiteDePuissance));
        OnPropertyChanged(nameof(LimitePremiereLigne));
        OnPropertyChanged(nameof(LignesLimiteSuite));
    }

    /// <summary>La phrase du processeur, reliee au reglage BIOS lu. La composition vit
    /// dans <see cref="DrDxt.Measurement.Cpu.CpuDeficitSoutenu.PhraseAvec"/>, partagee avec le rapport.</summary>
    internal static string? PhraseProcesseur(DrDxt.Measurement.Cpu.CpuDeficitSoutenu? deficit, IReadOnlyList<BiosSettingReading>? bios)
        => deficit?.PhraseAvec(bios);

    /// <summary>En partie, le jeu est limité par le processeur, bridé
    /// par sa limite de puissance soutenue ; cachée sous « Pourquoi ? », l'explication passerait inaperçue. Quatre lignes en clair :
    /// ce qui bride (mesuré), le réglage lu, le risque, le retour. <c>null</c> sans limite démontrée ou sans BIOS lu.
    /// DrDXT n'écrit pas le BIOS et ne conseille pas de changer ce réglage.</summary>
    internal static IReadOnlyList<string>? GuideLimiteDePuissance(
        DrDxt.Measurement.Cpu.CpuDeficitSoutenu? deficit, IReadOnlyList<BiosSettingReading>? bios, Func<string, string> localiser)
    {
        var reglage = bios?.FirstOrDefault(r => r.Setting == "CPU Cooler Tuning");
        // La bride se mesure sur toute machine ; seul le réglage n'est lu que sur les cartes que
        // DrDXT connaît. Ailleurs, le guide reste, avec le nom générique du réglage, au lieu de se taire.
        if (deficit is not ({ Demontrable: true, LimiteDePuissance: true } or { BrideDesLeDepart: true })) return null;
        var watts = deficit.Reference?.PuissanceSoutenueW;
        string F(string cle, params object[] a) => string.Format(System.Globalization.CultureInfo.CurrentCulture, localiser(cle), a);
        return
        [
            deficit.BrideDesLeDepart && deficit.Reference is { PuissanceSoutenueW: { } pw, FrequenceSoutenueMhz: { } fs, FrequenceUnCoeurMhz: { } f1 }
                ? F("Cpu.Limite.DesLeDepart", Math.Round(pw), Math.Round(fs), Math.Round(f1))
            : watts is { } w
                ? F("Cpu.Limite.Mesure", Math.Round(w), Math.Round(deficit.DeficitPercent))
                : F("Cpu.Limite.MesureSansWatts", Math.Round(deficit.DeficitPercent)),
            reglage is null ? localiser("Cpu.Limite.ReglageInconnu") : F("Cpu.Limite.Reglage", reglage.Libelle),
            localiser(reglage is null ? "Cpu.Limite.RisqueGenerique" : "Cpu.Limite.Risque"),
            localiser(reglage is null ? "Cpu.Limite.RetourGenerique" : "Cpu.Limite.Retour"),
        ];
    }

    /// <summary>Les réglages du BIOS qui fixent la puissance du processeur, tels que lus, en une
    /// chaîne notée avec chaque mesure. <c>null</c> sans aucun d'eux lu : rien à comparer, aucune remesure.</summary>
    internal static string? ReglageDePuissanceLu(IReadOnlyList<BiosSettingReading>? bios)
    {
        if (bios is null) return null;
        string? L(string reglage) => bios.FirstOrDefault(r => r.Setting == reglage)?.Libelle;
        var parties = new[]
        {
            L("CPU Cooler Tuning"),
            L("Long Duration Power Limit (PL1)") is { } pl1 ? $"PL1 {pl1}" : null,
            L("Short Duration Power Limit (PL2)") is { } pl2 ? $"PL2 {pl2}" : null,
        }.OfType<string>().ToList();
        return parties.Count == 0 ? null : string.Join(", ", parties);
    }

    /// <summary>Le guide, pour la vue.</summary>
    public IReadOnlyList<string>? LignesLimiteDePuissance => GuideLimiteDePuissance(_dernierDeficitCpu, _reglagesBiosLus, _localize);
    public bool ALimiteDePuissance => LignesLimiteDePuissance is not null;
    /// <summary>La bride en une phrase ; le réglage, le risque et le retour arrière se déplient.</summary>
    public string? LimitePremiereLigne => LignesLimiteDePuissance?.FirstOrDefault();
    public IReadOnlyList<string> LignesLimiteSuite => LignesLimiteDePuissance?.Skip(1).ToList() ?? [];
    public string LabelLimiteTitre => _localize("Cpu.Limite.Titre");

    /// <summary>Le dernier verdict processeur, typé, pour le rapport (jamais une chaine libre).</summary>
    private DrDxt.Measurement.Cpu.CpuDeficitSoutenu? _dernierDeficitCpu;

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task RemesurerProcesseur()
    {
        if (RemesureCpuEnCours || _cpuBaseline is not { } service || _machineId is not { } id) return;
        if (_gameLocator.JeuOuvert())
        {
            ErreurRemesureCpu = _localize("Cpu.Remesure.JeuEnCours");
            return;
        }
        RemesureCpuEnCours = true;
        ErreurRemesureCpu = null;
        try
        {
            // La réserve du scan dit que l'ancienne référence a sans doute été prise pendant une partie.
            var suspecte = !string.IsNullOrEmpty(_dernierDeficitCpu?.Reserve);
            var reglage = ReglageDePuissanceLu(_reglagesBiosLus);
            var refAvant = service.Reference(id());
            var deficit = await Task.Run(() => service.Remesurer(id(), suspecte, reglage));
            NoterChangementBios(refAvant?.ReglageBios, service.Reference(id())?.ReglageBios, refAvant?.TakenUtc);
            if (deficit is null) ErreurRemesureCpu = _localize("Cpu.Remesure.Echec");
            else
            {
                _dernierDeficitCpu = deficit;
                CpuSustainedLabel = PhraseProcesseur(deficit, _reglagesBiosLus);
                RemesureCpuFaite = true;
                CpuSauteJeu = false; // la mesure existe maintenant : « pas mesuré » serait faux
                // La carte est construite au scan : sans reconstruction, elle garderait la phrase de l'ancienne
                // mesure (« ne peut pas dire… remesurer le dira ») sous la pastille « mesure refaite ».
                ReconstruireCarteSurLeDomaineOuvert();
            }
        }
        catch (Exception ex) { ErreurRemesureCpu = $"{_localize("Cpu.Remesure.Echec")}{Docteur.DeuxPoints}{RaisonLisible.Dire(ex, _localize)}"; }
        finally { RemesureCpuEnCours = false; }
    }

    // ── Remesurer sans geste ──────────────────────────────────────────────────────
    // Une reference soupconnee (prise pendant une partie) se refait seule quand le poste est au repos
    // (RemesureAuRepos). Memes commandes qu'a la main, donc memes gardes : jeu ouvert refuse, mesure trop
    // bruitee gardee. Une seule mesure par passage : deux bancs a la suite se generaient.

    /// <summary>Temps sans saisie et charge du processeur. <c>null</c> = aucune remesure seule (les
    /// tests qui ne la verifient pas ; l'appli pose le lecteur Windows au demarrage).</summary>
    internal Func<CancellationToken, Task<(TimeSpan SansSaisie, double? ChargeCpu)>>? LirePoste { get; set; }

    private DateTimeOffset? _dernierEssaiCpuAuRepos;
    private DateTimeOffset? _dernierEssaiMemoireAuRepos;
    private bool _memoireSuspecte;

    public async Task TenterRemesureAuReposAsync(DateTimeOffset maintenant, CancellationToken ct)
    {
        var cpuSuspect = !string.IsNullOrEmpty(_dernierDeficitCpu?.Reserve);
        if (LirePoste is null || (!cpuSuspect && !_memoireSuspecte)
            || OperationLongueEnCours) return;

        var (sansSaisie, charge) = await LirePoste(ct);
        var jeuOuvert = _gameLocator.JeuOuvert();
        DrDxt.Application.Support.DecisionRemesure Decider(bool suspecte, DateTimeOffset? dernier)
            => DrDxt.Application.Support.RemesureAuRepos.Decider(new(sansSaisie, jeuOuvert, charge, suspecte, dernier), maintenant);

        if (Decider(_memoireSuspecte, _dernierEssaiMemoireAuRepos) == DrDxt.Application.Support.DecisionRemesure.Mesurer)
        {
            _dernierEssaiMemoireAuRepos = maintenant;
            await RemesurerMemoireCommand.ExecuteAsync(null);
        }
        else if (Decider(cpuSuspect, _dernierEssaiCpuAuRepos) == DrDxt.Application.Support.DecisionRemesure.Mesurer)
        {
            _dernierEssaiCpuAuRepos = maintenant;
            await RemesurerProcesseurCommand.ExecuteAsync(null);
        }
    }

    // ── Remesurer la mémoire à la demande ──────────────────────────────────────────
    // La référence se mesure au premier scan puis se relit tant que la configuration ne change pas.
    // Mesuré sur la machine de référence : 12,7 Go/s et 128,5 ns le 16/09, 33,4 Go/s et 74 ns dix jours plus tard.
    // Sans ce bouton, l'écran garderait un chiffre faux que rien ne permet de refaire.
    [ObservableProperty] private bool _remesureMemoireEnCours;
    [ObservableProperty] private bool _remesureMemoireFaite;
    [ObservableProperty] private string? _erreurRemesureMemoire;

    public bool RemesureMemoireVisible => _memoryBaseline is not null && _machineId is not null
        && (MemoryMeasuredLabel is not null || MemoireSauteeJeu);

    [ObservableProperty] private bool _memoireSauteeJeu;
    partial void OnMemoireSauteeJeuChanged(bool value) => OnPropertyChanged(nameof(RemesureMemoireVisible));
    public string LabelMemoireSauteeJeu => _localize("Memoire.Remesure.SauteeJeu");
    public string LabelRemesureMemoireTitre => _localize("Memoire.Remesure.Titre");
    public string LabelRemesureMemoireDescription => _localize("Memoire.Remesure.Description");
    public string LabelRemesureMemoireEnCours => _localize("Memoire.Remesure.EnCours");
    public string LabelRemesureMemoireMaintenir => _localize("Cpu.Remesure.Maintenir");
    public string LabelRemesureMemoireFaite => _localize("Memoire.Remesure.Faite");

    partial void OnMemoryMeasuredLabelChanged(string? value) => OnPropertyChanged(nameof(RemesureMemoireVisible));

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task RemesurerMemoire()
    {
        if (RemesureMemoireEnCours || _memoryBaseline is not { } service || _machineId is not { } id
            || _lastScan?.Hw.Memory is not { } modules) return;
        // Une mesure prise jeu lancé décrit le jeu, pas la mémoire (la référence du 16/09 en est l'exemple).
        if (_gameLocator.JeuOuvert())
        {
            ErreurRemesureMemoire = _localize("Memoire.Remesure.JeuEnCours");
            return;
        }
        RemesureMemoireEnCours = true;
        ErreurRemesureMemoire = null;
        try
        {
            // Une mesure refusée (machine occupée) ne remplace rien : l'ancienne reste, et c'est dit.
            if (!await Task.Run(() => service.MesurerMaintenant(id(), modules)))
                ErreurRemesureMemoire = _localize("Memoire.Remesure.Echec");
            else
            {
                MemoryMeasuredLabel = LigneMemoire(modules);
                RemesureMemoireFaite = true;
                MemoireSauteeJeu = false;
                ReconstruireCarteSurLeDomaineOuvert();
            }
        }
        catch (Exception ex) { ErreurRemesureMemoire = $"{_localize("Memoire.Remesure.Echec")}{Docteur.DeuxPoints}{RaisonLisible.Dire(ex, _localize)}"; }
        finally { RemesureMemoireEnCours = false; }
    }

    // ── La carte « Agir » du domaine windows : réparer Windows avec les outils de Microsoft ───────
    // Armee seulement quand DISM /CheckHealth a dit « reparable ». La reparation dure de
    // longues minutes : elle ne passe pas par le helper (elle bloquerait ses baux, dont celui de la
    // protection du ping). Elle s'ouvre dans une console visible, elevee par la fenetre UAC de Windows,
    // ou le joueur voit DISM puis sfc avancer et peut fermer quand il veut.
    private bool _reparationARelancer;
    [ObservableProperty] private bool _reparationLancee;
    [ObservableProperty] private string? _erreurReparation;

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

    /// <summary>Une écriture « Normal » au global ne prend effet qu'au redémarrage : sa preuve, commencée avant, se
    /// conclut ici. Sans baisse nette, le réglage est retiré. Mesuré au lancement : un jeu déjà ouvert à ce moment
    /// fausse la mesure dans le sens du retrait, jamais dans celui d'un gain inventé.</summary>
    internal async Task ConclureReposCarteAsync(ITweak levier, IHardwareProbe sonde,
        DrDxt.Application.Tweaks.PreuvesEnAttente attente, Func<TimeSpan, Task> attendre)
    {
        // Lancée sans attente au démarrage : personne n'observerait son exception, et la preuve resterait « en attente ».
        try { await ConclureReposCarteSansFiletAsync(levier, sonde, attente, attendre); }
        catch (Exception ex) { VerdictReposCarte = RaisonLisible.Dire(ex, _localize); }
    }

    private async Task ConclureReposCarteSansFiletAsync(ITweak levier, IHardwareProbe sonde,
        DrDxt.Application.Tweaks.PreuvesEnAttente attente, Func<TimeSpan, Task> attendre)
    {
        // Au lancement, sans clic : un retrait du réglage ne lance pas le helper (SansGesteDuJoueur).
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        EcritureReposCarte = attente.Charger();
        var v = await DrDxt.Application.Tweaks.PreuveDuRepos.ConclureAsync(levier, sonde, 20, attendre, attente, EtatDeLaPose);
        if (v is null) return;
        var cle = v.Etat switch
        {
            DrDxt.Application.Tweaks.EtatDeLaPreuve.Prouve => "Carte.Repos.Prouve",
            DrDxt.Application.Tweaks.EtatDeLaPreuve.PasDeGain => "Carte.Repos.PasDeGain",
            DrDxt.Application.Tweaks.EtatDeLaPreuve.RetraitRate => "Carte.Repos.PreuveRetraitRate",
            DrDxt.Application.Tweaks.EtatDeLaPreuve.ChangeDepuis => "Carte.Repos.ChangeDepuis",
            _ => "Carte.Repos.NonMesure",
        };
        VerdictReposCarte = string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize(cle), v.ApresW, v.AvantW);
        if (v.Etat != DrDxt.Application.Tweaks.EtatDeLaPreuve.Prouve || EcritureReposCarte is not { } e) return;
        // Prouvé, il reste : comme tout levier gardé, au journal des leviers (« Annuler » dans « Ce que DrDXT a posé ») et au
        // manifeste signé (la désinstallation le rend).
        var rapport = new ApplyReport([new ApplyOutcome(e.TweakId, true, false, new TweakSnapshot(e.TweakId, e.OriginalValueJson, e.AppliedValueJson), null)], null);
        _journalLeviers?.Enregistrer(rapport);
        if (_manifestMapper.ToRevertOnly(rapport) is { Count: > 0 } gardes
            && await _desiredStateStore.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), gardes)).ConfigureAwait(true) is { IsOk: false } refus)
            VerdictReposCarte += " " + string.Format(CultureInfo.CurrentCulture, _localize("Carte.Repos.PasInscrit"), RaisonLisible.Dire(refus, _localize));
        ChargerLeviersPoses();
        if (ReglagesDeLaCarte is not { } retenus) return;
        // Prouvé, mais le suivi en jeu ne s'est pas gardé : le réglage reste, sans la surveillance des parties, et c'est dit.
        try { retenus.Garder(new(e.TweakId, e.OriginalValueJson, e.AppliedValueJson, DateTimeOffset.Now)); }
        catch (IOException) { VerdictReposCarte += " " + _localize("Carte.Repos.SuiviNonGarde"); }
    }

    /// <summary>Ce qui garde le réglage prouvé au repos, et l'écriture qu'il annulerait. Posés au lancement.</summary>
    internal DrDxt.Application.Tweaks.ReglagesRetenus? ReglagesDeLaCarte { get; set; }
    internal ITweak? LevierReposCarte { get; set; }

    // ── Le geste, sur la page Carte graphique ───────────────────────────────────────────────────────────
    internal IHardwareProbe? SondeCarte { get; set; }
    internal DrDxt.Application.Tweaks.PreuvesEnAttente? AttenteReposCarte { get; set; }
    internal Func<TimeSpan, Task> AttendreCarte { get; set; } = t => Task.Delay(t);
    [ObservableProperty] private bool _reposCarteEnCours;
    [ObservableProperty] private bool _reposCarteEnAttente;
    [ObservableProperty] private string? _erreurReposCarte;

    /// <summary>La carte « Agir » du domaine graphique : le constat « performances maximales au global » lu, ou une
    /// preuve en cours ou conclue à dire.</summary>
    public bool ReposCarteVisible =>
        (LevierReposCarte is not null && _lastScan?.Findings.Any(f => f.TitleKey == "Finding.Gpu.PerfMaxGlobal") == true)
        || ReposCarteEnAttente || VerdictReposCarte is not null;
    // Sans sonde ni attente, le geste rendait la main en silence et Optimiser écrivait « rien n'a été
    // posé » pour une tentative qui n'avait pas eu lieu.
    public bool ReposCarteAOuvrir => ReposCarteVisible && SondeCarte is not null && AttenteReposCarte is not null
        && !ReposCarteEnAttente && !ReposCarteEnCours && VerdictReposCarte is null;
    partial void OnReposCarteEnAttenteChanged(bool value) { OnPropertyChanged(nameof(ReposCarteVisible)); OnPropertyChanged(nameof(ReposCarteAOuvrir)); }
    partial void OnReposCarteEnCoursChanged(bool value) { OnPropertyChanged(nameof(ReposCarteAOuvrir)); OperationLongueChangee(); }
    partial void OnVerdictReposCarteChanged(string? value) { OnPropertyChanged(nameof(ReposCarteVisible)); OnPropertyChanged(nameof(ReposCarteAOuvrir)); }
    internal void SignalerReposCarte() { OnPropertyChanged(nameof(ReposCarteVisible)); OnPropertyChanged(nameof(ReposCarteAOuvrir)); }
    public string LabelReposCarteTitre => _localize("Carte.Repos.Titre");
    public string LabelReposCarteDescription => _localize("Carte.Repos.Description");
    // La ligne qui reste à l'écran ; le texte entier passe en info-bulle.
    public string LabelReposCarteDescriptionCourte => _localize("Carte.Repos.Description.Court");
    public string LabelReposCarteMaintenir => _localize("Carte.Repos.Maintenir");
    public string LabelReposCarteEnCours => _localize("Carte.Repos.EnCours");
    public string LabelReposCarteEnAttente => _localize("Carte.Repos.EnAttente");
    public string LabelCapsuleReposCarte => _localize("Carte.Repos.Capsule");

    /// <summary>Premier temps de la preuve : 20 lectures du repos, l'écriture, la preuve gardée. Le second temps se
    /// joue au lancement qui suit un redémarrage (<see cref="ConclureReposCarteAsync"/>).</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task RendreLeReposALaCarte()
    {
        if (LevierReposCarte is not { } levier || SondeCarte is not { } sonde || AttenteReposCarte is not { } attente || ReposCarteEnCours) return;
        // Jeu ouvert, les lectures « avant » seraient celles de la charge : la preuve du lancement suivant mentirait.
        if (_gameLocator.JeuOuvert())
        {
            ErreurReposCarte = _localize("Carte.Repos.JeuOuvert");
            return;
        }
        ErreurReposCarte = null;
        ReposCarteEnCours = true;
        try
        {
            var r = await DrDxt.Application.Tweaks.PreuveDuRepos.EcrireAsync(levier, sonde, 20, AttendreCarte, attente);
            ReposCarteEnAttente = r.IsOk;
            if (!r.IsOk) ErreurReposCarte = RaisonLisible.OuIncertain(r, _localize("Carte.Repos.Refuse"), _localize);
        }
        // Posé, mais la preuve ne s'est pas gardée : la portée de pose reste ouverte, le journal du helper garde l'origine et
        // la rend au lancement suivant. L'App ne plante pas, et le dit.
        catch (IOException)
        {
            ReposCarteEnAttente = false;
            ErreurReposCarte = _localize("Carte.Repos.PreuveNonGardee");
        }
        finally { ReposCarteEnCours = false; }
    }

    /// <summary>Appelé quand la liste des parties change : surveille le réglage gardé, s'il y en a un.</summary>
    internal Task SurveillerSiPoseAsync(IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> parties) =>
        LevierReposCarte is { } l && ReglagesDeLaCarte is { } r ? SurveillerLaCarteEnJeuAsync(l, parties, r) : Task.CompletedTask;
    private DrDxt.Application.Tweaks.PreuveEnAttente? EcritureReposCarte { get; set; }

    /// <summary>Un réglage gardé au repos reste sous surveillance en jeu. Sur le jeu le plus joué depuis, trois
    /// parties toutes sous les trois d'avant (1 % le plus lent) : retiré, et dit. Sinon rien ne bouge. Changé depuis hors de
    /// DrDXT : rien n'est réécrit, il n'est plus suivi. « Retiré » ne se dit que relu à l'origine.</summary>
    internal async Task SurveillerLaCarteEnJeuAsync(ITweak levier, IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> parties,
        DrDxt.Application.Tweaks.ReglagesRetenus retenus)
    {
        // Lancée sans attente après chaque partie : son exception se dit au lieu de se perdre ; le réglage reste suivi.
        try { await SurveillerLaCarteEnJeuSansFiletAsync(levier, parties, retenus); }
        catch (Exception ex) { VerdictReposCarte = RaisonLisible.Dire(ex, _localize); }
    }

    private async Task SurveillerLaCarteEnJeuSansFiletAsync(ITweak levier, IReadOnlyList<DrDxt.Application.Parties.PartieRelevee> parties,
        DrDxt.Application.Tweaks.ReglagesRetenus retenus)
    {
        // Après une partie relevée, sans clic : aucune invite Windows ; un retrait refusé reste suivi et se dit.
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        if (retenus.Charger() is not { } r || r.TweakId != levier.Id) return;
        var jeu = parties.Where(p => p.Debut >= r.Le).GroupBy(p => p.Jeu, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        if (jeu is null) return;
        // Jugé jusqu'au changement suivant, comme les bilans : un autre réglage, un pilote ou Windows Update fait après lui
        // porterait sa perte. Tant que les mises à jour de la machine ne sont pas lues, rien ne se retire.
        if (InstantsDesMisesAJour() is not { } machine) return;
        var suivant = (JournalChangements?.Lire() ?? []).Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true)
            .Select(c => c.Quand).Concat(machine).Where(q => q > r.Le + MemeInstant).Order()
            .Select(q => (DateTimeOffset?)q).FirstOrDefault();
        if (DrDxt.Application.Parties.ComparaisonDesParties.Juger(parties, r.Le, jeu, suivant) != DrDxt.Application.Parties.VerdictEnJeu.Pire) return;
        var snapshot = new TweakSnapshot(r.TweakId, r.OriginalValueJson, r.AppliedValueJson);
        if (EtatDeLaPose(snapshot) != Pose.DeDrDxt)
        {
            retenus.Effacer();
            await OublierAsync(snapshot.TweakId);
            VerdictReposCarte = _localize("Carte.Repos.ChangeDepuis");
            return;
        }
        var retrait = await levier.RevertAsync(snapshot);
        // Un retrait refusé, ou relu encore posé, laisse le réglage en place : il reste suivi (le retenu reste), et l'échec est dit.
        var relu = retrait.IsOk ? Relu(snapshot) : DrDxt.Application.Tweaks.EtatApplique.Applied;
        if (relu == DrDxt.Application.Tweaks.EtatApplique.Applied)
        {
            VerdictReposCarte = string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Carte.Repos.RetraitRate"), jeu);
            return;
        }
        retenus.Effacer();
        await OublierAsync(snapshot.TweakId);
        VerdictReposCarte = string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize(
            relu == DrDxt.Application.Tweaks.EtatApplique.NotApplied ? "Carte.Repos.PireEnJeu" : "Carte.Repos.PireEnJeuNonRelu"), jeu);
    }

    /// <summary>Un levier qui n'est plus celui de DrDXT (retiré, rendu, ou changé depuis) : il sort du manifeste, puis du journal des
    /// leviers.</summary>
    private async Task OublierAsync(string id)
    {
        await RetirerDuManifesteAsync(id).ConfigureAwait(true);
        _journalLeviers?.Retirer(id);
        ChargerLeviersPoses();
    }

    /// <summary>La relecture apres la console : la meme lecture que le scan (DISM /CheckHealth, jamais d'UAC).</summary>
    internal Func<RepairFinding> RelireSanteWindows { get; set; } = () => DrDxt.App.Platform.SanteWindows.Constat();

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

    /// <summary>Au lancement : rallume le mode que le joueur avait laisse allume. Un echec se dit sur
    /// la carte et ne l'efface pas : le joueur l'avait voulu, le prochain lancement reessaiera.</summary>
    public async Task RallumerPlafondEnvoiAsync()
    {
        // Au lancement, sans clic : le helper déjà ouvert pose la protection, sinon la carte dit qu'elle attend un clic.
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        if (MemoirePlafond?.Lire() is not { } retenu) return;
        RetenirMesureEnvoi(retenu.PlafondBps, retenu.Exe);
        // Appele au lancement sans etre attendu : une exception s'y perdrait sans un mot, et
        // l'ecran laisserait croire la protection revenue. Elle est dite comme un refus.
        try
        {
            // Service en marche sans rien de retenu (protection allumee avant lui) : elle lui est transmise, sinon
            // la carte dirait « le service la porte » sans que rien ne la porte.
            var transmettre = ServiceDemarrageActif?.Invoke() == true && ServicePorteUnRetenu?.Invoke() == false;
            var issue = transmettre
                ? await ActiverEtRetenirAsync(retenu.Exe, retenu.PlafondBps)
                : await ActiverPlafondEnvoiAsync();
            if (issue is { IsOk: false } echec)
                ErreurPlafondEnvoi = RaisonLisible.Echec(echec, _localize("Ping.Protection.Echec"), _localize);
        }
        catch (Exception ex)
        {
            ErreurPlafondEnvoi = $"{_localize("Ping.Protection.Echec")}{Docteur.DeuxPoints}{RaisonLisible.Dire(ex, _localize)}";
        }
        SignalerPlafondEnvoi();
    }

    // La protection retenue part aussi au service de demarrage, qui la porte sans DrDXT ouvert.
    // Par le helper, seul a ecrire sous %ProgramData%\DrDxt. Un refus (service absent de cette version,
    // helper qui ne connait pas l'ordre) ne change rien a la protection de l'App : elle reste active.
    private async Task<Result> RetenirPourLeServiceAsync(string exe, long plafondBps)
    {
        if (_helper is null) return Result.Fail(ErrorCodes.TweakNotApplicable, "helper absent");
        try { return await _helper.ExecuteAsync(new RetainServiceUploadCapOp(exe, plafondBps), CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Result.Fail(ErrorCodes.IoFailure, ex.Message); }
    }

    // Service en marche, l'App ne pose rien elle-meme ; un refus de retenir (UAC declinee) laissait
    // la carte dire « active » alors que personne ne portait la protection. Ce refus-la eteint et se dit.
    private async Task<Result> ActiverEtRetenirAsync(string exe, long plafondBps)
    {
        if (await ActiverPlafondEnvoiAsync() is { IsOk: false } echec) return echec;
        var retenu = JeuPorteParLeService(exe) ? await RetenirPourLeServiceAsync(exe, plafondBps) : Result.Ok();
        if (_porteeParLeService && !retenu.IsOk)
        {
            await DesactiverPlafondEnvoiAsync();
            return retenu;
        }
        MemoirePlafond?.Retenir(new(exe, plafondBps, DateTimeOffset.Now));
        return Result.Ok();
    }

    private async Task<Result> OublierPourLeServiceAsync()
    {
        if (_helper is null) return Result.Fail(ErrorCodes.TweakNotApplicable, "helper absent");
        try { return await _helper.ExecuteAsync(new ForgetServiceUploadCapOp(), CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Result.Fail(ErrorCodes.IoFailure, ex.Message); }
    }

    /// <summary>Le résultat du retrait, relu par le helper ; refusé, le bail du helper le rejoue d'ici
    /// <see cref="DrDxt.Application.Network.PlafondEnvoiPendantLeJeu.BailSecondes"/> s.</summary>
    public async Task<Result> DesactiverPlafondEnvoiAsync()
    {
        _battementPlafond?.Cancel();
        var retrait = _modePlafond is { } mode ? await mode.ArreterAsync(CancellationToken.None) : Result.Ok();
        _modePlafond = null;
        _porteeParLeService = false;
        return retrait;
    }
}
