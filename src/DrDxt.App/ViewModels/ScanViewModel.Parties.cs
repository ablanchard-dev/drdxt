using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Core;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

/// <summary>
/// « Relever mes parties ». Éteint par défaut ; allumé, DrDXT prend
/// une capture courte toutes les trois minutes pendant une partie et garde, à la fin, une ligne : FPS et 1 % low
/// médians, et ce qui limitait (processeur ou carte). Le relevé continue DrDXT fermé : voir ReleveAuDemarrage.
/// </summary>
public partial class ScanViewModel
{
    /// <summary>Le stockage du choix et des parties. <c>null</c> dans les tests : rien n'est écrit.</summary>
    internal PartiesMemorisees? MemoireParties { get; set; }

    [ObservableProperty] private bool _releveDesPartiesActif;
    private CancellationTokenSource? _releveDesParties;

    public ObservableCollection<string> LignesParties { get; } = [];
    public bool ADesParties => LignesParties.Count > 0;
    // Les lignes de changements ne sont pas des parties ; « aucune partie relevée » reste vrai sans elles.
    private int _partiesAffichees;
    public bool AucunePartie => _partiesAffichees == 0;

    public string LabelPartiesTitre => _localize("Parties.Titre");

    // La carte se lit en deux temps.
    // En tête, ce qui compte (capture ratée, retraits, gains, changements, profil de chaque jeu) ; le détail (overlays,
    // route, une ligne par partie) se replie sous un lien. LignesParties garde tout, dans le même ordre.
    private int _lignesDeDetail;

    /// <summary>Les changements pas encore jugés en jeu, en tableau.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LigneEnAttente> ChangementsEnAttente { get; } = new();
    public bool ADesChangementsEnAttente => ChangementsEnAttente.Count > 0;
    public string LabelEnAttenteChangement => _localize("Changement.Tableau.Changement");
    public string LabelEnAttenteDate => _localize("Changement.Tableau.Date");
    public string LabelEnAttenteEnJeu => _localize("Changement.Tableau.EnJeu");
    public IReadOnlyList<string> LignesPartiesResume => LignesParties.Take(LignesParties.Count - _lignesDeDetail).ToList();
    public IReadOnlyList<string> LignesPartiesDetail => LignesParties.Skip(LignesParties.Count - _lignesDeDetail).ToList();
    public bool ADuDetailDeParties => _lignesDeDetail > 0;
    public string LabelDetailParties => _localize("Parties.Detail");
    public string LabelCommentCaMarche => _localize("Parties.CommentCaMarche");

    private void AnnoncerResumeEtDetail()
    {
        OnPropertyChanged(nameof(LignesPartiesResume));
        OnPropertyChanged(nameof(LignesPartiesDetail));
        OnPropertyChanged(nameof(ADuDetailDeParties));
    }

    // ── « prêt à jouer » en quelques secondes ───────────────────────────────────
    public ObservableCollection<string> LignesPretAJouer { get; } = [];
    [ObservableProperty] private bool _pretAJouerEnCours;
    public string LabelPretAJouer => _localize("PretAJouer.Bouton");

    /// <summary>La mesure réelle ; l'appli la pose au démarrage, <c>null</c> = bouton sans effet (tests).</summary>
    internal Func<CancellationToken, Task<EtatAvantPartie>>? MesurerAvantPartie { get; set; }

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task VerifierPretAJouer()
    {
        if (MesurerAvantPartie is null || PretAJouerEnCours) return;
        PretAJouerEnCours = true;
        LignesPretAJouer.Clear();
        LignesPretAJouer.Add(_localize("PretAJouer.EnCours"));
        try
        {
            var e = await MesurerAvantPartie(CancellationToken.None);
            var genes = PretAJouer.Genes(e, _localize);
            LignesPretAJouer.Clear();
            LignesPretAJouer.Add(_localize(genes.Count == 0 ? "PretAJouer.Ok" : "PretAJouer.AGenes"));
            foreach (var g in genes) LignesPretAJouer.Add(g);
            LignesPretAJouer.Add(PretAJouer.Mesure(e, _localize));
        }
        catch (Exception)
        {
            LignesPretAJouer.Clear();
            LignesPretAJouer.Add(_localize("PretAJouer.Echec"));
        }
        finally { PretAJouerEnCours = false; }
    }

