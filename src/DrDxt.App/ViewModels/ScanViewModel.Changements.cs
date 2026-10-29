using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Core;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

/// <summary>Les changements suivis : leur relevé, leur date, leur jugement en jeu et le retrait de ce qui coûte.</summary>
public partial class ScanViewModel
{
    /// <summary>Un changement de réglage BIOS vu entre deux références processeur. Il a pris effet au démarrage du PC :
    /// c'est cette heure qui sépare les parties d'avant de celles d'après, pas celle du scan.</summary>
    /// <param name="mesureAvant">La dernière mesure sous l'ancien réglage. Le PC a pu redémarrer
    /// plusieurs fois depuis le changement ; seule cette fenêtre est sûre.</param>
    internal void NoterChangementBios(string? avant, string? apres, DateTimeOffset? mesureAvant = null)
    {
        if (avant is null || apres is null || avant == apres || JournalChangements is null) return;
        JournalChangements.Ajouter(new Changement(DemarrageDuPc(),
            string.Format(CultureInfo.CurrentCulture, _localize("Changement.Bios"), avant, apres), mesureAvant));
        // La carte se refait tout de suite : le changement s'y lit sans attendre le prochain lancement.
        Afficher(MemoireParties?.Lire() ?? []);
    }

    /// <summary>Les réglages que le joueur bascule lui-même, relevés à chaque scan. L'appli
    /// pose le fichier réel au démarrage ; <c>null</c> = aucun suivi (les tests qui ne le vérifient pas).</summary>
    internal SuiviDesReglages? SuiviReglages { get; set; }

    /// <summary>Les états suivis, par identifiant stable ; un réglage illisible (Unknown) n'y figure pas. Le mode Jeu et
    /// HAGS, posés par DrDXT ou basculés par le joueur, se jugent sur les parties comme un réglage du BIOS. HAGS est lu dans
    /// son état effectif (ce que le pilote applique), pas dans le registre qui n'agit qu'au redémarrage.</summary>
    /// <param name="instantReplay">Lu par l'appelant (<see cref="LireInstantReplay"/>), jamais ici dans le registre du
    /// PC.</param>
    /// <param name="bios">Les réglages BIOS du processeur lus dans la NVRAM (limite de courant, Lite Load…) : datés et
    /// jugés sur les parties comme le reste.</param>
    internal static IReadOnlyDictionary<string, string> EtatsSuivis(SettingsState? st = null, HardwareInventory? hw = null,
        bool? instantReplay = null, IReadOnlyList<BiosSettingReading>? bios = null)
    {
        var etats = new Dictionary<string, string>(StringComparer.Ordinal);
        static string? OnOff(TriState t) => t switch { TriState.On => "on", TriState.Off => "off", _ => null };
        if (st is not null && OnOff(st.GameMode) is { } gm) etats["windows.game_mode"] = gm;
        if (st is not null && OnOff(st.HagsEffective) is { } hags) etats["windows.hags"] = hags;
        // Le fichier d'échange, que `fixit.sane_pagefile` remet au redémarrage ; sa bascule se juge
        // sur les parties comme HAGS, et « moins bien en jeu » le retire.
        if (st is not null && OnOff(st.PagefileConfigured) is { } pf) etats["memoire.fichier_echange"] = pf;
        // La mémoire se règle au BIOS, et les timings ne se lisent pas sans pilote noyau : fréquence et tension suffisent à
        // dater un changement, jugé sur les parties (le banc mémoire dit l'avant et l'après). L'ordre de WMI n'est pas
        // garanti : la plus haute fréquence configurée fait foi.
        if (hw?.Memory.Where(m => m.ConfiguredMhz > 0).OrderByDescending(m => m.ConfiguredMhz).FirstOrDefault() is { } ram)
        {
            etats["memoire.frequence"] = $"{ram.ConfiguredMhz} MT/s";
            if (ram.ConfiguredVoltageMv > 0)
                etats["memoire.tension"] = (ram.ConfiguredVoltageMv / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " V";
        }
        if (instantReplay is { } ir)
            etats["nvidia.instant_replay"] = ir ? "on" : "off";
        foreach (var r in bios ?? [])
            if (ReglagesBiosSuivis.TryGetValue(r.Setting, out var id)) etats[id] = r.Libelle;
        // Les quatre paliers de la courbe du ventilateur se suivent en une valeur : un changement de courbe = un changement daté.
        if (CarteDeLaMachine.CourbeVentilateurCpu(bios) is { } courbe) etats["bios.courbe_ventilateur_cpu"] = courbe;
        return etats;
    }

    /// <summary>Les réglages BIOS suivis, du nom de la matrice à l'identifiant du suivi. PL1/PL2 et « CPU Cooler Tuning »
    /// n'y sont pas : ils voyagent déjà avec la référence du banc processeur (<see cref="NoterChangementBios"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> ReglagesBiosSuivis = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CPU Current Limit"] = "bios.limite_courant",
        ["CPU Lite Load"] = "bios.lite_load",
        ["CPU Lite Load Control"] = "bios.lite_load_controle",
        ["IA CEP Support"] = "bios.cep",
        ["CPU Fan1 Smart Fan Control"] = "bios.ventilateur_cpu_intelligent",
    };

