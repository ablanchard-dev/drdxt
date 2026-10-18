namespace DrDxt.Core;

/// <summary>Pourquoi un candidat n'a pas ete garde. Chaque valeur est une phrase differente pour
/// le joueur : « pas teste » et « teste et rejete » ne se confondent jamais.</summary>
public enum CandidateOutcome
{
    /// <summary>Gain prouve au-dessus de la resolution de la session. Conserve.</summary>
    Kept,

    /// <summary>Degradation prouvee. Annule.</summary>
    RejectedRegression,

    /// <summary>Mesure, mais l'ecart reste sous ce que la session sait resoudre. Annule : on ne
    /// garde pas un changement qu'on ne sait pas justifier.</summary>
    RejectedUnresolved,

    /// <summary>L'ecriture n'a pas pris (relecture). Rien n'a ete mesure.</summary>
    ApplyFailed,

    /// <summary>La machine est devenue instable apres application. Annule immediatement.</summary>
    Unstable,

    /// <summary>Sa nature interdit a l'autopilote de le tenter : reboot, BIOS, action externe.</summary>
    NotAttempted,
}

/// <summary>Ce qu'un candidat a donne. <paramref name="Evidence"/> est nul quand rien n'a ete
/// mesure, et c'est une information, pas un trou.</summary>
public sealed record CandidateResult(
    string Id,
    CandidateOutcome Outcome,
    Evidence? Evidence,
    string? Detail,
    string? RaisonCle = null,
    /// <summary>Une preuve par grandeur : moyenne, 1 % low, p95 frametime, chacune avec son
    /// ecart et sa resolution. Vide quand rien n'a ete mesure. <see cref="Evidence"/> reste la
    /// preuve de la moyenne, pour les appelants qui n'en veulent qu'une.</summary>
    IReadOnlyList<MetricEvidence>? Metriques = null,
    /// <summary>Combien de passes d'alternance l'experience a reellement consommees. L'arret
    /// sequentiel fait varier ce nombre : c'est le temps de jeu que le candidat a coute.</summary>
    int PassesAb = 0,
    /// <summary>La grandeur de queue qui a valide le keep, recopiee telle quelle de
    /// <see cref="KeepDecision.Queue"/>, jamais recalculee ici. <see cref="PreuveDeQueue.Aucune"/>
    /// pour tout ce qui n'a pas ete garde. Pur enregistrement : c'est ce que le journal des
    /// candidats accumule pour trancher un jour si le p95 seul merite de garder un reglage.</summary>
    PreuveDeQueue Queue = PreuveDeQueue.Aucune);

/// <summary>Le profil construit. <paramref name="EndedInBestState"/> est la garantie qui compte.</summary>
public sealed record AutopilotProfile(
    SceneResolution Scene,
    IReadOnlyList<CandidateResult> Results,
    bool EndedInBestState)
{
    public IEnumerable<CandidateResult> Kept => Results.Where(r => r.Outcome == CandidateOutcome.Kept);
}

