using DrDxt.Measurement.Cpu;

namespace DrDxt.Tools.CpuBench;

/// <summary>Ce qu'une courbe de charge autorise a conclure.</summary>
public enum CpuBudgetIssue
{
    /// <summary>Le debit par thread BAISSE quand on charge des cœurs, au-dela du bruit : un
    /// budget (puissance ou thermique) bride cette machine. La courbe ne dit PAS lequel des
    /// deux — la puissance du paquet et la temperature cœur exigent le MSR.
    ///
    /// **Une BAISSE, et seulement une baisse.** Une premiere version testait la valeur ABSOLUE
    /// de l'ecart : une hausse de 10 % du debit par thread rendait donc « budget limitant etabli ».
    /// L'outil d'origine ne le laissait pas voir — il n'imprimait la phrase du budget que sous
    /// condition de signe — et l'extraction a perdu cette distinction en chemin.</summary>
    BudgetEtabli,

    /// <summary>L'ecart mesure ne depasse pas le bruit. C'est une BORNE, pas une absence : voir
    /// <see cref="CpuBudgetVerdict.SeuilPercent"/> pour ce qui resterait invisible.</summary>
    AucunEffetAuDessusDuSeuil,

    /// <summary>Aucune comparaison valide n'existe. Les raisons sont nommees une par une.</summary>
    Indetermine,

    /// <summary>Les sommes de controle divergent a nombre de threads egal : la charge n'a pas fait
    /// le meme travail, comparer des debits n'a plus de sens.</summary>
    ChargesIncomparables,
}

/// <summary>Le verdict, et de quoi le contester.</summary>
/// <param name="Issue">Ce qui peut etre conclu.</param>
/// <param name="Empechements">Ce qui interdit de conclure. Vide sauf en <see cref="CpuBudgetIssue.Indetermine"/>.</param>
/// <param name="ChutePercent">Variation du debit par thread entre le premier point et le plus
/// grand point de MEME classe de cœurs.</param>
/// <param name="DerivePercent">Ecart entre le temoin initial et le temoin de retour.</param>
/// <param name="BruitPercent">Le plus grand des deux : derive et dispersion maximale.</param>
/// <param name="SeuilPercent">Trois fois le bruit. En dessous, rien n'est distinguable.</param>
/// <param name="ThreadsCompares">Les deux nombres de cœurs effectivement compares.</param>
/// <param name="PointMixte">Le point qui deborde sur une autre classe de cœurs, s'il existe : il
/// s'affiche mais ne sert JAMAIS au verdict.</param>
public sealed record CpuBudgetVerdict(
    CpuBudgetIssue Issue,
    IReadOnlyList<string> Empechements,
    double ChutePercent,
    double DerivePercent,
    double BruitPercent,
    double SeuilPercent,
    (int De, int A) ThreadsCompares,
    CpuBenchResult? PointMixte);

