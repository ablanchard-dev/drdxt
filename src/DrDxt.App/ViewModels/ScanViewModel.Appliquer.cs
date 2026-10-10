using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Navigation;
using DrDxt.App.Profiles;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

public sealed partial class ScanViewModel
{
    // -----------------------------------------------------------------------
    // ApplyCommand — « OPTIMISER » (PHASE 18 F6, L8 b ; revue du 10/09).
    //
    //   1. Juger d'abord : diagnostic du scan → CandidateSelector sur les graines du profil.
    //      Rien a tenter ⇒ on le dit, avec les motifs (le gate payant a ete retire le 23/09 : gratuit, GPL).
    //   2. Point de restauration verifie (§16).
    //   3. Ce qui ne se mesure pas (effet au redemarrage) : le moteur, sans baseline — apply,
    //      relecture, revert sur echec — puis le manifeste signe pour le reconciliateur.
    //   4. Un levier a chaud, en session mesuree a-b-b-a. Jamais deux : un seul KeepPolicy.
    //
    // Le mode lot (tout le plan, re-bench global) n'est plus atteignable d'ici. Le tuning GPU
    // reste une action separee (ApplyGpuTuningCommand).
    // -----------------------------------------------------------------------
    /// <summary>Les leviers posés depuis le dernier scan : le plan du scan ne les connaît pas encore comme posés, et
    /// sans ça « Prochaine étape : Optimiser » restait affiché après la pose (revue du 10/10/2026). Vidé au scan.</summary>
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
        // 10/10/2026 (« j'ai revoulu opti, je peux toujours pas ») : sans jeu ouvert, un réglage qui se mesure en jeu était
        // « écarté, lance ton jeu, refais un scan ». L'essai en alternance sait attendre les parties : le clic vaut accord.
        // Revue du 10/10 : il part aussi quand il y avait autre chose à poser, comme l'étape en tête le promet.
        var essaiAFaire = _gamePid == 0 && LevierAEssayer is not null && EssaiProposable;
        if (jugement.RienATenter && !essaiAFaire && !ReposCarteAOuvrir)
        {
            // 08/10/2026 : les arrêts ne bloquent plus ; rien à tenter veut dire rien à tenter. Et jamais sans suite (U5).
            ApplyBlockedMessage = _localize(KeyNothingToApply);
            ResultatOptimisation = string.Join(Environment.NewLine,
                new[] { Resumer(jugement, null, null), Ordonnance(reposFait: ReposCarteEnAttente) }.Where(l => l.Length > 0));
            return;
        }

        // (2) la porte §16 : un point de restauration vérifié avant tout ce qui écrit. Revue du 10/10/2026 : le geste du
        // docteur et l'essai lancé d'ici passaient avant elle, alors que l'écran l'annonçait.
        var gate = await _safety.EnsureRestorePointAsync().ConfigureAwait(true);
        if (!gate.IsOk)
        {
            ApplyBlockedMessage = MessageDuPoint(gate);
            return;
        }

