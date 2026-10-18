using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DrDxt.Detection.Cpu;

/// <summary>Ce que le processeur fait reellement, par opposition a ce que WMI en dit.</summary>
/// <param name="FrequenceNominaleMhz">Ce que le compteur `Processor Frequency` rend. Sur cette
/// plateforme c'est la frequence de base, pas l'effective : il ne suffit donc pas.</param>
/// <param name="PourcentageDeNominal">Sa part de la frequence nominale, en pourcent. Au-dessus de
/// 100 = la machine boost.</param>
/// <param name="PartDeLaLimite">Le compteur `% of Maximum Frequency`, en pourcent. Ce n'est pas une
/// limite atteinte : au repos, il passe de 56 a 99 % en deux secondes (mesure du 25/09/2026). Une
/// lecture instantanee n'est donc pas affichee.</param>
/// <param name="IndicateursDeLimite">Le masque des raisons de bridage que Windows expose
/// (`Performance Limit Flags`). <c>0</c> = aucun indicateur Windows leve, une vraie valeur, a ne
/// pas confondre avec <c>null</c> = non lu. Mais ce compteur ne voit pas les limites de puissance
/// du paquet (RAPL) : mesure sur cette machine, il vaut 0 pendant que la frequence tombe de 5292 a
/// 3698 MHz sous charge soutenue. Un 0 ne prouve donc pas que rien ne bride.</param>
public sealed record CpuFrequencyState(
    double? FrequenceNominaleMhz,
    double? PourcentageDeNominal,
    double? PartDeLaLimite,
    double? IndicateursDeLimite)
{
    /// <summary>
    /// La frequence effective, calculee, et non lue directement.
    ///
    /// Lue seule, elle donnerait « 2100 MHz (198 % du nominal) », deux chiffres qui se
    /// contredisent dans la meme ligne. Le compteur `Processor Frequency` rend la frequence de
    /// base sur cette plateforme ; c'est `% Processor Performance` qui porte le boost. La
    /// frequence effective est donc un produit, jamais une lecture unique.
    ///
    /// <c>null</c> quand l'un des deux manque : une moitie de la formule ne fait pas un resultat.
    /// </summary>
    public double? FrequenceEffectiveMhz =>
        FrequenceNominaleMhz is { } b && b > 0 && PourcentageDeNominal is { } p
            ? b * p / 100.0
            : null;

    /// <summary>Le processeur est-il bride ? <c>null</c> quand on n'a pas lu.</summary>
    public bool? EstBride => IndicateursDeLimite is { } f ? f != 0 : null;
}

/// <summary>La frequence d'un seul processeur logique, et ce qu'il faisait au moment de la lecture.</summary>
/// <param name="Instance">Le nom de l'instance du compteur, « groupe,processeur ».</param>
/// <param name="IndexLogique">L'index du processeur logique, ou <c>-1</c> si l'instance n'a pas
/// pu etre decodee. Sur une machine a plusieurs groupes de processeurs, l'index d'instance
/// n'est pas l'index logique global ; cette machine n'en a qu'un, et le cas est signale plutot
/// que devine.</param>
/// <param name="PourcentageDeNominal">Sa part de la frequence nominale. Au-dessus de 100 = boost.</param>
/// <param name="UtilisationPourcent">Sa charge au moment de la lecture. Sans elle la frequence
/// ne veut rien dire : un cœur inactif tombe de lui-meme, et le moyenner avec les autres donne
/// exactement le chiffre trompeur que rend l'instance `_Total`.</param>
public sealed record CoeurFrequence(
    string Instance, int IndexLogique, double? PourcentageDeNominal, double? UtilisationPourcent);