/// <summary>
/// LE VERDICT D'UNE COURBE DE CHARGE — et les QUATRE façons dont il a deja menti.
///
/// ══ POURQUOI CE TYPE EXISTE PLUTOT QU'UN BLOC DANS UN OUTIL ══
/// Cette logique vivait dans `tools/cpubench`. Elle y etait invisible du produit, et surtout ses
/// gardes n'etaient prouvees que par des mutations faites a la main : un raisonnement qu'on ne
/// peut pas tester est un raisonnement qu'on repayera.
///
/// ══ LES TROIS MENSONGES DEJA PAYES, 09/09/2026 ══
///   1. Deux issues seulement : au-dessus du bruit « ca bride », en dessous « rien ne bride ».
///      Une chute de 20 % avec 10 % de derive passait donc pour « le debit ne bouge pas ». Une
///      mesure trop bruitee devenait un constat rassurant.
///   2. Le verdict portait sur le plus grand nombre de threads, donc sur un point qui DEBORDE sur
///      les cœurs lents : le debit y chute pour deux raisons melangees, le budget cherche et
///      l'heterogeneite des cœurs. Mesure : 1 -> 12 threads donnait -41,0 %, 1 -> 8 (meme classe)
///      -10,3 %. Quadrupler l'effet reel.
///   3. Trois chemins menaient encore a conclure sans comparaison valide : topologie non lue,
///      epinglage incomplet, un seul nombre de cœurs exploitable. Les corriger un par un aurait
///      laisse le quatrieme ouvert.
///   4. La valeur ABSOLUE de l'ecart decidait : une HAUSSE de 10 % du debit par thread rendait
///      donc « budget limitant etabli ». Le bloc d'origine ne le laissait pas voir parce qu'il
///      n'imprimait la phrase du budget que sous condition de signe — l'extraction vers ce type a
///      perdu la distinction, et c'est exactement ce qu'une extraction fait de pire : deplacer un
///      raisonnement en laissant derriere elle la condition qui le rendait juste.
///
/// La regle qui en sort tient en une phrase : **le verdict ne s'execute que si les conditions de sa
/// validite tiennent, et chaque manquement se nomme au lieu de se deviner.**
/// </summary>
public static class CpuBudgetJuge
{
    /// <summary>Au-dela de ce seuil de bruit, le banc ne sait meme plus exclure un gros effet, et
    /// « aucun effet » cesse d'etre une information.</summary>
    private const double BruitUtileMax = 5.0;

    /// <summary>Combien de fois le bruit un ecart doit valoir pour etre distingue de lui.</summary>
    private const int FacteurSeuil = 3;

