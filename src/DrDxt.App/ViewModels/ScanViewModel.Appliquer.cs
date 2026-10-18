using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Profiles;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>« Optimiser » : juger ce qui peut être posé, prendre un point de restauration, poser et relire, puis résumer.</summary>
public sealed partial class ScanViewModel
{
    // -----------------------------------------------------------------------
    // ApplyCommand : « Optimiser ».
    //
    //   1. Juger d'abord : diagnostic du scan → CandidateSelector sur les graines du profil.
    //      Rien a tenter ⇒ on le dit, avec les motifs.
    //   2. Point de restauration verifie.
    //   3. Ce qui ne se mesure pas (effet au redemarrage) : le moteur, sans baseline (apply,
    //      relecture, revert sur echec), puis le manifeste signe pour le reconciliateur.
    //   4. Un levier a chaud, en session mesuree a-b-b-a. Jamais deux : un seul KeepPolicy.
    //
    // Le mode lot (tout le plan, re-bench global) n'est plus atteignable d'ici. Le tuning GPU
    // reste une action separee (ApplyGpuTuningCommand).
    // -----------------------------------------------------------------------
    /// <summary>Les leviers posés depuis le dernier scan : le plan du scan ne les connaît pas encore comme posés, et
    /// sans cette liste « Prochaine étape : Optimiser » resterait affiché après la pose. Vidé au scan.</summary>
    private readonly HashSet<string> _posesDepuisLeScan = new(StringComparer.Ordinal);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        ApplyBlockedMessage = null;
        MoatUnarmedMessage = null;
        ResultatOptimisation = null;
        if (_consumption is null) return; // pas de scan : la garde CanExecute l'empêche déjà.

        // (1) juger avant d'encaisser : pur et gratuit. Un clic qui n'a rien à poser ne crée pas de point de restauration.
        var jugement = await JugerAsync(GrainesOptimiser()).ConfigureAwait(true);
        // Sans jeu ouvert, un réglage qui se mesure en jeu était
        // « écarté, lance ton jeu, refais un scan ». L'essai en alternance sait attendre les parties : le clic vaut accord.
        // Il part aussi quand il y avait autre chose à poser, comme l'étape en tête le promet.
        var essaiAFaire = _gamePid == 0 && LevierAEssayer is not null && EssaiProposable;
        if (jugement.RienATenter && !essaiAFaire && !ReposCarteAOuvrir)
        {
            // Les arrêts ne bloquent plus ; rien à tenter veut dire rien à tenter. Et jamais sans suite (U5).
            ApplyBlockedMessage = _localize(KeyNothingToApply);
            ResultatOptimisation = string.Join(Environment.NewLine,
                new[] { Resumer(jugement, null, null), Ordonnance(reposFait: ReposCarteEnAttente) }.Where(l => l.Length > 0));
            return;
        }

        // (2) la porte : un point de restauration vérifié avant tout ce qui écrit. le geste du
        // docteur et l'essai lancé d'ici passaient avant elle, alors que l'écran l'annonçait.
        var gate = await _safety.EnsureRestorePointAsync().ConfigureAwait(true);
        if (!gate.IsOk)
        {
            ApplyBlockedMessage = MessageDuPoint(gate);
            return;
        }