    /// <summary>L'Instant Replay de NVIDIA. L'appli pose la lecture du registre ; <c>null</c> = non lu.</summary>
    internal Func<bool?>? LireInstantReplay { get; set; }

    /// <summary>Les seuls réglages suivis, lus sans scan. L'appli pose la lecture (registre, état effectif
    /// de HAGS) ; <c>null</c> = rien n'est relevé hors du scan.</summary>
    internal Func<IReadOnlyDictionary<string, string>>? LireReglagesSuivis { get; set; }

    /// <summary>Les réglages suivis lus sans scan complet : le mode Jeu et le fichier d'échange dans le registre, HAGS dans son
    /// état effectif, l'Instant Replay. Quelques lectures, aucune mesure ; la mémoire (WMI) attend le scan, et un réglage non
    /// relu garde son dernier état connu (<see cref="SuiviDesReglages"/>).</summary>
    internal static IReadOnlyDictionary<string, string> EtatsSuivisSansScan(DrDxt.Detection.Settings.IRegistryProvider registre,
        DrDxt.Detection.Settings.IHagsEffectiveReader? hags, bool? instantReplay) =>
        EtatsSuivis(new SettingsState(TriState.Unknown, TriState.Unknown, "", TriState.Unknown, registre.GetGameMode(), null,
            TriState.Unknown, PagefileConfigured: registre.GetPagefile(), HagsEffective: hags?.Lire() ?? TriState.Unknown), null, instantReplay);

    /// <summary>
    /// Les bascules se relèvent hors scan : à l'ouverture de l'appli et dans le relevé de fond (à l'ouverture de session,
    /// juste après le redémarrage qui les a fait basculer), pour juger et protéger un joueur qui ne rescanne pas. Le relevé
    /// de fond ne pose pas <see cref="LevierParId"/> : sans fenêtre ni helper (aucune élévation au démarrage de Windows), il
    /// note et juge ; le retrait part à l'ouverture suivante de l'appli, sur les mêmes bilans.
    /// </summary>
    /// <param name="seulement">Les seuls réglages à relever ; <c>null</c> = tous. Un réglage non relu garde son dernier
    /// état connu, et sa bascule se relève plus tard.</param>
    internal async Task ReleverReglagesHorsScanAsync(IReadOnlySet<string>? seulement = null)
    {
        if (LireReglagesSuivis is not { } lire) return;
        IReadOnlyDictionary<string, string> etats;
        try { etats = await Task.Run(lire); }
        catch (Exception) { return; }
        if (seulement is not null) etats = etats.Where(e => seulement.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value);
        try { await ReleverReglagesSuivisAsync(etats, Horloge()); }
        catch (Exception) { /* un relevé raté ne coûte que ce relevé */ }
    }

