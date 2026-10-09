using static DrDxt.Core.TexteFrancais;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement.Cpu;

/// <summary>Ce que le processeur d'une machine rend en RAFALE et en soutenu.</summary>
/// <param name="MachineId">A qui appartient cette mesure. Un disque voyage.</param>
/// <param name="TakenUtc">Quand.</param>
/// <param name="Threads">Combien de cœurs etaient charges. Le deficit n'a de sens qu'a nombre egal.</param>
/// <param name="RafaleMops">Debit par thread sans chauffe : ce que la machine sait faire quelques secondes.</param>
/// <param name="SoutenuMops">Debit par thread apres charge continue : ce qu'un JEU obtient.</param>
/// <param name="BruitPercent">La plus grande dispersion des deux mesures.</param>
/// <param name="PuissanceSoutenueW">Puissance moyenne du paquet pendant la mesure soutenue, apres
/// la chauffe. <c>null</c> = non lue (compteur absent, ou reference prise avant le 25/09/2026).</param>
/// <param name="PuissanceEcartPercent">Incertitude de leur moyenne (écart-type divisé par la racine du nombre de relevés), en % de la moyenne.</param>
/// <param name="JeuFermeVerifie">26/09/2026 : vrai pour une mesure prise par une version qui refuse de mesurer
/// jeu lancé. Faux (ou absent) : une référence d'avant ce garde, qui a pu être prise pendant une partie.</param>
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
    // 29/09/2026 (A2) : le réglage BIOS de puissance lu quand la mesure a été prise. Une référence qui n'en a
    // pas (antérieure) ou un réglage différent au scan suivant déclenche une remesure, jeu fermé.
    string? ReglageBios = null,
    // 03/10/2026 (M2) : la fréquence effective des cœurs du banc, un seul cœur chargé puis tous en charge soutenue.
    // Sans elles, une limite atteinte dès la première seconde ne se voit pas : le débit ne baisse pas, il est bas d'emblée.
    double? FrequenceUnCoeurMhz = null,
    double? FrequenceSoutenueMhz = null);

/// <summary>Ce que le produit a le droit de dire du regime soutenu.</summary>
/// <param name="DeficitPercent">De combien le soutenu est sous la rafale. Positif = il rend moins.</param>
/// <param name="BruitPercent">Le plancher de la comparaison.</param>
/// <param name="Demontrable">Vrai quand le deficit depasse trois fois le bruit.</param>
/// <param name="Lisible">La phrase, et rien de plus qu'elle.</param>
/// <param name="LimiteDePuissance">25/09/2026 — vrai quand la phrase conclut a une limite de puissance
/// (baisse demontree a puissance plate) : l'App peut alors nommer le reglage du BIOS qui la fixe.</param>
/// <param name="Reference">La mesure jugée : sa date et son garde « jeu fermé » disent ce qu'elle vaut.</param>
/// <param name="Reserve">26/09/2026 : ce que l'App sait de la mesure et que la mesure ignore (une partie en cours
/// quand elle a été prise). Elle ferme la phrase, à l'écran comme au rapport.</param>
public sealed record CpuDeficitSoutenu(
    double DeficitPercent, double BruitPercent, bool Demontrable, string Lisible, bool LimiteDePuissance = false,
    CpuBaseline? Reference = null, string Reserve = "", bool BrideDesLeDepart = false)
{
    /// <summary>25/09/2026 — la phrase complete, reliee au reglage du BIOS qui fixe les limites de
    /// puissance quand les deux sont lus. Une seule source pour l'ecran et le rapport. MSI : le profil
    /// « Intel Default Settings » tient le processeur dans les puissances d'Intel (blog MSI « Improving
    /// Stability of 13th 14th Gen Intel Core Processors with Intel Default Settings »).</summary>
    public string PhraseAvec(IReadOnlyList<BiosSettingReading>? bios) => PhraseSansReserve(bios) + Reserve;

    private string PhraseSansReserve(IReadOnlyList<BiosSettingReading>? bios)
    {
        var reglage = bios?.FirstOrDefault(r => r.Setting == "CPU Cooler Tuning");
        if (!LimiteDePuissance || reglage is null) return Lisible;
        var reglageBios = " Sur ta carte, les limites de puissance laissées sur Auto suivent le réglage BIOS "
            + $"« CPU Cooler Tuning », lu ici : « {reglage.Libelle} ».";
        // 25/09/2026 : nommer le réglage invitait à le relever. Ce profil est celui qu'Intel et MSI recommandent pour
        // la stabilité des Core de 13e et 14e génération (blog MSI cité plus haut) : la phrase le dit.
        if (reglage.Libelle == "Intel Default Settings")
            reglageBios += " C'est le profil qu'Intel et MSI recommandent pour la stabilité des Core de 13e et 14e "
                + "génération : DrDXT ne te conseille pas de le changer, la puissance en plus se paierait en chaleur "
                + "et en risque d'instabilité.";
        // 25/09/2026 : la date de la mesure ferme la phrase (vu au rapport réel : le réglage venait après elle).
        var date = Lisible.LastIndexOf(" Mesuré le ", StringComparison.Ordinal);
        return date < 0 ? Lisible + reglageBios : Lisible[..date] + reglageBios + Lisible[date..];
    }
}

