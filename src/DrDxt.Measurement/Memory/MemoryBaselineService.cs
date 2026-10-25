using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>Le banc, derriere une couture : les tests fournissent une mesure au lieu d'en
/// attendre une vraie pendant trente secondes.</summary>
public interface IMemoryBench
{
    MemoryMeasurement Measure();
}

/// <summary>Le banc reel, celui que l'application embarque.</summary>
public sealed class RealMemoryBench : IMemoryBench
{
    public MemoryMeasurement Measure() => MemoryBench.Measure();
}

/// <summary>
/// Decide quand la memoire se mesure, et ce que le scan a le droit d'en conclure.
///
/// La regle : automatique au premier scan (la machine est
/// censee etre au repos a l'installation), a la demande ensuite. Un scan qui remesurerait a
/// chaque fois couterait vingt a trente secondes et plusieurs centaines de Mo pour un chiffre qui ne bouge pas.
///
/// Trois refus qui comptent plus que le chemin nominal :
///   • une mesure rejetee par <see cref="MemoryQuality"/> n'est pas enregistree : une reference
///     prise sur une machine chargee resterait fausse pour toujours ;
///   • une reference portant une autre frequence, ou d'autres barrettes, est caduque : le plafond d'un canal ou le
///     nombre de canaux peuplés a change, donc le compte de canaux qu'elle portait ne vaut plus ;
///   • rien de prouve rend 0, jamais « simple canal ».
/// </summary>
public sealed class MemoryBaselineService
{
    private readonly IMemoryBench _bench;
    private readonly MemoryBaselineStore _store;
    private readonly Func<DateTimeOffset> _now;

    public MemoryBaselineService(IMemoryBench bench, MemoryBaselineStore store, Func<DateTimeOffset> now)
    {
        _bench = bench;
        _store = store;
        _now = now;
    }

    /// <summary>
    /// La mesure deja enregistree, telle quelle, et rien d'autre.
    ///
    /// Le scan paie trente a quarante secondes de banc au premier lancement et enregistre le
    /// resultat ; sans cet accesseur, aucune couche que l'utilisateur rencontre ne lirait le debit
    /// ni la latence, qui ne serviraient qu'a deduire un nombre de canaux : les deux chiffres qu'un
    /// joueur reconnaitrait seraient mesures puis jetes.
    ///
    /// Cet accesseur ne mesure jamais. Afficher un chiffre ne doit pas declencher un banc :
    /// c'est le scan qui decide quand mesurer, et lui seul. Rien enregistre ⇒ <c>null</c>.
    ///
    /// Et ce qui est enregistre a forcement ete accepte : le magasin ne stocke pas une mesure
    /// que <see cref="MemoryQuality"/> a rejetee. Lire le magasin, c'est donc lire une mesure
    /// valide : l'ecran n'a aucun tri a refaire, et ne peut pas publier un chiffre refuse.
    ///
    /// Mais valide ne veut pas dire actuelle, et c'est le piege propre a ce magasin. Il garde
    /// deliberement la mesure d'avant quand la configuration a change : c'est elle qui rend l'A/B
    /// possible apres un passage dans le BIOS. Si la remesure echoue ensuite (machine occupee,
    /// rejet Q-03), la reference d'avant reste seule en place, et l'afficher telle quelle
    /// annoncerait comme « ce que ta memoire rend » un chiffre pris dans une configuration que la
    /// machine a quittee. Precisement le cas ou le produit doit le moins se tromper.
    ///
    /// D'ou la comparaison a la configuration courante, avec la meme regle qui decide plus bas
    /// s'il faut remesurer : meme source de verite, pas une seconde convention.
    /// </summary>
    public MemoryBaseline? MesureCourante(string machineId, IReadOnlyList<MemoryModule> modules)
    {
        var lu = _store.Read(machineId);
        if (!lu.IsOk || lu.Value is not { } reference) return null;

        return MemeConfiguration(reference, modules) ? reference : null;
    }