    /// <summary>Les réglages suivis qu'un levier fait basculer dès son écriture, relevés juste après la
    /// pose. Pas le fichier d'échange : le registre le dit posé tout de suite, mais il ne prend effet qu'au redémarrage
    /// (HAGS se lit dans son état effectif, qui attend aussi le redémarrage).</summary>
    internal static readonly IReadOnlySet<string> ReglagesAEffetImmediat = new HashSet<string>(StringComparer.Ordinal) { "windows.game_mode" };

    /// <summary>Parmi les leviers à effet différé, ceux qui agissent au prochain lancement du jeu, sans redémarrage : leur
    /// annulation et leur retrait le disent ainsi.</summary>
    private static readonly IReadOnlySet<string> AuLancementDuJeu = new HashSet<string>(StringComparer.Ordinal) { "latency.gameModeOn" };

    /// <summary>La phrase d'un rendu différé : « au prochain lancement du jeu » pour un levier qui agit ainsi, sinon la clé du
    /// redémarrage.</summary>
    private static string Differe(ITweak levier, string cleAuRedemarrage, string cleAuLancement)
        => AuLancementDuJeu.Contains(levier.Id) ? cleAuLancement : cleAuRedemarrage;

    /// <summary>Ce qui a basculé depuis le scan précédent entre au journal, et la carte se refait. Le relevé attend son tour
    /// (quelques secondes au plus) hors du fil de l'écran ; le journal s'écrit sous le verrou du relevé, refusé : la bascule
    /// revient au relevé suivant. La carte reste sur le fil de l'appelant.</summary>
    internal async Task ReleverReglagesSuivisAsync(IReadOnlyDictionary<string, string> etats, DateTimeOffset maintenant)
    {
        if (SuiviReglages is not { } suivi || JournalChangements is not { } journal) return;
        string Valeur(string v) => _localize(v == "on" ? "Reglage.Allume" : v == "off" ? "Reglage.Eteint" : v);
        string Libeller(string id, string avant, string apres) => string.Format(CultureInfo.CurrentCulture,
            _localize("Changement.Reglage"), _localize("Reglage." + id), Valeur(avant), Valeur(apres));
        bool Consigner(IReadOnlyList<Changement> changes)
        {
            var deja = journal.Lire();
            var demarrage = DemarrageDuPc();
            return changes.Select(c => journal.Ajouter(DaterAuDemarrage(DaterSurLaPose(c, deja, demarrage), demarrage)))
                .ToList().TrueForAll(r => r.IsOk);
        }
        var changes = await Task.Run(() => suivi.Relever(etats, maintenant, Libeller, Consigner)).ConfigureAwait(true);
        if (changes.Count > 0) Afficher(MemoireParties?.Lire() ?? []);
    }

    /// <summary>Un réglage du BIOS ne prend effet qu'au démarrage ; vu entre deux scans, il date du dernier
    /// démarrage tombé entre eux (plusieurs redémarrages : les parties entre le scan d'avant et ce démarrage sortent du
    /// jugement, aucune n'est mal classée). Limite connue : un outil qui écrit le BIOS depuis Windows sans redémarrer.</summary>
    internal static Changement DaterAuDemarrage(Changement c, DateTimeOffset demarrage)
        => c.Cle?.StartsWith("bios.", StringComparison.Ordinal) == true && demarrage < c.Quand && (c.Depuis is not { } d || demarrage > d)
            ? c with { Quand = demarrage } : c;

