using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Ce qui est construit et que le produit n'appelle jamais.
///
/// ══ pourquoi ce controle existe ══
/// Ce defaut a ete paye deux FOIS dans la meme journee, le 09/09/2026. Le matin : dix types
/// construits en deux jours, zero reference par `DrDxt.App` ou `DrDxt.Application` — des lecteurs
/// orphelins branches dans un outil lui-meme orphelin. Le soir : sept familles de sante enfin
/// branchees, dont l'ecart d'alimentation qui n'atteignait toujours pas l'ecran, parce qu'un
/// constat sans geste est range parmi ceux qu'on ne montre jamais.
///
/// Le savoir n'a pas suffi la seconde fois. Il fallait que la faute devienne impossible a
/// reintroduire sans que quelque chose casse.
///
/// ══ ce qu'il mesure ══
/// Un type declare dans `src/` dont le nom n'apparait dans aucun autre fichier de `src/`, mais
/// apparait dans `tools/`. Autrement dit : du code que seule une ligne de commande atteint. Il
/// peut etre excellent — le banc CPU l'est — et il ne sert personne tant qu'aucun chemin produit
/// ne l'appelle.
///
/// ══ la prose ne compte plus comme un appel — corrige le 11/09/2026 ══
/// Ce controle a ete AVEUGLE par ses propres explications pendant deux jours. Il cherchait le
/// nom du type dans le texte BRUT des fichiers de `src/` : un type cite dans un COMMENTAIRE passait
/// donc pour appele. Cinq orphelins reels en profitaient, et le plus retourne est celui dont le
/// commentaire disait mot pour mot pourquoi il restait dans l'outil — `BiosQualificationMatrix`,
/// « reste dans l'outil : elle vit dans `DrDxt.Bios`, que ce projet ne reference pas ». Mieux la
/// raison etait ecrite, plus surement elle faisait taire le garde cense la reclamer.
/// ⇒ <see cref="CodeSeul"/> ampute commentaires, litteraux chaine et litteraux caractere avant de
/// chercher. Une chaine INTERPOLEE est gardee entiere : ses accolades portent du vrai code
/// (`$"...{GameObjective.Explication(but)}..."` est un appel), et garder sa prose ne peut rendre ce
/// controle qu'INDULGENT, jamais accusateur — c'est le bon sens de l'erreur pour un garde.
///
/// ══ ce qu'il ne mesure toujours pas, et c'est dit plutot que cache ══
/// La recherche se fait par NOM, pas par le compilateur. Elle ignore donc les classes d'extension
/// appelees par leur methode (`services.AddDetection()` ne cite jamais
/// `DetectionServiceCollectionExtensions`), et surtout **tout type qui ne circule qu'a travers un
/// `var`** : `new CpuTopology().Lire()` n'ecrit nulle part le nom du type rendu. Ce trou-la a
/// produit deux fausses accusations le 11/09 — `LogicalProcessor` et `CpuBenchResult`, toutes deux
/// corrigees en ECRIVANT le type au lieu de `var` au site d'appel. Le fermer demande un comptage
/// semantique (Roslyn, table de symboles), donc une dependance nouvelle dans ce projet de test :
/// geste a part entiere, pas un bricolage en passant.
/// **seuil de reouverture, chiffre plutot que laisse au jugement** : au TROISIEME faux positif
/// reel du trou `var` — un type que `src/` construit vraiment mais que ce controle accuse — la
/// reponse cesse d'etre « ecrire le type au site d'appel » et devient l'ajout de
/// `Microsoft.CodeAnalysis.CSharp` a ce seul `.csproj` de test plus un `SemanticModel` par fichier
/// de `src/`. Compteur au 11/09/2026 : **2 sur 3** (`LogicalProcessor`, `CpuBenchResult`). Elargir
/// encore <see cref="CodeSeul"/> ne compte pas comme reponse : le trou du `var` n'est pas une
/// question de texte, aucune amputation de prose ne fera apparaitre un nom qui n'est pas ecrit.
/// Un comptage voisin, « aucune reference hors tests », rend 78 types : il est MOINS fiable pour
/// ces raisons, et c'est pourquoi ce controle garde le comptage net plutot que le chiffre le plus
/// impressionnant.
///
/// ══ c'est un cliquet, pas une photo ══
/// La liste ne peut que RETRECIR. Ajouter un type non branche fait echouer le premier test ;
/// brancher un type de la liste fait echouer le second, qui exige alors qu'on le RETIRE. Les deux
/// echecs sont voulus : le premier empeche la dette de croitre, le second empeche la liste de
/// mentir sur l'etat reel.
/// </summary>
public class TheProductMayNotBuildWhatItNeverCallsTests
{
    /// <summary>Mesure du 09/09/2026. Chaque nom qui part d'ici est une victoire ; chaque nom qui
    /// s'y ajoute est une dette, et il faut alors dire pourquoi, ici meme, a cote du nom.
    ///
    /// **Mouvements du 09/09 au soir, et le cliquet les a tous les deux exiges :**
    /// • `CpuBench` et `ChargeCpu` SORTENT — `RealCpuSustainedBench` les appelle depuis `src`.
    /// • `CoeursHomogenes` et `DebitParCoeur` ENTRENT : la comparaison cœur par cœur coute huit
    ///   mesures successives, soit plusieurs minutes de charge, et n'a donc pas sa place dans un
    ///   scan. Elle appartient a la famille « a la demande », comme la reference CPU l'etait avant
    ///   d'etre cablee.
    ///
    /// **Mouvement du 10/09** : `ConditionsDeMesure`, `RepairChangement`, `RepairEtat` et
    /// `RepairHistoryStore` SORTENT — le pilier maintenir est cable, le produit garde enfin une
    /// photo et dit ce qui a change depuis la precedente. Puis `NetworkPathAnalyzer` (attribution
    /// reseau) et `CacheStaircase` (controle Q-05 : un escalier plat n'est pas une hierarchie).
    ///
    /// Et le cliquet a mordu apres un commit, pas avant : la suite d'integration n'avait pas
    /// ete relancee dans le tour qui a cable `CacheStaircase`. Verte ailleurs ne veut pas dire
    /// verte ici — c'est precisement le test qui parle de ce que les autres ne regardent pas.</summary>
    private static readonly HashSet<string> VusSeulementParUnOutil =
    [
        // 10/09/2026 (L8 a + b) : l'Optimiser de l'App appelle desormais CandidateSelector,
        // CatalogueDeCandidats, DiagnosticMachine, GameOptimizationProfile, GameOptimizationSession,
        // GameProfileIdentity et InstabiliteGpuObservee — 22 types geles, 16 restent.
        // 11/09/2026, DECISION ecrite plutot que redecouverte : `CalibrationVerdict` et
        // `NoOpCalibrationStore` RESTENT des outils. Le seul ecrivain du journal de controle est
        // `tools/optimize --controle` ; le produit n'a aucun candidat no-op, donc sur une machine
        // qui n'a jamais vu l'outil le fichier n'existe pas et le verdict vaudrait
        // `PasAssezDObservations` pour toujours, la meme phrase pour tout le monde. Et la
        // calibration juge une FORMULE, pas une decision : le produit annonce sa resolution
        // session par session (elle varie d'un facteur ~8 d'une partie a l'autre, `GameMetricSet`),
        // et le corpus no-op a deja atteint le produit — GELE en constantes le 08/09 par
        // `tools/tailbench --loco`. Se rouvre le jour ou le produit sait DEPOSER ses propres
        // controles.
        //
        // 11/09/2026, les cinq que la prose cachait. Ils ne sont pas neufs : ils etaient orphelins
        // depuis toujours et le balayage du texte brut les comptait « vus » parce que leur nom
        // apparaissait dans un commentaire de `src/`. Chacun est verifie : un seul `tools/*` l'appelle,
        // plus ses tests, et rien dans le code de `src/`.
        //
        // `AdlxGpuTuningWriter` — c'est la plume d'ecriture ADLX du BANC, pas celle du produit :
        // « harness-side ADLX write seam (Task 4) », appelee par `tools/amd-harness` seul. Le
        // produit a la sienne, `ADLXTuningActions` (ElevatedHelper), et **elle ecrit reellement le
        // GPU AMD** : l'App lance `DrDxt.ElevatedHelper.exe` a la premiere op privilegiee et lui
        // envoie l'op. Seul le reconciliateur de BOOT (LocalSystem) la remplace par
        // `DisabledAdlxActions` — un offset GPU est volatile, un service non surveille n'a pas a le
        // rejouer au demarrage —, et c'est la meme restriction cote NVIDIA (`DisabledNvApiActions`).
        // Corrige le 11/09 : ces lignes disaient « une seule plume est expediee », ce qui laissait
        // croire que rien n'ecrit le GPU AMD en production. Deux plumes sont expediees ; celle du
        // banc n'en fait pas partie, et c'est la seule raison de sa presence dans cette liste.
        "AdlxGpuTuningWriter",
        "CalibrationVerdict",
        "CpuFrequencyState",
        // 06/10/2026 (chantier J2) : `GainCumule` quitte la liste : la carte des parties de l'App compose les gains prouvés sur les vraies parties.
        // `GpuBenchSession` et `OcSweep` — le balayage d'offset GPU de `tools/octune`. `AutopilotEngine`
        // ecrit lui-meme la succession : « `OcSweep` avait deja cette philosophie et n'etait appele
        // que par `tools/octune` : il ne savait balayer qu'un offset GPU entier » — le moteur
        // generique du produit l'a remplace. Et la question GPU est close par la mesure, pas par un
        // avis : a `gpuWait` 0 %, un overclock GPU ne rend rien (hors perimetre).
        "GpuBenchSession",
        // `HostElevatedClientFactory` — le canal reconciliateur bas-privilege + courtier est retenu
        // comme durcissement **V2 documente**, explicitement pas le defaut V1 : l'hote de boot
        // utilise l'executeur en processus, sans aucun point d'entree privilegie ouvert. Un chemin
        // volontairement non pris n'est pas un branchement oublie.
        "HostElevatedClientFactory",
        // 10/09/2026 (branchement B1) : GameThreadPressure et ThreadCpuReader retires — le scan de l'App
        // observe les threads du jeu et le diagnostic les lit. 16 ⇒ 14.
        // 11/09/2026 : DpcLatencyTracer et DpcOutcome retires — le scan mesure les DPC/ISR en jeu. 13 ⇒ 11.
        //
        // 11/09/2026, DEPLACEMENTS et non branchements : `CoeursHomogenes`, `DebitParCoeur`,
        // `CpuBudgetIssue` et `CpuBudgetJuge` ont quitte `src/DrDxt.Measurement` pour `tools/coeurs`
        // et `tools/cpubench`, avec leurs tests. La raison de leur absence du produit reste celle
        // ecrite le 09/09 et rappelee plus haut — huit mesures successives pour la
        // comparaison cœur a cœur, une courbe de charge en rafale pour le budget : ni l'une ni
        // l'autre n'a sa place dans un scan. Ce qui change, c'est qu'elles ne se compilent plus
        // dans `DrDxt.Measurement.dll` expediee. Le perimetre de ce test etant `src/`, elles
        // disparaissent du comptage : les garder ici ferait reclamer des noms hors perimetre.
        // 10 ⇒ 6.
        // 06/10/2026 (chantier B14) : `MachineDeTest.Machine` est construit par JsonSerializer, et seulement dans
        // un build Debug (argument « machine=<nom> », pour dessiner les écrans d'une machine qu'on n'a pas). La version
        // livrée ne le compile pas : il n'a pas de chemin produit, par construction.
        "Machine",
        "NoOpCalibrationStore",
        "OcSweep",
        // 11/09/2026, DECISION ecrite : `PerformanceTimeline` et `TimelineSample` ne
        // sont pas de la famille « outil par nature » — la fusion sur base de temps commune est
        // ecrite pour la production. Elle n'est simplement pas branchable AUJOURD'HUI, et les deux
        // manques sont verifiables : (1) la capture de frames du produit demande 7 elements
        // PresentMon, aucun horodatage (`PM_METRIC_CPU_START_QPC` est declare dans l'interop et
        // jamais interroge), donc le produit a des DUREES de frame et jamais des INSTANTS ;
        // (2) aucune seconde serie horodatee n'existe pendant la meme fenetre — les sondes du scan
        // (threads, NIC, DPC) rendent des agregats sur 20 s. `IMonotonicTimestamps`, la couture
        // prevue pour ca, n'a ZERO implementation dans tout le depot, `tools/timeline` compris.
        // A rouvrir des qu'une de ces deux series existe ; pas avant, sinon c'est une tranche
        // verticale deguisee en branchement.
        "PerformanceTimeline",
        // 10/09/2026 : SceneRepresentativity retire — la regle de decidabilite du moteur l'appelle.
        // 10/09/2026 (B3) : SystemNicCounterSource retire — le scan mesure les pertes a l'interface. 14 ⇒ 13.
        //
        // 11/09/2026, `SceneRole` RESTE, et le tour ou il aurait ete facile de le retirer est celui
        // ou il fallait le laisser. `SceneContract` est desormais RELU par le produit : la session
        // dit ce que la scene n'autorise pas (`CeQuiManque` dans `SceneExplanation`, a l'ecran par
        // `ResultatOptimisation`). Mais l'App n'a toujours pas de scene DECLAREE — elle appelle
        // `Run` sans contrat, donc `Inconnue`, et le role n'est jamais atteint. Ecrire
        // `SceneRole.Decouverte` au site d'appel ferait taire ce test sans changer un seul
        // comportement : ce serait decorer le cliquet, pas brancher un type. Le vrai geste est de
        // DEMANDER au joueur ce qu'il jouait — une question d'interface, donc une decision produit.
        //
        // 11/09/2026 au soir : `SceneRole` RETIRE, et cette fois ce n'est pas decorer le cliquet.
        // Tranche : le joueur declare sa scene dans l'App (`ScanViewModel.SceneDeclaree`). Une
        // partie reelle declaree ATTEINT la branche du role, et l'ecran dit « trouve en scene de
        // decouverte : a revalider ». Le role reste `Decouverte` au site d'appel, et la raison est
        // ecrite la-bas (`ContratDeScene`) : le code ne sait pas prouver qu'une session REJOUE un
        // profil deja trouve, et une validation fabriquee serait le seul chemin vers un keep durable.
        // 12 ⇒ 11.
        // 11/09/2026 : `TraceOutcome` retire, et ce n'est pas un branchement — c'est un
        // DEPLACEMENT. `ThreadResidencyTracer` (inacheve, aucune donnee rendue depuis le 08/09) et
        // son enum n'ont jamais eu d'appelant hors `tools/cpuprobe` ; le fichier vit desormais
        // dans cet outil, donc le type ne se compile plus dans `DrDxt.Detection.dll` expediee.
        // Le perimetre de ce test etant `src/`, il disparait du comptage : le laisser dans la
        // liste gelee la ferait reclamer un nom qui n'existe plus. 11 ⇒ 10.
        "TimelineSample",
    ];