/// <summary>Le fichier sous le magasin. Meme couture que la reference memoire : les tests
/// injectent un faux et n'ecrivent jamais sur le disque.</summary>
public interface ICpuBaselineFile
{
    Result WriteAtomic(string json);
    Result<string?> Read();
}

/// <summary>Le banc, derriere une couture : un test ne va pas charger huit cœurs pendant
/// quarante secondes pour verifier une regle de decision.</summary>
public interface ICpuSustainedBench
{
    /// <summary>Mesure rafale puis soutenu, sur le meme nombre de cœurs. <c>null</c> quand le banc
    /// n'a pas pu tourner — ne pas savoir est un resultat, un ecran d'echec ne l'est pas.</summary>
    CpuBaseline? Mesurer(string machineId);
}

/// <summary>
/// Le magasin de la reference CPU. Il porte la meme garantie que celui de la memoire, et pour la
/// meme raison : une reference prise sur une autre machine est ignoree comme si elle n'existait
/// pas. Un disque voyage — image systeme, restauration, second PC — et comparer le processeur
/// d'une machine a la reference d'une autre rendrait un deficit invente de toutes pieces, avec
/// l'air d'un resultat.
///
/// Lecture FAIL-CLEAN : fichier absent = <c>Ok(null)</c>, contenu illisible = echec nomme.
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
        catch (Exception ex) { return Result.Fail(ErrorCodes.IoFailure, $"référence CPU non écrite : {ex.Message}"); }
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
        catch (Exception ex) { return Result<CpuBaseline?>.Fail(ErrorCodes.IoFailure, $"référence CPU illisible : {ex.Message}"); }

        // Une reference d'une autre machine n'est pas une reference : elle est ignoree, pas
        // corrigee. La rendre vaudrait mieux que rien seulement si comparer deux machines avait
        // un sens, et ca n'en a aucun.
        return Result<CpuBaseline?>.Ok(
            baseline is not null && string.Equals(baseline.MachineId, machineId, StringComparison.Ordinal)
                ? baseline
                : null);
    }
}

/// <summary>
/// Ce que le processeur rend a un jeu, par opposition a ce qu'il sait faire deux secondes.
///
/// ══ la grandeur, et pourquoi elle manquait ══
/// Mesure du 09/09/2026 sur la machine de reference : un cœur seul tient **5 292 MHz**, huit cœurs
/// sous charge CONTINUE tombent a **3 698**, et la bascule se voit a la quinzieme seconde. Un banc
/// qui mesure en rafales ne voit jamais cette marche — il rend le regime que le joueur ne connait
/// Jamais, puisqu'un jeu tourne des heures.
///
/// Le produit ne savait donc pas dire ce que le processeur rend reellement a un jeu. C'est cette
/// grandeur-la, et elle ne demande **aucune ecriture sur la machine** : c'est une propriete du
/// materiel, pas un gain de levier.
///
/// ══ quand elle se mesure ══
/// La regle est celle tranchee pour la memoire le 06/09 : **automatique au premier
/// scan**, a la demande ensuite. Un scan qui remesurerait a chaque fois couterait plus de quarante
/// secondes de charge complete pour un chiffre qui ne bouge pas — et il chaufferait la machine du
/// client a chaque ouverture du produit.
///
/// Et une reference dont le nombre de cœurs charges differe est CADUQUE : le deficit soutenu
/// depend directement de combien de cœurs travaillent, donc comparer huit cœurs a quatre
/// rendrait un ecart qui ne parle que du protocole.
/// </summary>
public sealed class CpuBaselineService
{
    /// <summary>Au-dela de trois fois le bruit, le deficit est distinguable. Seuil EMPRUNTE au juge
    /// de budget CPU — aucune mesure ne dit que trois soit le bon nombre ici.</summary>
    private const int FacteurSeuil = 3;