    /// <summary>
    /// Un réglage que DrDXT pose au redémarrage bascule au démarrage suivant, vu parfois des jours plus tard : borné aux
    /// deux scans, il laisserait hors jeu toutes les parties d'entre eux et ne se jugerait jamais. La pose de son levier,
    /// si elle tombe dans la fenêtre, en borne le début ; le démarrage du PC qui la suit, la fin.
    /// </summary>
    internal static Changement DaterSurLaPose(Changement c, IReadOnlyList<Changement> journal, DateTimeOffset demarrage)
    {
        if (c.Cle is not { } cle || !cle.EndsWith(":on", StringComparison.Ordinal)
            || !LevierQuiAllume.TryGetValue(cle[..^3], out var id)) return c;
        var pose = journal.Where(p => p.Cle == "pose:" + id && p.Quand <= c.Quand && (c.Depuis is not { } d || p.Quand >= d))
            .OrderByDescending(p => p.Quand).FirstOrDefault();
        if (pose is null) return c;
        return c with { Depuis = pose.Quand, Quand = demarrage > pose.Quand && demarrage < c.Quand ? demarrage : c.Quand };
    }

    /// <summary>Un réglage que DrDXT allume, dont la bascule a un début connu (la pose, une fois daté par
    /// <see cref="DaterSurLaPose"/>).</summary>
    private static bool DateeSurLaPose(Changement c) => c.Depuis is not null && c.Cle is { } cle
        && cle.EndsWith(":on", StringComparison.Ordinal) && LevierQuiAllume.ContainsKey(cle[..^3]);

    /// <summary>Deux réglages allumés partis du même début : le mode Jeu relevé à la pose et HAGS au redémarrage, posés par
    /// le même clic. On ne sait pas lequel a joué sur quelles parties ; ils se jugent ensemble.</summary>
    private static bool DuMemeClic(Changement a, Changement b) => DateeSurLaPose(a) && DateeSurLaPose(b) && a.Depuis == b.Depuis;

    internal static bool LevierDeLatence(Changement c)
        => c.Cle?.Split(':')[0] is { } reglage && LevierQuiAllume.TryGetValue(reglage, out var id) && AlternanceDesEssais.JugeSurLaLatence(id);

    /// <summary>La pose d'un levier se note avec sa date (clé « pose:id »), pour que le
    /// retrait ne touche que ce que DrDXT a posé. Elle n'est pas affichée : la bascule, elle, l'est.</summary>
    internal void NoterPoses(IEnumerable<string> leviersPoses, DateTimeOffset maintenant)
    {
        if (JournalChangements is null) return;
        // Toutes les poses se datent, pas seulement celles qui allument un réglage suivi.
        foreach (var id in leviersPoses.Distinct())
            JournalChangements.Ajouter(new Changement(maintenant, id, null, "pose:" + id));
    }

    /// <summary>Un retrait automatique (ce qui coûte en jeu, la garde d'incident) rend des réglages : « Annuler » l'attend, et
    /// il ne part pas pendant un « Annuler » (il se refera au prochain affichage). Sinon deux rendus du même instantané.</summary>
    internal bool RetraitAutomatiqueEnCours
    {
        get => _retraitEnCours;
        set { _retraitEnCours = value; OperationLongueChangee(); }
    }

