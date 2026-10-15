using System.Globalization;

namespace DrDxt.Core;

/// <summary>
/// Quelle grandeur de queue a valide un keep : pur enregistrement, aucun pouvoir de decision.
///
/// Pourquoi ce champ existe. <see cref="KeepPolicy.DecideParMetrique"/> accepte le p95
/// frametime **seul** comme preuve de queue. Deux mesures se contredisent sur ce que ca vaut :
///
///   * Sur un reglage qui ajoute un a-coup toutes les 60 frames, le p95 rend
///     **+1,6 %** pendant que le 1 % low rend **−65,6 %**. Les deux grandeurs se separent donc
///     pour de bon sur un vrai defaut de fluidite : exiger le 1 % low n'aurait rien sauve ici,
///     la regression l'aurait de toute facon interdit ;
///   * Sur 9 tranches d'une capture inerte, moyenne et p95 partagent **95,5 %** de
///     leur variance. Un keep « moyenne prouvee + p95 prouve » n'y est pas loin d'un keep sur la
///     moyenne seule, ce que la regle existe precisement pour interdire.
///
/// Un cas de chaque cote ne tranche rien, et durcir la regle sur un seul cas serait exactement la
/// faute que ce depot traque. Ce champ nomme ce qui a valide, verdict par verdict, pour qu'on
/// puisse un jour trancher sur un corpus de cas reels au lieu de deux mesures isolees.
///
/// Il ne change aucun critere. Un gain garde ou rejete avant l'existence de ce champ l'est
/// exactement pareil apres : c'est ce que verrouille `ProvenanceDeQueueTests`.
/// </summary>
public enum PreuveDeQueue
{
    /// <summary>Aucune grandeur de queue n'est en gain prouve. C'est le cas de tout verdict qui
    /// ne garde pas : sans queue prouvee, un keep est impossible.</summary>
    Aucune,

    /// <summary>Le p95 frametime seul. **C'est le cas a compter** : celui dont on ne sait pas
    /// encore s'il merite de garder un reglage.</summary>
    P95Seul,

    /// <summary>Le 1 % low seul : la grandeur de queue la plus profonde, et la moins bien
    /// resolue (±9,2 % en rejeu contre ±1,2 % pour le p95).</summary>
    UnPourCentLowSeul,

    /// <summary>Les deux. Le cas ou la question ne se pose pas.</summary>
    LesDeux,
}

/// <summary>Ce que la politique decide, et la phrase qui va avec.</summary>
/// <param name="Keep">Conserver le reglage ?</param>
/// <param name="RaisonCle">Cle de message pour l'ecran, jamais une phrase assemblee ici.</param>
/// <param name="Detail">De quoi construire la phrase : les chiffres qui ont decide.</param>
/// <param name="Queue">ce qui a valide la queue pour ce verdict precis : voir
/// <see cref="PreuveDeQueue"/>. Optionnel et en queue : les decisions qui n'ont pas de notion de
/// grandeur (<see cref="KeepPolicy.Decide"/>, qui juge un score de jeu scalaire) le laissent a
/// <see cref="PreuveDeQueue.Aucune"/>, ce qui est vrai et pas un defaut.</param>
public sealed record KeepDecision(bool Keep, string RaisonCle, string Detail,
    PreuveDeQueue Queue = PreuveDeQueue.Aucune);

/// <summary>
/// La regle qui protege la promesse : un gain de sous-systeme n'est pas un gain de jeu.
///
/// Le premier autopilote a obtenu **+2,54 % de debit GPU synthetique, prouve a ±1,53 %** sur le
/// banc deterministe. C'est un vrai resultat, et pour un profil **gaming**, il ne suffit pas a
/// garder le reglage. Le banc mesure ce que la carte calcule ; le joueur ressent ce que le jeu
/// affiche. Entre les deux il y a le CPU, le moteur, le pilote et la scene.
///
/// ══ ce que chaque preuve a le droit de decider ══
/// <see cref="SubsystemEvidence"/> : resolution fine (0,122 % sur le banc GPU). Elle sert a :
///   * prouver qu'un levier fait reellement quelque chose ;
///   * ordonner les candidats ;
///   * detecter une regression materielle ;
///   * ne pas perdre vingt minutes sur un levier inerte.
///   Elle n'autorise **jamais** un keep permanent en profil gaming.
///
/// <see cref="GameEvidence"/> : resolution grossiere (~13 % en rejeu in-game). C'est la seule qui
/// decide si le reglage ameliore le jeu.
///
/// ══ les deux cas qui resument tout ══
///   debit GPU +2,6 % prouve · FPS +0,8 % pour ±3,5 %  →  non resolu  →  on n'garde pas
///   debit GPU +2,4 % prouve · score jeu +3,1 % pour ±0,7 %  →  keep
///
/// Le banc synthetique est un **pre-filtre haute precision**, pas le juge du profil gaming.
/// </summary>
public static class KeepPolicy
{
    /// <summary>Clés de message (resx). L'UI parle, le moteur nomme.</summary>
    public const string CleGainProuve = "autopilot.keep.game_gain";
    public const string CleRegression = "autopilot.revert.regression";
    public const string CleNonResolu = "autopilot.revert.unresolved";
    public const string CleInvalide = "autopilot.revert.invalid";
    public const string CleSousSystemeSeul = "autopilot.revert.subsystem_only";
    public const string CleAucuneMesure = "autopilot.revert.no_measurement";

