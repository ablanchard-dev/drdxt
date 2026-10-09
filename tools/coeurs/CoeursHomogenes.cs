namespace DrDxt.Tools.Coeurs;

/// <summary>Ce qu'un cœur physique a rendu, seul.</summary>
/// <param name="ProcesseurLogique">Le processeur logique charge pour ce cœur.</param>
/// <param name="MopsParThread">Son debit.</param>
/// <param name="DispersionPercent">La dispersion de SES passes — son bruit a lui.</param>
public sealed record DebitParCoeur(int ProcesseurLogique, double MopsParThread, double DispersionPercent);

/// <summary>Ce que la comparaison des cœurs autorise a dire.</summary>
/// <param name="MedianeMops">Le debit median, la reference.</param>
/// <param name="EcartLePlusBasPercent">De combien le cœur le plus lent est sous la mediane.</param>
/// <param name="CoeurLePlusLent">Son processeur logique, ou <c>-1</c>.</param>
/// <param name="CoeurLePlusRapide">Celui qui rend le plus, ou <c>-1</c>.</param>
/// <param name="BruitPercent">La dispersion MEDIANE : le plancher de la comparaison.</param>
/// <param name="PireDispersionPercent">La plus grande dispersion d'un seul cœur. Quand elle
/// depasse largement la mediane, ce cœur a mesure bruyamment et son debit ne vaut pas grand-chose
/// — mais il ne doit pas relever le seuil des autres.</param>
/// <param name="MeriteUnRegard">Vrai quand l'ecart depasse trois fois le bruit. Ce n'est PAS
/// « ce cœur est degrade » — voir la classe.</param>
/// <param name="Lisible">Ce qui peut etre dit, en une phrase.</param>
public sealed record EcartEntreCoeurs(
    double MedianeMops,
    double EcartLePlusBasPercent,
    int CoeurLePlusLent,
    int CoeurLePlusRapide,
    double BruitPercent,
    double PireDispersionPercent,
    bool MeriteUnRegard,
    string Lisible);

/// <summary>
/// TOUS LES CŒURS RENDENT-ILS LA MEME CHOSE ? — et pourquoi la reponse n'est jamais un diagnostic.
///
/// ══ CE QUE CETTE COMPARAISON PEUT TROUVER ══
/// Un cœur qui rend nettement moins que ses voisins, alors qu'il execute la MEME charge
/// deterministe, seul, epingle. C'est le seul moyen dont ce depot dispose pour approcher la
/// question « une piece est-elle en train de lacher », sans MSR, sans temperature cœur lisible et
/// sans pilote en anneau 0.
///
/// ══ CE QU'ELLE NE PEUT PAS CONCLURE, ET C'EST LA MOITIE DU TYPE ══
/// **Une dispersion entre cœurs est ATTENDUE sur ce materiel.** Intel Turbo Boost Max 3.0 designe
/// un ou deux cœurs FAVORIS, capables de monter plus haut que les autres — par construction, pas
/// par accident. Un ecart entre le plus rapide et le plus lent est donc le comportement NORMAL
/// d'un processeur sain, et l'appeler « degradation » accuserait le silicium d'un defaut que le
/// constructeur a mis la expres.
///
/// Ce type rend donc `MeriteUnRegard`, jamais `EstDegrade`. Il dit « ce cœur se distingue
/// au-dessus du bruit », il ne dit pas pourquoi. Le pourquoi demanderait de savoir QUELS cœurs
/// sont les favoris de cette puce — une information que ce depot ne lit pas.
///
/// Et le seuil de trois fois le bruit est **emprunte**, comme partout ailleurs ici : c'est le
/// facteur deja utilise par le juge de budget CPU. Aucune mesure ne dit que trois soit le bon
/// nombre pour comparer des cœurs entre eux.
///
/// ══ POURQUOI LA MEDIANE ET NON LE MAXIMUM ══
/// La premiere version prenait la PLUS GRANDE dispersion comme plancher. Mesure du 09/09/2026 sur
/// huit cœurs : sept mesuraient sous 1,3 % et **un seul a 5,86 %**. Le seuil montait donc a 17,6 %
/// et **plus rien ne pouvait se distinguer** — un cœur a 6,6 % sous la mediane passait inapercu.
/// Un seul cœur bruyant suffisait a rendre la garde aveugle pour tous les autres.
/// La mediane est le choix que ce depot fait deja ailleurs pour la meme raison, et il est ecrit
/// dans `BenchmarkRunner` : « une moyenne de ratios se fait tirer par quelques frames aberrantes ».
/// La pire dispersion sort a cote, parce qu'un cœur qui mesure mal est une information — pas un
/// motif pour aveugler la comparaison.
///
/// ══ LE PROCESSEUR 0 N'EST PAS UN CŒUR COMME LES AUTRES ══
/// Windows y dirige par defaut une part des interruptions et des DPC. Il rend donc moins qu'un
/// autre sur la meme charge **sans etre abime**, et c'est precisement lui que la mesure du
/// 09/09 a trouve en dessous. Le nommer sans dire ca enverrait quelqu'un soupconner son
/// processeur pour un comportement voulu par le systeme.
/// </summary>
public static class CoeursHomogenes
{
    private const int FacteurSeuil = 3;

    public static EcartEntreCoeurs Analyser(IReadOnlyList<DebitParCoeur>? mesures)
    {
        var valides = (mesures ?? []).Where(m => m.MopsParThread > 0).ToList();
        if (valides.Count < 2)
            return new EcartEntreCoeurs(0, 0, -1, -1, 0, 0, false,
                "moins de deux cœurs mesures : il n'y a rien a comparer");

        var tries = valides.OrderBy(m => m.MopsParThread).ToList();
        var mediane = tries[tries.Count / 2].MopsParThread;
        var plusLent = tries[0];
        var plusRapide = tries[^1];
        var dispersions = valides.Select(m => m.DispersionPercent).OrderBy(d => d).ToList();
        var bruit = dispersions[dispersions.Count / 2];
        var pire = dispersions[^1];

        var ecart = mediane > 0 ? (mediane - plusLent.MopsParThread) / mediane * 100 : 0;
        var seuil = FacteurSeuil * bruit;
        var merite = ecart > seuil;

        var noteCpu0 = plusLent.ProcesseurLogique == 0
            ? " Et c'est le processeur 0 : Windows y dirige une part des interruptions et des "
              + "DPC, donc il rend moins sans etre abime."
            : string.Empty;

        return new EcartEntreCoeurs(mediane, ecart, plusLent.ProcesseurLogique,
            plusRapide.ProcesseurLogique, bruit, pire, merite,
            merite
                ? $"le processeur {plusLent.ProcesseurLogique} rend {ecart:0.0} % de moins que la "
                  + $"mediane, au-dela du bruit ({seuil:0.0} %). Une dispersion entre cœurs est "
                  + "ATTENDUE — Turbo Boost Max designe des cœurs favoris — donc ceci merite un "
                  + "regard, ce n'est pas un diagnostic de degradation." + noteCpu0
                : $"aucun cœur ne se distingue au-dessus du bruit : ecart le plus bas {ecart:0.0} %, "
                  + $"seuil {seuil:0.0} %");
    }
}