/// <summary>
/// La boucle : observer → mesurer ce qu'on peut voir → tenter → prouver → garder ou annuler.
///
/// `OcSweep` avait deja cette philosophie et n'etait appele que par `tools/octune` : il ne savait
/// balayer qu'un offset GPU entier, contre un banc GPU precis. Ce moteur-ci est la meme idee rendue
/// generique : il ne sait rien du GPU, rien des FPS, rien du materiel. Il sait juste tenter des
/// actions et refuser de garder ce qu'il ne peut pas prouver.
///
/// ══ ce qui le rend different d'un catalogue de tweaks ══
/// 1. **Il mesure d'abord ce qu'il peut voir.** L'A/A (<see cref="SceneResolution"/>) passe avant
///    tout candidat : si la scene ne resout que +/-5 %, tenter un levier a +1 % est du temps
///    perdu d'avance, et le moteur le sait avant de le depenser.
/// 2. **Il refuse une scene qui derive.** Mesure du 07/09/2026 : deux des trois captures reelles
///    derivent plus qu'elles ne bruitent. Sur une telle scene, un A/B credite le temps ecoule au
///    premier levier essaye.
/// 3. **Il relit ce qu'il ecrit.** Mesure du 01/09 : une limite de puissance « appliquee a 110 % »
///    relisait 100 %, le pilote clampant en silence.
/// 4. **Il n'attribue jamais un gain a une action qui ne peut pas le produire** :
///    <see cref="CandidateDescriptor.AffectedMetrics"/>.
/// 5. **La machine finit dans le meilleur etat prouve**, y compris si tout casse en route. C'est
///    la seule garantie qui protege le joueur d'un autopilote a moitie passe.
///
/// Pur : aucune I/O, aucune horloge. Les mesures, les ecritures et la stabilite sont des couture
/// injectees, donc toute la boucle se teste sans machine.
/// </summary>
public static class AutopilotEngine
{
    /// <summary>Construit le meilleur profil prouve.</summary>
    /// <param name="candidats">Les actions a tenter, dans l'ordre ou l'appelant les a classees.</param>
    /// <param name="mesurer">Une passe de mesure. Rend la grandeur d'objectif (FPS moyen, debit…) ;
    /// 0 ou non fini = passe inexploitable.</param>
    /// <param name="passesAa">Nombre de passes de l'A/A initial. Sous trois, aucune resolution.</param>
    /// <param name="stable">La machine est-elle stable ? Consulte seulement quand le candidat le
    /// demande.</param>
    public static AutopilotProfile Run(
        IReadOnlyList<IAutopilotCandidate> candidats,
        Func<double> mesurer,
        int passesAa = 3,
        Func<bool>? stable = null,
        OptimizationGoal but = OptimizationGoal.Competitive,
        int alternancesMax = 4)
        => Run(candidats,
               () => { var v = mesurer(); return v > 0 && double.IsFinite(v) ? new GameMetrics(v, v) : null; },
               passesAa, stable, but, alternancesMax);