    /// <summary>Règle 4 du plan : un levier sans gain se retire tout seul. Un changement jugé « moins bien en jeu »,
    /// posé par un levier de DrDXT encore au journal des leviers, est annulé (valeur d'origine relue au moment de la
    /// pose) ; le journal le perd, et la carte le dit, « retiré » seulement relu à l'origine (<see cref="Relu"/>). Rendu :
    /// les phrases à ajouter en tête de la carte.</summary>
    internal async Task<IReadOnlyList<string>> RetirerCeQuiCouteAsync(IReadOnlyList<PartieRelevee> parties)
    {
        // Sans clic : un helper arrêté entre-temps n'est pas relancé (aucune invite Windows), le retour attend et se dit.
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        var dits = new List<string>();
        // Un retrait se juge avec les mises à jour de la machine lues.
        try { await MisesAJourAsync(); }
        catch (Exception) { /* mises à jour non lues : le retrait se juge sans elles, comme avant cette lecture */ }
        // Le calmeur de voisins n'est pas un levier du catalogue ; il se juge pareil.
        if (JournalChangements is not null) dits.AddRange(RetirerLeCalmeurSIlCoute(JournalChangements.Lire(), parties));
        if (JournalChangements is null || LevierParId is null || _journalLeviers is null || RetraitAutomatiqueEnCours
            || AnnulerLevierCommand.IsRunning) return dits;
        // Rien ne s'écrit sans clic pendant qu'un jeu tourne (une partie relancée aussitôt, un second jeu) : le retrait attend
        // une fin de partie jeu fermé, le levier reste annulable d'un clic.
        if ((JeuOuvertPourLeTest ?? _gameLocator.JeuOuvert)()) return dits;
        RetraitAutomatiqueEnCours = true;
        try
        {
            dits.AddRange(await RendreApresUnArretAsync());
            var journal = JournalChangements.Lire();
            foreach (var (reglage, id) in LevierDuReglage)
            {
                // Seule la dernière bascule de ce réglage compte : une plus ancienne a déjà été suivie d'autre chose.
                var bascules = journal.Where(c => c.Cle?.StartsWith(reglage + ":", StringComparison.Ordinal) == true)
                    .OrderByDescending(c => c.Quand).ToList();
                if (bascules.FirstOrDefault() is not { } c || c.Cle != reglage + ":on") continue;
                // Elle doit venir de la pose de DrDXT : la pose la précède, et aucune autre bascule ne les sépare.
                var pose = journal.Where(p => p.Cle == "pose:" + id).OrderByDescending(p => p.Quand).FirstOrDefault();
                if (pose is null || pose.Quand > c.Quand) continue;
                if (bascules.Skip(1).FirstOrDefault() is { } precedente && precedente.Quand >= pose.Quand) continue;
                // Le même bilan que l'écran (bornes des changements voisins, bilan figé).
                if (BilansBornes(journal, parties).FirstOrDefault(x => x.C == c).B is not { Verdict: VerdictEnJeu.Pire } bilan) continue;
                if (_journalLeviers.Lire().FirstOrDefault(s => s.TweakId == id) is not { } snapshot || LevierParId(id) is not { } levier) continue;
                // La bascule suivie peut dater d'avant un changement du joueur que l'état effectif ne montre pas encore (HAGS
                // coupé, sans redémarrage) : seule la valeur de DrDXT se retire, comme partout ailleurs.
                if (EtatDeLaPose(snapshot) != Pose.DeDrDxt) continue;
                if (HelperJoignableSansUac is { } joignable && levier.RequiresElevation && !await joignable())
                {
                    dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Changement.ARetirer"), c.Libelle));
                    continue;
                }
                var r = await levier.RevertAsync(snapshot);
                if (!r.IsOk)
                {
                    // Resté posé et listé : « Annuler » le retire encore, l'écran dit seulement que le retrait n'est pas parti.
                    dits.Add(RaisonLisible.OuIncertain(r, string.Format(CultureInfo.CurrentCulture, _localize("Changement.RetraitRate"), c.Libelle,
                        RaisonLisible.Dire(r, _localize)), _localize, c.Libelle));
                    continue;
                }
                var relu = Relu(snapshot);
                if (relu == DrDxt.Application.Tweaks.EtatApplique.Applied)
                {
                    dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Changement.RetraitRate"), c.Libelle, _localize("Leviers.EncorePose")));
                    continue;
                }
                // Le verdict est figé : les parties jouées après le retrait le feraient passer à « sans effet ».
                try { JournalChangements.Figer(c, bilan); }
                catch (Exception) { /* un journal non écrit garde le retrait ; seul le libellé peut bouger */ }
                // Le service de réapplication le reposerait (manifeste signé) : il en sort d'abord, avant le journal que l'écran montre.
                var sorti = await RetirerDuManifesteAsync(id);
                relu = await RelireApresLaSortie(levier, snapshot, relu);
                if (relu == DrDxt.Application.Tweaks.EtatApplique.Applied)
                {
                    dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Changement.RetraitRate"), c.Libelle, _localize("Leviers.EncorePose")));
                    continue;
                }
                if (!_journalLeviers.Retirer(id)) dits.Add(_localize("Leviers.JournalPasMisAJour"));
                dits.Add(string.Format(CultureInfo.CurrentCulture, _localize(relu != DrDxt.Application.Tweaks.EtatApplique.NotApplied
                    ? "Changement.RetireNonRelu" : levier.TakesEffectAfterReboot
                        ? Differe(levier, "Changement.RetireAuRedemarrage", "Changement.RetireAuLancementDuJeu") : "Changement.Retire"), c.Libelle));
                if (!sorti.IsOk)
                    dits.Add(RaisonLisible.OuIncertain(sorti, string.Format(CultureInfo.CurrentCulture, _localize("Changement.PasSortiDuManifeste"),
                        c.Libelle, RaisonLisible.Dire(sorti, _localize)), _localize, c.Libelle));
            }
        }
        finally { RetraitAutomatiqueEnCours = false; }
        if (dits.Count > 0) ChargerLeviersPoses();
        return dits;
    }