    /// <param name="mesures">Les points de la courbe, dans l'ordre. Le dernier REPETE le premier :
    /// c'est le temoin de retour, et l'ecart entre les deux est ce que la machine a derive pendant
    /// la manip.</param>
    /// <param name="coeursDeLaClasseRapide">Nombre de cœurs physiques de la classe la plus rapide,
    /// ou <c>0</c> si la topologie n'a pas ete lue. Zero n'autorise AUCUN verdict : sans elle on
    /// ignore quels cœurs ont ete charges, donc « meme classe » ne veut rien dire.</param>
    public static CpuBudgetVerdict Juger(IReadOnlyList<CpuBenchResult> mesures, int coeursDeLaClasseRapide)
    {
        if (mesures is null || mesures.Count == 0)
            return Indetermine(["aucune mesure"], 0, 0, (0, 0), null);

        var premier = mesures[0];
        var temoin = mesures[^1];
        var derive = premier.MopsParThread > 0
            ? Math.Abs(temoin.MopsParThread - premier.MopsParThread) / premier.MopsParThread * 100
            : 0;
        var bruit = Math.Max(derive, mesures.Max(m => m.EcartTypePourcent));
        var seuil = FacteurSeuil * bruit;

        // A nombre de threads egal, la somme de controle DOIT etre identique : sinon la charge n'a
        // pas fait le meme travail, et aucun debit n'est comparable a un autre.
        if (mesures.GroupBy(m => m.Threads).Any(g => g.Select(x => x.Somme).Distinct().Count() > 1))
            return new CpuBudgetVerdict(CpuBudgetIssue.ChargesIncomparables, [], 0, derive, bruit,
                seuil, (premier.Threads, premier.Threads), null);

        var empechements = new List<string>();

        if (coeursDeLaClasseRapide == 0)
            empechements.Add("la topologie n'a pas ete lue : on ignore quels cœurs ont ete charges, "
                             + "donc « meme classe » ne veut rien dire");

        // Un epinglage rate veut dire que l'ordonnanceur a place le thread ou il voulait : le debit
        // mesure n'est plus celui du cœur qu'on croit charger.
        var rates = mesures.Where(m => m.EpinglesReussis != m.Threads).ToList();
        if (rates.Count > 0)
            empechements.Add($"{rates.Count} mesure(s) avec un epinglage incomplet "
                             + $"({string.Join(", ", rates.Select(m => $"{m.EpinglesReussis}/{m.Threads}"))}) : "
                             + "les cœurs reellement charges ne sont pas ceux annonces");

        var memeClasse = coeursDeLaClasseRapide > 0
            ? mesures.Where(m => m.Threads <= coeursDeLaClasseRapide).ToList()
            : mesures;

        // Avec un seul nombre de threads exploitable, le point « plein » serait la mesure de depart
        // elle-meme : la chute vaudrait zero PAR CONSTRUCTION, et « aucun effet » serait une
        // tautologie qu'aucune machine ne pourrait dementir.
        if (memeClasse.Select(m => m.Threads).Distinct().Count() < 2)
            empechements.Add("un seul nombre de cœurs de la classe rapide a pu etre mesure : "
                             + "il n'y a pas deux points a comparer");

        var mixte = coeursDeLaClasseRapide > 0
            ? mesures.Where(m => m.Threads > coeursDeLaClasseRapide).OrderByDescending(m => m.Threads).FirstOrDefault()
            : null;

        if (empechements.Count > 0)
            return Indetermine(empechements, derive, bruit, (premier.Threads, premier.Threads), mixte);

        var plein = memeClasse.OrderByDescending(m => m.Threads).First();
        var chute = premier.MopsParThread > 0
            ? (plein.MopsParThread - premier.MopsParThread) / premier.MopsParThread * 100
            : 0;
        var compares = (premier.Threads, plein.Threads);

        // UNE HAUSSE N'EST PAS UNE BONNE NOUVELLE, C'EST UNE MESURE SUSPECTE. Le debit PAR THREAD
        // ne peut pas monter parce qu'on charge des cœurs de plus : au mieux il reste egal. Un
        // ecart positif au-dela du bruit accuse donc le protocole — un premier point pris avant que
        // la machine se stabilise, une derive, un ordonnancement qui a bouge — et surement pas une
        // machine qui va bien. Le dire « indetermine » avec la raison est la seule lecture honnete :
        // le ranger dans « aucun effet » le cacherait, et dans « budget » l'inverserait.
        if (chute > seuil)
            return new CpuBudgetVerdict(CpuBudgetIssue.Indetermine,
                [$"le debit par thread MONTE de {chute:0.0} % quand on charge des cœurs, au-dela du "
                 + $"bruit ({seuil:0.0} %) : cela ne devrait pas arriver, et accuse la mesure plutot "
                 + "que la machine"],
                chute, derive, bruit, seuil, compares, mixte);

        if (-chute > seuil)
            return new CpuBudgetVerdict(CpuBudgetIssue.BudgetEtabli, [], chute, derive, bruit, seuil,
                compares, mixte);

        // L'ecart ne depasse pas le bruit — mais si le bruit lui-meme est enorme, « aucun effet »
        // ne dit rien : le banc ne sait plus exclure un effet qui compterait.
        if (seuil > BruitUtileMax)
            return new CpuBudgetVerdict(CpuBudgetIssue.Indetermine,
                [$"le bruit de cette execution ({bruit:0.00} %) ne permet d'exclure qu'un effet "
                 + $"superieur a {seuil:0.0} %, et l'ecart mesure ({chute:0.0} %) est en dessous"],
                chute, derive, bruit, seuil, compares, mixte);

        return new CpuBudgetVerdict(CpuBudgetIssue.AucunEffetAuDessusDuSeuil, [], chute, derive,
            bruit, seuil, compares, mixte);
    }

    private static CpuBudgetVerdict Indetermine(IReadOnlyList<string> raisons, double derive,
        double bruit, (int, int) compares, CpuBenchResult? mixte)
        => new(CpuBudgetIssue.Indetermine, raisons, 0, derive, bruit, FacteurSeuil * bruit, compares, mixte);
}