    /// <summary>La meme boucle, sur un jeu : chaque passe rend toutes ses grandeurs, et chacune
    /// est jugee separement.</summary>
    /// <param name="alternancesMax">Le nombre de passes d'alternance du protocole. **Fixe** : le
    /// moteur va au bout et ne prend qu'une decision statistique, sur l'experience complete.
    ///
    /// Ce parametre s'appelait un « plafond » et l'experience s'arretait des qu'un gain
    /// devenait significatif. C'etait de l'optional stopping : chaque regard est une occasion
    /// supplementaire de franchir le seuil par hasard, et s'arreter uniquement sur les succes est
    /// exactement ce qui cree le biais. Seuls des arrets physiques pre-declares interrompent le
    /// protocole, parce qu'ils ne dependent pas du resultat mesure.</param>
    /// <param name="representativite">Ce que la scene represente (partie reelle,
    /// entrainement…). Sert a la regle de decidabilite ; <c>Unknown</c> par defaut.</param>
    public static AutopilotProfile Run(
        IReadOnlyList<IAutopilotCandidate> candidats,
        Func<GameMetrics?> mesurer,
        int passesAa = 3,
        Func<bool>? stable = null,
        OptimizationGoal but = OptimizationGoal.Competitive,
        int alternancesMax = 4,
        SceneRepresentativity representativite = SceneRepresentativity.Unknown,
        CancellationToken ct = default)
    {
        stable ??= static () => true;
        var resultats = new List<CandidateResult>();

        // L'annulation se lit ici, pas dans la passe : `MesureJeu` avale toute exception comme une
        // passe inexploitable, donc une annulation levee par le banc serait devenue « une passe a 0 »
        // et la session aurait continue, levier pose.
        GameMetrics? Mesurer()
        {
            ct.ThrowIfCancellationRequested();
            return mesurer();
        }

        // ── 1. L'A/A : de quoi cette scene est-elle capable ? ────────────────────────────────
        // L'A/A se mesure sur la moyenne, pas sur le score composite. La derive est une
        // propriete de la scene, et la moyenne est la grandeur qui la lit le mieux : mesuree sur une
        // session Warzone le 07/09/2026 (docs/PROTOCOLES-DE-MESURE-IN-GAME.md), elle resout a +/-4,6 % la ou
        // le score composite annonce +/-72 %. Prendre le
        // score reviendrait a declarer inutilisable une scene parfaitement stable.
        //
        // L'A/A ne fixe pas le seuil de decision : l'experience appariee annonce sa propre
        // resolution, mesuree sur ses residus. Il reste a l'A/A deux roles : dire si la scene
        // derive, et dire d'avance ce qu'elle permettra d'esperer.
        var aa = new List<double>();
        for (var i = 0; i < Math.Max(0, passesAa); i++) aa.Add(MesureJeu(Mesurer).AvgFps);
        var scene = SceneResolution.Probe(aa);

        // Une scene qui derive ou qu'on ne sait pas caracteriser ne permet aucune conclusion :
        // tenter quand meme reviendrait a attribuer le temps qui passe au premier levier.
        if (scene.Verdict != SceneVerdict.Usable)
        {
            foreach (var c in candidats)
                resultats.Add(new CandidateResult(c.Descriptor.Id, CandidateOutcome.NotAttempted, null,
                    scene.Verdict == SceneVerdict.DriftDominant
                        ? Docteur.T("AE.SceneDerive")
                        : Docteur.T("AE.PassesInsuffisantes")));
            return new AutopilotProfile(scene, resultats, EndedInBestState: true);
        }

        var toutRemis = true;

        foreach (var candidat in candidats)
        {
            var d = candidat.Descriptor;

            // ── 2. Sa nature autorise-t-elle l'autopilote a le tenter ? ──────────────────────
            if (d.Kind != CandidateKind.AutoHot)
            {
                resultats.Add(new CandidateResult(d.Id, CandidateOutcome.NotAttempted, null,
                    Docteur.T("AE.Nature", d.Kind)));
                continue;
            }

            // ── 2 bis. La scene peut-elle voir son effet ? ────────────────────
            // Decide avant la moindre ecriture, sur la resolution que l'A/A vient de mesurer. Et le
            // nombre d'alternances se fixe ici, par l'effet declare, jamais par ce que la mesure
            // montrera : c'est un protocole pre-enregistre, pas un arret optionnel.
            // Effet inconnu : comportement inchange (parametre de l'appelant).
            var alternances = alternancesMax;
            if (d.EffetAttenduPct is { } effet)
            {
                if (Decidabilite.NonDecidable(effet, scene, representativite) is { } motif)
                {
                    resultats.Add(new CandidateResult(d.Id, CandidateOutcome.NotAttempted, null, motif));
                    continue;
                }
                alternances = Decidabilite.AlternancesPour(effet);
            }

            try
            {
                // ── 3. Appliquer, puis relire ────────────────────────────────────────────────
                var applique = candidat.Apply();
                var relu = applique.IsOk ? candidat.ReadBack() : Result<bool>.Ok(false);
                if (!applique.IsOk || !relu.IsOk || !relu.Value)
                {
                    toutRemis &= Annuler(candidat);
                    resultats.Add(new CandidateResult(d.Id, CandidateOutcome.ApplyFailed, null,
                        applique.IsOk
                            ? Docteur.T("AE.EcritureNonRelue")
                            : applique.Error?.Message));
                    continue;
                }

                // ── 4. La machine tient-elle encore debout ? ─────────────────────────────────
                if (d.NeedsStability && !stable())
                {
                    toutRemis &= Annuler(candidat);
                    resultats.Add(new CandidateResult(d.Id, CandidateOutcome.Unstable, null,
                        Docteur.T("AE.Instable")));
                    continue;
                }

                // ── 5. A-b-b-a, repete jusqu'a savoir : l'effet du reglage, pas celui du temps ─
                // Quatre passes ne laissent qu'un degre de liberte au modele (trois parametres a
                // ajuster) : la resolution qui en sort est enorme. Mesure du 07/09/2026 sur le corpus
                // des 8 captures Warzone : l'ecart p95 observe sous no-op tombe de 13,7 % a 3,9 % entre
                // 4 et 12 alternances, sans allonger aucune passe (fenetres glissantes, donc exploratoire).
                // Mesure en fenetres disjointes le 08/09 : seules 8 alternances sont validees hors corpus
                // d'entrainement (`tools/tailbench --loco`).
                //
                // A-b-b-a plutot qu'a-b-a-b : les deux sont valides, mais a-b-b-a est exactement
                // orthogonal au temps (r² = 0 contre 0,2), et son miroir b-a-a-b l'est aussi. On
                // peut donc empiler les blocs sans jamais recorreler l'etat et l'horloge.
                var (passes, interrompu) = MesurerEnAlternance(candidat, alternances, Mesurer);
                if (interrompu) toutRemis = false;
                IReadOnlyList<MetricEvidence> preuves = Juger(passes, scene.ResolutionPct);

                var preuve = preuves.FirstOrDefault(m => m.Metrique == GameMetricKind.Moyenne)?.Preuve
                             ?? preuves.FirstOrDefault()?.Preuve;

                // ── 6. Garder, ou remettre comme avant ───────────────────────────────────────
                // La decision n'appartient pas au moteur. Elle appartient a `KeepPolicy`, qui
                // sait qu'un gain de sous-systeme ne suffit jamais a garder un reglage en profil
                // gaming, et qu'une moyenne qui monte sans fluidite prouvee n'est pas un profil
                // Competitive ameliore. Un switch local ici aurait reintroduit exactement ce que
                // cette politique existe pour empecher.
                var decision = KeepPolicy.DecideParMetrique(preuves, but);

                var issue = decision.Keep
                    ? CandidateOutcome.Kept
                    : preuve?.Outcome switch
                    {
                        EvidenceOutcome.ProvenRegression => CandidateOutcome.RejectedRegression,
                        EvidenceOutcome.Invalid => CandidateOutcome.ApplyFailed,
                        _ => CandidateOutcome.RejectedUnresolved,
                    };

                if (issue == CandidateOutcome.Kept)
                {
                    // Ne pas supposer l'etat final de la boucle. L'ancien protocole a-b-a-b
                    // finissait en B, donc conserver ne demandait rien ; a-b-b-a finit en A, et un
                    // bloc miroir b-a-a-b finit en B. Supposer revient a rendre la machine dans
                    // l'etat de reference en croyant avoir garde le gain.
                    var remis = candidat.Apply();
                    var reluApresKeep = remis.IsOk ? candidat.ReadBack() : Result<bool>.Ok(false);
                    if (!remis.IsOk || !reluApresKeep.IsOk || !reluApresKeep.Value) toutRemis = false;
                    // Seulement une fois le levier relu en place : un keep qui n'a pas repris garde
                    // son filet arme, et le demarrage suivant le rendra.
                    else if (!candidat.Garder().IsOk)
                        decision = decision with
                        {
                            Detail = decision.Detail + Docteur.T("AE.JournalRetourNonMisAJour"),
                        };
                }
                else
                {
                    toutRemis &= Annuler(candidat);
                }

                resultats.Add(new CandidateResult(d.Id, issue, preuve, decision.Detail,
                    decision.RaisonCle, preuves, passes.Count, decision.Queue));
            }
            catch (Exception ex)
            {
                // Une exception ne doit jamais laisser la machine dans l'etat d'essai. C'est la
                // difference entre un autopilote et un script qui a plante au milieu.
                toutRemis &= Annuler(candidat);
                if (ex is OperationCanceledException)
                {
                    // Le joueur a arrete : l'etat de reference est rendu, et on ne tente rien d'autre.
                    resultats.Add(new CandidateResult(d.Id, CandidateOutcome.NotAttempted, null,
                        Docteur.T("AE.SessionAnnulee")));
                    break;
                }
                resultats.Add(new CandidateResult(d.Id, CandidateOutcome.ApplyFailed, null,
                    Docteur.T("AE.ExceptionPendantEssai", ex.GetType().Name)));
            }
        }

        return new AutopilotProfile(scene, resultats, toutRemis);
    }