    /// <summary>Cle : la fluidite n'a pas pu etre resolue, donc rien n'est garde en Competitive.</summary>
    public const string CleFluiditeNonResolue = "autopilot.revert.tail_unresolved";

    // Les nombres suivent la langue du docteur, jamais la culture du processus (un runner anglais rendait « 2.6 »).
    private static string Signe(double v) => v.ToString("+0.0;-0.0", Docteur.Langue);
    private static string Marge(double v) => v.ToString("0.0", Docteur.Langue);

    /// <summary>
    /// La decision par objectif, sur des preuves qui existent separement.
    ///
    /// La ponderation 0,35/0,65 est une preference du joueur. Ce n'est pas une permission de
    /// melanger une grandeur bien resolue avec une grandeur inconnue et d'appeler le resultat une
    /// information. Mesure sur une session Warzone du 07/09/2026 : le score composite annoncait +/-72 %
    /// quand la moyenne seule resolvait a +/-4,6 % ; le 1 % low ruinait la mesure des deux.
    ///
    /// ══ ce que `Competitive` exige ══
    ///   * aucune regression prouvee, sur aucune grandeur : un 1 % low qui s'effondre interdit le
    ///     keep meme si la moyenne monte ;
    ///   * au moins une grandeur de queue (1 % low ou p95 frametime) en gain prouve. Un gain de
    ///     moyenne avec une fluidite non resolue n'est pas un profil Competitive ameliore : c'est
    ///     un debit ameliore, et ca se dit comme ca.
    ///
    /// Le revert ne jette pas la preuve. « débit moyen ameliore ; fluidite non prouvee » reste
    /// dans le detail : c'est exactement ce qu'il faut pour qu'un futur objectif `MaxFPS`, lui,
    /// puisse s'en contenter.
    /// </summary>
    public static KeepDecision DecideParMetrique(
        IReadOnlyList<MetricEvidence> preuves, OptimizationGoal but)
    {
        if (preuves is null || preuves.Count == 0)
            return new KeepDecision(false, CleAucuneMesure, Docteur.T("KP.AucuneMesure"));

        string Dire(MetricEvidence m) =>
            $"{m.Libelle} {Signe(m.Preuve.DeltaPct)} % (±{Marge(m.Preuve.ResolutionPct)} %)";

        // Une experience qui n'a pas tenu ne dit rien, surtout pas « pas de changement ».
        var invalide = preuves.FirstOrDefault(m => m.Preuve.Outcome == EvidenceOutcome.Invalid);
        if (invalide is not null)
            return new KeepDecision(false, CleInvalide,
                invalide.Preuve.InvalidReason ?? Docteur.T("KP.Invalide"));

        var regression = preuves.FirstOrDefault(m => m.Preuve.Outcome == EvidenceOutcome.ProvenRegression);
        if (regression is not null)
            return new KeepDecision(false, CleRegression,
                Docteur.T("KP.Regression", Dire(regression)));

        var queue = preuves.Where(m =>
            m.Metrique is GameMetricKind.UnPourCentLow or GameMetricKind.P95Frametime).ToList();
        var queueProuvee = queue.FirstOrDefault(m => m.Preuve.Outcome == EvidenceOutcome.ProvenGain);
        var moyenne = preuves.FirstOrDefault(m => m.Metrique == GameMetricKind.Moyenne);

        if (queueProuvee is not null)
        {
            // Un keep nomme toujours ce qu'il n'a pas resolu.
            //
            // Le p95 frametime n'est pas « la fluidite » : il decrit les 5 % de frames les plus
            // lentes, pas le pire centile. Mesure du 08/09/2026 : sur un reglage qui ajoute un
            // gros a-coup toutes les 60 frames, le p95 rend +1,6 % pendant que le 1 % low rend
            // -65,6 %. Un keep qui annoncerait « fluidite prouvee » sur le seul p95 vendrait une
            // partie de la queue pour la totalite.
            //
            // La regression est deja interdite plus haut ; ce qui reste a dire, c'est ce qui est
            // reste inconnu. Le joueur a le droit de savoir sur quoi le produit se tait.
            var prouve = preuves.Where(m => m.Preuve.Outcome == EvidenceOutcome.ProvenGain).Select(Dire);
            var nonResolu = preuves
                .Where(m => m.Preuve.Outcome == EvidenceOutcome.NoResolvedChange)
                .Select(m => m.Libelle)
                .ToList();

            return new KeepDecision(true, CleGainProuve,
                string.Join(" · ", prouve)
                + (nonResolu.Count > 0 ? Docteur.T("KP.NonResolu", string.Join(", ", nonResolu)) : string.Empty),
                // Pur enregistrement : le keep vient d'etre decide, cette ligne le decrit.
                QueueQuiAValide(preuves));
        }

        // Le cas que le brief nomme explicitement : la moyenne monte, la fluidite reste
        // inconnue. En Competitive ce n'est pas un profil ameliore, et la preuve est conservee.
        if (moyenne?.Preuve.Outcome == EvidenceOutcome.ProvenGain)
            return new KeepDecision(false, CleFluiditeNonResolue,
                Docteur.T("KP.DebitSeul", Dire(moyenne), string.Join(", ", queue.Select(Dire))));

        return new KeepDecision(false, CleNonResolu,
            Docteur.T("KP.SousLeBruit", string.Join(" · ", preuves.Select(Dire))));
    }

