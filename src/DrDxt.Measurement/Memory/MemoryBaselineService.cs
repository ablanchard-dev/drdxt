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
/// Decide quand la memoire se mesure, et ce que le scan a le droit d'en conclure : automatique au premier scan (machine
/// censee au repos a l'installation), a la demande ensuite (20 a 30 s et plusieurs centaines de Mo par mesure).
/// Trois refus :
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
    /// La mesure deja enregistree, telle quelle (debit et latence, payes 30 a 40 s de banc au premier lancement). Ne
    /// mesure jamais : seul le scan decide quand mesurer. Rien enregistre ⇒ <c>null</c>. Le magasin ne stocke pas une
    /// mesure que <see cref="MemoryQuality"/> a rejetee : l'ecran ne peut pas publier un chiffre refuse.
    /// Valide ne veut pas dire actuelle : le magasin garde la mesure d'avant un changement de configuration (elle rend
    /// l'A/B possible apres le BIOS) ; si la remesure echoue (machine occupee, Q-03), elle annoncerait une configuration
    /// quittee. D'ou la comparaison a la configuration courante, par la meme regle que la decision de remesurer.
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
        // Une panne de lecture n'est pas une reference absente : une reference amputee se remplace, mais un fichier
        // illisible (antivirus, disque occupe) est peut-etre parfait, et continuer l'ecraserait pour une panne passagere.
        if (!existing.IsOk && existing.Error?.Code == ErrorCodes.MemoryBaselineIo) return Suite.Rien;
        if (existing.IsOk && existing.Value is { } baseline && MemeConfiguration(baseline, modules)) return Suite.Relire;
        // Jamais de référence pendant un jeu : le 16/09, mesurée dans le scan de Warzone (17:53, capture à 17:54),
        // 12,7 Go/s contre 33,4 Go/s au repos, et une charge régulière ne disperse pas assez pour que Q-03 l'écarte.
        return jeuEnCours ? Suite.Rien : Suite.Mesurer;
    }

    /// <summary>Le nombre de canaux que la mesure prouve, pour ce scan. Mesure au besoin (premier scan, reference
    /// caduque), sinon relit. 0 = rien de prouve, pas « simple canal ».</summary>
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

        // Si l'ecriture echoue (disque plein, fichier verrouille), la mesure reste vraie pour ce scan : on la rend, et le
        // scan suivant remesurera.
        _store.Save(new MemoryBaseline(machineId, _now(), mts, measurement, JeuFermeVerifie: true, Signature(modules)));
        return MemoryInference.ProvenMinimumChannels(MemoryInference.MeilleurDebit(measurement), modules);
    }

    /// <summary>L'A/B : mesure maintenant, compare a la reference, verdict a 3 sigma. <c>null</c> sans rien a comparer
    /// (se taire faute de reference ne s'affiche pas comme se taire faute d'ecart). Un verdict rendu remplace la
    /// reference, sinon le scan suivant comparerait a une configuration quittee.</summary>
    public MemoryAbVerdict? CompareAfterChange(string machineId, IReadOnlyList<MemoryModule> modules,
        DateTimeOffset? departUtc = null, bool jeuEnCours = false)
    {
        var stored = _store.Read(machineId);
        if (!stored.IsOk || stored.Value is not { } baseline) return null;

        // La mesure d'avant date du depart vers le BIOS. Machine de reference : 12,7 Go/s et 128,5 ns le 16/09, 33,4 Go/s
        // et 74 ns dix jours plus tard a configuration egale ; comparee a la premiere, 3 800 → 4 000 MT/s (+5,3 % au
        // plus en theorie) aurait ete annonce a +160 %.
        if (departUtc is { } depart)
        {
            // Posee apres le depart : l'A/B de ce voyage a deja eu lieu (un changement qui n'a pas pris garde son
            // marqueur, et chaque scan refait la verification du retour), ou une remesure a suivi. On se tait.
            if (baseline.TakenUtc > depart) return null;
            if (baseline.TakenUtc < depart - FraicheurAvantVoyage)
                return new MemoryAbVerdict(false, AvantPerime, 0, 0, false, false);
        }

        // Jeu lancé au retour : la mesure d'après décrirait le partage avec le jeu, et l'écart lu serait le
        // sien, pas celui du changement (mesuré sur la machine de référence : 62 % du débit perdu ainsi).
        if (jeuEnCours) return new MemoryAbVerdict(false, JeuLance, 0, 0, false, false);

        // Le plafond couvre les deux configurations : sur un retour en JEDEC, calcule sur la seule frequence du jour (plus
        // basse), il ferait rejeter la mesure d'avant par Q-01.
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
    /// Essais accordes a une mesure. **Choix produit, cadre par une mesure** : le 07/09/2026, dix lancements sur la
    /// machine de reference ont donne **six acceptees et quatre rejets Q-03**, moyennes a 2 % les unes des autres. Un
    /// essai perdrait la reference deux fois sur cinq ; a trois, environ 6 %. Au-dela, un banc de plusieurs centaines
    /// de Mo tournerait en boucle.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>Le seul refus qui parle de la machine, donc le seul qu'on reessaie.</summary>
    private const string MachineBusy = "Q-03";

    /// <summary>
    /// Le banc n'a pas pu tourner (allocation refusee, par exemple). **Pas un refus de qualite** : « machine occupee »
    /// enverrait le client fermer des programmes pour rien.
    /// </summary>
    public const string BenchUnavailable = "banc indisponible";

    /// <summary>Motif du refus quand la mesure d'avant ne date pas du depart vers le BIOS.</summary>
    public const string AvantPerime = "mesure d'avant perimee";
    public const string JeuLance = "jeu en cours";

    /// <summary>L'ecart tolere entre la mesure d'avant et le marqueur du voyage (prise juste avant : quelques dizaines
    /// de secondes). Quinze minutes couvrent une machine lente et un joueur qui lit, pas une mesure d'un autre jour.</summary>
    public static readonly TimeSpan FraicheurAvantVoyage = TimeSpan.FromMinutes(15);

    /// <summary>La mesure d'avant, prise au depart vers le BIOS et enregistree comme reference. Faux quand elle n'est
    /// pas acceptee ou pas enregistree : <see cref="CompareAfterChange"/> refusera de conclure contre l'ancienne.</summary>
    public bool MesurerMaintenant(string machineId, IReadOnlyList<MemoryModule> modules)
    {
        // Ses appelants (départ vers le BIOS, remesure à la demande) vérifient que le jeu est fermé.
        var (mesure, _) = MeasureAccepted(MemoryInference.UpperBoundPeakGBs(modules));
        return mesure is not null
            && _store.Save(new MemoryBaseline(machineId, _now(), CurrentMts(modules), mesure, JeuFermeVerifie: true, Signature(modules))).IsOk;
    }

    /// <summary>Une mesure acceptee, ou <c>null</c>. Reessaie seulement sur <c>Q-03</c> (machine occupee, qui peut
    /// changer) ; Q-01, Q-02 et Q-04 accusent l'instrument, et rendraient trois fois la meme erreur en une minute.</summary>
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
    /// latence et 64 Mo par fil pour le debit a plusieurs fils : sur une machine qui ne les a pas, l'allocation echoue,
    /// et le scan ne s'arrete jamais pour ca.
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
