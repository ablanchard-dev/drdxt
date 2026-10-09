using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>Un instant : ce que la machine ENVOYAIT (compteur de la carte, Mbit/s) et le ping
/// vers la reference internet au meme moment. <c>null</c> = pas de reponse.</summary>
public sealed record EchantillonEnvoi(double EnvoiMbps, double? PingMs);

/// <param name="Detecte">Le ping monte quand l'envoi sature, d'au moins <see cref="Bufferbloat.SeuilMs"/>.</param>
/// <param name="CapaciteMbps">Le plus fort envoi observe : une borne basse de la capacite reelle.</param>
/// <param name="PlafondConseilleBps">0 quand rien n'est detecte : pas de plafond sans preuve.</param>
public sealed record VerdictBufferbloat(
    bool Detecte,
    double CapaciteMbps,
    double? PingReposP90,
    double? PingChargeP90,
    int NRepos,
    int NCharge,
    long PlafondConseilleBps,
    string Raison);

/// <summary>
/// 24/09/2026 — le bufferbloat MONTANT, juge sur le trafic reel de la machine, sans rien envoyer.
///
/// Pourquoi passif : le README promet que rien ne quitte la machine hors mise a jour. Un test de
/// debit enverrait des centaines de Mo a un tiers. Ici on lit le compteur d'envoi de la carte
/// pendant que le joueur envoie deja (stream, sauvegarde cloud, partage d'ecran) : si l'envoi ne
/// sature jamais, il n'y a rien a corriger et le verdict le dit.
///
/// Mesure sur la machine de reference le 24/09 (upload sature ~720 Mbit/s, A/B alterne x3, 120
/// pings par cote) : repos p90 9 ms ; envoi libre p90 26 / p99 48 ; plafond 400 Mbit/s demande
/// (~540 obtenus) p90 14 / p99 18. Sources du seuil et de la methode : bufferspeed.com/learn/bufferbloat
/// (un jeu souffre des +20 a +30 ms), learn.microsoft.com/powershell/module/netqos/new-netqospolicy.
/// </summary>
public static class Bufferbloat
{
    /// <summary>Hausse du p90 qui compte comme un defaut. Le bas de la fourchette « un jeu en
    /// souffre » (20-30 ms) moins une marge : le ping est quantifie a la milliseconde.</summary>
    public const double SeuilMs = 15;

    /// <summary>Echantillons minimaux de chaque cote. En dessous, un p90 est un tirage.</summary>
    public const int MinParCote = 20;

    // Un echantillon est « en charge » a 70 % du plus fort envoi vu, « au repos » sous 10 %.
    private const double FractionCharge = 0.7;
    private const double FractionRepos = 0.1;

    // Simplification voulue : plafond = 50 % du plus fort envoi vu. Calibre sur une machine (400 demandes sur
    // ~780 vus = p99 48 -> 18 ms) ; la consigne QoS est lache (200 demandes -> ~340 obtenus). A
    // remplacer par une boucle qui ajuste sur le ping mesure quand une 2e machine le demandera.
    public const double FractionPlafond = 0.5;

    public static VerdictBufferbloat Juger(IReadOnlyList<EchantillonEnvoi> echantillons)
    {
        var capacite = echantillons.Count == 0 ? 0 : echantillons.Max(e => e.EnvoiMbps);
        if (capacite <= 0)
            return Rien(0, null, null, 0, 0, "aucun envoi observé");

        var repos = Pings(echantillons, e => e.EnvoiMbps <= capacite * FractionRepos);
        var charge = Pings(echantillons, e => e.EnvoiMbps >= capacite * FractionCharge);
        double? p90Repos = repos.Count > 0 ? P90(repos) : null;
        double? p90Charge = charge.Count > 0 ? P90(charge) : null;

        if (repos.Count < MinParCote || charge.Count < MinParCote)
            return Rien(capacite, p90Repos, p90Charge, repos.Count, charge.Count,
                $"pas assez observé ({repos.Count} au repos, {charge.Count} en charge ; il en faut {MinParCote} de chaque)");

        var hausse = p90Charge!.Value - p90Repos!.Value;
        if (hausse < SeuilMs)
            return Rien(capacite, p90Repos, p90Charge, repos.Count, charge.Count,
                $"le ping monte de {hausse:0} ms quand l'envoi sature : sous le seuil de {SeuilMs:0} ms, rien à corriger");

        return new VerdictBufferbloat(true, capacite, p90Repos, p90Charge, repos.Count, charge.Count,
            (long)(capacite * FractionPlafond * 1_000_000),
            $"le ping monte de {hausse:0} ms quand l'envoi sature");
    }

