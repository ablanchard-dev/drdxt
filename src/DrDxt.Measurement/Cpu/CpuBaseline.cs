using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement.Cpu;

/// <summary>Ce que le processeur d'une machine rend en rafale et en soutenu.</summary>
/// <param name="MachineId">A qui appartient cette mesure. Un disque voyage.</param>
/// <param name="TakenUtc">Quand.</param>
/// <param name="Threads">Combien de cœurs etaient charges. Le deficit n'a de sens qu'a nombre egal.</param>
/// <param name="RafaleMops">Debit par thread sans chauffe : ce que la machine sait faire quelques secondes.</param>
/// <param name="SoutenuMops">Debit par thread apres charge continue : ce qu'un jeu obtient.</param>
/// <param name="BruitPercent">La plus grande dispersion des deux mesures.</param>
/// <param name="PuissanceSoutenueW">Puissance moyenne du paquet pendant la mesure soutenue, apres
/// la chauffe. <c>null</c> = non lue (compteur absent, ou reference prise par une version qui ne la relevait pas).</param>
/// <param name="PuissanceEcartPercent">Incertitude de leur moyenne (écart-type divisé par la racine du nombre de relevés), en % de la moyenne.</param>
/// <param name="JeuFermeVerifie">Vrai pour une mesure d'une version qui refuse de mesurer jeu lancé. Faux (ou absent) :
/// une référence d'avant ce garde, peut-être prise pendant une partie.</param>
public sealed record CpuBaseline(
    string MachineId,
    DateTimeOffset TakenUtc,
    int Threads,
    double RafaleMops,
    double SoutenuMops,
    double BruitPercent,
    double? PuissanceSoutenueW = null,
    double? PuissanceEcartPercent = null,
    bool JeuFermeVerifie = false,
    // Le réglage BIOS de puissance lu à la mesure. Absent ou différent au scan suivant : remesure, jeu fermé.
    string? ReglageBios = null,
    // La fréquence effective des cœurs du banc, un seul cœur chargé puis tous en charge soutenue.
    // Sans elles, une limite atteinte dès la première seconde ne se voit pas : le débit ne baisse pas, il est bas d'emblée.
    double? FrequenceUnCoeurMhz = null,
    double? FrequenceSoutenueMhz = null);

/// <summary>Ce que le produit a le droit de dire du regime soutenu.</summary>
/// <param name="DeficitPercent">De combien le soutenu est sous la rafale. Positif = il rend moins.</param>
/// <param name="BruitPercent">Le plancher de la comparaison.</param>
/// <param name="Demontrable">Vrai quand le deficit depasse trois fois le bruit.</param>
/// <param name="Lisible">La phrase, et rien de plus qu'elle.</param>
/// <param name="LimiteDePuissance">Vrai quand la phrase conclut a une limite de puissance
/// (baisse demontree a puissance plate) : l'App peut alors nommer le reglage du BIOS qui la fixe.</param>
/// <param name="Reference">La mesure jugée : sa date et son garde « jeu fermé » disent ce qu'elle vaut.</param>
/// <param name="Reserve">Ce que l'App sait de la mesure et que la mesure ignore (une partie en cours
/// quand elle a été prise). Elle ferme la phrase, à l'écran comme au rapport.</param>
public sealed record CpuDeficitSoutenu(
    double DeficitPercent, double BruitPercent, bool Demontrable, string Lisible, bool LimiteDePuissance = false,
    CpuBaseline? Reference = null, string Reserve = "", bool BrideDesLeDepart = false)
{
    /// <summary>La phrase complete, reliee au reglage du BIOS qui fixe les limites de puissance quand les deux sont lus ;
    /// une seule source pour l'ecran et le rapport. MSI : « Intel Default Settings » tient le processeur dans les
    /// puissances d'Intel (blog MSI « Improving Stability of 13th 14th Gen Intel Core Processors with Intel Default
    /// Settings »).</summary>
    public string PhraseAvec(IReadOnlyList<BiosSettingReading>? bios) => PhraseSansReserve(bios) + Reserve;

    private string PhraseSansReserve(IReadOnlyList<BiosSettingReading>? bios)
    {
        var reglage = bios?.FirstOrDefault(r => r.Setting == "CPU Cooler Tuning");
        if (!LimiteDePuissance || reglage is null) return Lisible;
        var reglageBios = Docteur.T("CB.ReglageBios", reglage.Libelle);
        // Ce profil est celui qu'Intel et MSI recommandent pour la stabilité des Core 13e et 14e génération : nommé seul,
        // il inviterait à le relever.
        if (reglage.Libelle == "Intel Default Settings")
            reglageBios += Docteur.T("CB.IntelDefault");
        // La date de la mesure ferme la phrase.
        var date = Lisible.LastIndexOf(Docteur.T("CB.MesureLePrefixe"), StringComparison.Ordinal);
        return date < 0 ? Lisible + reglageBios : Lisible[..date] + reglageBios + Lisible[date..];
    }
}