        // 10/10/2026 (le mainteneur : « c'est pas normal qu'Optimiser n'ait rien à poser chez moi ») : les gestes que le
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
            // 10/10/2026 (le mainteneur : « ça manque de conclusion, de réelle aide ») : Optimiser finit toujours par une
            // ordonnance. Le soir même (« c'est relou que DrDXT ne s'occupe pas plus des points à corriger avant opti ») :
            // ce qui reste à ta main s'écrivait après « Posé » et « C'est parti », enterré. Le docteur le dit en tête,
            // puis ce qu'il a fait, puis la suite.
            var (tete, fin) = OrdonnanceEnDeux(reposFait: ReposCarteEnAttente);
            ResultatOptimisation = string.Join(Environment.NewLine,
                new[] { tete }.Concat(gestes).Append(ResultatOptimisation ?? string.Empty).Append(essai ?? string.Empty)
                    .Append(fin).Where(l => l.Length > 0));
        }
        // (6) Revue du 10/10/2026 : l'étape en tête suivait le scan, pas Optimiser. Rejugée sur ce qui reste.
        await JugerCeQuiResteAPoserAsync().ConfigureAwait(true);
    }

    /// <summary>Les points « à faire d'abord » encore vrais : le mode NVIDIA au global sort de la liste une fois le repos
    /// rendu par DrDXT (reconnu par sa clé, jamais par son texte).</summary>
    internal IEnumerable<PointAFaire> PointsAFaireRestants(bool reposFait) =>
        PointsAFaire.Where(p => !(reposFait && p.Entree.Cle == "Finding.Gpu.PerfMaxGlobal"));

    /// <summary>Ce qui reste vraiment à la main du joueur : les points à faire, moins celui qu'Optimiser va faire ou a fait
    /// lui-même (le repos de la carte).</summary>
    internal IEnumerable<PointAFaire> PointsALaMain => PointsAFaireRestants(ReposCarteEnAttente || ReposCarteAOuvrir);

    /// <summary>10/10/2026 : la conclusion du docteur après Optimiser, construite sur ce que le scan a déjà dit (la liste
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

    /// <summary>10/10/2026 : ce qu'Optimiser fait sans mesure en jeu, parce que le scan l'a constaté et que DrDXT a son
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
        // 06/10/2026 (L-WU) : les réglages sans effet sur les images (heures d'activité) partent aussi sans mesure.
        // 07/10/2026 (L-ECRAN) : chacun dans son propre lot. Le moteur défait un lot entier sur un échec : un mode d'écran
        // que le joueur ne garde pas défaisait le fichier d'échange et les heures cliqués avec lui.
        var lots = (jugement.AuRedemarrage.Count > 0 ? new[] { jugement.AuRedemarrage.ToList() } : [])
            .Concat(jugement.PosesSansMesure.Select(id => new List<string> { id })).ToList();
        if (lots.Count > 0)
        {
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
            // Le reconciliateur reaffirme ces leviers gardes au demarrage (§9.12) ; non fatal.
            await CommitDesiredStateAsync(reussi).ConfigureAwait(true);
            // 24/09/2026 — tous les leviers gardes, cote utilisateur : le scan suivant remarquera
            // s'ils ont ete defaits (le manifeste n'en porte que quatre).
            _journalLeviers?.Enregistrer(reussi);
            ChargerLeviersPoses();
            var posees = reussi.Outcomes.Where(o => o.Applied && !o.AutoReverted).Select(o => o.TweakId).ToList();
            NoterPoses(posees, Horloge());
            _posesDepuisLeScan.UnionWith(posees);
            // 07/10/2026 (revue F-25) : le mode Jeu agit tout de suite. Relevé seulement à la prochaine ouverture ou au
            // prochain scan, sa bascule était datée là, et les parties jouées entre la pose et ce relevé ne comptaient d'aucun
            // côté. Seuls les réglages à effet immédiat se relèvent ici : un levier au redémarrage n'a pas encore basculé.
            await ReleverReglagesHorsScanAsync(ReglagesAEffetImmediat).ConfigureAwait(true);
        }

        // (5) un levier a chaud, mesure en jeu.
        var session = jugement.AChaud is { } aChaud
            ? await MesurerEnJeuAsync(aChaud).ConfigureAwait(true)
            : null;

        ResultatOptimisation = Resumer(jugement, auRedemarrage, session);
    }

    /// <summary>03/10/2026 (D5.1, revue) : « Optimiser » s'affichait sous « 0 à changer » et ne disait « rien à
    /// appliquer » qu'au clic. Le même jugement est rendu dès la fin de l'analyse : s'il n'y a rien que DrDXT puisse
    /// poser par Optimiser, le bouton laisse la place à une phrase vraie (les gestes des domaines, « Agir », restent).</summary>
    [ObservableProperty]
    // 06/10/2026 (le mainteneur : « j'ai toujours pas le bouton opti », « DrDXT n'a pas le droit de nous empêcher de toucher ») :
    // le bouton se cachait quand le jugement ne voyait rien à poser, une règle de l'époque payante (« un client à qui il
    // n'y a rien à tenter ne paie pas pour l'apprendre »). DrDXT est gratuit : il dit ce qu'il voit, le bouton reste.
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
        // 07/10/2026 (J4) : le levier à chaud qu'un essai en alternance jugerait sur tes parties (il n'a pas besoin d'un jeu
        // lancé au scan : il se juge sur les parties qui viennent).
        LevierAEssayer = JugementApply.Juger(diagnostic, GrainesOptimiser(), jeuEnCours: true).AChaud?.Id is { } id && PeutAlterner(id) ? id : null;
        // 06/10/2026 (L-GPU) : pourquoi la carte est ouverte ou fermee ICI, avec la famille d'arret en cause.
        PhraseReglageCarte = _consumption is { HasGpuPlan: true }
            ? CandidateSelector.PhraseReglageCarte(diagnostic, DateTimeOffset.UtcNow)
            : _localize("Apply.Carte.NonProposee");
        // GARDE2 (09/10/2026) : les incidents du pilote ne ferment plus la carte ; la garde d'incident les surveille et rend seule.
    }

    /// <summary>06/10/2026 (L-GPU) : le réglage de la carte passe par Optimiser, comme les autres leviers à chaud : un
    /// à la fois, mesuré en jeu, gardé seulement avec un gain mesuré, sinon annulé (règle du 29/09).</summary>
    private IReadOnlyList<string> GrainesOptimiser() =>
        _consumption is { } c
            ? c.Plan.TweakIds.Concat(c.GpuPlan.TweakIds.Where(id => !CandidateSelector.ReglagesCarteInterdits.Contains(id)))
                .Where(id => !RendusParLaGarde().ContainsKey(id) && !_posesDepuisLeScan.Contains(id)).ToList()
            : [];

    /// <summary>GARDE1 (09/10/2026) : les leviers que la garde d'incident a rendus il y a moins d'une semaine, avec la date
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

    /// <summary>06/10/2026 (L-GPU) : la phrase « réglage de la carte ouvert / fermé chez toi », null avant un scan.</summary>
    [ObservableProperty]
    private string? _phraseReglageCarte;

    /// <summary>10/10/2026 : Optimiser, aucun jeu ouvert, un réglage à chaud à essayer : l'essai en alternance démarre.
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
    /// Le contrat de scene que la session recoit, pris AU CLIC (la session dure ensuite des minutes).
    ///
    /// Le role est toujours `Decouverte`, et c'est ce que le code permet d'affirmer sans deviner
    /// (verifie le 11/09/2026) :
    /// • aucun profil n'est persiste (L9) : rien ne dit qu'un profil a deja ete TROUVE pour ce jeu ;
    /// • le journal des candidats n'a aucun lecteur, et il ne porte ni la scene ni le role de la
    ///   session qui a garde ;
    /// • surtout, rejouer un levier encore pose depuis un keep mesurerait la cible contre la cible :
    ///   `TweakCandidate` recapture l'etat courant comme « origine », donc son revert rend la cible —
    ///   un A/B nul qu'on appellerait « validation ».
    /// `Validation` est la seule chose qui rend un keep durable ; la declarer ici la fabriquerait. Une
    /// partie reelle declaree dit donc, a l'ecran, « trouve en decouverte : a revalider » — la vraie
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
        // 26/09/2026 : sur un seul écran, cliquer sur « Optimiser » réduit un plein écran exclusif. La
        // session aurait mesuré pendant des minutes un jeu qui n'affiche rien : elle attend qu'il revienne.
        if (_gameLocator.LocateRunningGamePid() is null && _gameLocator.JeuOuvert())
        {
            if (await AttendreLeJeuAsync(pourLaSession: true).ConfigureAwait(true) is not { } revenu)
                return _localize("Apply.Session.JeuReduit");
            pid = revenu;
        }

        if (_creerCandidat?.Invoke(descripteur) is not { } candidat)
            return $"« {Titre(descripteur.Id)} » {_localize("Apply.Session.Unavailable")}";

        // Simplification voulue : identite minimale — aucun profil n'est persiste tant qu'aucun KEEP n'existe (L9).
        // Le NOM du jeu vient du meme localisateur que le journal de performance ; le pid seulement
        // quand il ne sait pas nommer, pour que la ligne du journal des candidats porte un jeu.
        var jeu = _gameLocator.NameOfGame(pid) is { Length: > 0 } nom ? nom : $"pid {pid}";
        var session = new DrDxt.Application.Autopilot.GameOptimizationSession(_benchmark,
            () => new DrDxt.Application.Autopilot.GameProfileIdentity(
                jeu, null, null, Environment.OSVersion.VersionString, null),
            _journalCandidats);
        // Hors du fil d'interface : la session tient quatre a cinq minutes de jeu.
        var scene = ContratDeScene();
        // 26/09/2026 : même garde que la capture du scan, sur toute la session. Un retour sur DrDXT en cours de
        // route réduit le jeu : l'A/B mêle alors la partie et un jeu réduit, et le verdict le dit. Le levier
        // éventuellement gardé n'est pas défait ici : la session a déjà rendu son meilleur état.
        var surveillance = new SurveillanceDuJeu();
        _ = SurveillerLeJeuAsync(pid, surveillance, BorneSurveillanceSession);
        var profil = await Task.Run(() => session.Run(pid, new[] { candidat }, scene: scene)).ConfigureAwait(true);
        surveillance.Fini = true;
        // GARDE1 (09/10/2026) : un levier gardé par la session s'inscrit comme ceux d'Optimiser (journal + pose datée) :
        // « Annuler » le voit, et la garde d'incident le rend si un arrêt suit.
        if (profil.Results.Any(r => r.Id == candidat.Descriptor.Id && r.Outcome == CandidateOutcome.Kept)
            && candidat is DrDxt.Application.Autopilot.TweakCandidate { SnapshotPose: { } pose })
        {
            _journalLeviers?.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            ChargerLeviersPoses();
            NoterPoses([pose.TweakId], Horloge());
        }
        var recit = DecrireSession(profil, candidat);
        return surveillance.Quitte ? recit + Environment.NewLine + _localize("Apply.Session.JeuQuitte") : recit;
    }

    private string DecrireSession(
        DrDxt.Application.Autopilot.GameOptimizationProfile profil, IAutopilotCandidate candidat)
    {
        var sb = new System.Text.StringBuilder();
        // Relu sur le PROFIL que la session rend, pas sur le choix a l'ecran : c'est le contrat qui
        // a reellement atteint la session qui s'affiche.
        if (profil.Scene2 is { } recu)
            sb.AppendLine($"{_localize("Scan.Scene.Declared")} "
                          + ChoixDeScene.First(c => c.Valeur == recu.Representativite).Libelle
                          + (recu.Description is { Length: > 0 } dit ? $" : {dit}" : ""));
        sb.AppendLine(profil.SceneExplanation);
        foreach (var r in profil.Results)
        {
            // 14/09/2026 (plan 2.4) : les etats se disent par le resx, plus par le nom d'une enum.
            sb.AppendLine($"{Titre(r.Id)} : {_localize($"Autopilot.Outcome.{r.Outcome}")}"
                          + (r.Detail is { Length: > 0 } d ? $" : {d}" : ""));
            foreach (var m in r.Metriques ?? Array.Empty<MetricEvidence>())
                sb.AppendLine($"   {m.Libelle} : {_localize($"Autopilot.Evidence.{m.Preuve.Outcome}")}");
        }
        if (candidat is DrDxt.Application.Autopilot.TweakCandidate { DernierePreuve: { } preuve })
            sb.AppendLine($"{_localize("Apply.Session.ReadBack")} {_localize($"Autopilot.ReadBack.{preuve.Verdict}")}");
        // Un keep dont l'origine n'a pas atteint le manifeste signe : la desinstallation ne le rendra pas.
        if (candidat is DrDxt.Application.Autopilot.TweakCandidate { OrigineHorsManifeste: { } raison } refuse)
        {
            sb.AppendLine($"{_localize(KeyKeptOriginNotSigned)} ({raison})");
            SignalerManifesteIllisible(refuse.OrigineHorsManifesteCode, raison);
        }
        sb.Append(_localize(profil.EndedInBestState ? "Apply.Session.BestState" : "Apply.Session.RevertFailed"));
        return sb.ToString();
    }

    // 14/09/2026 (plan 2.4) : un levier se nomme par son titre traduit ; sans titre connu, son id
    // (le traducteur rend la cle quand elle manque) — rien n'est invente.
    private string Titre(string id) => _localize(TweakDisplayKeys.TitleKey(id));

    /// <summary>29/09/2026 : avant le clic, ce qu'Optimiser essaiera (seconde critique : il ne disait pas ce qu'il
    /// allait changer). Les réglages du plan du scan, nommés ; <c>null</c> sans scan ou sans rien à essayer.</summary>
    // UI1 (08/10/2026) : quand le jugement ne retient rien, Optimiser ne pose rien ; nommer les réglages contredisait
    // la phrase « rien à poser » juste au-dessus.
    public string? ApercuOptimisation => !RienAPoserSeul && _consumption is { } c && GrainesOptimiser() is { Count: > 0 } graines
        ? ApercuDe(graines, c.Items, Titre, _localize("Optimiser.Apercu"), _localize("Optimiser.Apercu.SansValidation"),
            _localize("Optimiser.Apercu.AucuneValidation"))
        : null;

    /// <summary>La phrase de l'aperçu ; un réglage que le profil n'a pas testé sur une vraie machine porte la même
    /// réserve que sur la carte (troisième critique : l'aperçu promettait sans la dire). 29/09/2026 : collée au
    /// titre, la réserve s'accordait mal (« Optimisations… pas encore validé ») ; elle devient une phrase à part.</summary>
    internal static string ApercuDe(IReadOnlyList<string> ids, IReadOnlyList<DrDxt.App.Profiles.ProfileRecommendationItem> items,
        Func<string, string> titre, string amorce, string sansValidation, string aucuneValidation)
    {
        var nonTestes = ids.Where(id => items.FirstOrDefault(i => i.TweakId == id) is { Tested: false }).ToList();
        var phrase = $"{amorce} {string.Join(", ", ids.Select(titre))}";
        // Tous sans validation : les renommer derrière la réserve les disait deux fois de suite.
        return nonTestes.Count == 0 ? phrase
            : nonTestes.Count == ids.Count ? $"{phrase}. {aucuneValidation}"
            : $"{phrase}. {sansValidation} {string.Join(", ", nonTestes.Select(titre))}.";
    }

    private string Resumer(JugementApply j, ApplyReport? auRedemarrage, string? session)
    {
        var sb = new System.Text.StringBuilder();
        // La condition du selecteur, lue telle quelle : l'App ne la recalcule pas.
        // 08/10/2026 : des arrêts récents ne refusent plus ; ils se disent, avec la garde qui les remplace.
        if (j.VetoDesArrets is not null) sb.AppendLine(_localize(KeySousGarde));
        // GARDE1 : ce que la garde a rendu, et jusqu'à quand DrDXT ne le repose pas.
        foreach (var (id, quand) in RendusParLaGarde())
            sb.AppendLine(string.Format(CultureInfo.CurrentCulture, _localize("Garde.PasRepose"), Titre(id),
                quand.ToLocalTime().ToString("dd/MM", CultureInfo.CurrentCulture),
                (quand + DrDxt.Application.Autopilot.GardeDIncident.Surveillance).ToLocalTime().ToString("dd/MM", CultureInfo.CurrentCulture)));
        foreach (var a in j.Appreciations)
        {
            var etiquette = _localize(!a.Retenu ? "Apply.Result.Discarded"
                : j.AChaud?.Id == a.Id ? "Apply.Result.ToMeasure"
                : j.AuRedemarrage.Contains(a.Id) ? "Apply.Result.AtReboot"
                : j.PosesSansMesure.Contains(a.Id) ? "Apply.Result.SetNow"
                : "Apply.Result.NotThisRound");
            sb.AppendLine($"{etiquette} · {Titre(a.Id)} : {a.Motif}");
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
            // 07/10/2026 (revue F-16) : chaque réglage parti dans son lot ; un lot refusé ou défait se nomme.
            var pasTenus = j.AuRedemarrage.Concat(j.PosesSansMesure).Where(id => tenus.All(o => o.TweakId != id))
                .Select(Titre).ToList();
            if (pasTenus.Count > 0) sb.AppendLine($"{_localize("Apply.Result.NotHeld")} {string.Join(", ", pasTenus)}.");
        }
        if (session is not null) sb.AppendLine(session);
        // 14/09/2026 : Windows a garde un point existant au lieu d'en creer un — on dit lequel protege.
        if (_safety.PointReutiliseUtc is { } point)
            sb.AppendLine(string.Format(CultureInfo.CurrentUICulture,
                _localize("Apply.RestorePoint.Reused"), point.ToLocalTime()));
        return sb.ToString().TrimEnd();
    }

    // -----------------------------------------------------------------------
    // ApplyGpuTuningCommand — the dedicated, isolated GPU-tuning apply (M6).
    //
    // Opt-in/explicit: a SEPARATE user action from the safe Apply (never runs on scan). 06/10/2026 (L-GPU) : the
    // GPU lever runs in the same measured in-game session as Optimize (one lever, kept only with a measured gain,
    // otherwise reverted). Enabled only when the profile recommended an APPLICABLE GPU step for this machine
    // (unsupported GPU, laptop → disabled → no-op).
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanApplyGpuTuning))]
    private async Task ApplyGpuTuningAsync()
    {
        ApplyBlockedMessage = null;
        MoatUnarmedMessage = null;
        ResultatOptimisation = null;

        if (_consumption is not { HasGpuPlan: true } consumption)
            return; // no applicable GPU step — the CanExecute guard normally prevents this.

        // Revue du 12/09 (C3) : le meme jugement que l'Optimiser, avant l'achat. Ce bouton ecrivait
        // sur la carte sans demander au selecteur — ni la decision « rien sur la carte depuis l'App »
        // (TuningGpuAutorise), ni le veto des arrets brutaux, ni l'instabilite graphique.
        var jugement = await JugerAsync(consumption.GpuPlan.TweakIds).ConfigureAwait(true);
        if (jugement.RienATenter)
        {
            if (await EssayerEnJeuSansJeuOuvertAsync().ConfigureAwait(true)) return;
            ApplyBlockedMessage = _localize(KeyNothingToApply);
            ResultatOptimisation = Resumer(jugement, null, null);
            return;
        }
        // 06/10/2026 (L-GPU) : la carte se regle comme tout levier a chaud, en jeu, un a la fois, garde seulement
        // avec un gain mesure, sinon annule (regle du 29/09). L'ancienne etape par lot gardait un reglage sans gain.
        if (jugement.AChaud is not { } aChaud) return;

        // Restore-point gate (§16) first — identical to the safe apply: a verified point must
        // exist before any privileged change. Blocked → explicit localized message, apply nothing.
        var gate = await _safety.EnsureRestorePointAsync().ConfigureAwait(true);
        if (!gate.IsOk)
        {
            ApplyBlockedMessage = MessageDuPoint(gate);
            return;
        }

        ResultatOptimisation = Resumer(jugement, null, await MesurerEnJeuAsync(aChaud).ConfigureAwait(true));
    }

    // Builds the desired state from the kept validated tweaks and commits the signed
    // manifest. Never throws into the apply UX and never blocks: if there is nothing
    // reconcilable to commit, it is a no-op; if the commit fails, it sets a non-fatal
    // "moat unarmed" signal. The privileged write surface is unchanged (this only writes
    // the App-owned, admin-write manifest via the signed store).
    private async Task CommitDesiredStateAsync(ApplyReport report)
    {
        try
        {
            // Nothing reconcilable kept ⇒ no manifest to arm (the origins below still go in).
            var tweaks = _manifestMapper.ToDesiredTweaks(report);
            var commit = tweaks.Count == 0
                ? Result.Ok()
                : await _desiredStateStore.CommitAsync(new DesiredState(tweaks)).ConfigureAwait(true);
            if (!commit.IsOk)
            {
                // 11/09/2026 — manifeste ecrit, repere de derive non pose : ce n'est pas « non arme ».
                MoatUnarmedMessage = commit.Error?.Code == ErrorCodes.ManifestReferenceNotSeeded
                    ? $"{_localize(KeyReferenceNotSeeded)} ({commit.Error?.Message})"
                    : _localize(KeyMoatUnarmed);
                SignalerManifesteIllisible(commit.Error?.Code, commit.Error?.Message);
            }
        }
        catch (Exception)
        {
            // The moat is best-effort hardening on top of an already-successful apply;
            // any boundary error degrades to "unarmed", never breaks the apply UX.
            MoatUnarmedMessage = _localize(KeyMoatUnarmed);
        }
        await InscrireLesOriginesAsync(report).ConfigureAwait(true);
    }

    /// <summary>06/10/2026 (plan 4.3) — l'origine des leviers gardés que le service ne réaffirme pas, au manifeste,
    /// pour que la désinstallation les rende. Un seul envoi (revue du 06/10 : un envoi par levier pouvait
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
            raison = r.Error?.Message;
            SignalerManifesteIllisible(r.Error?.Code, r.Error?.Message);
        }
        catch (Exception ex)
        {
            raison = ex.Message;
        }
        MoatUnarmedMessage = (MoatUnarmedMessage is { Length: > 0 } avant ? avant + " " : "")
            + $"{_localize(KeyKeptOriginNotSigned)} ({string.Join(", ", gardes.Select(g => Titre(g.TweakId)))} : {raison})";
    }

    /// <summary>Revue du 06/10 (P2) : création du point puis relectures, jusqu'à ~40 s ; ce texte le dit.</summary>
    public string LabelOptimisationEnCours => _localize("Apply.EnCours");

    /// <summary>06/10/2026 (plan 4.5) : la vraie raison d'un point de restauration manquant. Avant, tout échec disait « la
    /// restauration est désactivée » ; chez moi elle était active et c'est Windows qui n'avait pas créé le point.</summary>
    private string MessageDuPoint(Result gate) => gate.Error?.Code switch
    {
        ErrorCodes.RestoreDeclined => _localize("Apply.RestoreRequired.Declined"),
        ErrorCodes.RestoreDisabled => _localize(KeyRestoreBlocked),
        ErrorCodes.RestoreNotVerified => _localize("Apply.RestoreRequired.NotFound"),
        _ => string.Format(CultureInfo.CurrentCulture, _localize("Apply.RestoreRequired.Failed"), gate.Error?.Message),
    };

    // Un refus « illisible » n'est pas une panne passagere : il se repetera a chaque commit tant que
    // personne ne decide. Il est donc nomme, et la reprise est proposee.
    private void SignalerManifesteIllisible(string? code, string? raison)
    {
        if (code is not (ErrorCodes.ManifestUnreadable or ErrorCodes.ManifestKeyUnusable))
            return;
        ManifesteIllisibleMessage = $"{_localize(KeyManifestUnreadable)} ({raison})";
        PeutRepartirDuManifeste = true;
    }

    /// <summary>11/09/2026 — la reprise explicite. Le helper met l'ancien manifeste de cote (renomme, jamais
    /// efface) et en commence un neuf avec ce qui avait ete refuse. L'ecran le DIT : les origines de l'ancien
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
            ManifesteIllisibleMessage = $"{_localize(KeyManifestResetFailed)} ({r.Error?.Message})";
        }
    }
}