        // Les gestes que le
        // docteur propose et que DrDXT sait faire seul étaient cachés dans « Agir ». Optimiser les fait d'abord, et le dit
        // en tête de son résultat, quelle que soit la suite.
        IReadOnlyList<string> gestes = [];
        string? essai = null;
        try
        {
            gestes = await GestesDuDocteurAsync().ConfigureAwait(true);
            if (!jugement.RienATenter) await PoserAsync(jugement).ConfigureAwait(true);
            var pose = ResultatOptimisation;
            if (essaiAFaire && await EssayerEnJeuSansJeuOuvertAsync().ConfigureAwait(true))
            {
                essai = ResultatOptimisation;
                ResultatOptimisation = pose;
            }
        }
        finally
        {
            // Optimiser finit toujours par une
            // ordonnance. Le soir même (« c'est relou que DrDXT ne s'occupe pas plus des points à corriger avant opti ») :
            // ce qui reste à ta main s'écrivait après « Posé » et « C'est parti », enterré. Le docteur le dit en tête,
            // puis ce qu'il a fait, puis la suite.
            var (tete, fin) = OrdonnanceEnDeux(reposFait: ReposCarteEnAttente);
            ResultatOptimisation = string.Join(Environment.NewLine,
                new[] { tete }.Concat(gestes).Append(ResultatOptimisation ?? string.Empty).Append(essai ?? string.Empty)
                    .Append(fin).Where(l => l.Length > 0));
        }
        // (6) L'étape en tête suit Optimiser, pas seulement le scan : Rejugée sur ce qui reste.
        await JugerCeQuiResteAPoserAsync().ConfigureAwait(true);
    }

    /// <summary>Les points « à faire d'abord » encore vrais : le mode NVIDIA au global sort de la liste une fois le repos
    /// rendu par DrDXT (reconnu par sa clé, jamais par son texte). Tous, pas les trois visibles sous le verdict : l'ordonnance
    /// les compte.</summary>
    internal IEnumerable<PointAFaire> PointsAFaireRestants(bool reposFait) =>
        CarteDeLaMachine.AFaire(Domaines, int.MaxValue).Where(p => !(reposFait && p.Entree.Cle == "Finding.Gpu.PerfMaxGlobal"));

    /// <summary>Ce qui reste vraiment à la main du joueur : les points à faire, moins celui qu'Optimiser va faire ou a fait
    /// lui-même (le repos de la carte).</summary>
    internal IEnumerable<PointAFaire> PointsALaMain => PointsAFaireRestants(ReposCarteEnAttente || ReposCarteAOuvrir);

    /// <summary>La conclusion du docteur après Optimiser, construite sur ce que le scan a déjà dit (la liste
    /// « à faire d'abord » et le compte des réglages lus) : jamais une impasse.</summary>
    internal string Ordonnance(bool reposFait)
    {
        var (tete, fin) = OrdonnanceEnDeux(reposFait);
        return tete + Environment.NewLine + fin;
    }

    /// <summary>L'ordonnance en deux morceaux : la tête (ce qui est à ta main, d'abord) s'écrit avant ce qu'Optimiser a
    /// fait ; la fin (ce qui est déjà en place, la suite) après.</summary>
    internal (string Tete, string Fin) OrdonnanceEnDeux(bool reposFait)
    {
        var tete = new List<string> { _localize("Ordonnance.Titre") };
        var aLaMain = PointsAFaireRestants(reposFait).ToList();
        if (aLaMain.Count > 0)
        {
            tete.Add(string.Format(CultureInfo.CurrentCulture, _localize("Ordonnance.ATaMain"), aLaMain.Count));
            tete.AddRange(aLaMain.Take(3).Select(p => $"• {p.Entree.Titre.TrimEnd('.', ' ')} ({p.Domaine.Nom})"));
            tete.Add(_localize("Ordonnance.Guide"));
        }
        else tete.Add(_localize("Ordonnance.RienAMain"));
        var fin = new List<string>();
        if (Coverage is { ApplicableLevers: > 0 } c)
            fin.Add(string.Format(CultureInfo.CurrentCulture, _localize("Ordonnance.DejaEnPlace"), c.KnownLevers, c.ApplicableLevers));
        fin.Add(_localize("Ordonnance.Suite"));
        return (string.Join(Environment.NewLine, tete), string.Join(Environment.NewLine, fin));
    }

    /// <summary>Ce qu'Optimiser fait sans mesure en jeu, parce que le scan l'a constaté et que DrDXT a son
    /// geste : aujourd'hui, rendre le repos à la carte quand le pilote NVIDIA est sur « performances maximales » au global
    /// (le geste prouve lui-même son effet au repos et se retire seul sans baisse nette). Une ligne par geste tenté.</summary>
    internal async Task<IReadOnlyList<string>> GestesDuDocteurAsync()
    {
        var lignes = new List<string>();
        if (ReposCarteAOuvrir)
        {
            await RendreLeReposALaCarteCommand.ExecuteAsync(null).ConfigureAwait(true);
            lignes.Add(string.Format(CultureInfo.CurrentCulture, _localize("Apply.GesteFait"), _localize("Carte.Repos.Titre"),
                ReposCarteEnAttente ? _localize("Carte.Repos.EnAttente") : ErreurReposCarte ?? _localize("Carte.Repos.Refuse")));
        }
        return lignes;
    }

    /// <summary>Ce qu'Optimiser pose une fois le jugement rendu et la porte passée.</summary>
    private async Task PoserAsync(JugementApply jugement)
    {
        // (4) ce qui ne se mesure pas : un A/B avant redemarrage mesurerait le neant. Le moteur
        //     applique, relit et revient sur echec ; baseline null = aucune pretention de gain.
        ApplyReport? auRedemarrage = null;
        string? pasInscrit = null;
        // Les réglages sans effet sur les images (heures d'activité) partent aussi sans mesure.
        // Chacun dans son propre lot. Le moteur défait un lot entier sur un échec : un mode d'écran
        // que le joueur ne garde pas défaisait le fichier d'échange et les heures cliqués avec lui.
        var lots = (jugement.AuRedemarrage.Count > 0 ? new[] { jugement.AuRedemarrage.ToList() } : [])
            .Concat(jugement.PosesSansMesure.Select(id => new List<string> { id })).ToList();
        if (lots.Count > 0)
        {
            // L'origine de ces poses n'est notée qu'au journal des leviers, plus bas : le helper la garde jusque-là.
            using var portee = PorteeDePose.Ouvrir();
            var issues = new List<ApplyOutcome>();
            foreach (var lot in lots)
            {
                var apply = await _engine
                    .ApplyAsync(new TweakPlan(lot), baseline: null, _gamePid, CancellationToken.None)
                    .ConfigureAwait(true);
                if (apply.IsOk && apply.Value is not null) issues.AddRange(apply.Value.Outcomes);
            }
            if (issues.Count == 0)
            {
                ApplyBlockedMessage = _localize(KeyApplyFailed);
                ResultatOptimisation = Resumer(jugement, null, null);
                return;
            }
            var reussi = new ApplyReport(issues, null);
            auRedemarrage = reussi;
            // Le reconciliateur reaffirme ces leviers gardes au demarrage ; non fatal.
            await CommitDesiredStateAsync(reussi).ConfigureAwait(true);
            // Tous les leviers gardes, cote utilisateur : le scan suivant remarquera
            // s'ils ont ete defaits (le manifeste n'en porte que quatre).
            (var inscrit, pasInscrit) = InscrireAuJournal(reussi);
            if (inscrit) await portee.FermerAsync().ConfigureAwait(true);
            else portee.Dispose();
            ChargerLeviersPoses();
            var posees = reussi.Outcomes.Where(o => o.Applied && !o.AutoReverted).Select(o => o.TweakId).ToList();
            NoterPoses(posees, Horloge());
            _posesDepuisLeScan.UnionWith(posees);
            // Le mode Jeu agit tout de suite. Relevé seulement à la prochaine ouverture ou au
            // prochain scan, sa bascule était datée là, et les parties jouées entre la pose et ce relevé ne comptaient d'aucun
            // côté. Seuls les réglages à effet immédiat se relèvent ici : un levier au redémarrage n'a pas encore basculé.
            await ReleverReglagesHorsScanAsync(ReglagesAEffetImmediat).ConfigureAwait(true);
        }

        // (5) un levier a chaud, mesure en jeu.
        var session = jugement.AChaud is { } aChaud
            ? await MesurerEnJeuAsync(aChaud).ConfigureAwait(true)
            : null;

        ResultatOptimisation = Resumer(jugement, auRedemarrage, session) + (pasInscrit is null ? "" : Environment.NewLine + pasInscrit);
    }

    /// <summary>Les leviers gardés entrent au journal des leviers, celui que « Annuler » relit. Raté, ils restent posés sans
    /// ligne pour les annuler : la phrase le dit, sans promettre que DrDXT les rende. Sans journal branché, rien n'est dit.</summary>
    private (bool Inscrit, string? Phrase) InscrireAuJournal(ApplyReport gardes)
    {
        if (_journalLeviers is null) return (false, null);
        if (_journalLeviers.Enregistrer(gardes)) return (true, null);
        var titres = string.Join(", ", gardes.Outcomes.Where(o => o.Applied && !o.AutoReverted).Select(o => Titre(o.TweakId)));
        return (false, string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasInscrit"), titres));
    }

    /// <summary>Le jugement d'Optimiser, rendu dès la fin de l'analyse plutôt qu'au clic : s'il n'y a rien que DrDXT
    /// puisse poser seul, une phrase vraie le dit sous le bouton (les gestes des domaines, « Agir », restent).</summary>
    [ObservableProperty]
    // Le bouton reste visible même quand rien n'est à poser : DrDXT dit ce qu'il voit, il ne cache pas le geste.
    private bool _rienAPoserSeul;
    public string LabelRienAPoserSeul => _localize("Apply.RienAPoserSeul");
    partial void OnRienAPoserSeulChanged(bool value)
    {
        OnPropertyChanged(nameof(ApercuOptimisation));
        OnPropertyChanged(nameof(ApplyHintKey));
        OnPropertyChanged(nameof(ApplyHint));
    }

    private async Task JugerCeQuiResteAPoserAsync()
    {
        var (instabilite, arrets) = await Task.Run(LireStabiliteSure).ConfigureAwait(true);
        var diagnostic = DiagnosticDeScan.Construire(_goulotScan, _scannedBaseline, instabilite, arrets, _pressionThreads);
        RienAPoserSeul = JugementApply.Juger(diagnostic, GrainesOptimiser(), jeuEnCours: _gamePid > 0).RienATenter;
        // Le levier à chaud qu'un essai en alternance jugerait sur tes parties (il n'a pas besoin d'un jeu
        // lancé au scan : il se juge sur les parties qui viennent).
        LevierAEssayer = JugementApply.Juger(diagnostic, GrainesOptimiser(), jeuEnCours: true).AChaud?.Id is { } id && PeutAlterner(id) ? id : null;
        // Pourquoi la carte est ouverte ou fermee ici, avec la famille d'arret en cause.
        PhraseReglageCarte = _consumption is { HasGpuPlan: true }
            ? CandidateSelector.PhraseReglageCarte(diagnostic, DateTimeOffset.UtcNow)
            : _localize("Apply.Carte.NonProposee");
        // Les incidents du pilote ne ferment plus la carte ; la garde d'incident les surveille et rend seule.
    }

    /// <summary>Le réglage de la carte passe par Optimiser, comme les autres leviers à chaud : un
    /// à la fois, mesuré en jeu, gardé seulement avec un gain mesuré, sinon annulé (règle de la carte).</summary>
    private IReadOnlyList<string> GrainesOptimiser() =>
        _consumption is { } c
            ? c.Plan.TweakIds.Concat(c.GpuPlan.TweakIds.Where(id => !CandidateSelector.ReglagesCarteInterdits.Contains(id)))
                .Where(id => !RendusParLaGarde().ContainsKey(id) && !_posesDepuisLeScan.Contains(id)).ToList()
            : [];

    /// <summary>Les leviers que la garde d'incident a rendus il y a moins d'une semaine, avec la date
    /// du rendu. Optimiser ne les repose pas avant : sinon pose, arrêt, rendu, repose tournaient sans fin, et « si l'arrêt
    /// revient sans lui » ne se voyait jamais.</summary>
    internal IReadOnlyDictionary<string, DateTimeOffset> RendusParLaGarde()
    {
        if (JournalChangements is null) return new Dictionary<string, DateTimeOffset>();
        var maintenant = Horloge();
        return JournalChangements.Lire()
            .Where(c => c.Cle?.StartsWith(ClePoseGarde, StringComparison.Ordinal) == true
                        && maintenant - c.Quand < DrDxt.Application.Autopilot.GardeDIncident.Surveillance)
            .GroupBy(c => c.Cle![ClePoseGarde.Length..])
            .ToDictionary(g => g.Key, g => g.Max(c => c.Quand));
    }

    /// <summary>La clé du rendu par la garde. Elle commence par « pose: » : tous les lecteurs du journal (frise, bilans,
    /// stabilité, plantages) l'écartent déjà comme une pose, sans affichage.</summary>
    internal const string ClePoseGarde = "pose:garde:";

    /// <summary>La phrase « réglage de la carte ouvert / fermé chez toi », null avant un scan.</summary>
    [ObservableProperty]
    private string? _phraseReglageCarte;

    /// <summary>Optimiser, aucun jeu ouvert, un réglage à chaud à essayer : l'essai en alternance démarre.
    /// Faux quand il n'y a rien à essayer ainsi (jeu ouvert, aucun levier, essai déjà en cours) ou que la pose a échoué.</summary>
    internal async Task<bool> EssayerEnJeuSansJeuOuvertAsync()
    {
        if (_gamePid > 0 || LevierAEssayer is not { } id || !EssaiProposable) return false;
        if (!await DemarrerEssaiAsync(id).ConfigureAwait(true)) return false;
        ApplyBlockedMessage = null;
        ResultatOptimisation = string.Format(CultureInfo.CurrentCulture, _localize("Apply.EssaiLanceSansJeu"), Titre(id));
        return true;
    }

    /// <summary>Le jugement du scan sur ces graines : pur, rien n'est encaissé ni écrit.</summary>
    private async Task<JugementApply> JugerAsync(IReadOnlyList<string> graines)
    {
        var (instabilite, arrets) = await Task.Run(LireStabiliteSure).ConfigureAwait(true);
        var diagnostic = DiagnosticDeScan.Construire(_goulotScan, _scannedBaseline, instabilite, arrets, _pressionThreads);
        return JugementApply.Juger(diagnostic, graines, jeuEnCours: _gamePid > 0);
    }

    private (InstabiliteGpuObservee?, IReadOnlyList<ArretClasse>?) LireStabiliteSure()
    {
        // Non lu n'est pas « aucun incident » : le selecteur ne ferme alors rien, comme partout.
        try { return _lireStabilite?.Invoke() ?? (null, null); }
        catch { return (null, null); }
    }

    /// <summary>
    /// Le contrat de scene que la session recoit, pris au clic (la session dure ensuite des minutes).
    ///
    /// Le role est toujours `Decouverte`, et c'est ce que le code permet d'affirmer sans deviner :
    /// • aucun profil n'est persiste : rien ne dit qu'un profil a deja ete trouve pour ce jeu ;
    /// • le journal des candidats n'a aucun lecteur, et il ne porte ni la scene ni le role de la
    ///   session qui a garde ;
    /// • surtout, rejouer un levier encore pose depuis un keep mesurerait la cible contre la cible :
    ///   `TweakCandidate` recapture l'etat courant comme « origine », donc son revert rend la cible :
    ///   un A/B nul qu'on appellerait « validation ».
    /// `Validation` est la seule chose qui rend un keep durable ; la declarer ici la fabriquerait. Une
    /// partie reelle declaree dit donc, a l'ecran, « trouve en decouverte : a revalider », la vraie
    /// reserve.
    /// </summary>
    private SceneContract ContratDeScene() => new(
        SceneDeclaree.Valeur,
        SceneRole.Decouverte,
        string.IsNullOrWhiteSpace(DescriptionScene) ? null : DescriptionScene.Trim());

    private static readonly TimeSpan BorneSurveillanceSession = TimeSpan.FromMinutes(30);

    private async Task<string> MesurerEnJeuAsync(CandidateDescriptor descripteur)
    {
        // Sans jeu, le jugement a deja ecarte ce levier (JugementApply, jeuEnCours) : on n'arrive ici
        // qu'avec un jeu mesure au scan.
        var pid = _gamePid;
        // Sur un seul écran, cliquer sur « Optimiser » réduit un plein écran exclusif. La
        // session aurait mesuré pendant des minutes un jeu qui n'affiche rien : elle attend qu'il revienne.
        if (_gameLocator.LocateRunningGamePid() is null && _gameLocator.JeuOuvert())
        {
            if (await AttendreLeJeuAsync(pourLaSession: true).ConfigureAwait(true) is not { } revenu)
                return _localize("Apply.Session.JeuReduit");
            pid = revenu;
        }

        if (_creerCandidat?.Invoke(descripteur) is not { } candidat)
            return $"« {Titre(descripteur.Id)} » {_localize("Apply.Session.Unavailable")}";

        // Simplification voulue : identite minimale, aucun profil n'est persiste tant qu'aucun keep n'existe.
        // Le nom du jeu vient du meme localisateur que le journal de performance ; le pid seulement
        // quand il ne sait pas nommer, pour que la ligne du journal des candidats porte un jeu.
        var jeu = _gameLocator.NameOfGame(pid) is { Length: > 0 } nom ? nom : $"pid {pid}";
        var session = new DrDxt.Application.Autopilot.GameOptimizationSession(_benchmark,
            () => new DrDxt.Application.Autopilot.GameProfileIdentity(
                jeu, null, null, Environment.OSVersion.VersionString, null),
            _journalCandidats);
        // Hors du fil d'interface : la session tient quatre a cinq minutes de jeu.
        var scene = ContratDeScene();
        // Même garde que la capture du scan, sur toute la session. Un retour sur DrDXT en cours de
        // route réduit le jeu : l'A/B mêle alors la partie et un jeu réduit, et le verdict le dit. Le levier
        // éventuellement gardé n'est pas défait ici : la session a déjà rendu son meilleur état.
        var surveillance = new SurveillanceDuJeu();
        _ = SurveillerLeJeuAsync(pid, surveillance, BorneSurveillanceSession);
        var profil = await Task.Run(() => session.Run(pid, new[] { candidat }, scene: scene)).ConfigureAwait(true);
        surveillance.Fini = true;
        string? pasInscrit = null;
        // Un levier gardé par la session s'inscrit comme ceux d'Optimiser (journal + pose datée) :
        // « Annuler » le voit, et la garde d'incident le rend si un arrêt suit.
        if (profil.Results.Any(r => r.Id == candidat.Descriptor.Id && r.Outcome == CandidateOutcome.Kept)
            && candidat is DrDxt.Application.Autopilot.TweakCandidate { SnapshotPose: { } pose })
        {
            pasInscrit = InscrireAuJournal(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null)).Phrase;
            ChargerLeviersPoses();
            NoterPoses([pose.TweakId], Horloge());
        }
        var recit = DecrireSession(profil, candidat) + (pasInscrit is null ? "" : Environment.NewLine + pasInscrit);
        return surveillance.Quitte ? recit + Environment.NewLine + _localize("Apply.Session.JeuQuitte") : recit;
    }

    private string DecrireSession(
        DrDxt.Application.Autopilot.GameOptimizationProfile profil, IAutopilotCandidate candidat)
    {
        var sb = new System.Text.StringBuilder();
        // Relu sur le profil que la session rend, pas sur le choix a l'ecran : c'est le contrat qui
        // a reellement atteint la session qui s'affiche.
        if (profil.Scene2 is { } recu)
            sb.AppendLine($"{_localize("Scan.Scene.Declared")} "
                          + ChoixDeScene.First(c => c.Valeur == recu.Representativite).Libelle
                          + (recu.Description is { Length: > 0 } dit ? $"{Docteur.DeuxPoints}{dit}" : ""));
        sb.AppendLine(profil.SceneExplanation);
        foreach (var r in profil.Results)
        {
            // Les etats se disent par le resx, plus par le nom d'une enum.
            sb.AppendLine($"{Titre(r.Id)}{Docteur.DeuxPoints}{_localize($"Autopilot.Outcome.{r.Outcome}")}"
                          + (r.Detail is { Length: > 0 } d ? $"{Docteur.DeuxPoints}{d}" : ""));
            foreach (var m in r.Metriques ?? Array.Empty<MetricEvidence>())
                sb.AppendLine($"   {m.Libelle}{Docteur.DeuxPoints}{_localize($"Autopilot.Evidence.{m.Preuve.Outcome}")}");
        }
        if (candidat is DrDxt.Application.Autopilot.TweakCandidate { DernierePreuve: { } preuve })
            sb.AppendLine($"{_localize("Apply.Session.ReadBack")} {_localize($"Autopilot.ReadBack.{preuve.Verdict}")}");
        // Un keep dont l'origine n'a pas atteint le manifeste signe : la desinstallation ne le rendra pas.
        if (candidat is DrDxt.Application.Autopilot.TweakCandidate { OrigineHorsManifeste: { } raison } refuse)
        {
            var echec = Result.Fail(refuse.OrigineHorsManifesteCode ?? ErrorCodes.IoFailure, raison);
            sb.AppendLine(RaisonLisible.Echec(echec, _localize(KeyKeptOriginNotSigned), _localize));
            SignalerManifesteIllisible(echec);
        }
        sb.Append(_localize(profil.EndedInBestState ? "Apply.Session.BestState" : "Apply.Session.RevertFailed"));
        return sb.ToString();
    }

    // Un levier se nomme par son titre traduit ; sans titre connu, son id
    // (le traducteur rend la cle quand elle manque) : rien n'est invente.
    private string Titre(string id) => _localize(TweakDisplayKeys.TitleKey(id));

    /// <summary>Avant le clic, ce qu'Optimiser essaiera : les réglages du plan du scan, nommés ; <c>null</c> sans
    /// scan ou sans rien à essayer.</summary>
    // Quand le jugement ne retient rien, Optimiser ne pose rien ; nommer les réglages contredirait
    // la phrase « rien à poser » juste au-dessus.
    public string? ApercuOptimisation => !RienAPoserSeul && _consumption is { } c && GrainesOptimiser() is { Count: > 0 } graines
        ? ApercuDe(graines, c.Items, Titre, _localize("Optimiser.Apercu"), _localize("Optimiser.Apercu.SansValidation"),
            _localize("Optimiser.Apercu.AucuneValidation"))
        : null;

    /// <summary>La phrase de l'aperçu ; un réglage que le profil n'a pas testé sur une vraie machine porte la même
    /// réserve que sur la carte. Collée au titre, elle s'accorderait mal (« Optimisations… pas encore validé ») :
    /// elle forme une phrase à part.</summary>
    internal static string ApercuDe(IReadOnlyList<string> ids, IReadOnlyList<DrDxt.App.Profiles.ProfileRecommendationItem> items,
        Func<string, string> titre, string amorce, string sansValidation, string aucuneValidation)
    {
        var nonTestes = ids.Where(id => items.FirstOrDefault(i => i.TweakId == id) is { Tested: false }).ToList();
        var phrase = $"{amorce} {string.Join(", ", ids.Select(titre))}";
        // Tous sans validation : les renommer derrière la réserve les dirait deux fois de suite.
        return nonTestes.Count == 0 ? phrase
            : nonTestes.Count == ids.Count ? $"{phrase}. {aucuneValidation}"
            : $"{phrase}. {sansValidation} {string.Join(", ", nonTestes.Select(titre))}.";
    }

    private string Resumer(JugementApply j, ApplyReport? auRedemarrage, string? session)
    {
        var sb = new System.Text.StringBuilder();
        // La condition du selecteur, lue telle quelle : l'App ne la recalcule pas.
        // Des arrêts récents ne refusent plus ; ils se disent, avec la garde qui les remplace.
        if (j.VetoDesArrets is not null) sb.AppendLine(_localize(KeySousGarde));
        // GARDE1 : ce que la garde a rendu, et jusqu'à quand DrDXT ne le repose pas.
        foreach (var (id, quand) in RendusParLaGarde())
            sb.AppendLine(string.Format(CultureInfo.CurrentCulture, _localize("Garde.PasRepose"), Titre(id),
                quand.ToLocalTime().ToString(_localize("Format.Jour"), CultureInfo.CurrentCulture),
                (quand + DrDxt.Application.Autopilot.GardeDIncident.Surveillance).ToLocalTime().ToString(_localize("Format.Jour"), CultureInfo.CurrentCulture)));
        foreach (var a in j.Appreciations)
        {
            var etiquette = _localize(!a.Retenu ? "Apply.Result.Discarded"
                : j.AChaud?.Id == a.Id ? "Apply.Result.ToMeasure"
                : j.AuRedemarrage.Contains(a.Id) ? "Apply.Result.AtReboot"
                : j.PosesSansMesure.Contains(a.Id) ? "Apply.Result.SetNow"
                : "Apply.Result.NotThisRound");
            sb.AppendLine($"{etiquette} · {Titre(a.Id)}{Docteur.DeuxPoints}{a.Motif}");
        }
        foreach (var id in j.SansDescripteur)
            sb.AppendLine($"{_localize("Apply.Result.Unknown")} · {Titre(id)}");
        if (auRedemarrage is { } rapport)
        {
            var tenus = rapport.Outcomes.Where(o => o.Applied && !o.AutoReverted).ToList();
            var poses = tenus.Where(o => !j.PosesSansMesure.Contains(o.TweakId)).Select(o => Titre(o.TweakId)).ToList();
            var tout = tenus.Where(o => j.PosesSansMesure.Contains(o.TweakId)).Select(o => Titre(o.TweakId)).ToList();
            if (j.AuRedemarrage.Count > 0)
                sb.AppendLine(poses.Count > 0
                    ? $"{_localize("Apply.Result.RebootSet")} {string.Join(", ", poses)}."
                    : _localize("Apply.Result.RebootNoneHeld"));
            if (j.PosesSansMesure.Count > 0)
                sb.AppendLine(tout.Count > 0
                    ? $"{_localize("Apply.Result.SetNowDone")} {string.Join(", ", tout)}."
                    : _localize("Apply.Result.SetNowNoneHeld"));
            // Chaque réglage parti dans son lot ; un lot refusé ou défait se nomme.
            var pasTenus = j.AuRedemarrage.Concat(j.PosesSansMesure).Where(id => tenus.All(o => o.TweakId != id))
                .Select(Titre).ToList();
            if (pasTenus.Count > 0) sb.AppendLine($"{_localize("Apply.Result.NotHeld")} {string.Join(", ", pasTenus)}.");
        }
        if (session is not null) sb.AppendLine(session);
        // Windows a garde un point existant au lieu d'en creer un : on dit lequel protege.
        if (_safety.PointReutiliseUtc is { } point)
            sb.AppendLine(string.Format(CultureInfo.CurrentUICulture,
                _localize("Apply.RestorePoint.Reused"), point.ToLocalTime()));
        return sb.ToString().TrimEnd();
    }

    // -----------------------------------------------------------------------
    // ApplyGpuTuningCommand : l'application du réglage GPU, dédiée et isolée.
    //
    // Sur demande explicite : une action du joueur séparée de l'Apply sûr (jamais lancée au scan).
    // Le levier GPU tourne dans la même session mesurée en jeu qu'Optimiser (un levier, gardé
    // seulement avec un gain mesuré, sinon retour arrière). Activée seulement si le profil a
    // recommandé une étape GPU applicable à cette machine (GPU non pris en charge, portable →
    // désactivée → ne fait rien).
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanApplyGpuTuning))]
    private async Task ApplyGpuTuningAsync()
    {
        ApplyBlockedMessage = null;
        MoatUnarmedMessage = null;
        ResultatOptimisation = null;

        if (_consumption is not { HasGpuPlan: true } consumption)
            return; // pas d'étape GPU applicable : la garde CanExecute l'empêche normalement.

        // Le meme jugement que l'Optimiser, avant l'achat. Ce bouton ecrivait
        // sur la carte sans demander au selecteur : ni la decision « rien sur la carte depuis l'App »
        // (TuningGpuAutorise), ni le veto des arrets brutaux, ni l'instabilite graphique.
        var jugement = await JugerAsync(consumption.GpuPlan.TweakIds).ConfigureAwait(true);
        if (jugement.RienATenter)
        {
            if (await EssayerEnJeuSansJeuOuvertAsync().ConfigureAwait(true)) return;
            ApplyBlockedMessage = _localize(KeyNothingToApply);
            ResultatOptimisation = Resumer(jugement, null, null);
            return;
        }
        // La carte se regle comme tout levier a chaud, en jeu, un a la fois, garde seulement
        // avec un gain mesure, sinon annule (regle du 29/09). L'ancienne etape par lot gardait un reglage sans gain.
        if (jugement.AChaud is not { } aChaud) return;

        // La garde du point de restauration d'abord, comme pour l'application sûre : un point vérifié
        // doit exister avant tout changement privilégié. Bloqué → message traduit explicite, rien d'appliqué.
        var gate = await _safety.EnsureRestorePointAsync().ConfigureAwait(true);
        if (!gate.IsOk)
        {
            ApplyBlockedMessage = MessageDuPoint(gate);
            return;
        }

        ResultatOptimisation = Resumer(jugement, null, await MesurerEnJeuAsync(aChaud).ConfigureAwait(true));
    }

    // Construit l'état voulu à partir des leviers validés gardés et enregistre le manifeste
    // signé. Ne lève jamais d'exception vers l'écran d'application et ne bloque jamais : s'il n'y
    // a rien de réconciliable à enregistrer, ne fait rien ; si l'enregistrement échoue, pose un
    // signal non fatal « persistance non armée ». La surface d'écriture privilégiée ne change pas
    // (on n'écrit que le manifeste de l'App, en écriture admin, via le magasin signé).
    private async Task CommitDesiredStateAsync(ApplyReport report)
    {
        try
        {
            // Rien de réconciliable gardé ⇒ pas de manifeste à armer (les origines ci-dessous sont quand même écrites).
            var tweaks = _manifestMapper.ToDesiredTweaks(report);
            var commit = tweaks.Count == 0
                ? Result.Ok()
                : await _desiredStateStore.CommitAsync(new DesiredState(tweaks)).ConfigureAwait(true);
            if (!commit.IsOk)
            {
                // Manifeste ecrit, repere de derive non pose : ce n'est pas « non arme ».
                MoatUnarmedMessage = commit.Error?.Code == ErrorCodes.ManifestReferenceNotSeeded
                    ? RaisonLisible.Echec(commit, _localize(KeyReferenceNotSeeded), _localize)
                    : _localize(KeyMoatUnarmed);
                SignalerManifesteIllisible(commit);
            }
        }
        catch (Exception)
        {
            // La persistance est un renfort, au mieux, par-dessus une application déjà réussie ; une
            // erreur de frontière donne « non armée » et ne casse jamais l'écran d'application.
            MoatUnarmedMessage = _localize(KeyMoatUnarmed);
        }
        await InscrireLesOriginesAsync(report).ConfigureAwait(true);
    }

    /// <summary>L'origine des leviers gardés que le service ne réaffirme pas, au manifeste,
    /// pour que la désinstallation les rende. Un seul envoi (un envoi par levier pouvait
    /// enchaîner les invites UAC) ; chaque origine qu'il porte est acceptée par le helper (CommitManifestOpTests).
    /// Un échec ne défait rien : il est dit, levier par levier.</summary>
    private async Task InscrireLesOriginesAsync(ApplyReport report)
    {
        var gardes = _manifestMapper.ToRevertOnly(report);
        if (gardes.Count == 0) return;
        string? raison;
        try
        {
            var r = await _desiredStateStore.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), gardes))
                .ConfigureAwait(true);
            if (r.IsOk) return;
            raison = RaisonLisible.Dire(r, _localize);
            SignalerManifesteIllisible(r);
        }
        catch (Exception ex)
        {
            raison = ex.Message;
        }
        MoatUnarmedMessage = (MoatUnarmedMessage is { Length: > 0 } avant ? avant + " " : "")
            + $"{_localize(KeyKeptOriginNotSigned)} ({string.Join(", ", gardes.Select(g => Titre(g.TweakId)))}{Docteur.DeuxPoints}{raison})";
    }

    /// <summary>Création du point puis relectures, jusqu'à ~40 s ; ce texte le dit.</summary>
    public string LabelOptimisationEnCours => _localize("Apply.EnCours");

    /// <summary>La vraie raison d'un point de restauration manquant. « La restauration est désactivée » ne vaut que
    /// pour ce cas : la restauration peut être active et Windows ne pas avoir créé le point.</summary>
    private string MessageDuPoint(Result gate) => gate.Error?.Code switch
    {
        ErrorCodes.RestoreDeclined => _localize("Apply.RestoreRequired.Declined"),
        ErrorCodes.RestoreDisabled => _localize(KeyRestoreBlocked),
        ErrorCodes.RestoreNotVerified => _localize("Apply.RestoreRequired.NotFound"),
        _ => RaisonLisible.OuIncertain(gate,
            string.Format(CultureInfo.CurrentCulture, _localize("Apply.RestoreRequired.Failed"), RaisonLisible.Dire(gate, _localize)), _localize),
    };

    // Un refus « illisible » n'est pas une panne passagere : il se repetera a chaque commit tant que
    // personne ne decide. Il est donc nomme, et la reprise est proposee.
    private void SignalerManifesteIllisible(Result r)
    {
        if (r.Error?.Code is not (ErrorCodes.ManifestUnreadable or ErrorCodes.ManifestKeyUnusable))
            return;
        ManifesteIllisibleMessage = RaisonLisible.Echec(r, _localize(KeyManifestUnreadable), _localize);
        PeutRepartirDuManifeste = true;
    }

    /// <summary>La reprise explicite. Le helper met l'ancien manifeste de cote (renomme, jamais
    /// efface) et en commence un neuf avec ce qui avait ete refuse. L'ecran le dit : les origines de l'ancien
    /// ne seront plus rendues par la desinstallation.</summary>
    [RelayCommand]
    private async Task RepartirDuManifesteAsync()
    {
        Result r;
        try
        {
            r = await _desiredStateStore.RepartirDUnManifesteVideAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            r = Result.Fail(ErrorCodes.IoFailure, ex.Message);
        }

        if (r.IsOk)
        {
            ManifesteIllisibleMessage = _localize(KeyManifestResetDone);
            PeutRepartirDuManifeste = false;
            MoatUnarmedMessage = null;
        }
        else
        {
            ManifesteIllisibleMessage = RaisonLisible.Echec(r, _localize(KeyManifestResetFailed), _localize);
        }
    }
}