    /// <summary>Sort un levier du manifeste que le service relit au démarrage. Échec : le retrait reste fait sur la
    /// machine, et la carte le dit (l'entrée restée pourrait être reposée après une mise à jour de Windows).</summary>
    internal Func<string, Task<Result>>? RetirerDuManifeste { get; set; }

    private Task<Result> RetirerDuManifesteAsync(string id) => RetirerDuManifeste?.Invoke(id) ?? Task.FromResult(Result.Ok());

    /// <summary>
    /// La carte des parties commence par ce que les changements ont fait en jeu : les gains (et pertes) prouvés par séparation nette sur trois
    /// parties de chaque côté, composés par <see cref="DrDxt.Measurement.GainCumule"/> (jamais additionnés), ce qui n'a rien
    /// changé, et ce qui attend des parties. null = aucun changement.
    /// </summary>
    internal static string? LigneDesGains(IReadOnlyList<(Changement C, BilanDuChangement B)> changements, Func<string, string> localiser)
    {
        if (changements.Count == 0) return null;
        var fr = CultureInfo.CurrentCulture;
        // Le bruit d'un gain = l'écart entre la médiane et le pire cas des bornes (3 contre 3 se séparent
        // par hasard une fois sur 20) ; le total en hérite, et la phrase dit « au moins ».
        var prouves = changements
            .Where(x => x.B.Verdict is VerdictEnJeu.Mieux or VerdictEnJeu.Pire && x.B.Low1Avant is > 0 && x.B.Low1Apres is not null
                        && x.B.EcartBornesPercent is not null)
            // Les changements d'un même instant (d'un même clic, même le mode Jeu daté à la pose et HAGS au redémarrage)
            // n'ont qu'une mesure : comptée une fois, le bilan figé faisant foi. Limite : l'instant est arrondi à la
            // minute, deux changements de part et d'autre d'une minute ronde se comptent séparément.
            .GroupBy(x => (DateeSurLaPose(x.C) ? x.C.Depuis!.Value : x.C.Quand).UtcTicks / MemeInstant.Ticks)
            .SelectMany(g => g.Any(y => y.C.Fige is not null) ? [g.ToList()] : g.GroupBy(y => y.B).Select(b => b.ToList()))
            .Select(g =>
            {
                var x = g.FirstOrDefault(y => y.C.Fige is not null, g.First());
                var ecart = (x.B.Low1Apres!.Value / x.B.Low1Avant!.Value - 1) * 100;
                var borne = Math.Min(x.B.EcartBornesPercent!.Value, ecart);
                return (Gain: new DrDxt.Measurement.GainProuve(string.Join(" + ", g.Select(y => y.C.Libelle)),
                    Docteur.T("App.LowMedianEnPartie", x.B.Jeu), ecart, ecart - borne), BornePercent: borne);
            })
            .ToList();
        var total = DrDxt.Measurement.GainCumule.Additionner(prouves.Select(p => p.Gain).ToList());
        // « Au moins » : la pire paire de chaque mesure, composée comme le total, une valeur prise par les parties. Une
        // perte figée compte pour son écart mesuré. Pas de somme quadratique : ce sont des bornes, pas des écarts-types.
        var auMoins = (prouves.Where(g => total.Retenus.Contains(g.Gain.Levier))
            .Aggregate(1.0, (f, g) => f * (1 + g.BornePercent / 100)) - 1) * 100;
        // Un total sous son propre bruit ne s'annonce pas (règle de GainCumule.Phrase), pas de « +0,6 % (au moins -7,0 %) ».
        var gains = total.Retenus.Count == 0
            ? localiser("Parties.Gains.Aucun")
            : Math.Abs(total.TotalPercent) <= total.BruitPercent
                ? string.Format(fr, localiser("Parties.Gains.SousLeBruit"), total.TotalPercent, total.Retenus.Count, total.BruitPercent)
            : string.Format(fr, localiser("Parties.Gains.Total"), total.TotalPercent, total.Retenus.Count, auMoins);
        var ligne = string.Format(fr, localiser("Parties.Gains"), gains,
            changements.Count(x => x.B.Verdict == VerdictEnJeu.PasDeDifference),
            changements.Count(x => x.B.Verdict == VerdictEnJeu.PasEncoreJuge));
        // Un gain prouvé sur un autre jeu ne s'additionne pas (autre instrument) : il est compté, pas tu.
        var autreJeu = prouves.Count - total.Retenus.Count;
        return Pluriels.Accorder(autreJeu > 0 ? ligne + " " + string.Format(fr, localiser("Parties.Gains.AutreJeu"), autreJeu) : ligne);
    }