/// <summary>Le fichier sous le magasin, couture comme la reference memoire : les tests n'ecrivent pas sur le
/// disque.</summary>
public interface ICpuBaselineFile
{
    Result WriteAtomic(string json);
    Result<string?> Read();
}

/// <summary>Le banc, derriere une couture : un test ne charge pas huit cœurs quarante secondes.</summary>
public interface ICpuSustainedBench
{
    /// <summary>Mesure rafale puis soutenu, sur le meme nombre de cœurs. <c>null</c> quand le banc n'a pas pu
    /// tourner.</summary>
    CpuBaseline? Mesurer(string machineId);
}

/// <summary>
/// Le magasin de la reference CPU. Comme pour la memoire, une reference d'une autre machine est ignoree : un disque
/// voyage (image, restauration, second PC), et la comparer inventerait un deficit. Fichier absent = <c>Ok(null)</c>,
/// contenu illisible = echec nomme.
/// </summary>
public sealed class CpuBaselineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ICpuBaselineFile _file;

    public CpuBaselineStore(ICpuBaselineFile file)
        => _file = file ?? throw new ArgumentNullException(nameof(file));

    public Result Save(CpuBaseline baseline)
    {
        try { return _file.WriteAtomic(JsonSerializer.Serialize(baseline, JsonOptions)); }
        catch (Exception ex) { return Result.Fail(ErrorCodes.IoFailure, Docteur.T("CB.NonEcrite", ex.Message)); }
    }

    public Result<CpuBaseline?> Read(string machineId)
    {
        Result<string?> lu;
        try { lu = _file.Read(); }
        catch (Exception ex) { return Result<CpuBaseline?>.Fail(ErrorCodes.IoFailure, ex.Message); }

        if (!lu.IsOk) return Result<CpuBaseline?>.Fail(lu.Error!.Value.Code, lu.Error!.Value.Message);
        if (string.IsNullOrWhiteSpace(lu.Value)) return Result<CpuBaseline?>.Ok(null);

        CpuBaseline? baseline;
        try { baseline = JsonSerializer.Deserialize<CpuBaseline>(lu.Value!); }
        catch (Exception ex) { return Result<CpuBaseline?>.Fail(ErrorCodes.IoFailure, Docteur.T("CB.Illisible", ex.Message)); }

        // Une reference d'une autre machine est ignoree, pas corrigee.
        return Result<CpuBaseline?>.Ok(
            baseline is not null && string.Equals(baseline.MachineId, machineId, StringComparison.Ordinal)
                ? baseline
                : null);
    }
}

/// <summary>
/// Ce que le processeur rend a un jeu, par opposition a ce qu'il sait faire deux secondes. 09/09/2026 sur la machine
/// de reference : un cœur seul tient 5 292 MHz, huit cœurs en charge continue tombent a 3 698, bascule a la quinzieme
/// seconde ; un banc en rafales ne voit jamais cette marche. Propriete du materiel, aucune ecriture. Comme la memoire :
/// automatique au premier scan, a la demande ensuite (plus de quarante secondes de pleine charge sinon, a chaque
/// ouverture). Le deficit depend du nombre de cœurs charges : la comparaison se fait a nombre egal.
/// </summary>
public sealed class CpuBaselineService
{
    /// <summary>Au-dela de trois fois le bruit, le deficit est distinguable. Seuil emprunte au juge de budget CPU, non
    /// mesure ici.</summary>
    private const int FacteurSeuil = 3;