    /// <summary>L'etape 5 : les passes A-b-b-a, repetees selon le protocole. <c>Interrompu</c> = une bascule
    /// d'etat a echoue, et la machine n'est peut-etre plus dans l'etat de reference.</summary>
    private static (List<(bool B, GameMetrics M)> Passes, bool Interrompu) MesurerEnAlternance(
        IAutopilotCandidate candidat, int alternances, Func<GameMetrics?> Mesurer)
    {
        var passes = new List<(bool B, GameMetrics M)>();
        var interrompu = false;

        for (var bloc = 0; bloc * 4 < Math.Max(4, alternances); bloc++)
        {
            // Bloc pair : A-b-b-a. Bloc impair : B-a-a-b. L'empilement reste symetrique.
            var motif = bloc % 2 == 0
                ? new[] { false, true, true, false }
                : new[] { true, false, false, true };

            foreach (var etatB in motif)
            {
                var ok = etatB ? candidat.Apply().IsOk : Annuler(candidat);
                if (!ok) { interrompu = true; break; }
                passes.Add((etatB, MesureJeu(Mesurer)));
            }
            if (interrompu) break;

            // Aucune decision statistique ici. Le protocole est fixe.
            //
            // Ce code s'arretait des qu'un gain devenait significatif, avec pour
            // justification : « un Unresolved ne stoppe jamais, donc les regards repetes
            // ne gonflent pas les faux positifs ». **C'est faux.** Regarder n fois et
            // s'arreter au premier franchissement est de l'optional stopping : chaque
            // regard est une occasion supplementaire de franchir le seuil par hasard, et
            // le taux de faux positifs reel depasse le 5 % annonce, meme si l'on ne
            // s'arrete que sur les succes. C'est precisement parce qu'on ne s'arrete que
            // sur les succes que le biais existe.
            //
            // Un arret sequentiel correct existe (alpha-spending, confidence sequences) ;
            // il demande de depenser le risque au fil des regards, pas de faire semblant
            // qu'il n'y en a pas. Tant que ce n'est pas implemente et calibre, le
            // protocole va au bout : une decision, sur l'experience complete.
            //
            // La seule interruption permise est physique : une bascule d'etat qui echoue
            // (`Interrompu`, plus haut). Elle ne depend pas du resultat mesure, donc elle
            // ne biaise pas le verdict.
        }
        return (passes, interrompu);
    }