    /// <summary>Au-delà de ce seuil de détection, « garde sa puissance » ne se dit pas. **NON CHIFFRÉ** : une
    /// baisse de 15 % doit rester visible, la baisse réelle mesurée sur la machine de référence étant de 19 à
    /// 22 %. Les mesures ordinaires ont un seuil de 2 à 22 % (bruit de 0,6 à 7,4 %).</summary>
    internal const double SeuilConcluantPercent = 15;

    private readonly ICpuSustainedBench _bench;
    private readonly CpuBaselineStore _store;

    public CpuBaselineService(ICpuSustainedBench bench, CpuBaselineStore store)
    {
        _bench = bench;
        _store = store;
    }

    /// <summary>04/10/2026 (plan 1.1) : la référence enregistrée, relue sans mesurer, pour comparer une partie au banc.
    /// <c>null</c> si absente ou illisible.</summary>
    public CpuBaseline? Reference(string machineId) => _store.Read(machineId) is { IsOk: true, Value: { } r } ? r : null;

    /// <summary>Le deficit soutenu pour le scan en cours. Mesure au besoin (premiere fois),
    /// sinon relit. <c>null</c> quand rien n'est mesurable — jamais un deficit invente.</summary>
    public CpuDeficitSoutenu? DeficitPourScan(string machineId, bool jeuEnCours = false, string? reglageBios = null)
    {
        var existante = _store.Read(machineId);

        // Une panne de lecture n'est pas une reference absente. Le meme reflexe que pour la
        // memoire : la suite de cette methode ecrit, et ecraser une reference parfaite a cause
        // d'un antivirus qui tenait le fichier detruirait la seule mesure d'avant que le client
        // possede.
        if (!existante.IsOk) return null;

        if (existante.Value is { } connue)
        {
            // 29/09/2026 (A2) : le réglage de puissance du BIOS a changé depuis la mesure, ou la référence est
            // d'avant la mesure de puissance. Jeu fermé, on remesure et on dit l'avant et l'après.
            // Revue R1 : une référence d'avant le relevé des fréquences ne peut pas montrer une limite atteinte dès le début ;
            // elle est remesurée une fois, jeu fermé. Les références suivantes portent leur date : jamais de boucle, même
            // sur une machine dont les compteurs de fréquence manquent.
            var sansFrequence = connue.FrequenceUnCoeurMhz is null && connue.TakenUtc < BancAvecFrequence;
            if (jeuEnCours || (!sansFrequence && (reglageBios is null || connue.ReglageBios == reglageBios))) return Juger(connue);
            var nouvelle = Mesurer(machineId);
            if (nouvelle is null) return Juger(connue);
            nouvelle = nouvelle with { ReglageBios = reglageBios ?? connue.ReglageBios };
            _store.Save(nouvelle);
            var apres = Juger(nouvelle);
            return connue.ReglageBios is null || connue.PuissanceSoutenueW is null || connue.ReglageBios == nouvelle.ReglageBios
                ? apres
                : apres with { Lisible = AvantApres(Juger(connue), connue, apres, nouvelle) + apres.Lisible };
        }

        // 26/09/2026 : jeu lancé, pas de banc. Il charge tous les cœurs rapides plus de quarante secondes :
        // le joueur le paierait en images, et la référence enregistrée décrirait le partage avec le jeu
        // (la référence mémoire du 16/09 a été faussée ainsi). Le scan suivant, sans jeu, mesurera.
        if (jeuEnCours) return null;

        var mesuree = Mesurer(machineId);
        if (mesuree is null) return null;
        mesuree = mesuree with { ReglageBios = reglageBios };

        // L'ecriture peut echouer ; la mesure reste valide pour ce scan et on la rend. Ce qu'on
        // perd alors, c'est la memoire du resultat — un cout, pas un mensonge.
        _store.Save(mesuree);
        return Juger(mesuree);
    }