    /// <summary>Au-delà de ce seuil de détection, « garde sa puissance » ne se dit pas. Non chiffré : une baisse de 15 %
    /// doit rester visible (19 à 22 % mesurés sur la machine de référence ; seuils ordinaires de 2 à 22 %, bruit de 0,6 à
    /// 7,4 %).</summary>
    internal const double SeuilConcluantPercent = 15;

    private readonly ICpuSustainedBench _bench;
    private readonly CpuBaselineStore _store;

    public CpuBaselineService(ICpuSustainedBench bench, CpuBaselineStore store)
    {
        _bench = bench;
        _store = store;
    }

    /// <summary>La référence enregistrée, relue sans mesurer, pour comparer une partie au banc.
    /// <c>null</c> si absente ou illisible.</summary>
    public CpuBaseline? Reference(string machineId) => _store.Read(machineId) is { IsOk: true, Value: { } r } ? r : null;

    /// <summary>Le scan va-t-il lancer le banc (plus de quarante secondes à pleine charge) ? La même règle que
    /// <see cref="DeficitPourScan"/>, lue avant : la jauge du scan prévoit la vraie durée de l'étape.</summary>
    public bool MesureraPourScan(string machineId, bool jeuEnCours = false, string? reglageBios = null)
    {
        var existante = _store.Read(machineId);
        if (!existante.IsOk) return false;
        return existante.Value is { } connue ? !ReferenceSuffit(connue, jeuEnCours, reglageBios) : !jeuEnCours;
    }

    // Jeu fermé, on remesure quand le réglage BIOS a changé (avant et après dits) ou quand la référence est d'avant le
    // relevé des fréquences (une fois : les suivantes portent leur date, jamais de boucle sans compteurs de fréquence).
    private static bool ReferenceSuffit(CpuBaseline connue, bool jeuEnCours, string? reglageBios)
    {
        var sansFrequence = connue.FrequenceUnCoeurMhz is null && connue.TakenUtc < BancAvecFrequence;
        return jeuEnCours || (!sansFrequence && (reglageBios is null || connue.ReglageBios == reglageBios));
    }

    /// <summary>Le deficit soutenu pour le scan en cours. Mesure au besoin (premiere fois),
    /// sinon relit. <c>null</c> quand rien n'est mesurable : jamais un deficit invente.</summary>
    public CpuDeficitSoutenu? DeficitPourScan(string machineId, bool jeuEnCours = false, string? reglageBios = null)
    {
        var existante = _store.Read(machineId);

        // Une panne de lecture n'est pas une reference absente : la suite ecrit, et un antivirus qui tient le fichier
        // ferait ecraser la seule mesure d'avant.
        if (!existante.IsOk) return null;

        if (existante.Value is { } connue)
        {
            if (ReferenceSuffit(connue, jeuEnCours, reglageBios)) return Juger(connue);
            var nouvelle = Mesurer(machineId);
            if (nouvelle is null) return Juger(connue);
            nouvelle = nouvelle with { ReglageBios = reglageBios ?? connue.ReglageBios };
            _store.Save(nouvelle);
            var apres = Juger(nouvelle);
            return connue.ReglageBios is null || connue.PuissanceSoutenueW is null || connue.ReglageBios == nouvelle.ReglageBios
                ? apres
                : apres with { Lisible = AvantApres(Juger(connue), connue, apres, nouvelle) + apres.Lisible };
        }

        // Jeu lancé, pas de banc : le joueur le paierait en images et la référence décrirait le partage avec le jeu (la
        // référence mémoire du 16/09 a été faussée ainsi). Le scan suivant, sans jeu, mesurera.
        if (jeuEnCours) return null;

        var mesuree = Mesurer(machineId);
        if (mesuree is null) return null;
        mesuree = mesuree with { ReglageBios = reglageBios };

        // Une ecriture ratee perd la memoire du resultat, pas la mesure de ce scan.
        _store.Save(mesuree);
        return Juger(mesuree);
    }

    /// <summary>La phrase de tête quand le réglage BIOS a changé entre deux mesures.</summary>
    private static string AvantApres(CpuDeficitSoutenu avant, CpuBaseline ancienne, CpuDeficitSoutenu apres, CpuBaseline nouvelle)
    {
        var langue = Docteur.Langue;
        string Watts(CpuBaseline b) => b.PuissanceSoutenueW is { } w ? Docteur.T("CB.Watts", w.ToString("0", langue)) : Docteur.T("CB.PuissanceNonLue");
        return Docteur.T("CB.AvantApres", ancienne.ReglageBios, nouvelle.ReglageBios, Watts(ancienne),
            avant.DeficitPercent.ToString("0.0", langue), Watts(nouvelle), apres.DeficitPercent.ToString("0.0", langue));
    }