    /// <summary>Une passe. Une passe qui leve ou qui ne rend rien n'est pas une mesure basse :
    /// elle vaut zero, et le modele apparie l'ecarte au lieu de la croire.</summary>
    private static GameMetrics MesureJeu(Func<GameMetrics?> mesurer)
    {
        try { return mesurer() ?? new GameMetrics(0, 0); }
        catch (OperationCanceledException) { throw; }   // une annulation n'est pas une passe basse
        catch { return new GameMetrics(0, 0); }
    }

    /// <summary>Une preuve par grandeur, toutes issues des memes passes. C'est ce qui permet de
    /// dire « debit prouve, fluidite non resolue » au lieu d'un seul chiffre qui melange les
    /// deux.</summary>
    private static IReadOnlyList<MetricEvidence> Juger(
        IReadOnlyList<(bool B, GameMetrics M)> passes, double resolutionScene)
        => GameMetricSet.Toutes
            .Select(k => new MetricEvidence(k, PairedAb.Analyse(
                passes.Select(p => new AbPass(p.B, GameMetricSet.Valeur(p.M, k))).ToList(),
                resolutionScene).Game!.Preuve))
            // Une grandeur jamais mesuree (p95 absent d'une capture) ne doit pas peser : elle
            // rendrait un `Invalid` qui bloquerait toute la decision.
            .Where(m => passes.Any(p => GameMetricSet.Valeur(p.M, m.Metrique) > 0))
            .ToList();

    private static bool Annuler(IAutopilotCandidate candidat)
    {
        try { return candidat.Revert().IsOk; }
        catch { return false; }
    }
}