    /// <summary>Seuls les changements jugés restent une phrase ; ceux qui ne le sont pas
    /// encore (ou ne le seront jamais) passent dans le tableau <see cref="TableauDesChangementsEnAttente"/>.</summary>
    internal static IReadOnlyList<string> LignesDesChangements(IReadOnlyList<(Changement C, BilanDuChangement B)> changements, Func<string, string> localiser) =>
        changements.Where(x => !EnAttente(x.B)).Select(x => LigneChangement(x.C, x.B, localiser)).ToList();

    private static bool EnAttente(BilanDuChangement b) => b.Avant == 0 || b.Verdict == VerdictEnJeu.PasEncoreJuge || b.Jeu is null;

    /// <summary>Les changements pas encore jugés tiennent dans un tableau plutôt qu'en paragraphe : le changement, sa date, et où il en est. Les parties d'avant ne
    /// peuvent plus augmenter : sans assez de parties du même jeu avant, il n'est pas jugé (une partie d'un autre jeu peut le juger plus tard), et le tableau le dit.</summary>
    internal static IReadOnlyList<LigneEnAttente> TableauDesChangementsEnAttente(IReadOnlyList<(Changement C, BilanDuChangement B)> changements,
        Func<string, string> localiser)
    {
        var fr = CultureInfo.CurrentCulture;
        var min = ComparaisonDesParties.PartiesMinimum;
        return changements.Where(x => EnAttente(x.B)).Select(x =>
        {
            var b = x.B;
            var etat = b.Avant == 0 ? localiser("Changement.Tableau.SansAvant")
                : b.Avant < min ? string.Format(fr, localiser("Changement.Tableau.PeuAvant"), b.Avant, min)
                : Pluriels.Accorder(string.Format(fr, localiser("Changement.Tableau.Attend"), Math.Max(1, min - b.Apres)));
            return new LigneEnAttente(x.C.Libelle, x.C.Quand.LocalDateTime.ToString(localiser("Format.Jour"), fr), etat);
        }).ToList();
    }