    /// <summary>« A la demande ensuite » : remesure sans relire ; l'ancienne reference n'est remplacee que si la mesure
    /// aboutit. <c>null</c> = rien n'a change.</summary>
    /// <param name="ancienneSuspecte">La référence a sans doute été prise pendant une partie : elle est remplacée même par
    /// une remesure qui ne conclut pas, plutôt que garder un chiffre qu'on sait faux.</param>
    public CpuDeficitSoutenu? Remesurer(string machineId, bool ancienneSuspecte = false, string? reglageBios = null)
    {
        // Lue avant le banc, pour dire l'avant et l'après si le BIOS a changé depuis.
        var ancienne = _store.Read(machineId) is { IsOk: true, Value: { } a } ? a : null;
        var mesuree = Mesurer(machineId);
        if (mesuree is null) return null;
        // Sans le réglage noté, le scan suivant croirait le BIOS changé et remesurerait pour rien.
        mesuree = mesuree with { ReglageBios = reglageBios };
        var verdict = Juger(mesuree);
        if (ancienne is { ReglageBios: { } avant, PuissanceSoutenueW: not null } && reglageBios is not null && avant != reglageBios)
            verdict = verdict with { Lisible = AvantApres(Juger(ancienne), ancienne, verdict, mesuree) + verdict.Lisible };
        // Une remesure qui ne conclut rien (sous une autre charge : ±7,4 %, baisse de -7,5 %, la rafale freinée) est dite,
        // pas gardée à la place d'une bonne référence.
        if (!ancienneSuspecte && !verdict.Demontrable && FacteurSeuil * mesuree.BruitPercent > SeuilConcluantPercent)
        {
            var date = verdict.Lisible.LastIndexOf(Docteur.T("CB.MesureLePrefixe"), StringComparison.Ordinal);
            var gardee = Docteur.T("CB.Gardee");
            return verdict with
            {
                Lisible = date < 0 ? verdict.Lisible + gardee : verdict.Lisible[..date] + gardee + verdict.Lisible[date..],
            };
        }
        _store.Save(mesuree);
        return verdict;
    }