/// <summary>
/// La frequence reelle du processeur, et pourquoi les deux sources evidentes mentent.
///
/// Deux approches essayees et rejetees sur cette machine :
///
///   1. `Win32_Processor` rend `MaxClockSpeed = 2100` et `CurrentClockSpeed = 2100`. C'est la
///      frequence de base, et `Current` la recopie. Un outil qui s'y fie conclut « CPU a
///      2100/2100, aucun bridage » sur une puce qui monte a 5,4 GHz.
///   2. `CallNtPowerInformation(ProcessorInformation)` reussit (code 0) et rend
///      `max=2100 courant=2100 limite=2100` pour les 28 processeurs logiques. Depuis Speed Shift
///      (HWP), Windows ne pilote plus les P-states et rapporte le nominal.
///
/// Mesure reelle par les compteurs : **3 630 MHz**, soit 184,8 % du nominal. Les deux sources
/// evidentes se trompaient d'un facteur 1,7.
///
/// ══ pourquoi la resolution par index ══
/// Les noms de compteurs sont localises. Sur ce Windows francais, le jeu s'appelle
/// « Informations sur le processeur » et le compteur « Fréquence réelle » : la requete anglaise
/// echoue avec « L'objet spécifié n'a pas été trouvé ». Coder les noms francais marcherait ici et
/// nulle part ailleurs.
///
/// Windows publie la table : `Perflib\009\Counter` porte les paires index/nom en anglais,
/// `Perflib\CurrentLanguage\Counter` les memes index dans la langue du systeme. On cherche donc
/// l'index par le nom anglais (stable) et on lit le nom localise. Aucun index code en dur,
/// aucun nom traduit a la main.
///
/// Échec propre : ne leve jamais ; toute lecture ratee rend <c>null</c>, jamais zero.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CpuFrequencyReader
{
    private const string JeuAnglais = "Processor Information";
    private const string CompteurFrequence = "Processor Frequency";
    private const string CompteurPourcentage = "% Processor Performance";
    private const string CompteurLimite = "% of Maximum Frequency";
    private const string CompteurIndicateurs = "Performance Limit Flags";
    private const string CompteurTemps = "% Processor Time";

    private const string CheminPerflib = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib";

    /// <summary>Lit l'etat du processeur. Deux echantillons espaces sont necessaires : un compteur
    /// de taux rend 0 a la premiere lecture, et prendre ce 0 pour une frequence nulle serait la
    /// meme faute que de croire WMI.</summary>
    /// <param name="attendre">L'attente entre l'amorce et la lecture. Injectee comme le reste
    /// des seams de ce depot : sans elle, un test ne peut compter que les lignes `Thread.Sleep`,
    /// et une boucle n'en contient qu'une tout en attendant quatre fois. Ce crible textuel
    /// laisserait passer la mutation qu'il doit attraper.</param>
    public CpuFrequencyState Lire(Action<int>? attendre = null)
    {
        var jeu = Localiser(JeuAnglais);
        if (jeu is null) return new CpuFrequencyState(null, null, null, null);

        // Une seule attente pour les quatre compteurs.
        //
        // Cette methode appelait `Mesurer` quatre fois, et chaque appel ouvrait son compteur,
        // l'amorcait, dormait 300 ms, puis lisait. Quatre attentes en serie pour quatre compteurs
        // du meme jeu. Mesure du jour, chronometre lecteur par lecteur : 6 963 ms sur les
        // 7 111 ms de l'audit complet, soit 98 % : c'est le scan du client qui attendait.
        //
        // La lecon etait deja ecrite a deux methodes d'ici, dans le resume de `LireParProcesseur` :
        // « Un seul intervalle d'attente pour tous les compteurs : les amorcer un par un avec
        // 300 ms chacun ferait durer une lecture 28 fois trop longtemps. » Une surface
        // l'appliquait, l'autre non.
        //
        // L'amorce reste indispensable : un compteur de taux rend 0 a la premiere lecture, et
        // prendre ce 0 pour une frequence nulle serait la meme faute que de croire WMI.
        var compteurs = new[] { CompteurFrequence, CompteurPourcentage, CompteurLimite, CompteurIndicateurs }
            .Select(nom => Ouvrir(jeu, nom))
            .ToArray();

        try
        {
            foreach (var c in compteurs) TenterAmorce(c);
            if (compteurs.Any(c => c is not null)) (attendre ?? Thread.Sleep)(IntervalleDAmorceMs);

            return new CpuFrequencyState(
                Relire(compteurs[0]),   // la base sur cette plateforme
                Relire(compteurs[1]),
                Relire(compteurs[2]),
                Relire(compteurs[3]));
        }
        finally
        {
            foreach (var c in compteurs) c?.Dispose();
        }
    }

    /// <summary>La frequence de chaque processeur logique, avec sa charge.
    ///
    /// Pourquoi cette methode existe. <see cref="Lire"/> interroge l'instance `_Total`, qui est
    /// la moyenne des 28 processeurs logiques de cette machine, cœurs inactifs compris. Mesure du
    /// 09/09/2026 pendant une partie : `_Total` rendait 3 622 MHz quand les 8 a 10 cœurs reellement
    /// charges tournaient a 3 861 MHz. La moyenne repond a une question que personne ne pose, et
    /// elle repond a cote de celle qui compte : « a quelle frequence tourne le cœur qui travaille ? »
    ///
    /// Un seul intervalle d'attente pour tous les compteurs : les amorcer un par un avec 300 ms
    /// chacun ferait durer une lecture 28 fois trop longtemps pour une boucle de surveillance.</summary>
    public IReadOnlyList<CoeurFrequence> LireParProcesseur()
    {
        var jeu = Localiser(JeuAnglais);
        var nomPerf = Localiser(CompteurPourcentage);
        var nomTemps = Localiser(CompteurTemps);
        if (jeu is null || nomPerf is null) return Array.Empty<CoeurFrequence>();

        try
        {
            var categorie = new PerformanceCounterCategory(jeu);
            var instances = categorie.GetInstanceNames()
                .Where(n => !n.Contains('_'))       // « _Total » et compagnie ne sont pas des cœurs
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            if (instances.Length == 0) return Array.Empty<CoeurFrequence>();

            var perf = new List<PerformanceCounter>();
            var temps = new List<PerformanceCounter?>();
            try
            {
                foreach (var inst in instances)
                {
                    var cp = new PerformanceCounter(jeu, nomPerf, inst, readOnly: true);
                    cp.NextValue();
                    perf.Add(cp);

                    PerformanceCounter? ct = null;
                    if (nomTemps is not null)
                    {
                        try { ct = new PerformanceCounter(jeu, nomTemps, inst, readOnly: true); ct.NextValue(); }
                        catch { ct = null; }
                    }
                    temps.Add(ct);
                }

                Thread.Sleep(300);   // une seule attente pour tout le monde

                var resultat = new List<CoeurFrequence>(instances.Length);
                for (var i = 0; i < instances.Length; i++)
                {
                    double? p = null, u = null;
                    // Un compteur illisible reste null pour ce cœur : non lu, jamais zéro.
                    try { p = perf[i].NextValue(); } catch { }
                    try { u = temps[i]?.NextValue(); } catch { }
                    resultat.Add(new CoeurFrequence(instances[i], IndexDe(instances[i]), p, u));
                }
                return resultat;
            }
            finally
            {
                foreach (var c in perf) c.Dispose();
                foreach (var c in temps) c?.Dispose();
            }
        }
        catch { return Array.Empty<CoeurFrequence>(); }
    }

    private double? _nominaleMhz;

    /// <summary>La fréquence effective moyenne des processeurs logiques donnés (groupe 0), pour le banc
    /// processeur ; <c>null</c> si l'un des compteurs manque. La fréquence nominale ne change pas : lue une fois.</summary>
    public double? FrequenceDe(IReadOnlyList<int> processeurs)
    {
        var nominale = _nominaleMhz ??= Lire().FrequenceNominaleMhz;
        if (nominale is not { } b || b <= 0) return null;
        var parts = LireParProcesseur()
            .Where(c => processeurs.Contains(c.IndexLogique) && c.PourcentageDeNominal is > 0)
            .Select(c => c.PourcentageDeNominal!.Value).ToList();
        return parts.Count == 0 ? null : b * parts.Average() / 100;
    }

    /// <summary>La fréquence du plus rapide des processeurs logiques donnés, pour une partie. La
    /// moyenne ne convient pas en jeu : relevée au repos sur la machine de référence, elle tombait à 2 270-3 123 MHz, les
    /// cœurs que le jeu n'occupe pas la tirant vers le bas. La chaleur et la limite de puissance baissent tous les cœurs,
    /// le plus rapide compris.</summary>
    public double? FrequenceMaxDe(IReadOnlyList<int> processeurs)
    {
        var nominale = _nominaleMhz ??= Lire().FrequenceNominaleMhz;
        if (nominale is not { } b || b <= 0) return null;
        var parts = LireParProcesseur()
            .Where(c => processeurs.Contains(c.IndexLogique) && c.PourcentageDeNominal is > 0)
            .Select(c => c.PourcentageDeNominal!.Value).ToList();
        return parts.Count == 0 ? null : b * parts.Max() / 100;
    }

    /// <summary>« 0,5 » → 5. Rend -1 quand l'instance n'a pas cette forme, ou quand le groupe
    /// n'est pas 0 : hors du groupe 0 l'index d'instance ne vaut plus l'index logique, et deviner
    /// serait pire que se taire.</summary>
    private static int IndexDe(string instance)
    {
        var parts = instance.Split(',');
        if (parts.Length != 2) return int.TryParse(instance, out var seul) ? seul : -1;
        return int.TryParse(parts[0], out var groupe) && groupe == 0
               && int.TryParse(parts[1], out var idx) ? idx : -1;
    }

    /// <summary>L'intervalle entre l'amorce et la lecture, non chiffre : 300 ms, attendus une seule fois
    /// pour les quatre compteurs au lieu de quatre fois de suite.</summary>
    private const int IntervalleDAmorceMs = 300;

    /// <summary>Ouvre un compteur du jeu, ou <c>null</c> s'il n'existe pas sur cette machine.</summary>
    private static PerformanceCounter? Ouvrir(string jeuLocalise, string compteurAnglais)
    {
        var compteur = Localiser(compteurAnglais);
        if (compteur is null) return null;
        try { return new PerformanceCounter(jeuLocalise, compteur, "_Total", readOnly: true); }
        catch { return null; }
    }

    /// <summary>Premiere lecture, jetee : un compteur de taux rend 0 au premier appel.</summary>
    private static void TenterAmorce(PerformanceCounter? c)
    {
        if (c is null) return;
        try { c.NextValue(); } catch { /* un compteur illisible reste null a la relecture */ }
    }

    private static double? Relire(PerformanceCounter? c)
    {
        if (c is null) return null;
        try { return c.NextValue(); }
        catch { return null; }
    }

    // ══ la table se lit une fois, pas une fois par nom ══
    //
    // `Lire()` demande cinq noms (le jeu, puis quatre compteurs) ; si chaque demande lisait les
    // deux tables completes du registre, puis les balayait l'une dans l'autre, ce seraient dix lectures de
    // dizaines de milliers d'entrees pour resoudre cinq noms qui ne changent jamais de la vie
    // du processus. Et `LireParProcesseur`, la boucle de surveillance appelee en continu
    // pendant une partie, referait le meme travail a chaque tour.
    //
    // Ce que cette correction n'est pas : elle n'explique pas les 5 483 ms que le chronometre impute a ce
    // lecteur (97 % d'un audit). Mesure : le total tombe a 4 996 ms, soit **490 ms** gagnes, pas 5 200.
    // L'attribution, mesuree en trois phases dans un processus neuf :
    //
    //     1. cinq noms, tables froides       427 ms    <- ce que ce correctif divise
    //     2. Lire(), noms deja resolus     4 543 ms    <- la premiere ouverture de compteur
    //     3. Lire(), tout chaud             11,6 ms
    //
    // Le plateau de temps restant est donc nomme, et il n'est pas ici :
    // c'est l'initialisation de perflib au premier `new PerformanceCounter` du processus, qui
    // se paie une fois quoi qu'on fasse dans cette classe. Ce qui reste vrai de ce correctif :
    // dix lectures de registre deviennent deux, et un second scan n'en fait plus aucune.
    //
    // Un echec n'est pas une reponse : seules les lectures reussies sont retenues. Memoriser
    // un `null` condamnerait le lecteur pour toute la duree du processus sur un incident
    // passager : l'application afficherait « frequence non lue » jusqu'au redemarrage.
    private static readonly object _verrou = new();
    private static readonly Dictionary<string, string[]> _tables = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _nomsLocalises = new(StringComparer.Ordinal);

    /// <summary>D'ou viennent les tables de compteurs. Couture : elle rend le compte des
    /// lectures observable, seule chose prouvable ici : une garde de duree dependrait de la
    /// machine et de la charge.</summary>
    internal static Func<string, string[]?> SourceDesTables = TableDuRegistre;

    /// <summary>Oublie ce qui a ete retenu. Reservee aux tests : en production ces noms ne
    /// changent pas de la vie du processus.</summary>
    internal static void OublierLesTables()
    {
        lock (_verrou) { _tables.Clear(); _nomsLocalises.Clear(); }
    }

    /// <summary>Le nom localise d'un jeu ou d'un compteur, a partir de son nom anglais.
    /// <c>null</c> quand la table n'est pas lisible ou que le nom n'y figure pas.</summary>
    public static string? Localiser(string nomAnglais)
    {
        lock (_verrou)
            if (_nomsLocalises.TryGetValue(nomAnglais, out var retenu)) return retenu;

        var trouve = Resoudre(nomAnglais);
        if (trouve is not null)
            lock (_verrou) _nomsLocalises[nomAnglais] = trouve;
        return trouve;
    }

    private static string? Resoudre(string nomAnglais)
    {
        try
        {
            var anglais = Table("009");
            var locale = Table("CurrentLanguage");
            if (anglais is null || locale is null) return null;

            // La table est une suite de paires : index, nom, index, nom…
            for (var i = 0; i + 1 < anglais.Length; i += 2)
            {
                if (!string.Equals(anglais[i + 1], nomAnglais, StringComparison.OrdinalIgnoreCase))
                    continue;

                for (var j = 0; j + 1 < locale.Length; j += 2)
                    if (locale[j] == anglais[i])
                        return locale[j + 1];
            }
            return null;
        }
        catch { return null; }
    }

    private static string[]? Table(string langue)
    {
        lock (_verrou)
            if (_tables.TryGetValue(langue, out var retenue)) return retenue;

        var lue = SourceDesTables(langue);
        if (lue is not null)
            lock (_verrou) _tables[langue] = lue;
        return lue;
    }

    private static string[]? TableDuRegistre(string langue)
    {
        using var cle = Registry.LocalMachine.OpenSubKey($@"{CheminPerflib}\{langue}");
        return cle?.GetValue("Counter") as string[];
    }
}