    /// <summary>Une ligne par changement : jugé en jeu avec ses chiffres, ou combien de parties il manque.</summary>
    internal static string LigneChangement(Changement c, BilanDuChangement b, Func<string, string> localiser)
    {
        var fr = CultureInfo.CurrentCulture;
        var date = c.Quand.LocalDateTime.ToString(localiser("Format.Jour"), fr);
        // Aucune partie d'avant : les parties à venir n'en ajouteront jamais, ce changement ne se jugera pas en jeu.
        if (b.Avant == 0)
            return string.Format(fr, localiser("Changement.SansAvant"), c.Libelle, date);
        if (b.Verdict == VerdictEnJeu.PasEncoreJuge || b.Jeu is null)
            return Pluriels.Accorder(string.Format(fr, localiser("Changement.PasEncore"), c.Libelle, date,
                b.Apres, b.Avant, ComparaisonDesParties.PartiesMinimum));
        // Sans médiane commune, aucun chiffre ne se dit : seulement pourquoi, et ce qui le jugera.
        if (b.Verdict == VerdictEnJeu.PasComparable)
            return string.Format(fr, localiser("Changement.PasComparable"), c.Libelle, date, ComparaisonDesParties.PartiesMinimum);
        var verdict = localiser(b.Verdict switch
        {
            VerdictEnJeu.Mieux => "Changement.Mieux",
            VerdictEnJeu.Pire => "Changement.Pire",
            _ => "Changement.Pareil",
        });
        string N(double? v) => v is { } x ? x.ToString("0", fr) : "?";
        // La mesure qui a jugé se dit : la latence pour un levier de latence qui la porte ; sinon les 1 % bas, et pour un levier
        // de latence, que ce ne sont pas sa mesure.
        if (b.SurLaLatence)
            return string.Format(fr, localiser("Changement.JugeLatence"), c.Libelle, date, verdict,
                NomDuJeu(b.Jeu), N(b.LatenceAvant), N(b.LatenceApres), b.Avant, b.Apres);
        return string.Format(fr, localiser("Changement.Juge"), c.Libelle, date, verdict,
            NomDuJeu(b.Jeu), N(b.Low1Avant), N(b.Low1Apres), N(b.FpsAvant), N(b.FpsApres), b.Avant, b.Apres)
            + (LevierDeLatence(c) ? " " + localiser("Changement.JugeSurLesImages") : "");
    }

    private async Task RetirerEtDireAsync(IReadOnlyList<PartieRelevee> parties)
    {
        try
        {
            var dits = await RetirerCeQuiCouteAsync(parties);
            foreach (var phrase in dits)
            {
                _ditsDuRetrait.Add(phrase);
                LignesParties.Insert(0, phrase);
            }
            if (dits.Count > 0) AnnoncerResumeEtDetail();
            // Un réglage à effet immédiat rendu se relève tout de suite, comme à la pose.
            if (dits.Count > 0) await ReleverReglagesHorsScanAsync(ReglagesAEffetImmediat).ConfigureAwait(true);
        }
        catch (Exception) { /* un retrait raté laisse le verdict à l'écran ; le bouton Annuler reste */ }
    }

    /// <summary>Une capture ratée plus récente que la dernière partie se dit en tête de la carte : le relevé de fond ne
    /// reste pas « allumé » des semaines sans rien relever. UI1 : sans son code d'erreur, qui reste dans le fichier.</summary>
    internal static string? LigneEchec(PartiesMemorisees.EchecDeCapture? echec, IReadOnlyList<PartieRelevee> parties,
        Func<string, string> localiser)
    {
        if (echec is null || parties.Any(p => p.Fin >= echec.Quand)) return null;
        var fr = CultureInfo.CurrentCulture;
        // Le service arrêté ne revient pas en jouant : le relevé sans droits ne peut pas le relancer.
        var cle = echec.Raison.Contains(ErrorCodes.PmonUnavailable, StringComparison.Ordinal) ? "Parties.CaptureRateeService" : "Parties.CaptureRatee";
        return string.Format(fr, localiser(cle), echec.Quand.LocalDateTime.ToString(localiser("Format.JourHeure"), fr));
    }
}