    /// <summary>Le scan va-t-il lancer le banc mémoire ? La même règle que <see cref="ProvenChannelsForScan"/>, lue
    /// avant : la jauge du scan prévoit la vraie durée de l'étape.</summary>
    public bool MesureraPourScan(string machineId, IReadOnlyList<MemoryModule> modules, bool jeuEnCours = false)
        => Decider(_store.Read(machineId), modules, jeuEnCours) == Suite.Mesurer;

    private enum Suite { Rien, Relire, Mesurer }

    private static Suite Decider(Result<MemoryBaseline?> existing, IReadOnlyList<MemoryModule> modules, bool jeuEnCours)
    {
        // Une panne de lecture n'est pas une reference absente. Une reference amputee se
        // remplace : elle est inutilisable. Mais un fichier qu'on n'a pas pu lire (antivirus,
        // disque occupe) est peut-etre parfait, et la suite du scan ecrit. Continuer
        // detruirait la seule mesure d'avant que le client possede, pour une panne passagere.
        if (!existing.IsOk && existing.Error?.Code == ErrorCodes.MemoryBaselineIo) return Suite.Rien;
        if (existing.IsOk && existing.Value is { } baseline && MemeConfiguration(baseline, modules)) return Suite.Relire;
        // Jamais de référence prise pendant qu'un jeu tourne. Celle du 16/09 a été mesurée dans le même scan
        // que Warzone (17:53, capture à 17:54) : 12,7 Go/s, contre 33,4 Go/s pour la même machine au repos.
        // Le jeu prend la bande passante, et une charge régulière ne disperse pas assez la mesure pour que
        // Q-03 l'écarte. Rien de prouvé ce scan-ci ; le suivant, sans jeu, mesurera.
        return jeuEnCours ? Suite.Rien : Suite.Mesurer;
    }

    /// <summary>
    /// Le nombre de canaux que la mesure prouve, pour le scan en cours. Mesure au besoin (premier
    /// scan, ou reference caduque), sinon relit celle qui existe. Rend 0 quand rien n'est prouve :
    /// aucune conclusion, pas un diagnostic de simple canal.
    /// </summary>
    public int ProvenChannelsForScan(string machineId, IReadOnlyList<MemoryModule> modules, bool jeuEnCours = false)
    {
        var mts = CurrentMts(modules);
        var existing = _store.Read(machineId);
        switch (Decider(existing, modules, jeuEnCours))
        {
            case Suite.Rien: return 0;
            case Suite.Relire:
                return MemoryInference.ProvenMinimumChannels(MemoryInference.MeilleurDebit(existing.Value!.Measurement), modules);
        }

        var (measurement, _) = MeasureAccepted(MemoryInference.UpperBoundPeakGBs(modules));
        if (measurement is null) return 0;

        // L'ecriture peut echouer (disque plein, fichier verrouille). La mesure, elle, est valide,
        // et ce qu'elle prouve reste vrai pour ce scan : on le rend. Ce qu'on perd alors, c'est la
        // memoire du resultat : le scan suivant remesurera. C'est un cout, pas un mensonge.
        _store.Save(new MemoryBaseline(machineId, _now(), mts, measurement, JeuFermeVerifie: true, Signature(modules)));
        return MemoryInference.ProvenMinimumChannels(MemoryInference.MeilleurDebit(measurement), modules);
    }

