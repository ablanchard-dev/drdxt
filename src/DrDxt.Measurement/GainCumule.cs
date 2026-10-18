using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>Un gain prouve par un instrument nomme.</summary>
/// <param name="Levier">Ce qui a produit ce gain.</param>
/// <param name="Instrument">L'instrument qui l'a mesure : « banc GPU », « banc CPU soutenu »,
/// « frametime en jeu ». C'est lui qui decide si deux gains sont additionnables.</param>
/// <param name="EcartPercent">L'ecart mesure, en pourcent. Peut etre negatif.</param>
/// <param name="BruitPercent">Le plancher de l'instrument pour cette mesure : dispersion, derive,
/// ou etendue des temoins. Un gain plus petit que lui n'est pas un gain.</param>
public sealed record GainProuve(string Levier, string Instrument, double EcartPercent, double BruitPercent);

/// <summary>Ce que l'addition de plusieurs gains autorise a dire.</summary>
/// <param name="TotalPercent">Le total compose, ou 0 si rien n'est additionnable.</param>
/// <param name="BruitPercent">Le bruit du total, compose lui aussi.</param>
/// <param name="Instrument">L'instrument commun aux gains additionnes.</param>
/// <param name="Retenus">Les leviers effectivement additionnes.</param>
/// <param name="Refus">Pourquoi les autres ne le sont pas. Vide quand tout a ete retenu.</param>
public sealed record TotalDeGains(
    double TotalPercent,
    double BruitPercent,
    string? Instrument,
    IReadOnlyList<string> Retenus,
    IReadOnlyList<string> Refus);

