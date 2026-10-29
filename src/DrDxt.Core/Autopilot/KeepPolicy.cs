using System.Globalization;

namespace DrDxt.Core;

/// <summary>
/// Quelle grandeur de queue a valide un keep : pur enregistrement, aucun pouvoir de decision.
/// <see cref="KeepPolicy.DecideParMetrique"/> accepte le p95 seul comme preuve de queue, et deux mesures se
/// contredisent sur ce que ca vaut : un a-coup toutes les 60 frames rend p95 +1,6 % et 1 % low −65,6 % (la regression
/// l'interdit de toute facon) ; sur 9 tranches d'une capture inerte, moyenne et p95 partagent 95,5 % de leur variance.
/// Un cas de chaque cote ne tranche rien : ce champ nomme ce qui a valide, pour trancher un jour sur un corpus reel.
/// Il ne change aucun critere (`ProvenanceDeQueueTests`).
/// </summary>
public enum PreuveDeQueue
{
    /// <summary>Aucune grandeur de queue en gain prouve : tout verdict qui ne garde pas.</summary>
    Aucune,

    /// <summary>Le p95 frametime seul : le cas a compter, dont on ne sait pas s'il merite de garder un reglage.</summary>
    P95Seul,

    /// <summary>Le 1 % low seul : la queue la plus profonde, la moins bien resolue (±9,2 % en rejeu, ±1,2 % pour le
    /// p95).</summary>
    UnPourCentLowSeul,

    /// <summary>Les deux. Le cas ou la question ne se pose pas.</summary>
    LesDeux,
}

/// <summary>Ce que la politique decide, et la phrase qui va avec.</summary>
/// <param name="Keep">Conserver le reglage ?</param>
/// <param name="RaisonCle">Cle de message pour l'ecran, jamais une phrase assemblee ici.</param>
/// <param name="Detail">De quoi construire la phrase : les chiffres qui ont decide.</param>
/// <param name="Queue">Ce qui a valide la queue pour ce verdict (<see cref="PreuveDeQueue"/>).
/// <see cref="KeepPolicy.Decide"/>, qui juge un score scalaire, le laisse a <see cref="PreuveDeQueue.Aucune"/>.</param>
public sealed record KeepDecision(bool Keep, string RaisonCle, string Detail,
    PreuveDeQueue Queue = PreuveDeQueue.Aucune);

/// <summary>
/// Un gain de sous-systeme n'est pas un gain de jeu : +2,54 % de debit GPU prouve a ±1,53 % au banc ne suffit pas a
/// garder un reglage en profil gaming ; entre la carte et l'ecran il y a le CPU, le moteur, le pilote et la scene.
/// <see cref="SubsystemEvidence"/> (0,122 % au banc GPU) prouve qu'un levier agit, ordonne les candidats, detecte une
/// regression materielle, ecarte un levier inerte : jamais un keep permanent en gaming. Seule
/// <see cref="GameEvidence"/> (~13 % en rejeu in-game) decide. GPU +2,6 % · FPS +0,8 % ± 3,5 % → non resolu, pas garde ;
/// GPU +2,4 % · score jeu +3,1 % ± 0,7 % → keep.
/// </summary>
public static class KeepPolicy
{
    /// <summary>La marge qu'une seule grandeur de queue doit dépasser pour un keep : Bonferroni sur deux essais, z 2,24 contre
    /// 1,96 (×1,14), porté à ×1,25 pour couvrir t de Student à cinq degrés de liberté (3,16 contre 2,57, ×1,23).</summary>
    public const double FacteurDeBonferroni = 1.25;

    // Codes stables de la décision, rendus dans CandidateResult.RaisonCle : des identifiants, pas des phrases.
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
    /// La decision par objectif, sur des preuves separees : melanger une grandeur resolue et une inconnue n'est pas une
    /// information (Warzone, 07/09/2026 : composite a ±72 %, moyenne seule a ±4,6 %, le 1 % low ruinait les deux).
    /// `Competitive` exige aucune regression prouvee sur aucune grandeur, et au moins une grandeur de queue (1 % low ou
    /// p95) en gain prouve : une moyenne seule est un debit ameliore, et se dit comme ca. Le revert garde la preuve dans
    /// le detail, pour un futur objectif `MaxFPS`.
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
        // Deux grandeurs de queue, deux chances qu'un levier inerte passe pour un gain : à 95 % chacune, environ 5 % de faux
        // keep au lieu de 2,5 %. Seule prouvée, une grandeur doit dépasser sa marge d'un facteur de Bonferroni (deux essais,
        // arrondi vers le haut pour les petits échantillons de Student). Prouvées toutes deux, elles se confirment.
        var gains = queue.Where(m => m.Preuve.Outcome == EvidenceOutcome.ProvenGain).ToList();
        var queueProuvee = gains.Count >= 2 ? gains[0]
            : gains.FirstOrDefault(m => Math.Abs(m.Preuve.DeltaPct) > FacteurDeBonferroni * m.Preuve.ResolutionPct);
        var moyenne = preuves.FirstOrDefault(m => m.Metrique == GameMetricKind.Moyenne);

        if (queueProuvee is not null)
        {
            // Un keep nomme toujours ce qu'il n'a pas resolu. Le p95 decrit les 5 % de frames les plus lentes, pas le pire
            // centile (08/09/2026 : un a-coup toutes les 60 frames rend p95 +1,6 %, 1 % low -65,6 %) : « fluidite
            // prouvee » sur le seul p95 vendrait une partie de la queue pour la totalite.
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

        // La moyenne monte, la fluidite reste inconnue : pas un profil Competitive ameliore, la preuve est conservee.
        if (moyenne?.Preuve.Outcome == EvidenceOutcome.ProvenGain)
            return new KeepDecision(false, CleFluiditeNonResolue,
                Docteur.T("KP.DebitSeul", Dire(moyenne), string.Join(", ", queue.Select(Dire))));

        return new KeepDecision(false, CleNonResolu,
            Docteur.T("KP.SousLeBruit", string.Join(" · ", preuves.Select(Dire))));
    }

    /// <summary>
    /// Quelles grandeurs de queue sont en gain prouve. Publique expres : une lecture du corpus
    /// (`CandidateResult.Metriques`) doit passer par elle plutot que redefinir « ce qui compte comme queue ». Elle ne
    /// decide rien.
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

            // Le cas le plus frequent : on nomme le gain sous-systeme quand il existe, « ca fait quelque chose, mais pas
            // quelque chose que tu ressentiras » est une reponse utile.
            _ => new KeepDecision(false,
                sousSysteme?.Outcome == EvidenceOutcome.ProvenGain ? CleSousSystemeSeul : CleNonResolu,
                sousSysteme is { } sp && double.IsFinite(sp.DeltaPct)
                    ? Docteur.T("KP.SousSystemeJeuSousResolution", Signe(sp.DeltaPct), Signe(j.DeltaPct), Marge(j.ResolutionPct))
                    : Docteur.T("KP.JeuSousResolution", Signe(j.DeltaPct), Marge(j.ResolutionPct))),
        };
    }
}