    /// <summary>
    /// L'A/B : mesure maintenant, compare a la reference, et rend le verdict a 3 sigma. Rend
    /// <c>null</c> quand il n'y a rien a comparer : se taire faute de reference n'est pas la
    /// meme chose que se taire faute d'ecart, et les deux ne s'affichent pas pareil.
    ///
    /// Un verdict rendu remplace la reference : sans ca, le scan suivant comparerait encore la
    /// machine a une configuration qu'elle a quittee.
    /// </summary>
    public MemoryAbVerdict? CompareAfterChange(string machineId, IReadOnlyList<MemoryModule> modules,
        DateTimeOffset? departUtc = null, bool jeuEnCours = false)
    {
        var stored = _store.Read(machineId);
        if (!stored.IsOk || stored.Value is not { } baseline) return null;

        // La mesure d'avant doit dater du depart vers le BIOS, pas d'un autre jour.
        // Mesure sur la machine de reference : 12,7 Go/s et 128,5 ns le 16/09 ; le meme banc,
        // meme configuration, 33,4 Go/s et 74 ns dix jours plus tard. Comparee a elle, un passage de
        // 3 800 a 4 000 MT/s (+5,3 % au plus en theorie) aurait ete annonce a +160 %.
        if (departUtc is { } depart)
        {
            // Posee apres le depart : l'A/B de ce voyage a deja eu lieu et l'a remplacee par sa mesure
            // d'apres (un changement qui n'a pas pris garde son marqueur, donc chaque scan refait la
            // verification du retour), ou une remesure a la demande a suivi. Rien a comparer : on se tait.
            if (baseline.TakenUtc > depart) return null;
            if (baseline.TakenUtc < depart - FraicheurAvantVoyage)
                return new MemoryAbVerdict(false, AvantPerime, 0, 0, false, false);
        }

        // Jeu lancé au retour : la mesure d'après décrirait le partage avec le jeu, et l'écart lu serait le
        // sien, pas celui du changement (mesuré sur la machine de référence : 62 % du débit perdu ainsi).
        if (jeuEnCours) return new MemoryAbVerdict(false, JeuLance, 0, 0, false, false);

        // Le plafond doit couvrir les deux configurations. Sur un retour en JEDEC, la frequence du
        // jour est plus basse que celle de la reference : calculer le plafond sur la seule
        // frequence actuelle ferait rejeter la mesure d'avant par Q-01, et le produit se tairait
        // exactement au moment ou il a quelque chose a dire.
        var peak = Math.Max(
            MemoryInference.PeakGBs(baseline.ConfiguredMts, modules.Count),
            MemoryInference.UpperBoundPeakGBs(modules));

        var (after, raison) = MeasureAccepted(peak);
        if (after is null)
            return new MemoryAbVerdict(false, $"{raison} (apres)", 0, 0, false, false);

        var verdict = MemoryVerdict.Compare(baseline.Measurement, after, peak);
        if (verdict.HasVerdict)
            _store.Save(new MemoryBaseline(machineId, _now(), CurrentMts(modules), after, JeuFermeVerifie: true, Signature(modules)));

        return verdict;
    }

    /// <summary>
    /// Essais accordes a une mesure avant d'abandonner. **Choix produit, cadre par une mesure** :
    /// le 07/09/2026, dix lancements du banc sur la machine de reference ont donne **six mesures
    /// acceptees et quatre rejets Q-03**, alors que les moyennes tenaient a 2 % les unes des
    /// autres. Un seul essai perdrait donc la reference deux fois sur cinq sur une machine qui
    /// travaille normalement ; a trois essais, il reste environ 6 % de cas sans reference.
    /// Au-dela, on ferait tourner un banc de plusieurs centaines de Mo en boucle sur la machine d'un joueur.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>Le seul refus qui parle de la machine, donc le seul qu'on reessaie.</summary>
    private const string MachineBusy = "Q-03";

    /// <summary>
    /// Le banc n'a pas pu tourner du tout (allocation refusee, par exemple). **Ce n'est pas un
    /// refus de qualite** : l'ecran propose au client d'agir sur ce motif, et lui repondre
    /// « machine occupee » l'enverrait fermer des programmes pour un probleme qui n'a rien a voir.
    /// Un motif faux est pire que pas de motif.
    /// </summary>
    public const string BenchUnavailable = "banc indisponible";

    /// <summary>Motif du refus quand la mesure d'avant ne date pas du depart vers le BIOS.</summary>
    public const string AvantPerime = "mesure d'avant perimee";
    public const string JeuLance = "jeu en cours";