    /// <summary>Les bilans des changements de DrDXT bornés aussi par les pilotes et Windows Update : le gain d'un pilote ne
    /// passe pas pour celui d'un levier. Tant qu'elles ne sont pas lues, rien ne se fige.</summary>
    private IReadOnlyList<(Changement C, BilanDuChangement B)> BilansBornes(IReadOnlyList<Changement> journal, IReadOnlyList<PartieRelevee> parties)
    {
        var machine = InstantsDesMisesAJour();
        return BilansDesChangements(journal, parties, machine is null ? null : JournalChangements, machine ?? []);
    }

    /// <summary>Ce que DrDXT a posé et garde, chacun annulable depuis l'écran, et pas seulement
    /// juste après un réglage de carte graphique : un levier d'« Optimiser » n'attend pas le retrait automatique.</summary>
    public ObservableCollection<LevierPose> LeviersPoses { get; } = [];

    public string LabelLeviersTitre => _localize("Leviers.Titre");
    public string LabelLeviersDescription => _localize("Leviers.Description");
    public string LabelLeviersAnnuler => _localize("Leviers.BoutonAnnuler");

    /// <summary>GARDE2 : les vidages à chaud du pilote graphique, branchés par l'App ; null = non lus.</summary>
    internal Func<IReadOnlyList<(DateTimeOffset Quand, int Code)>?>? LireVidagesCarte { get; set; }

    /// <summary>Au lancement : les parties gardées, et le relevé relancé si le joueur l'avait laissé allumé.</summary>
    internal void ChargerParties()
    {
        if (MemoireParties is null) return;
        var parties = MemoireParties.Lire();
        Afficher(parties);
        // Un essai en cours se dit dès le lancement, avec son bouton d'arrêt.
        DireEssai(parties);
        OnPropertyChanged(nameof(EssaiEnCours));
        if (MemoireParties.EstAllume() && !ReleveDesPartiesActif) _ = BasculerReleveDesPartiesCommand.ExecuteAsync(null);
    }

    /// <summary>Les changements datés, jugés sur les parties. L'appli pose le fichier réel au
    /// démarrage ; <c>null</c> = rien n'est noté (les tests qui ne le vérifient pas).</summary>
    internal JournalDesChangements? JournalChangements { get; set; }

    /// <summary>L'heure des changements notés et des parties relevées ; les tests rejouent des jours sur une horloge
    /// factice.</summary>
    internal Func<DateTimeOffset> Horloge { get; set; } = () => DateTimeOffset.Now;
    // Le dernier démarrage, rapide ou complet, et pas seulement le compteur depuis le démarrage.
    internal Func<DateTimeOffset> DemarrageDuPc { get; set; } = () => DrDxt.Detection.Hardware.DemarrageDuSysteme.Valeur;

    private SettingsState? _reglagesLus;
    private HardwareInventory? _materielLu;

    /// <summary>Le levier d'un catalogue par son identifiant. L'appli le pose au démarrage ;
    /// <c>null</c> = aucun retrait automatique (les tests qui ne le vérifient pas).</summary>
    internal Func<string, ITweak?>? LevierParId { get; set; }

    /// <summary>Le levier de l'essai en alternance, seul : le relevé de fond le branche sans <see cref="LevierParId"/>, pour
    /// que l'essai avance l'App fermée sans qu'aucun autre levier ne se pose ni ne se retire sans fenêtre.</summary>
    internal Func<string, ITweak?>? LevierDeLEssaiParId { get; set; }

    /// <summary>Le helper répond-il déjà, sans être lancé ? Le retrait automatique, sans geste, ne fait jamais surgir
    /// d'invite UAC : helper absent, rien n'est retiré, la carte dit « annule-le d'un clic ». <c>null</c> (tests) =
    /// joignable.</summary>
    internal Func<Task<bool>>? HelperJoignableSansUac { get; set; }

    /// <summary>Le retrait lancé par le dernier affichage, pour que les tests l'attendent au lieu de
    /// compter sur un helper factice qui rend une tâche déjà finie.</summary>
    internal Task DernierRetrait { get; private set; } = Task.CompletedTask;

