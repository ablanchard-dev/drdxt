using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DrDxt.Detection.Cpu;

/// <summary>Ce que le processeur fait reellement, par opposition a ce que WMI en dit.</summary>
/// <param name="FrequenceNominaleMhz">Le compteur `Processor Frequency` : ici la frequence de base, pas l'effective.</param>
/// <param name="PourcentageDeNominal">Sa part de la frequence nominale, en pourcent. Au-dessus de 100 = boost.</param>
/// <param name="PartDeLaLimite">Le compteur `% of Maximum Frequency`, en pourcent, pas une limite atteinte : au repos il
/// passe de 56 a 99 % en deux secondes (25/09/2026), une lecture instantanee n'est pas affichee.</param>
/// <param name="IndicateursDeLimite">Le masque `Performance Limit Flags`. <c>0</c> = aucun indicateur leve, <c>null</c>
/// = non lu. Il ne voit pas les limites de puissance du paquet (RAPL) : 0 mesure pendant une chute de 5292 a 3698 MHz
/// sous charge soutenue. Un 0 ne prouve pas que rien ne bride.</param>
public sealed record CpuFrequencyState(
    double? FrequenceNominaleMhz,
    double? PourcentageDeNominal,
    double? PartDeLaLimite,
    double? IndicateursDeLimite)
{
    /// <summary>
    /// La frequence effective, calculee : `Processor Frequency` rend la base, `% Processor Performance` porte le boost
    /// (lue seule : « 2100 MHz (198 % du nominal) »). <c>null</c> quand l'un des deux manque.
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
/// <param name="IndexLogique">L'index du processeur logique, ou <c>-1</c> si l'instance n'est pas decodee ou hors du
/// groupe 0 (l'index d'instance n'y vaut plus l'index global) : signale plutot que devine.</param>
/// <param name="PourcentageDeNominal">Sa part de la frequence nominale. Au-dessus de 100 = boost.</param>
/// <param name="UtilisationPourcent">Sa charge a la lecture : un cœur inactif tombe de lui-meme, et le moyenner donne le
/// chiffre trompeur de `_Total`.</param>
public sealed record CoeurFrequence(
    string Instance, int IndexLogique, double? PourcentageDeNominal, double? UtilisationPourcent);

/// <summary>
/// La frequence reelle du processeur, par les compteurs : les deux sources evidentes rendent le nominal.
/// `Win32_Processor` rend `MaxClockSpeed = CurrentClockSpeed = 2100` sur une puce qui monte a 5,4 GHz ;
/// `CallNtPowerInformation(ProcessorInformation)` rend `max=courant=limite=2100` pour les 28 logiques (depuis Speed
/// Shift/HWP, Windows ne pilote plus les P-states). Mesure par les compteurs : 3 630 MHz, 184,8 % du nominal.
/// Les noms de compteurs sont localises (« Informations sur le processeur » / « Fréquence réelle » ici ; un chemin PDH
/// anglais echoue). PerformanceCounter accepte le nom anglais sous toute langue, et le nom localise echoue quand le
/// format regional differe : l'anglais d'abord, le localise en repli, resolu par index entre `Perflib\009\Counter` et
/// `Perflib\CurrentLanguage\Counter`. Aucun index ni nom traduit en dur. Ne leve jamais : lecture ratee = <c>null</c>.
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

    /// <summary>Lit l'etat du processeur, en deux echantillons espaces : un compteur de taux rend 0 a la premiere
    /// lecture.</summary>
    /// <param name="attendre">L'attente entre l'amorce et la lecture, injectee : un test compte les attentes reelles, une
    /// boucle n'a qu'une ligne d'attente pour quatre attentes.</param>
    public CpuFrequencyState Lire(Action<int>? attendre = null)
    {
        var jeu = Nom(JeuAnglais);
        if (jeu is null) return new CpuFrequencyState(null, null, null, null);

        // Une seule attente pour les quatre compteurs : quatre attentes de 300 ms en serie coutaient 6 963 ms sur les
        // 7 111 ms d'un audit complet (98 %). L'amorce reste indispensable : un compteur de taux rend 0 la premiere fois.
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

    /// <summary>La frequence de chaque processeur logique, avec sa charge. `_Total` moyenne les 28 logiques, cœurs
    /// inactifs compris : mesure en partie le 09/09/2026, 3 622 MHz quand les 8 a 10 cœurs charges tournaient a 3 861 MHz. Une
    /// seule attente pour tous : 300 ms par compteur rendraient la lecture 28 fois trop longue.</summary>
    public IReadOnlyList<CoeurFrequence> LireParProcesseur()
    {
        var jeu = Nom(JeuAnglais);
        var nomPerf = Nom(CompteurPourcentage);
        var nomTemps = Nom(CompteurTemps);
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

    /// <summary>La fréquence du plus rapide des processeurs logiques donnés, pour une partie : la moyenne tombait à
    /// 2 270-3 123 MHz au repos sur la machine de référence, tirée par les cœurs inoccupés. Chaleur et limite de puissance
    /// baissent tous les cœurs, le plus rapide compris.</summary>
    public double? FrequenceMaxDe(IReadOnlyList<int> processeurs)
    {
        var nominale = _nominaleMhz ??= Lire().FrequenceNominaleMhz;
        if (nominale is not { } b || b <= 0) return null;
        var parts = LireParProcesseur()
            .Where(c => processeurs.Contains(c.IndexLogique) && c.PourcentageDeNominal is > 0)
            .Select(c => c.PourcentageDeNominal!.Value).ToList();
        return parts.Count == 0 ? null : b * parts.Max() / 100;
    }

    /// <summary>« 0,5 » → 5. -1 pour une autre forme ou un groupe autre que 0, ou l'index d'instance ne vaut plus l'index
    /// logique.</summary>
    private static int IndexDe(string instance)
    {
        var parts = instance.Split(',');
        if (parts.Length != 2) return int.TryParse(instance, out var seul) ? seul : -1;
        return int.TryParse(parts[0], out var groupe) && groupe == 0
               && int.TryParse(parts[1], out var idx) ? idx : -1;
    }

    /// <summary>L'intervalle entre l'amorce et la lecture, attendu une seule fois pour les quatre compteurs.</summary>
    private const int IntervalleDAmorceMs = 300;

    /// <summary>Ouvre un compteur du jeu, ou <c>null</c> s'il n'existe pas sur cette machine.</summary>
    private static PerformanceCounter? Ouvrir(string jeuLocalise, string compteurAnglais)
    {
        var compteur = Nom(compteurAnglais);
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
    // Cinq noms par `Lire()`, et `LireParProcesseur` en boucle pendant une partie : sans memoire, dix lectures de tables
    // de dizaines de milliers d'entrees pour des noms qui ne changent pas. Deux lectures au total, aucune au second scan.
    // Mesure en processus neuf : noms a froid 427 ms, premiere ouverture de compteur 4 543 ms (l'initialisation de
    // perflib, payee une fois quoi qu'on fasse ici), tout chaud 11,6 ms. Seules les lectures reussies sont retenues :
    // memoriser un `null` afficherait « frequence non lue » jusqu'au redemarrage apres un incident passager.
    private static readonly object _verrou = new();
    private static readonly Dictionary<string, string[]> _tables = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _nomsLocalises = new(StringComparer.Ordinal);

    /// <summary>D'ou viennent les tables de compteurs. Couture : le compte des lectures est prouvable, une duree
    /// dependrait de la machine.</summary>
    internal static Func<string, string[]?> SourceDesTables = TableDuRegistre;

    /// <summary>Oublie ce qui a ete retenu. Reservee aux tests.</summary>
    internal static void OublierLesTables()
    {
        lock (_verrou) { _tables.Clear(); _nomsLocalises.Clear(); _anglaisOuvrable = null; }
    }

    /// <summary>Le nom sous lequel ouvrir un jeu ou un compteur : l'anglais d'abord ; le localise echoue quand le format
    /// regional est d'une autre langue (« la categorie n'existe pas ») et reste le repli.</summary>
    private static string? Nom(string nomAnglais) => AnglaisOuvrable() ? nomAnglais : Localiser(nomAnglais);

    private static bool? _anglaisOuvrable;

    /// <summary>Couture : seule la réponse est retenue, jamais l'échec (même règle que les tables).</summary>
    internal static Func<bool> AnglaisExiste = () => PerformanceCounterCategory.Exists(JeuAnglais);

    internal static bool AnglaisOuvrable()
    {
        if (_anglaisOuvrable is { } connu) return connu;
        try { return (_anglaisOuvrable = AnglaisExiste()).Value; }
        catch { return false; }
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