    public const string Nom = "ping quand tu envoies";

    /// <summary>Le verdict, tel que la sante de la machine l'affiche. Un plafond n'est propose que
    /// sur detection : jamais sur « pas assez observe ».</summary>
    public static RepairFinding Constat(VerdictBufferbloat v, TimeSpan fenetre)
    {
        var secondes = Math.Round(fenetre.TotalSeconds);
        if (v.Detecte)
            return new RepairFinding(PertesInterface.Domaine, Nom,
                $"+{v.PingChargeP90 - v.PingReposP90:0} ms (p90 {v.PingReposP90:0} → {v.PingChargeP90:0} ms)",
                $"moins de +{SeuilMs:0} ms", RepairStatus.Suspicious,
                $"pendant {secondes} s de jeu, ton envoi a saturé à ~{v.CapaciteMbps:0} Mbit/s et le ping a monté : "
                + "c'est la file d'attente de ta box qui se remplit (bufferbloat). Plafonner l'envoi des autres "
                + $"programmes à ~{v.PlafondConseilleBps / 1_000_000} Mbit/s pendant le jeu l'évite. Sur la "
                // 25/09/2026 : la mesure venait d'un envoi saturé par un transfert, pings vers 1.1.1.1, hors
                // jeu (registre t1) ; la phrase la laissait lire comme une mesure en jeu.
                + "machine de référence, avec un envoi saturé par un transfert et hors jeu, les pics p99 vers "
                + "1.1.1.1 sont passés de 48 à 18 ms. Rien n'est changé sans ton accord. "
                // 04/10/2026 (plan 2.6) : le plafond ne règle que CE PC ; une console, un téléphone ou un téléchargement
                // ailleurs dans la maison remplissent la même file. Source : bufferbloattest.org, guide SQM.
                + "Ce plafond ne règle que ce PC : si d'autres appareils de la maison envoient en même temps, la vraie "
                + "correction est la gestion de file de ton routeur (souvent appelée SQM, QoS adaptative ou Smart Queue, "
                + "algorithmes CAKE ou fq_codel), réglée à environ 90 % du débit mesuré. Beaucoup de box d'opérateur ne la "
                + "proposent pas ; un routeur placé derrière la box le peut.",
                RepairAction.Recommandation);

        return new RepairFinding(PertesInterface.Domaine, Nom, null, null,
            v.NRepos >= MinParCote && v.NCharge >= MinParCote ? RepairStatus.ExpectedDefault : RepairStatus.Observed,
            $"fenêtre de {secondes} s : {v.Raison}");
    }

    private static VerdictBufferbloat Rien(double cap, double? r, double? c, int nr, int nc, string raison)
        => new(false, cap, r, c, nr, nc, 0, raison);

    private static List<double> Pings(IReadOnlyList<EchantillonEnvoi> e, Func<EchantillonEnvoi, bool> garde)
        => e.Where(garde).Where(x => x.PingMs is not null).Select(x => x.PingMs!.Value).ToList();

    private static double P90(List<double> v)
    {
        v.Sort();
        // 26/09/2026 : rang le plus proche, comme FrametimeStatistics.PercentileOrNull. L'indice
        // floor(0,9·n) prenait le 19e de 20, un p95.
        return v[Math.Max(0, (int)Math.Ceiling(v.Count * 0.9) - 1)];
    }
}