    /// <summary>
    /// Quelles grandeurs de queue sont en gain prouve dans ces preuves.
    ///
    /// Publique et partagee expres. `DecideParMetrique` s'en sert pour etiqueter son verdict, et
    /// un recolteur de corpus s'en sert sur `CandidateResult.Metriques`, qui porte deja la liste
    /// complete. Deux lectures qui ne peuvent pas diverger, parce qu'il n'y en a qu'une : une
    /// seconde definition de « ce qui compte comme queue », ecrite ailleurs, est exactement la
    /// classe de derive silencieuse que ce fichier existe pour eviter.
    ///
    /// Elle ne decide rien. Elle lit.
    /// </summary>
    public static PreuveDeQueue QueueQuiAValide(IReadOnlyList<MetricEvidence>? preuves)
    {
        bool Prouvee(GameMetricKind k) => preuves?.Any(
            m => m.Metrique == k && m.Preuve.Outcome == EvidenceOutcome.ProvenGain) == true;

        return (Prouvee(GameMetricKind.P95Frametime), Prouvee(GameMetricKind.UnPourCentLow)) switch
        {
            (true, true) => PreuveDeQueue.LesDeux,
            (true, false) => PreuveDeQueue.P95Seul,
            (false, true) => PreuveDeQueue.UnPourCentLowSeul,
            _ => PreuveDeQueue.Aucune,
        };
    }

    /// <summary>Faut-il conserver ce reglage pour cet objectif ?</summary>
    public static KeepDecision Decide(PerformanceEvidence preuve, OptimizationGoal but)
    {
        var jeu = preuve?.Game?.Preuve;
        var sousSysteme = preuve?.Subsystem?.Preuve;

        // Aucune mesure en jeu : le sous-systeme a beau briller, rien ne dit que le joueur y gagne.
        if (jeu is null)
            return new KeepDecision(false,
                sousSysteme?.Outcome == EvidenceOutcome.ProvenGain ? CleSousSystemeSeul : CleAucuneMesure,
                sousSysteme is { } s && double.IsFinite(s.DeltaPct)
                    ? Docteur.T("KP.SousSystemeSansJeu", Signe(s.DeltaPct))
                    : Docteur.T("KP.AucuneMesure"));

        var j = jeu.Value;

        return j.Outcome switch
        {
            // Le seul chemin vers un keep en profil gaming.
            EvidenceOutcome.ProvenGain => new KeepDecision(true, CleGainProuve,
                Docteur.T("KP.JeuGain", Signe(j.DeltaPct), Marge(j.ResolutionPct))),

            EvidenceOutcome.ProvenRegression => new KeepDecision(false, CleRegression,
                Docteur.T("KP.JeuRegression", Signe(j.DeltaPct), Marge(j.ResolutionPct))),

            EvidenceOutcome.Invalid => new KeepDecision(false, CleInvalide,
                j.InvalidReason ?? Docteur.T("KP.Invalide")),

            // Le cas le plus frequent, et celui qui distingue un autopilote honnete d'un placebo.
            // On nomme le gain sous-systeme quand il existe : « ca fait quelque chose, mais pas
            // quelque chose que tu ressentiras » est une reponse utile, pas un echec.
            _ => new KeepDecision(false,
                sousSysteme?.Outcome == EvidenceOutcome.ProvenGain ? CleSousSystemeSeul : CleNonResolu,
                sousSysteme is { } sp && double.IsFinite(sp.DeltaPct)
                    ? Docteur.T("KP.SousSystemeJeuSousResolution", Signe(sp.DeltaPct), Signe(j.DeltaPct), Marge(j.ResolutionPct))
                    : Docteur.T("KP.JeuSousResolution", Signe(j.DeltaPct), Marge(j.ResolutionPct))),
        };
    }
}