    /// <summary>Le levier de DrDXT qui allume chaque réglage suivi.</summary>
    internal static readonly IReadOnlyDictionary<string, string> LevierDuReglage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["windows.hags"] = "latency.hagsOn",
        ["windows.game_mode"] = "latency.gameModeOn",
        // Pas le fichier d'échange : un levier de fiabilité, qu'un « moins bien en jeu » sur trois parties ne retire pas.
    };

    /// <summary>Les réglages suivis que DrDXT allume, pour dater leur bascule sur la pose. Plus large que
    /// <see cref="LevierDuReglage"/> (déclaré après lui, qu'il copie) : le fichier d'échange se date comme les autres, il
    /// ne se retire pas.</summary>
    internal static readonly IReadOnlyDictionary<string, string> LevierQuiAllume =
        new Dictionary<string, string>(LevierDuReglage, StringComparer.Ordinal) { ["memoire.fichier_echange"] = "fixit.sane_pagefile" };

    /// <summary>Le changement bascule un réglage que pose un levier de latence (HAGS, mode Jeu) : il se juge sur la latence
    /// d'affichage, comme dans l'essai en alternance, pas sur les images qu'il coûte souvent.</summary>
#if DEBUG
    private const bool BuildDeDeveloppement = true;
#else
    private const bool BuildDeDeveloppement = false;