    /// <summary>29/09/2026 (A2) : la phrase de tête quand le réglage BIOS a changé entre deux mesures.</summary>
    private static string AvantApres(CpuDeficitSoutenu avant, CpuBaseline ancienne, CpuDeficitSoutenu apres, CpuBaseline nouvelle)
    {
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        string Watts(CpuBaseline b) => b.PuissanceSoutenueW is { } w ? $"{w.ToString("0", fr)} W" : "puissance non lue";
        return $"Réglage du BIOS changé depuis la dernière mesure (« {ancienne.ReglageBios} » puis « {nouvelle.ReglageBios} ») : "
            + $"avant, {Watts(ancienne)} et {avant.DeficitPercent.ToString("0.0", fr)} % de puissance perdue en charge longue ; "
            + $"maintenant, {Watts(nouvelle)} et {apres.DeficitPercent.ToString("0.0", fr)} %. ";
    }

    /// <summary>25/09/2026 — « a la demande ensuite », la seconde moitie de la regle du 06/09,
    /// jamais codee jusqu'ici. Remesure sans relire ; l'ancienne reference n'est remplacee que si la
    /// nouvelle mesure aboutit. <c>null</c> = rien n'a change.</summary>
    /// <param name="ancienneSuspecte">27/09/2026 : l'App sait que la référence enregistrée a sans doute été prise
    /// pendant une partie. La garder contre une remesure bruitée, c'est garder un chiffre qu'on sait faux contre
    /// une mesure honnête qui ne conclut pas : elle est alors remplacée.</param>
    public CpuDeficitSoutenu? Remesurer(string machineId, bool ancienneSuspecte = false, string? reglageBios = null)
    {
        // 04/10/2026 : lue avant le banc, pour dire l'avant et l'après si le BIOS a changé depuis (vu sur la machine de référence : la
        // remesure au repos passait avant le scan et effaçait le gain de son geste).
        var ancienne = _store.Read(machineId) is { IsOk: true, Value: { } a } ? a : null;
        var mesuree = Mesurer(machineId);
        if (mesuree is null) return null;
        // Sans le réglage noté, le scan suivant croirait le BIOS changé et remesurerait pour rien.
        mesuree = mesuree with { ReglageBios = reglageBios };
        var verdict = Juger(mesuree);
        if (ancienne is { ReglageBios: { } avant, PuissanceSoutenueW: not null } && reglageBios is not null && avant != reglageBios)
            verdict = verdict with { Lisible = AvantApres(Juger(ancienne), ancienne, verdict, mesuree) + verdict.Lisible };
        // 25/09/2026 : une remesure faite sous une autre charge (±7,4 %, baisse de -7,5 % : la rafale elle-même
        // était freinée) remplaçait une bonne référence par une mesure qui ne conclut rien. Elle est dite, pas gardée.
        if (!ancienneSuspecte && !verdict.Demontrable && FacteurSeuil * mesuree.BruitPercent > SeuilConcluantPercent)
        {
            var date = verdict.Lisible.LastIndexOf(" Mesuré le ", StringComparison.Ordinal);
            const string gardee = " Ta mesure précédente est gardée.";
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

    /// <summary>Au-dessous de cette incertitude relative de la moyenne, la puissance est dite tenue. **NON CHIFFRÉ**.
    /// 25/09/2026 : c'etait l'ecart-type des releves ; il basculait autour de 10 % d'une mesure a l'autre
    /// (7 ; 12,8 ; 10,4 ; 7,9 %) pour une moyenne stable, d'ou l'incertitude de la moyenne (2 a 3 % sur ces
    /// memes mesures). Historique du seuil :
    /// sur la machine de reference, le banc soutenu tenait 63,4 W a 7 % pres (13 releves d'une
    /// seconde), en jeu entre 63,2 et 69,5 W. Une baisse de debit
    /// a puissance plate est la signature d'une limite de puissance, pas d'une chauffe, qui ferait
    /// varier la puissance avec la temperature.</summary>
    internal const double EcartPlatPercent = 10;

    private static string LimiteDePuissance(CpuBaseline b) =>
        b is { PuissanceSoutenueW: { } w, PuissanceEcartPercent: { } e } && e <= EcartPlatPercent
            ? $" Pendant cette charge, il tenait une moyenne de {w:0} W : c'est la marque d'une limite "
              + "de puissance longue réglée dans le BIOS, pas d'une surchauffe."
            : "";

    // 25/09/2026 : mesure fraîche sur la machine de reference, 63 W à 12,8 % près. Sans cette phrase, une puissance relevée
    // mais trop variable ne laissait rien à l'écran : ni le chiffre, ni pourquoi on ne conclut pas.
    private static string PuissanceNonConcluante(CpuBaseline b) =>
        b is { PuissanceSoutenueW: { } w, PuissanceEcartPercent: { } e } && e > EcartPlatPercent
            ? $" Il consommait {w:0} W en moyenne, connue à ±{e:0} % seulement : trop incertain pour dire d'où vient la baisse, "
              + "un réglage du BIOS ou la chaleur."
            : "";

    /// <summary>03/10/2026 (M2). Sous ce rapport entre la fréquence des cœurs en charge soutenue et celle d'un seul cœur,
    /// à puissance plate, la limite de puissance retient le processeur. **NON CHIFFRÉ** : l'écart naturel entre le turbo
    /// d'un cœur et celui de tous est de quelques pour cent (fiches Intel : 5,6 contre 5,4 GHz sur un i7-14700, 4,3
    /// contre 4,0 sur un i5-10400) ; 15 % le dépasse nettement. Mesure du 09/09 sur la machine de référence : 5 292 puis
    /// 3 698 MHz, soit 70 %.</summary>
    internal const double RapportBride = 0.85;

    /// <summary>La version du banc qui relève les fréquences (commit du 03/10/2026, 20 h 30 à Paris).</summary>
    internal static readonly DateTimeOffset BancAvecFrequence = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);

    private static bool BrideDesLeDepart(CpuBaseline b) =>
        b is { PuissanceSoutenueW: not null, PuissanceEcartPercent: { } e, FrequenceUnCoeurMhz: { } un, FrequenceSoutenueMhz: { } tous }
        && e <= EcartPlatPercent && un > 0 && tous / un < RapportBride;

    internal static CpuDeficitSoutenu Juger(CpuBaseline b) => JugerLaMesure(b) with { Reference = b };

    private static CpuDeficitSoutenu JugerLaMesure(CpuBaseline b)
    {
        // 25/09/2026 : le banc refuse deja un soutenu nul (sommes de controle), mais la reference se
        // relit d'un fichier que tout programme de l'utilisateur peut modifier : 0 y rendait 100 %.
        if (b.RafaleMops <= 0 || b.SoutenuMops <= 0)
            return new CpuDeficitSoutenu(0, b.BruitPercent, false,
                "la mesure en rafale ou soutenue est nulle : rien ne peut en être déduit");
        // 26/09/2026 : meme fichier, autre champ. Un bruit absent se relit 0 et le seuil de trois fois le
        // bruit tombait a 0 : le moindre ecart devenait demontrable. Le banc ne rend jamais un bruit nul.
        if (b.BruitPercent <= 0)
            return new CpuDeficitSoutenu(0, b.BruitPercent, false,
                "la précision de cette mesure n'a pas été enregistrée : rien ne peut en être déduit");

        var deficit = (b.RafaleMops - b.SoutenuMops) / b.RafaleMops * 100;
        var seuil = FacteurSeuil * b.BruitPercent;
        var limite = deficit > seuil && LimiteDePuissance(b).Length > 0;
        // 03/10/2026 (M2, machine réelle) : 66 W plats sous « Intel Default Settings », aucune baisse, et la page disait
        // « rien à faire ». Le processeur ne baisse pas parce qu'il est retenu dès le début de la charge.
        // Revue R1 : watts et fréquence se relèvent à partir de la 20e seconde ; ce qui dit « dès le début », c'est la rafale,
        // mesurée à froid, qui ne va pas plus vite. Une surchauffe laisserait la rafale froide plus rapide. Le BIOS n'est
        // pas affirmé : sur un portable, c'est le fabricant qui fixe cette limite.
        if (deficit <= seuil && seuil <= SeuilConcluantPercent && BrideDesLeDepart(b))
            return new CpuDeficitSoutenu(deficit, b.BruitPercent, false, LimiteDePuissance: true, BrideDesLeDepart: true,
                Lisible: "Ton processeur ne baisse pas quand la charge dure : à froid, il n'allait déjà pas plus vite. "
                    + F($"En charge longue, ses {b.Threads} cœurs tournent à {b.FrequenceSoutenueMhz:0} MHz contre {b.FrequenceUnCoeurMhz:0} MHz ")
                    + F($"quand un seul travaille, pour {b.PuissanceSoutenueW:0} W stables : c'est une limite de puissance atteinte dès le ")
                    + "début de la charge (fixée par le BIOS, ou par le fabricant sur un portable), pas une surchauffe."
                    + F($" Mesuré le {b.TakenUtc.ToLocalTime():dd/MM/yyyy}."));
        // 25/09/2026 : la reference se mesure une fois puis se relit (celle de la machine de reference date
        // du 11/09). Sans date, le rapport la presentait comme d'aujourd'hui.
        var quand = F($" Mesuré le {b.TakenUtc.ToLocalTime():dd/MM/yyyy}.");

        return new CpuDeficitSoutenu(deficit, b.BruitPercent, deficit > seuil, LimiteDePuissance: limite, Lisible: (
            deficit > seuil
                ? F($"Sous une charge longue sur {b.Threads} cœurs, ton processeur tient {deficit:0.0} % de puissance ")
                  + F($"en moins qu'au premier instant (précision ±{b.BruitPercent:0.0} %). Un test rapide ne voit pas ")
                  // 25/09/2026 : « un jeu charge rarement autant de cœurs : il en perd en général moins »
                  // n'avait aucune source. La mesure ne dit rien d'un jeu : elle le dit.
                  + "cette baisse. Elle charge tous ces cœurs sans pause : ce qu'un jeu en perd n'est pas mesuré ici."
                  + LimiteDePuissance(b)
                  + PuissanceNonConcluante(b)
                  // 25/09/2026 : la reference d'avant le releve de puissance ne peut pas dire si la baisse
                  // vient du BIOS ; sur la machine de reference, une mesure fraiche le disait (67 W plats).
                  + (b.PuissanceSoutenueW is null
                      ? " Cette mesure ne contient pas la puissance consommée : elle ne peut pas dire si la baisse "
                        + "vient d'une limite réglée dans le BIOS. « Remesurer ton processeur », dans le domaine "
                        + "Processeur, hors partie, le dira."
                      : "")
                : seuil > SeuilConcluantPercent
                    // 25/09/2026 : une remesure à ±14,8 % disait « garde sa puissance… aucune baisse au-delà de
                    // 44,5 % » ; une baisse comme celle qu'on mesure sur la machine de reference (19 à 22 %) y serait passée inaperçue.
                    ? F($"La mesure a trop varié (précision ±{b.BruitPercent:0.0} %) : une baisse de moins de {seuil:0} % ")
                      + "passerait inaperçue, elle ne peut donc pas dire si ton processeur garde sa puissance. "
                      + "Remesure-le au repos, sans autre programme lourd ouvert."
                    : $"Ton processeur garde sa puissance quand la charge dure : aucune baisse au-delà de "
                      + F($"{seuil:0.0} % (la précision de cette mesure). Cela ne vaut que si aucun autre programme ")
                      // 25/09/2026 : mesuré sous un banc lourd en parallèle, ce verdict sortait à tort (baisse réelle
                      // 19 à 22 % au repos) ; ni la charge totale, ni celle des cœurs du banc, ni la puissance du
                      // paquet ne séparaient le repos de cette charge sur la machine de référence. La condition est dite.
                      + "lourd ne tournait pendant la mesure.") + quand);
    }
}
