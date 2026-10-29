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