#endif

    private bool _retraitEnCours;

    /// <summary>Deux changements plus proches que ça sont du même instant (le journal dédoublonne déjà à la minute).</summary>
    internal static readonly TimeSpan MemeInstant = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Chaque changement jugé sur les parties d'entre lui et le changement suivant (sinon
    /// l'après de l'un portait le gain de l'autre), et son bilan figé une fois tranché (le relevé ne garde que 20 parties).
    /// </summary>

    internal static IReadOnlyList<(Changement C, BilanDuChangement B)> BilansDesChangements(IReadOnlyList<Changement> changements,
        IReadOnlyList<PartieRelevee> parties, JournalDesChangements? journal, IReadOnlyList<DateTimeOffset>? misesAJour = null)
    {
        var machine = misesAJour ?? [];
        var parDate = changements.Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true).OrderBy(c => c.Quand).ToList();
        var bilans = new List<(Changement, BilanDuChangement)>();
        for (var i = 0; i < parDate.Count; i++)
        {
            var c = parDate[i];
            // Les réglages d'un même clic basculent au même redémarrage : bornés l'un par l'autre, aucun ne se jugerait. Ils
            // se jugent ensemble (à moins de <see cref="MemeInstant"/> : « au démarrage du PC » se recalcule à quelques ms
            // près), mode Jeu daté à la pose et HAGS au redémarrage compris.
            var autres = parDate.Where(x => !DuMemeClic(x, c)).ToList();
            // L'après s'arrête où le suivant a pu commencer : son Depuis quand l'heure exacte n'est pas connue. Limite : un
            // Depuis d'avant ce changement (le suivant l'enjambe) laisserait un après vide pour toujours ; la borne reste
            // alors son Quand.
            var suivant = autres.Where(x => x.Quand > c.Quand + MemeInstant)
                .Select(x => x.Depuis is { } d && d > c.Quand && d < x.Quand ? d : x.Quand)
                .Concat(machine.Where(q => q > c.Quand + MemeInstant)).Order()
                .Select(q => (DateTimeOffset?)q).FirstOrDefault();
            var precedent = autres.Select(x => x.Quand).Concat(machine).Where(q => q < c.Quand - MemeInstant).Order()
                .Select(q => (DateTimeOffset?)q).LastOrDefault();
            var close = suivant is not null;
            var bilan = c.Fige ?? ComparaisonDesParties.Bilan(parties, c.Quand, c.Depuis, suivant, precedent, LevierDeLatence(c));
            // Figé seulement quand plus rien ne peut le corriger : fenêtre close, ou relevé plein (20 parties) dont la
            // prochaine à sortir est une partie d'avant ce changement. Figé au premier 3 contre 3, un faux verdict resterait.
            var plusAncienne = parties.Count >= ReleveDesParties.PartiesGardees ? parties.Min(p => p.Debut) : (DateTimeOffset?)null;
            var perdAvant = plusAncienne is { } a && a < c.Quand && (precedent is not { } p || a >= p);
            if (c.Fige is null && bilan.Verdict != VerdictEnJeu.PasEncoreJuge && (close || perdAvant))
            {
                try { journal?.Figer(c, bilan); }
                catch (Exception) { /* un journal non écrit se rejuge au prochain affichage */ }
            }
            bilans.Add((c, bilan));
        }
        // L'écran lit le plus récent d'abord, comme le journal.
        bilans.Reverse();
        return bilans;
    }

    /// <summary>Ce que le retrait a fait pendant cette session, gardé à travers les rafraîchissements de la carte.</summary>
    private readonly List<string> _ditsDuRetrait = [];

    private void Afficher(IReadOnlyList<PartieRelevee> parties)
    {
        LignesParties.Clear();
        foreach (var dit in _ditsDuRetrait) LignesParties.Insert(0, dit);
        ChangementsEnAttente.Clear();   // Un journal illisible ne laisse pas l'ancien tableau
        try
        {
            var tous = BilansBornes(JournalChangements?.Lire() ?? [], parties);
            // Ce que l'ensemble des changements a fait en jeu, en tête de la carte.
            if (LigneDesGains(tous, _localize) is { } gains) LignesParties.Add(gains);
            foreach (var ligne in LignesDesChangements(tous.Take(3).ToList(), _localize)) LignesParties.Add(ligne);
            foreach (var l in TableauDesChangementsEnAttente(tous.Take(3).ToList(), _localize)) ChangementsEnAttente.Add(l);
        }
        catch (Exception) { /* un journal illisible ne cache pas les parties */ }
        OnPropertyChanged(nameof(ADesChangementsEnAttente));
        // Un affichage pendant un retrait en cours attend les deux ; un retrait fini ne s'enchaîne pas (la chaîne
        // s'allongerait à chaque affichage).
        var retrait = RetirerEtDireAsync(parties);
        DernierRetrait = DernierRetrait.IsCompletedSuccessfully ? retrait : Task.WhenAll(DernierRetrait, retrait);
        _ = AfficherHistoriqueAsync(parties);
        // Le profil de chaque jeu assez joué, avant ses parties.
        foreach (var profil in ProfilDesJeux.Construire(parties)) LignesParties.Add(LigneProfil(profil, _localize));
        var avantLeDetail = LignesParties.Count;
        try
        {
            var changements = (JournalChangements?.Lire() ?? []).Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true)
                .Select(c => c.Quand).Concat(InstantsDesMisesAJour() ?? []);
            foreach (var ligne in LignesOverlays(parties, _localize, changements)) LignesParties.Add(ligne);
            if (LigneRoute(parties, _localize) is { } route) LignesParties.Add(route);
            if (LigneMecanismeCarte(parties, _localize) is { } mecanisme) LignesParties.Add(mecanisme);
        }
        catch (Exception) { /* un journal illisible ne cache pas les parties */ }
        foreach (var p in parties) LignesParties.Add(LignePartie(p, _localize));
        _lignesDeDetail = LignesParties.Count - avantLeDetail;
        if (LigneEchec(MemoireParties?.DernierEchec(), parties, _localize) is { } echec) LignesParties.Insert(0, echec);
        _partiesAffichees = parties.Count;
        ProposerDepuis(parties);
        OnPropertyChanged(nameof(ADesParties));
        OnPropertyChanged(nameof(AucunePartie));
        AnnoncerResumeEtDetail();
        // Chaque partie relevée juge aussi le réglage de la carte gardé au repos, s'il y en a un.
        _ = SurveillerSiPoseAsync(parties);
    }

    private readonly DrDxt.Detection.Cpu.CpuFrequencyReader _frequenceCpu = new();
    private DrDxt.Detection.EntreesDuJoueur? _entreesDuJoueur;
    private IReadOnlyList<int>? _coeursRapides;

    /// <summary>Un seul relevé à la fois entre l'App ouverte et celui de fond (<see cref="VerrouDuReleve"/>).
    /// <c>null</c> = pas de verrou (tests).</summary>
    internal Func<IDisposable?>? PrendreVerrouReleve { get; set; }

    /// <summary>Une partie finie par la veille : gardée sur le disque, puis la carte se refait (jugement des changements et
    /// retrait de ce qui coûte compris).</summary>
    internal void EnregistrerPartie(PartieRelevee p, SynchronizationContext? ui = null)
    {
        // Le banc de l'époque voyage avec la partie : comparée plus tard à un banc pris après un changement de
        // BIOS, elle se dirait ralentie à tort.
        try { p = p with { BancMhz = _machineId is { } id ? _cpuBaseline?.Reference(id())?.FrequenceSoutenueMhz : null }; }
        catch (Exception) { /* sans banc, la partie se garde quand même ; elle ne se comparera pas à un autre banc */ }
        // La partie porte l'état de l'essai en alternance qui l'a vue jouer.
        p = MarquerEssai(p);
        var ajout = MemoireParties?.Ajouter(p);
        IReadOnlyList<PartieRelevee> liste = ajout?.Liste ?? [p];
        // Non écrite, la partie est montrée mais perdue au relancement : la carte le dit en tête.
        void Montrer()
        {
            Afficher(liste);
            if (ajout is { Ecrite.IsOk: false }) LignesParties.Insert(0, _localize("Parties.NonGardee"));
        }
        if (ui is null) Montrer(); else ui.Post(_ => Montrer(), null);
        // Sur le fil de l'écran, comme l'affichage (poser un levier rafraîchit des listes liées à l'écran). Personne n'a
        // cliqué : les bascules de l'essai n'ouvrent aucune invite Windows (SansGesteDuJoueur).
        if (ui is null) AvancerEssaiSansGeste(liste);
        else ui.Post(_ => AvancerEssaiSansGeste(liste), null);
    }

    private void AvancerEssaiSansGeste(IReadOnlyList<PartieRelevee> liste)
    {
        // La méthode asynchrone garde le contexte de son appel : la portée la suit jusqu'au bout, puis se referme ici.
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        DerniereAvanceeEssai = AvancerPuisPrevenirAsync(liste);
    }

    private async Task AvancerPuisPrevenirAsync(IReadOnlyList<PartieRelevee> liste)
    {
        await AvancerEssaiAsync(liste).ConfigureAwait(true);
        PrevenirSiLEssaiAttend();
    }

    /// <summary>Le relevé de fond la branche (une notification de Windows) ; null dans l'App, qui montre l'essai à l'écran.</summary>
    internal Action<string>? NotifierEssaiEnAttente { get; set; }

    /// <summary>Une App avec sa fenêtre tourne-t-elle ? Elle montre déjà l'essai : le relevé ne notifie pas.</summary>
    internal Func<bool> AppOuverte { get; set; } = () => false;

    /// <summary>Le dernier état d'essai dont le joueur a été prévenu : un même essai en attente ne se redit pas à chaque partie.</summary>
    private string? _essaiPrevenu;

    /// <summary>Sans le helper déjà lancé, le relevé de fond ne pose rien : un essai qui attend l'App ouverte resterait figé en silence. Jeu fermé, le joueur
    /// est prévenu une fois par attente.</summary>
    private void PrevenirSiLEssaiAttend()
    {
        if (NotifierEssaiEnAttente is not { } notifier || MemoireEssai?.Lire() is not { } e || !EssaiEnAttente || JeuOuvertPourLEssai()
            || AppOuverte()) return;
        var cle = $"{e.LevierId}|{Effectif(e).Depuis:O}|{e.PoseMaintenant}";
        if (cle == _essaiPrevenu) return;
        _essaiPrevenu = cle;
        notifier(string.Format(CultureInfo.CurrentCulture, _localize("Essai.Notification"), Titre(e.LevierId)));
    }

    // La veille dure toute la session : sans exécutions simultanées, le bouton resterait grisé.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task BasculerReleveDesParties()
    {
        // L'interrupteur se coche tout seul au clic : une erreur avant le changement d'état le laisserait faux.
        try { await BasculerReleveCoeurAsync(); }
        finally { OnPropertyChanged(nameof(ReleveDesPartiesActif)); }
    }

    /// <summary>Une capture de la veille, en pleine partie et sans clic : PresentMon manquant ne s'installe que par un helper
    /// déjà ouvert, jamais par une invite Windows en jeu. Sinon la capture échoue, la veille note pourquoi, le prochain scan
    /// l'installera.</summary>
    internal async Task<BenchmarkResult?> CapturerSansGesteAsync(int pid, TimeSpan duree, CancellationToken ct)
    {
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        using var enPartie = DrDxt.Measurement.MesureEnPartie.Ouvrir();
        var r = await _benchmark.RunAsync(pid, duree, 1, ct);
        // La raison de l'échec remonte à la veille, qui la note.
        return r.IsOk ? r.Value : throw new InvalidOperationException(r.Error is { } e ? RaisonDuRate(e.Code, e.Message) : Docteur.T("App.CaptureSansResultat"));
    }

    /// <summary>La raison notée d'une capture ratée. Le service de mesure injoignable y garde son code, que son message le
    /// porte ou non : la ligne de la carte le reconnaît et dit quoi faire, au lieu de « réessaie à ta prochaine partie ».</summary>
    internal static string RaisonDuRate(string code, string message)
        => code == ErrorCodes.PmonUnavailable && !message.Contains(code, StringComparison.Ordinal) ? $"{message} ({code})" : message;

    private async Task BasculerReleveCoeurAsync()
    {
        if (ReleveDesPartiesActif)
        {
            _releveDesParties?.Cancel();
            ReleveDesPartiesActif = false;
            // Le relevé de fond (tâche planifiée de l'installeur, à l'ouverture de session) relit ce choix et
            // s'arrête de lui-même ; rien à inscrire ni à retirer ici.
            MemoireParties?.Eteindre();
            return;
        }

        _releveDesParties = new CancellationTokenSource();
        ReleveDesPartiesActif = true;
        MemoireParties?.Allumer();
        var ui = SynchronizationContext.Current;
        var veille = new VeilleDesParties(
            // Le filtre du plafond réseau (ni navigateur, ni lecteur, ni programme de Windows) : une fenêtre plein écran
            // (YouTube en F11, VLC) chasserait de vraies parties de l'anneau de 20.
            // Le jeu choisi qui tourne apprend ici son chemin complet, App ouverte comme relevé de fond.
            jeu: () =>
            {
                if (_gameLocator.LocateRunningGamePid() is not { } pid || pid <= 0 || NomExeDuProcessus(pid) is not { } exe) return null;
                var chemin = CheminDuProcessus(pid);
                if (!DrDxt.Core.NomExeDeJeu.EstUnJeuPossible(exe, chemin)) return null;
                MemoireJeuChoisi?.Apprendre(chemin);
                return (exe, pid);
            },
            capturer: CapturerSansGesteAsync,
            classer: r => DrDxt.Detection.Scoring.BottleneckClassifier.Classify(r, gpuThrottling:
                DrDxt.Detection.Scoring.BottleneckClassifier.Bridage(r.Materiel)).State,
            enregistrer: p => EnregistrerPartie(p, ui),
            patienter: Task.Delay,
            // Mêmes cœurs et même compteur que le banc ; le plus rapide, pas la moyenne (voir FrequenceMaxDe).
            processeur: () => (_frequenceCpu.FrequenceMaxDe(_coeursRapides ??= DrDxt.Detection.Cpu.CpuTopology.CoeursRapides(new DrDxt.Detection.Cpu.CpuTopology().Lire())),
                DrDxt.Detection.Cpu.PuissancePaquetReader.LireWatts(new DrDxt.Detection.Wmi.ManagementWmiQuery()),
                DrDxt.Detection.Cpu.TemperatureProcesseur.Lire()?.Celsius),
            memoireVideo: DrDxt.Detection.Gpu.MemoireVideoOccupee.Lire,
            // Les programmes à côté du jeu, lus sur 600 ms juste après la capture.
            voisins: pid => _voisins.LireParNom(pid, CancellationToken.None)?.Select(v => (v.Nom, v.ProcesseurPourcent)).ToList(),
            signalerEchec: raison => MemoireParties?.NoterEchec(new PartiesMemorisees.EchecDeCapture(Horloge(), raison)),
            // La marque « partie en cours » ; restée ouverte, elle dit qu'un arrêt est tombé en partie.
            suivre: p => MemoireParties?.NoterEnCours(p),
            // La carte dédiée lue pendant chaque capture : NVML sur une GeForce (bridage par la puissance, fréquence), ADLX sur
            // une Radeon (puissance, fréquence, point chaud, ventilateur ; pas de raisons de bridage publiées).
            lireCarte: () => DrDxt.Detection.SondeDeLaCarte.Pour(_materielLu is { } hw ? CartesGraphiques.CarteDediee(hw) : null).Lire(),
            // Le processeur chaque seconde (PawnIO ne répond qu'en administrateur : le relevé de fond, lancé sans droits
            // par la tâche planifiée, le lit par le service DrDXT), et l'activité du joueur, pour mettre menus et lobby à part.
            temperatureProcesseur: () => DrDxt.Detection.Cpu.TemperatureProcesseur.Lire()?.Celsius,
            entrees: (_entreesDuJoueur ??= new DrDxt.Detection.EntreesDuJoueur()).ActiveRecemment,
            // Cœur le plus chaud, cœur 0 et raisons de freiner, chaque seconde (PawnIO, administrateur). Posé
            // seulement là où il doit répondre (PawnIO installé, Intel) : ailleurs, un « non lu » à chaque partie serait du bruit.
            etatProcesseur: DrDxt.Detection.Cpu.TemperaturePawnIo.Installe() && DrDxt.Detection.Cpu.TemperaturePawnIo.ProcesseurIntel()
                ? DrDxt.Detection.Cpu.TemperaturePawnIo.LireEtat : null,
            horloge: Horloge,
            // La carte, quelle que soit sa marque, par la bibliothèque de son pilote ; sans HWiNFO.
            carte: () => DrDxt.Detection.Gpu.TemperatureCarte.Lire().Celsius,
            overlays: () => OverlaysEnPartie.Reconnaitre(System.Diagnostics.Process.GetProcesses().Select(p => { using (p) return p.ProcessName; })),
            reseau: DrDxt.App.Platform.MesureAvantPartie.ReseauAsync,
            carteDuJeu: pid => DrDxt.Detection.Gpu.CarteDuJeu.Lire(pid) is { } c ? (c.Carte, c.Integree ? c.CarteDediee : null) : null,
            horsCarte: DrDxt.Detection.Gpu.MemoireVideoOccupee.HorsCarteMo,
            // Option de développement : les images de chaque partie, au dossier jetable seulement (jamais en version publiée).
            garderLesImages: ImagesDesParties.Activee(BuildDeDeveloppement, Environment.GetEnvironmentVariable)
                ? (p, c) => ImagesDesParties.Ecrire(DrDxt.Core.DossierDonnees.Chemin(ImagesDesParties.SousDossier), p, c)
                : null);
        var jeton = _releveDesParties.Token;
        try
        {
            await Task.Run(() => PrendreVerrouReleve is { } prendre
                ? VerrouDuReleve.VeillerSeulAsync(prendre, Veiller, Task.Delay, jeton)
                : Veiller(jeton));
            // La marque restée ouverte ne se relève qu'avec le verrou du relevé, jamais sous un
            // relevé de fond qui suit encore la partie.
            Task Veiller(CancellationToken ct)
            {
                MemoireParties?.ReleverInterrompue(Horloge());
                return veille.VeillerAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* la veille s'arrête : fermeture ou relevé coupé par le joueur */ }
        catch (Exception)
        {
            // Sans ceci, une erreur imprévue arrêterait le relevé en silence, l'interrupteur resté sur « allumé ». Le choix du
            // joueur reste noté : le prochain lancement le rallume, et le relevé de fond (tâche planifiée) continue.
            ReleveDesPartiesActif = false;
            DireAuDemarrage(_localize("Parties.Releve.ArreteSurErreur"));
        }
    }

    /// <summary>Une ligne de plus au message du haut de l'écran.</summary>
    private void DireAuDemarrage(string ligne)
        => MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? ligne : MessageDemarrage + Environment.NewLine + ligne;
}