    /// <summary>L'ecart tolere entre la mesure d'avant et le marqueur du voyage. Elle est prise dans le
    /// meme geste, juste avant le marqueur : quelques dizaines de secondes. Quinze minutes couvrent une
    /// machine lente et un joueur qui lit l'ecran avant de valider, sans laisser passer une mesure d'un
    /// autre jour.</summary>
    public static readonly TimeSpan FraicheurAvantVoyage = TimeSpan.FromMinutes(15);

    /// <summary>
    /// La mesure d'avant, prise au moment de partir vers le BIOS, et enregistree comme reference.
    /// Rend faux quand elle n'a pas ete acceptee ou pas enregistree : la reference d'avant reste alors
    /// celle d'un autre jour, et <see cref="CompareAfterChange"/> refusera de conclure contre elle.
    /// </summary>
    public bool MesurerMaintenant(string machineId, IReadOnlyList<MemoryModule> modules)
    {
        // Ses appelants (départ vers le BIOS, remesure à la demande) vérifient que le jeu est fermé.
        var (mesure, _) = MeasureAccepted(MemoryInference.UpperBoundPeakGBs(modules));
        return mesure is not null
            && _store.Save(new MemoryBaseline(machineId, _now(), CurrentMts(modules), mesure, JeuFermeVerifie: true, Signature(modules))).IsOk;
    }

    /// <summary>
    /// Une mesure acceptee, ou <c>null</c>. Reessaie uniquement sur <c>Q-03</c> : c'est le seul
    /// refus qui parle de la machine (elle est occupee) et qui peut donc changer d'avis a l'essai
    /// suivant. Q-01, Q-02 et Q-04 accusent l'instrument : les relancer rendrait trois fois la
    /// meme erreur et couterait une minute au client.
    /// </summary>
    private (MemoryMeasurement? Measurement, string Reason) MeasureAccepted(double peakGBs)
    {
        for (var essai = 0; essai < MaxAttempts; essai++)
        {
            var measurement = MeasureOrNull();
            if (measurement is null) return (null, BenchUnavailable);

            var quality = MemoryQuality.Check(measurement, peakGBs);
            if (quality.Accepted) return (measurement, string.Empty);

            // Un refus d'instrument ne changera pas d'avis : il sort avec son propre numero.
            if (quality.RejectionId != MachineBusy) return (null, quality.RejectionId!);
        }

        return (null, MachineBusy);
    }

    /// <summary>
    /// Le banc, ou <c>null</c> quand il n'a pas pu tourner. Il alloue 256 Mo pour le debit, au moins 128 Mo pour la
    /// latence et 64 Mo par fil pour le debit a plusieurs fils : sur une machine qui
    /// ne les a pas (c'est-a-dire exactement celle dont on veut parler), l'allocation echoue.
    /// Mesurer la memoire ne doit jamais etre la raison pour laquelle un scan s'arrete ; ne pas
    /// savoir est un resultat acceptable, un ecran d'echec ne l'est pas.
    /// </summary>
    private MemoryMeasurement? MeasureOrNull()
    {
        try
        {
            return _bench.Measure();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>La référence décrit-elle la mémoire d'aujourd'hui ? Même fréquence, et mêmes barrettes aux mêmes emplacements :
    /// une barrette ajoutée ou déplacée change le nombre de canaux que le débit prouve, sans changer la fréquence.</summary>
    private static bool MemeConfiguration(MemoryBaseline reference, IReadOnlyList<MemoryModule> modules)
        => reference.ConfiguredMts == CurrentMts(modules) && reference.Barrettes == Signature(modules);

    /// <summary>Le nombre de barrettes et leurs emplacements, triés : l'ordre de lecture WMI ne change pas la configuration.</summary>
    internal static string Signature(IReadOnlyList<MemoryModule> modules)
        => $"{modules.Count}:{string.Join(",", modules.Select(m => m.DeviceLocator ?? "?").Order(StringComparer.Ordinal))}";

    private static int CurrentMts(IReadOnlyList<MemoryModule> modules)
        => modules.Where(m => m.ConfiguredMhz > 0).Select(m => m.ConfiguredMhz).DefaultIfEmpty(0).Min();
}