    /// <summary>`ref\s+` et `(?:record\s+)?` ajoutes le 11/09/2026 : sans eux, **21 types**
    /// declares `record struct` / `readonly record struct` / `record class` etaient invisibles a ce
    /// controle — jamais declares, donc jamais accusables. Aucun des 21 ne s'est revele orphelin,
    /// mais un inventaire qui ne voit pas une forme de declaration ne peut pas le prouver.</summary>
    private static readonly Regex Declaration = new(
        @"^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+|ref\s+)*"
        + @"(?:record\s+)?(?:class|record|interface|enum|struct)\s+([A-Z][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Un point d'entree n'est appele par personne par son nom : c'est le runtime qui le
    /// lance. `DrDxt.ElevatedHelper.Program` tombait dans l'accusation des que sa prose cessait de
    /// compter — meme famille que les classes d'extension, et l'inscrire dans la liste gelee aurait
    /// ete un mensonge : il est expedie et il s'execute.</summary>
    private static readonly Regex PointDEntree = new(
        @"\bstatic\s+(?:async\s+)?[A-Za-z0-9_<>\[\]]+\s+Main\s*\(", RegexOptions.Compiled);

    /// <summary>Le fichier ampute de ce qui n'est pas du code : commentaires, litteraux chaine,
    /// litteraux caractere. La chaine INTERPOLEE est gardee entiere — ses accolades portent du vrai
    /// code. Un litteral brut (trois guillemets) ferait mentir ce balayage : il n'en existe aucun
    /// dans `src/`, et ce controle refuse plutot que de lire de travers.</summary>
    private static string CodeSeul(string texte, string fichier)
    {
        if (texte.Contains("\"\"\"", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "litteral brut (trois guillemets) dans " + fichier + " : CodeSeul ne sait pas le "
                + "lire, et lire de travers serait pire que refuser. L'etendre avant d'en ecrire un.");

        var sortie = new StringBuilder(texte.Length);
        var i = 0;
        while (i < texte.Length)
        {
            var c = texte[i];
            if (c == '/' && i + 1 < texte.Length && texte[i + 1] == '/')
            {
                var fin = texte.IndexOf('\n', i);
                i = fin < 0 ? texte.Length : fin;
            }
            else if (c == '/' && i + 1 < texte.Length && texte[i + 1] == '*')
            {
                var fin = texte.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = fin < 0 ? texte.Length : fin + 2;
            }
            else if (c == '"')
            {
                var avant1 = i > 0 ? texte[i - 1] : ' ';
                var avant2 = i > 1 ? texte[i - 2] : ' ';
                var interpole = avant1 == '$' || avant2 == '$';
                var verbatim = avant1 == '@' || avant2 == '@';
                var debut = i;
                i++;
                while (i < texte.Length)
                {
                    if (verbatim)
                    {
                        if (texte[i] != '"') { i++; continue; }
                        if (i + 1 < texte.Length && texte[i + 1] == '"') { i += 2; continue; }
                        i++;
                        break;
                    }

                    if (texte[i] == '\\') { i += 2; continue; }
                    if (texte[i] == '"') { i++; break; }
                    if (texte[i] == '\n') break;
                    i++;
                }

                if (interpole) sortie.Append(texte, debut, i - debut);
                else sortie.Append(' ');
            }
            else if (c == '\'')
            {
                i++;
                while (i < texte.Length)
                {
                    if (texte[i] == '\\') { i += 2; continue; }
                    if (texte[i] == '\'') { i++; break; }
                    i++;
                }

                sortie.Append(' ');
            }
            else
            {
                sortie.Append(c);
                i++;
            }
        }

        return sortie.ToString();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Fichiers(string racine, params string[] extensions)
        => Directory.Exists(racine)
            ? FichiersDuDepot.Sous(racine, "*.*")
                .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            : [];

    /// <summary>Les types de `src/` que seul un outil atteint, mesures maintenant.</summary>
    private static HashSet<string> Mesurer()
    {
        var racine = RepoRoot();
        var declarationPar = new Dictionary<string, string>();
        var textes = new Dictionary<string, string>();
        var pointsDEntree = new HashSet<string>();

        foreach (var f in Fichiers(Path.Combine(racine, "src"), ".cs", ".xaml"))
        {
            var texte = File.ReadAllText(f);
            if (!f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // Le XAML n'est pas du C# : pas de commentaires a amputer ici, une reference y est
                // un nom de type dans un attribut ou une balise.
                textes[f] = texte;
                continue;
            }

            // Les DECLARATIONS se lisent sur le texte brut : amputer une chaine verbatim souderait
            // deux lignes et ferait perdre la declaration suivante. Les REFERENCES, elles, ne se
            // cherchent plus que dans le code.
            textes[f] = CodeSeul(texte, f);
            if (PointDEntree.IsMatch(textes[f])) pointsDEntree.Add(f);
            foreach (Match m in Declaration.Matches(texte))
                declarationPar.TryAdd(m.Groups[1].Value, f);
        }

        var outils = Fichiers(Path.Combine(racine, "tools"), ".cs")
            .ToDictionary(f => f, File.ReadAllText);

        var seuls = new HashSet<string>();
        foreach (var (nom, declare) in declarationPar)
        {
            if (pointsDEntree.Contains(declare)) continue;
            var mot = new Regex($@"\b{Regex.Escape(nom)}\b", RegexOptions.Compiled);
            var vuDansSrc = textes.Any(kv => kv.Key != declare && mot.IsMatch(kv.Value));
            if (vuDansSrc) continue;
            if (outils.Any(kv => mot.IsMatch(kv.Value))) seuls.Add(nom);
        }

        return seuls;
    }

    [Fact]
    public void AUCUN_TYPE_NON_BRANCHE_NE_S_AJOUTE_A_LA_DETTE()
    {
        var nouveaux = Mesurer().Except(VusSeulementParUnOutil).OrderBy(n => n).ToList();

        nouveaux.Should().BeEmpty(
            "ces types sont construits et seul un outil les appelle — le produit ne les voit pas. "
            + "Soit un chemin produit les atteint, soit la raison de leur absence est ecrite dans "
            + "ce fichier, a cote de la liste gelee, et le nom la rejoint. "
            + "Nouveaux : " + string.Join(", ", nouveaux));
    }

    [Fact]
    public void UN_TYPE_ENFIN_BRANCHE_DOIT_QUITTER_LA_LISTE()
    {
        // Cet echec est une BONNE nouvelle, et il est voulu : sans lui la liste gelee resterait
        // vraie pour toujours et cesserait de decrire le depot. Un cliquet qui ne serre jamais est
        // une decoration.
        var partis = VusSeulementParUnOutil.Except(Mesurer()).OrderBy(n => n).ToList();

        partis.Should().BeEmpty(
            "ces types ne sont plus orphelins — soit ils sont branches, soit ils ont ete supprimes. "
            + "Retire-les de la liste de ce test pour que le cliquet serre d'un cran. "
            + "A retirer : " + string.Join(", ", partis));
    }
}