    private CpuBaseline? Mesurer(string machineId)
    {
        // Chaque appelant de cette version vérifie que le jeu est fermé avant de mesurer (scan, remesure).
        try { return _bench.Mesurer(machineId) is { } b ? b with { JeuFermeVerifie = true } : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Au-dessous de cette incertitude relative de la moyenne, la puissance est dite tenue. Non chiffré :
    /// l'ecart-type des releves bascule autour de 10 % (7 ; 12,8 ; 10,4 ; 7,9 %) pour une moyenne stable, d'ou
    /// l'incertitude de la moyenne (2 a 3 %). Machine de reference : 63,4 W a 7 % pres au banc (13 releves d'une seconde),
    /// 63,2 a 69,5 W en jeu. Une baisse de debit a puissance plate signe une limite de puissance ; une chauffe ferait
    /// varier la puissance avec la temperature.</summary>
    internal const double EcartPlatPercent = 10;

    private static string LimiteDePuissance(CpuBaseline b) =>
        b is { PuissanceSoutenueW: { } w, PuissanceEcartPercent: { } e } && e <= EcartPlatPercent
            ? Docteur.T("CB.LimiteDePuissance", w)
            : "";

    // Une puissance relevée mais trop variable (63 W à 12,8 % près sur la machine de reference) : le chiffre et pourquoi
    // on ne conclut pas.
    private static string PuissanceNonConcluante(CpuBaseline b) =>
        b is { PuissanceSoutenueW: { } w, PuissanceEcartPercent: { } e } && e > EcartPlatPercent
            ? Docteur.T("CB.PuissanceNonConcluante", w, e)
            : "";

    /// <summary>Sous ce rapport entre la fréquence en charge soutenue et celle d'un seul cœur, à puissance plate, la
    /// limite de puissance retient le processeur. Non chiffré : le turbo d'un cœur et celui de tous diffèrent de quelques
    /// pour cent (fiches Intel : 5,6 contre 5,4 GHz sur un i7-14700, 4,3 contre 4,0 sur un i5-10400). 09/09 sur la
    /// machine de référence : 5 292 puis 3 698 MHz, 70 %.</summary>
    internal const double RapportBride = 0.85;

    /// <summary>La version du banc qui relève les fréquences (commit du 03/10/2026 à 20 h 34, heure de Paris ; la borne
    /// est posée à 20 h 00).</summary>
    internal static readonly DateTimeOffset BancAvecFrequence = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);

    private static bool BrideDesLeDepart(CpuBaseline b) =>
        b is { PuissanceSoutenueW: not null, PuissanceEcartPercent: { } e, FrequenceUnCoeurMhz: { } un, FrequenceSoutenueMhz: { } tous }
        && e <= EcartPlatPercent && un > 0 && tous / un < RapportBride;

    internal static CpuDeficitSoutenu Juger(CpuBaseline b) => JugerLaMesure(b) with { Reference = b };

    private static CpuDeficitSoutenu JugerLaMesure(CpuBaseline b)
    {
        // La reference se relit d'un fichier que tout programme de l'utilisateur peut modifier : 0 rendrait 100 %.
        if (b.RafaleMops <= 0 || b.SoutenuMops <= 0)
            return new CpuDeficitSoutenu(0, b.BruitPercent, false,
                Docteur.T("CB.Nulle"));
        // Un bruit absent se relit 0, et un seuil nul rendrait le moindre ecart demontrable. Le banc ne rend jamais 0.
        if (b.BruitPercent <= 0)
            return new CpuDeficitSoutenu(0, b.BruitPercent, false,
                Docteur.T("CB.SansPrecision"));

        var deficit = (b.RafaleMops - b.SoutenuMops) / b.RafaleMops * 100;
        var seuil = FacteurSeuil * b.BruitPercent;
        var limite = deficit > seuil && LimiteDePuissance(b).Length > 0;
        // Bridé dès le départ (66 W plats sous « Intel Default Settings », aucune baisse) : la rafale à froid ne va pas plus
        // vite, ce qu'une surchauffe ne ferait pas. Watts et fréquence se relèvent dès la 20e seconde. Le BIOS n'est pas
        // affirmé : sur un portable, le fabricant fixe cette limite.
        if (deficit <= seuil && seuil <= SeuilConcluantPercent && BrideDesLeDepart(b))
            return new CpuDeficitSoutenu(deficit, b.BruitPercent, false, LimiteDePuissance: true, BrideDesLeDepart: true,
                Lisible: Docteur.T("CB.BrideDesLeDepart", b.Threads, b.FrequenceSoutenueMhz, b.FrequenceUnCoeurMhz, b.PuissanceSoutenueW)
                    + Docteur.T("CB.MesureLe", b.TakenUtc.ToLocalTime()));
        // La reference se relit : sans sa date, elle passerait pour d'aujourd'hui.
        var quand = Docteur.T("CB.MesureLe", b.TakenUtc.ToLocalTime());

        return new CpuDeficitSoutenu(deficit, b.BruitPercent, deficit > seuil, LimiteDePuissance: limite, Lisible: (
            deficit > seuil

                ? Docteur.T("CB.Baisse", b.Threads, deficit, b.BruitPercent)
                  + LimiteDePuissance(b)
                  + PuissanceNonConcluante(b)
                  // Sans puissance relevee, impossible de dire si la baisse vient du BIOS (une mesure fraiche l'a montre
                  // sur la machine de reference : 67 W plats).
                  + (b.PuissanceSoutenueW is null
                      ? Docteur.T("CB.SansPuissance")
                      : "")
                : seuil > SeuilConcluantPercent
                    // A ±14,8 %, « aucune baisse au-delà de 44,5 % » cacherait une baisse de 19 à 22 % comme celle de la
                    // machine de reference.
                    ? Docteur.T("CB.TropVarie", b.BruitPercent, seuil)
                    : Docteur.T("CB.GardeSaPuissance", seuil)
                      // Sous un banc lourd en parallèle, ce verdict sort à tort (19 à 22 % au repos), et ni la charge ni la
                      // puissance du paquet ne séparent les deux cas : la condition est dite.
                      ) + quand);
    }
}