/// <summary>
/// Additionner des gains sans mentir, et refuser quand c'est impossible.
///
/// ══ pourquoi ce type existe ══
/// `Cumulative game gain` est la metrique que le mandat « PERFORMANCE first » designe comme juge du
/// produit. Sans ce type, rien dans ce depot ne garde un gain prouve : le moteur rend un verdict
/// par plan applique, jamais un par levier, et ne l'ecrit nulle part ; le seul gain prouve de la
/// machine de reference, +2,2 % au banc GPU, ne vit que dans un fichier de plan.
///
/// ══ les deux fautes que ce type rend impossibles ══
///
/// **1. Additionner des pourcentages.** Deux gains de 2 % et 3 % ne font pas 5 % : ils font
/// 1,02 x 1,03 - 1 = 5,06 %. L'ecart est petit ici et grossit vite ; sur cinq leviers a 10 %,
/// l'addition naive rend 50 % quand la composition rend 61 %. Un produit qui annonce le premier
/// chiffre se trompe contre lui-meme, ce qui ne le rend pas plus honnete.
///
/// **2. Melanger les instruments, et c'est la vraie.** Un gain de 2,2 % au banc GPU et un gain
/// de 10 % au banc CPU ne s'additionnent pas : ils ne mesurent pas la meme chose, et aucun des
/// deux ne dit ce qu'un jeu obtient. Les sommer produirait « +12,4 % de performance », un nombre
/// qui n'a aucun referent, exactement le genre d'annonce que ce depot reproche aux autres. Ce type
/// refuse, nomme le refus, et n'additionne que ce qui partage un instrument.
///
/// **3. Composer le gain et additionner le bruit.** Composer les ecarts (un produit) tout en
/// empilant les bruits en points de pourcentage (racine de la somme des carres) serait faux : les
/// deux ne vivent pas a la meme echelle. Le bruit d'un produit se propage en relatif, puis se
/// reporte a l'echelle du total. Sur cinq leviers a +10 % ± 1 %, la formule en points annonce
/// ± 2,24 % la ou la propagation correcte donne ± 3,27 % (verifie par Monte-Carlo, 400 000
/// tirages, qui rend 3,27 %). Elle sous-estime l'incertitude, donc surestime la confiance : la
/// direction dangereuse pour un depot dont la loi est « un ecart plus petit que le bruit n'est
/// pas un gain ».
///
/// Sur un seul gain les deux formules coincident exactement. Le seul chiffre publie de cette
/// machine, +2,2 % ± 0,122 % au banc GPU, ne bouge donc pas d'un millieme.
///
/// Et il ne transforme jamais un gain de banc en gain de jeu. Le banc GPU deterministe resout
/// 0,122 %, le jeu en ligne rend ±45 % sur le 1 % low : le second ne peut pas confirmer le premier,
/// et le premier ne peut pas predire le second.
/// </summary>
public static class GainCumule
{
    /// <summary>Compose les gains qui partagent l'instrument le plus represente, et dit pourquoi
    /// les autres sont ecartes.</summary>
    public static TotalDeGains Additionner(IReadOnlyList<GainProuve>? gains)
    {
        if (gains is null || gains.Count == 0)
            return new TotalDeGains(0, 0, null, [], [Docteur.T("GC.AucunGainProuve")]);

        // On additionne dans l'instrument le plus represente plutot que de choisir au hasard : le
        // total le plus etaye est celui qui a le plus de mesures derriere lui.
        var instrument = gains
            .GroupBy(g => g.Instrument, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;

        var refus = new List<string>();
        var retenus = new List<string>();
        var facteur = 1.0;
        var bruitRelatifCarre = 0.0;

        foreach (var g in gains)
        {
            if (!string.Equals(g.Instrument, instrument, StringComparison.OrdinalIgnoreCase))
            {
                refus.Add(Docteur.T("GC.Instrument", g.Levier, g.Instrument, instrument));
                continue;
            }

            // Un ecart plus petit que le bruit de sa propre mesure n'est pas un gain. Le retenir
            // ferait grossir un total avec du bruit, ce qui est la definition d'un faux gain.
            if (Math.Abs(g.EcartPercent) <= g.BruitPercent)
            {
                refus.Add(Docteur.T("GC.Bruit", g.Levier, g.EcartPercent, g.BruitPercent));
                continue;
            }

            var f = 1 + g.EcartPercent / 100.0;

            // Un levier qui coute 100 % ou plus annule le produit : le total ne serait plus
            // compose, il serait ecrase, et son incertitude relative n'existe plus. Ce n'est pas une
            // mesure de performance, c'est une machine qui ne rend plus d'images.
            if (f <= 0)
            {
                refus.Add(Docteur.T("GC.Annule", g.Levier, g.EcartPercent));
                continue;
            }

            facteur *= f;
            // Le bruit se compose dans l'espace du produit, pas en points de pourcentage. Chaque
            // mesure apporte son incertitude relative (bruit / son propre facteur) ; elles s'ajoutent
            // en quadrature parce qu'elles sont independantes, et le total les reporte a son echelle.
            bruitRelatifCarre += g.BruitPercent / f * (g.BruitPercent / f);
            retenus.Add(g.Levier);
        }

        return retenus.Count == 0
            ? new TotalDeGains(0, 0, instrument, [], refus)
            : new TotalDeGains(
                (facteur - 1) * 100.0,
                facteur * Math.Sqrt(bruitRelatifCarre),
                instrument, retenus, refus);
    }

    /// <summary>
    /// Ce qu'une campagne a laisse dans la machine, sur une grandeur.
    ///
    /// Seuls les candidats <see cref="CandidateOutcome.Kept"/> comptent, et c'est la garantie
    /// qui porte : un candidat rejete a ete annule ; son ecart est reel, il n'est simplement plus
    /// dans la machine. Le compter ferait annoncer un total que le PC ne rend pas.
    ///
    /// Une grandeur a la fois, jamais un composite. Un levier garde pour ses FPS moyens peut
    /// avoir degrade le 1 % low : sur cette grandeur-la il entre au total avec son signe, et le
    /// total baisse. C'est exactement ce qu'on veut lire.
    ///
    /// Une preuve non concluante (sous le bruit, ou invalide) n'entre pas : son ecart existe
    /// mais l'experience ne l'a pas resolu, et composer un chiffre non resolu fabriquerait un total
    /// que rien ne soutient.
    /// </summary>
    public static TotalDeGains DepuisLaCampagne(
        IReadOnlyList<CandidateResult>? resultats, GameMetricKind metrique)
    {
        var instrument = GameMetricSet.Libelle(metrique);

        var gains = (resultats ?? [])
            .Where(r => r.Outcome == CandidateOutcome.Kept)
            .Select(r => (r.Id, Preuve: r.Metriques?.FirstOrDefault(m => m.Metrique == metrique)?.Preuve))
            .Where(x => x.Preuve is { } p && p.EstConcluante)
            .Select(x => new GainProuve(x.Id, instrument, x.Preuve!.Value.DeltaPct, x.Preuve.Value.ResolutionPct))
            .ToList();

        return gains.Count == 0
            // La raison ne repete pas l'instrument : `Phrase` le nomme deja, sinon le rejeu rendrait
            // « RIEN DE PROUVE sur « FPS moyens » : aucun levier garde n'a prouve quoi que ce soit sur
            // « FPS moyens » ».
            ? new TotalDeGains(0, 0, instrument, [],
                [Docteur.T("GC.AucunLevier")])
            : Additionner(gains);
    }

    /// <summary>
    /// Le verdict en une phrase, pour que personne n'ait a lire un tableau pour savoir si c'est
    /// prouve.
    ///
    /// Le total se juge contre son propre bruit (la propagation des incertitudes), pas contre celui
    /// d'un levier. Un total de +3 % avec un bruit compose de ±3,3 % ne
    /// prouve rien, meme si chacun des leviers qui le composent avait prouve le sien.
    /// </summary>
    public static string Phrase(TotalDeGains total)
    {
        var ou = total.Instrument is { } i ? Docteur.T("GC.Sur", i) : "";

        if (total.Retenus.Count == 0)
            return Docteur.T("GC.Rien", ou, total.Refus.Count > 0 ? total.Refus[0] : Docteur.T("GC.AucunGain"));

        var leviers = string.Join(", ", total.Retenus);

        if (Math.Abs(total.TotalPercent) <= total.BruitPercent)
            return Docteur.T("GC.PasProuve", ou, total.TotalPercent, total.BruitPercent, total.Retenus.Count, leviers);

        return total.TotalPercent > 0
            ? Docteur.T("GC.Prouve", ou, total.TotalPercent, total.BruitPercent, total.Retenus.Count, leviers)
            : Docteur.T("GC.Regression", ou, total.TotalPercent, total.BruitPercent, total.Retenus.Count, leviers);
    }
}
