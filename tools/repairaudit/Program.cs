using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Bios;
using DrDxt.Detection.Bios;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using DrDxt.Infrastructure.Repair;
using DrDxt.Measurement;
using DrDxt.Detection.Network;
using DrDxt.Measurement.Memory;
using DrDxt.Measurement.Network;

// ============================================================================================
// AUDITER LES DEGATS D'OPTIMISATION.
//
// LECTURE PURE DE LA MACHINE : cet outil ne modifie aucun etat systeme, ne demande aucune
// elevation, ne repare rien.
//
// CORRIGE LE 09/09/2026 : cette ligne disait « n'ecrit rien », et c'est devenu FAUX le jour ou
// le journal des constats est arrive. Il ECRIT un fichier — `%LOCALAPPDATA%\DrDXT\
// repair-history.json` — a cote de `memory-baseline.json`. C'est l'etat du produit, pas un
// reglage de la machine, mais la nuance devait etre ecrite plutot que sous-entendue : une
// affirmation d'innocuite qui vieillit mal est exactement ce que ce depot traque ailleurs.
//
// LA REGLE QUI GOUVERNE TOUT CE FICHIER : « different » n'est pas « endommage ». Sur cette
// machine, quatre etats non-default ont ete lus le 08/09/2026 — plan Equilibre, aucun coeur
// parque, `LowLatencyMode` absent, 1728x1080 sur un ecran 1920x1080. **Aucun des quatre n'est un
// defaut.** Un outil qui « repare » ca casse le PC de quelqu'un en croyant l'aider.
//
// Chaque constat porte donc son STATUT (voir `RepairStatus`) et le type interdit par construction
// une reparation automatique sur autre chose qu'un etat connu comme nuisible.
//
//   dotnet run --project tools/repairaudit
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;
var constats = new List<RepairFinding>();

// ── POWER : le parking de coeurs, seule lecture de cette famille qui reste ici ──────────
// Le plan actif et ses sous-valeurs processeur sont rendus par `RepairAuditService` (pure
// lecture). Le parking, lui, est un ECHANTILLONNAGE de 3 s : il reste dans l'outil.

// LE PARKING EST UN ETAT TRANSITOIRE, PAS UNE CONFIGURATION — et le produit l'ignorait.
//
// Mesure du 08/09/2026, trois lectures consecutives a quelques secondes d'ecart sur cette
// machine : « 19, 20 », puis « 20 », puis « 19, 20 ». Et pendant que Warzone tournait :
// « aucun ». Windows parque et deparque en permanence selon la charge.
//
// `SanePowerPlanTweak.AppliesTo` decide pourtant sur UNE lecture instantanee. Son applicabilite
// depend donc de la seconde ou on l'interroge — ce qui n'est pas une condition, c'est un tirage.
// Ici on echantillonne et on rend la FREQUENCE d'observation, jamais un instantane.
var registre = new RegistryProvider();
var echantillons = new List<IReadOnlyList<int>?>();
for (var i = 0; i < 12; i++)
{
    echantillons.Add(registre.GetParkedCoreIndexes());
    Thread.Sleep(250);
}

var lus = echantillons.OfType<IReadOnlyList<int>>().ToList();
if (lus.Count == 0)
{
    constats.Add(new RepairFinding("POWER", "parking de coeurs", null, null, RepairStatus.Unknown,
        "l'API CPU Sets n'a jamais repondu — ce n'est PAS « aucun coeur parque »"));
}
else
{
    var avecParking = lus.Count(x => x.Count > 0);
    var vus = lus.SelectMany(x => x).Distinct().OrderBy(x => x).ToList();
    constats.Add(new RepairFinding("POWER", "parking de coeurs",
        vus.Count == 0
            ? $"aucun sur {lus.Count} lectures"
            : $"{avecParking}/{lus.Count} lectures — coeurs {string.Join(", ", vus)}",
        null,
        // Ni ExpectedDefault ni NonDefault : c'est une OBSERVATION d'un etat qui bouge. Le
        // classer comme un ecart de configuration serait deja une erreur d'interpretation.
        RepairStatus.Observed,
        vus.Count == 0
            ? "aucun coeur parque pendant l'echantillonnage — mais l'absence a un instant n'est "
              + "pas une propriete de la machine"
            : "Windows parque et deparque selon la charge : c'est le comportement NORMAL du plan "
              + "Equilibre, pas un degat. Seule une micro-saccade MESUREE justifierait d'y toucher"));
}

// ── LES FAMILLES DE PURE LECTURE, VIA LE SERVICE PARTAGE ──────────────────────────────
//
// Elles vivaient ICI, dans cet outil, et NULLE PART ailleurs. Mesure du 09/09/2026 : dix types
// construits en deux jours, **zero** reference par `DrDxt.App` ou `DrDxt.Application`. Je
// branchais des lecteurs orphelins dans un outil lui-meme orphelin — un outil prouve qu'un
// lecteur fonctionne, il ne met rien entre les mains de quelqu'un.
//
// POWER (plan + sous-valeurs) · CPU · SERVICES · MMCSS · type de vidage · BCD · incidents noyau ·
// DISPLAY · GPU DRIVER · PAGEFILE · BIOS (identite + interface constructeur) · THERMIQUE
// viennent desormais de `RepairAuditService`, que l'application consomme telle quelle. L'outil
// garde ce qui exige une MESURE qui dure (parking, memoire, reseau) et ce qui depend de
// `DrDxt.Bios` (la matrice de qualification) : les melanger imposerait ce cout a une interface
// qui veut afficher un etat tout de suite.
constats.AddRange(new RepairAuditService().Lire());

double? dispersionDebit = null;

// ── MEMOIRE ─────────────────────────────────────────────────────────────────────────────
var modules = new MemoryDetector(new ManagementWmiQuery()).Detect();
if (!modules.IsOk || modules.Value is not { Count: > 0 } barrettes)
{
    // DEUX SITUATIONS TRES DIFFERENTES TENAIENT DANS LA MEME PHRASE. Le message rendait
    // « SMBIOS n'a rien rendu ({Error?.Code}) » — et quand la requete REUSSIT en renvoyant zero
    // ligne, `Error` est nul : la phrase s'affichait avec des parentheses vides que personne ne
    // remarquerait. Or une exception WMI et une reponse vide n'appellent pas le meme remede : la
    // premiere est typiquement transitoire et se reessaie, la seconde dit que le fournisseur a
    // repondu et n'a rien a dire.
    //
    // Constate le 09/09/2026 : la famille RAM entiere a disparu d'une passe d'audit puis est
    // revenue a la suivante, sans qu'on puisse savoir laquelle des deux s'etait produite. Rendre
    // l'echec LISIBLE avant de le traiter — on ne repare pas ce qu'on ne sait pas nommer.
    constats.Add(new RepairFinding("RAM", "modules", null, null, RepairStatus.Unknown,
        modules.IsOk
            ? "SMBIOS a REPONDU mais n'a rendu AUCUNE barrette. Le fournisseur WMI a donc "
              + "fonctionne et n'avait rien a dire — ce n'est pas une panne de lecture, et "
              + "reessayer ne changerait probablement rien."
            : $"SMBIOS a ECHOUE ({modules.Error?.Code} — {modules.Error?.Message}). C'est une "
              + "panne de lecture, pas une machine sans memoire. Capacite, frequence et canaux "
              + "restent inconnus."));
}
else
{
    var frequences = barrettes.Select(m => m.ConfiguredMhz).Where(x => x > 0).Distinct().ToList();
    var nominales = barrettes.Select(m => m.RatedMhz).Where(x => x > 0).Distinct().ToList();
    var controleurs = barrettes.Select(m => Controleur(m.DeviceLocator)).Where(x => x is not null)
        .Distinct().ToList();

    constats.Add(new RepairFinding("RAM", "modules installes",
        $"{barrettes.Count} — " + string.Join(" · ", barrettes.Select(Decrire)),
        null, RepairStatus.Observed, "inventaire SMBIOS, sans jugement"));

    // SUR-AFFIRMATION CORRIGEE LE 08/09/2026. Ce constat disait « deux controleurs distincts
    // PROUVENT le double canal ». **C'est faux.** `DeviceLocator` est une chaine de TOPOLOGIE
    // physique venue de SMBIOS — elle decrit ou les barrettes sont branchees, pas le mode dans
    // lequel le controleur memoire fonctionne actuellement. Un entrelacement peut etre desactive,
    // degrade, ou refuse par le firmware sans que le nom du slot change d'un caractere.
    //
    // Ce qu'on peut dire honnetement : la topologie est COMPATIBLE avec du double canal. Le mode
    // reel reste Unknown tant que personne n'a lu l'entrelacement.
    // LA TOPOLOGIE NE PROUVE RIEN, MAIS LE DEBIT SI.
    //
    // Hier ce constat rendait `Unknown` : `DeviceLocator` decrit ou les barrettes sont branchees,
    // pas le mode du controleur. C'etait juste — et le produit avait DEJA la preuve ailleurs sans
    // que l'audit la lise. Un canal a une vitesse PLAFOND (MT/s x 8 octets) ; un debit mesure
    // au-dessus de ce plafond ne peut pas venir d'un seul canal, quelle que soit la facon dont la
    // carte nomme ses emplacements.
    //
    // Mesure du 08/09/2026, jeu ferme, 25 passes : 33,15 GB/s pour un plafond d'un canal de
    // 30,4 GB/s a 3800 MT/s — soit 1,09 x. Multi-canal PROUVE par la mesure.
    //
    // La borne est unilaterale : elle prouve « au moins N canaux », jamais « exactement N », et
    // jamais « simple canal » — un debit bas peut venir du nombre d'acces qu'un coeur garde en vol.
    var bancMemoire = MemoryBench.Measure(passes: 12);
    // Retenue pour la PHOTO : c'est la condition qui qualifie vraiment les conclusions
    // memoire, la charge processeur ne les voit pas.
    dispersionDebit = bancMemoire.BandwidthSigmaPct;
    var canauxProuves = MemoryInference.ProvenMinimumChannels(bancMemoire.BandwidthGBs, barrettes);
    var plafondUnCanal = MemoryInference.PerChannelPeakGBs(barrettes);

    constats.Add(canauxProuves >= 2
        ? new RepairFinding("RAM", "mode memoire",
            $"{canauxProuves} canaux PROUVES par le debit", null, RepairStatus.ExpectedDefault,
            $"{bancMemoire.BandwidthGBs:0.0} GB/s mesures pour un plafond d'un canal de "
            + $"{plafondUnCanal:0.0} GB/s : un seul canal ne peut pas produire ce debit. "
            + $"(la topologie SMBIOS en montre {controleurs.Count}, ce qui est coherent mais ne "
            + "prouvait rien a soi seul)")
        : new RepairFinding("RAM", "mode memoire",
            controleurs.Count > 0 ? $"topologie compatible {controleurs.Count} canal(aux)" : null,
            null, RepairStatus.Unknown,
            $"{bancMemoire.BandwidthGBs:0.0} GB/s ne depassent pas le plafond d'un canal "
            + $"({plafondUnCanal:0.0} GB/s) : la borne ne prouve rien. Ce n'est PAS un "
            + "diagnostic de simple canal — un debit bas peut venir du nombre d'acces en vol, pas "
            + "du nombre de canaux."));

    constats.Add(new RepairFinding("RAM", "debit / latence",
        $"{bancMemoire.BandwidthGBs:0.0} GB/s · {bancMemoire.LatencyNs:0.0} ns", null,
        RepairStatus.Observed,
        $"dispersion {bancMemoire.BandwidthSigmaPct:0.0} % sur le debit et "
        + $"{bancMemoire.LatencySigmaPct:0.0} % sur la latence. Une mesure prise pendant "
        + "qu'un jeu tourne decrit l'agitation de la machine, pas sa memoire : le meme banc rendait "
        + "21,7 GB/s avec Warzone ouvert et 33,2 GB/s ferme."));

    // XMP / EXPO : un ecart entre configure et nominal est un DIAGNOSTIC, pas une promesse de FPS.
    if (frequences.Count > 0 && nominales.Count > 0)
    {
        var configure = frequences.Max();
        var nominal = nominales.Max();
        // DEUX SUR-AFFIRMATIONS CORRIGEES LE 08/09/2026.
        //
        // « un profil est charge » : la tension et la frequence ne prouvent PAS l'identite du
        // profil BIOS. 1350 mV et 3800 MT/s sont compatibles avec XMP, avec un XMP modifie, avec
        // un reglage entierement manuel, ou avec un autre mecanisme du firmware. XmpActive reste
        // Unknown tant que nous ne savons pas le lire.
        //
        // « NonDefault + GuidedBios » : un ecart au nominal du kit n'est ni un defaut ni une
        // raison de proposer une modification. Un GuidedBios n'a de sens qu'avec un objectif, une
        // stabilite etablie et une mesure avant/apres possible — nous n'avons aucun des trois.
        constats.Add(configure >= nominal
            ? new RepairFinding("RAM", "vitesse memoire", $"{configure} MT/s", $"{nominal} MT/s",
                RepairStatus.Observed,
                "la memoire tourne a sa vitesse nominale ou au-dela")
            : new RepairFinding("RAM", "vitesse memoire", $"{configure} MT/s", $"{nominal} MT/s",
                RepairStatus.Observed,
                $"le kit est donne pour {nominal} MT/s et tourne a {configure}. C'est un ECART "
                + "OBSERVE, pas un defaut : une frequence sous le nominal est frequemment un choix "
                + "de stabilite, et ces kits ne tiennent pas toujours leur nominal sur toutes les "
                + "cartes. Aucune action proposee — un GuidedBios exigerait un objectif, une "
                + "stabilite etablie et une mesure avant/apres, et nous n'avons aucun des trois."));
    }

    var voltages = barrettes.Select(m => m.ConfiguredVoltageMv).Where(x => x > 0).ToList();
    if (voltages.Count > 0)
        constats.Add(new RepairFinding("RAM", "tension configuree", $"{voltages.Max()} mV", "1200 mV",
            RepairStatus.Observed,
            "au-dessus du JEDEC DDR4 (1200 mV) : la memoire tourne dans une configuration "
            + "OVERCLOCKEE. Cela ne dit PAS lequel des mecanismes l'a posee — XMP, XMP modifie, "
            + "reglage manuel, ou autre configuration firmware sont tous compatibles avec cette "
            + "lecture. XmpActive = Unknown."));
}

// UN BLOC « POLITIQUE DE VIDAGE » VIVAIT ICI, ET IL CONTREDISAIT LA SECTION STABILITE.
// Il declarait `7 (automatique)` comme « le defaut Windows », classait `3` en Suspicious et
// RECOMMANDAIT d'y toucher — alors que la section STABILITE, ecrite le meme jour, refusait de
// l'affirmer faute de source. Verification faite sur la documentation Microsoft : elle decrit les
// types de vidage SANS publier de valeur par defaut. Le doublon est supprime, la regle vit dans
// `CrashDumpPolicy` et le constat unique est rendu par la section STABILITE.

// ── BCD ─────────────────────────────────────────────────────────────────────────────────
// Rendu par `RepairAuditService` : c'est une pure lecture, donc elle appartient au produit et non
// a cet outil. Le jugement lui-meme vit dans `BcdInterpretation` depuis le 08/09.

// ── BIOS : la matrice de qualification, seule lecture BIOS qui reste ici ────────────────
// L'identite (carte, firmware) et l'interface constructeur sont rendues par `RepairAuditService`.
// L'identite est relue ici uniquement pour INDEXER la matrice, qui vit dans `DrDxt.Bios` — un
// projet que le service ne reference pas.
var carte = Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard", "Product");
var fabricant = Wmi("SELECT Manufacturer FROM Win32_BaseBoard", "Manufacturer");
var versionBios = Wmi("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion");
// LA MATRICE DE QUALIFICATION — la connaissance reutilisable, pas le parcours de clics.
//
// `BiosQualification` etait modelisee depuis des semaines et construite par PERSONNE. Ce qui
// suit est la premiere lecture reelle : ce que DrDXT sait de CETTE carte avec CE firmware, et
// pourquoi il n'a le droit d'ecrire aucun de ces reglages.
var entrees = BiosQualificationMatrix.Pour(carte is null ? null : (fabricant + " " + carte), versionBios);
if (entrees.Count == 0)
{
    constats.Add(new RepairFinding("BIOS", "matrice de qualification", "aucune entree", null,
        RepairStatus.Unknown,
        "cette combinaison carte + version de BIOS n'est pas dans la matrice. Elle ne rend "
        + "JAMAIS une entree « approchante » : un reglage valable sur une carte voisine ou sur un "
        + "autre firmware ne l'est pas ici."));
}
else
{
    foreach (var e in entrees)
        constats.Add(new RepairFinding("BIOS", "reglage · " + e.Setting,
            e.CurrentValue ?? "valeur courante NON LUE", e.TargetValue,
            RepairStatus.Unknown,
            Action: RepairAction.GuidedBios,
            Explication:
            (e.MenuPath is { } m ? m + ". " : "")
            + (e.NoPost == NoPostRisk.CanPreventPost
                ? "Ce reglage peut laisser un ECRAN NOIR : c'est un resultat normal de la "
                  + "manœuvre, pas une panne — et ca se sait AVANT de cliquer. "
                : "")
            + "Ecriture automatique refusee : " + (e.CeQuiManque ?? "—")));
}

// ── RESEAU : la famille dont l'audit ne disait RIEN ─────────────────────────────────────
//
// `NicErrorCounters` est ecrit, teste, et n'avait **aucun consommateur** — troisieme fois
// aujourd'hui qu'une grandeur deja mesuree n'atteignait personne. Et son contrat compte : ces
// compteurs sont des TOTAUX depuis le demarrage, inutilisables tels quels. On mesure donc une
// FENETRE, comme l'API le demande.

var cartes = new NetworkAdapterReader(new ManagementWmiQuery()).Read();
if (!cartes.IsOk || cartes.Value is not { Count: > 0 } liens)
{
    constats.Add(new RepairFinding("RESEAU", "carte", null, null, RepairStatus.Unknown,
        cartes.IsOk ? "aucune carte physique connectee" : cartes.Error?.Message ?? "lecture impossible"));
}
else
{
    foreach (var lien in liens)
    {
        constats.Add(new RepairFinding("RESEAU", "carte", lien.Description, null,
            RepairStatus.Observed, lien.IsWired ? "lien filaire" : "lien sans fil"));

        // Aucun de ces trois n'est declare NonDefault : leur valeur par defaut sort du PILOTE
        // du constructeur, pas de Windows, et nous ne l'avons pas etablie sur source. Ce sont des
        // constats — et surtout PAS des recommandations : aucune source ne nous autorise
        // aujourd'hui a promettre un gain en jeu a les desactiver. Le dire est deja utile ; le
        // vendre serait un tweak mythologique.
        constats.Add(Reglage("moderation d'interruption", lien.InterruptModeration,
            "elle regroupe les interruptions : moins de reveils du processeur, plus de delai avant "
            + "qu'un paquet ne soit vu"));
        constats.Add(Reglage("RSS", lien.Rss,
            "il repartit la reception sur plusieurs coeurs au lieu d'un seul"));
        constats.Add(Reglage("economie d'energie du lien", lien.LinkPowerSaving,
            "Ethernet vert / Power Saving Mode / Selective Suspend : le lien reduit sa puissance "
            + "quand il est calme. Son reveil se compte en MICROSECONDES et aucune source ne "
            + "chiffre l'effet en jeu (docs/LEVIERS-REPOS-ET-RESEAU.md, N-01 a N-03) — sous notre "
            + "plancher de bruit in-game de 9,9 %, c'est inmesurable, donc impromettable"));
        constats.Add(Reglage("Ethernet a economie d'energie (EEE)", lien.EnergyEfficientEthernet,
            "802.3az : le lien s'endort entre deux trames"));

        // La seule grandeur de cette section qui soit une MESURE et non une lecture de reglage.
        var id = PertesInterface.IdDeLaCarte(lien.Name);
        constats.Add(id is null
            ? new RepairFinding("RESEAU", "pertes a l'interface", null, null, RepairStatus.Unknown,
                "identifiant d'interface introuvable : aucune fenetre n'a pu etre mesuree")
            : MesurerPertes(id));
    }
}

// ── STABILITE : ce que la machine sait garder de ses propres accidents ──────────────────
//
// Un produit qui va experimenter sur le GPU et la memoire DOIT savoir si la machine est
// capable de conserver la preuve d'un plantage. Sans cette capacite, une experience qui fait
// tomber le PC n'est pas attribuable, et le KEEP/REVERT statue a l'aveugle.

// Le TYPE de vidage est rendu par `RepairAuditService` (pure lecture de registre). Ce qui suit
// est ce que cet outil apporte en plus : l'inventaire de ce qui a REELLEMENT ete produit.

// Les vidages presents et les echecs de creation (volmgr 161) sont rendus par `RepairAuditService`
// depuis le 17/09/2026 : le produit les montre aussi.

// Les incidents noyau LIVE sont rendus par `RepairAuditService` : pure lecture des rapports WER,
// donc elle appartient au produit.

// ── LE RAPPORT ──────────────────────────────────────────────────────────────────────────
Console.WriteLine("== AUDIT DES DEGATS D'OPTIMISATION ==");
Console.WriteLine("   LECTURE PURE — rien n'a ete ecrit, rien n'a ete repare.");
Console.WriteLine();

foreach (var domaine in constats.Select(c => c.Domaine).Distinct())
{
    Console.WriteLine($"   {domaine}");
    foreach (var c in constats.Where(x => x.Domaine == domaine))
    {
        // Un nom plus long que la colonne collait sa valeur : « economie d'energie du lienactif ».
        Console.WriteLine($"     {Symbole(c.Statut)} {(c.Nom.Length >= 26 ? c.Nom + "  " : c.Nom.PadRight(26))}{c.ValeurCourante ?? "non lu"}"
                          + (c.ValeurAttendue is { } a && a != c.ValeurCourante ? $"   (attendu : {a})" : ""));
        Console.WriteLine($"        {c.Statut}{(c.Action != RepairAction.Aucune ? $" → {c.Action}" : "")}");
        Console.WriteLine($"        {c.Explication}");
    }
    Console.WriteLine();
}

// ── CE QU'IL FAUT EN RETENIR ────────────────────────────────────────────────────────────
//
// Cette section rendait un decompte de statuts — « 2 recommandations, 6 Unknown ». Exact, et
// illisible : rien n'y disait CE QU'IL FAUT REGARDER parmi une cinquantaine de constats sur onze
// domaines. Un rapport que personne ne lit ne vaut pas mieux qu'un rapport faux.
var tri = RepairTriage.Trier(constats);

Console.WriteLine("== CE QUI MERITE TON ATTENTION ==");
Console.WriteLine();
if (tri.Attention.Count == 0)
    Console.WriteLine("   Rien. Et c'est un RESULTAT, pas une absence de resultat.");
else
    foreach (var c in tri.Attention)
        Console.WriteLine($"   {Symbole(c.Statut)} {c.Domaine,-10} {c.Nom,-30}{c.ValeurCourante ?? "non lu"}"
                          + (c.Action != RepairAction.Aucune ? $"   → {c.Action}" : ""));

Console.WriteLine();
Console.WriteLine("== MES ANGLES MORTS ==");
Console.WriteLine();
// Aussi important que la liste ci-dessus. Un audit qui tait ce qu'il n'a pas pu lire se fait
// passer pour exhaustif — meme faute que de rendre zero pour un compteur illisible.
if (tri.NonLus.Count == 0)
    Console.WriteLine("   Aucun : tout ce que cet outil sait lire a ete lu.");
else
    foreach (var c in tri.NonLus)
        Console.WriteLine($"   ? {c.Domaine,-10} {c.Nom}");

// ── CE QUI A CHANGE DEPUIS LA DERNIERE FOIS ─────────────────────────────────────────────
//
// Le pilier MAINTENIR etait vide : l'audit recalculait tout a chaque passe et ne gardait rien.
// On voyait un etat, jamais une trajectoire — donc aucune reparation ne pouvait etre prouvee dans
// le temps.
var journal = new RepairHistoryStore();
var photoActuelle = new RepairSnapshot(
    DateTimeOffset.Now,
    // Les familles EXAMINEES voyagent avec la photo. Sans elles, le jour ou l'audit apprend a
    // lire une famille de plus, le diff annonce une degradation de la machine.
    constats.Select(c => c.Domaine).Distinct().ToList(),
    constats.Select(c => new RepairEtat(c.Domaine, c.Nom, c.ValeurCourante, c.Statut)).ToList(),
    // Et l'etat de la machine PENDANT la lecture. Sans lui, le diff a annonce « RAM : le
    // multi-canal n'est plus prouve » alors que la machine n'avait rien perdu — c'est une
    // compilation qui tournait.
    new ConditionsDeMesure(ChargeProcesseur(), dispersionDebit),
    // 16/09/2026 : le meme magasin que l'appli. Sans l'empreinte et les droits, une photo prise ici
    // en administrateur faisait dire a l'appli, non elevee, que des familles avaient « disparu ».
    typeof(DrDxt.Detection.Repair.RepairAuditService).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12],
    new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator),
    // 17/09/2026 : l'outil lit des familles que l'appli ne lit pas ; chacun se compare à lui-même.
    Lecteur: "outil");

Console.WriteLine();
Console.WriteLine("== CE QUI A CHANGE DEPUIS LA DERNIERE FOIS ==");
Console.WriteLine();

var toutes = journal.Lire();
var precedente = toutes.IsOk
    ? Result<RepairSnapshot?>.Ok(RepairHistory.PhotoComparable(toutes.Value!, photoActuelle))
    : Result<RepairSnapshot?>.Fail(toutes.Error!.Value.Code, toutes.Error.Value.Message);
if (!precedente.IsOk)
{
    // Un journal illisible se DIT. Le traiter comme « aucune photo » ferait passer tout l'etat
    // actuel pour du nouveau.
    Console.WriteLine($"   {precedente.Error!.Value.Message}");
}
else if (precedente.Value is null)
{
    Console.WriteLine("   Premiere photo : il n'y a rien a comparer. La prochaine passe le dira.");
}
else
{
    var deltas = RepairHistory.Comparer(precedente.Value, photoActuelle);
    Console.WriteLine($"   (photo precedente : {precedente.Value.Quand:dd/MM/yyyy HH:mm}"
                      + (precedente.Value.Conditions?.ChargeProcesseurPct is { } cp
                          ? $", charge {cp:0} %" : "")
                      + $" — celle-ci : charge "
                      + (photoActuelle.Conditions?.ChargeProcesseurPct is { } cn
                          ? $"{cn:0} %" : "non lue") + ")");

    // AVANT tout le reste : si les deux photos n'ont pas ete prises dans le meme etat, le
    // lecteur doit le savoir avant de lire une seule ligne de changement.
    foreach (var autre in new[] { RepairHistory.AvertissementDeLecteur(precedente.Value, photoActuelle),
                                  RepairHistory.AvertissementDeDroits(precedente.Value, photoActuelle) })
        if (autre is not null) { Console.WriteLine(); Console.WriteLine("   " + autre); }
    if (RepairHistory.AvertissementDeConditions(precedente.Value, photoActuelle) is { } alerte)
    {
        Console.WriteLine();
        Console.WriteLine("   " + alerte);
        Console.WriteLine();
    }

    // SEPARE DES LA PREMIERE SORTIE REELLE. Deux passes a une minute d'ecart ont produit quatre
    // « changements » : coeurs parques, frequence CPU, debit RAM, temperature GPU. Tous exacts, et
    // tous sans interet — ce sont des MESURES, elles bougent par nature. Les melanger aux vrais
    // changements donne un diff qui trouve toujours quelque chose, donc un diff qu'on cesse de
    // lire. Un changement de JUGEMENT, une apparition ou une disparition sont des nouvelles ; une
    // valeur mesuree qui varie n'en est pas une.
    var nouvelles = deltas.Where(d => d.Quoi != RepairChangement.ValeurChangee).ToList();
    var mesures = deltas.Where(d => d.Quoi == RepairChangement.ValeurChangee).ToList();

    if (nouvelles.Count == 0)
        Console.WriteLine("   Aucun changement de jugement, ni apparition, ni disparition.");
    else
        foreach (var d in nouvelles)
        {
            Console.WriteLine($"   {d.Quoi,-16} {d.Domaine,-10} {d.Nom}");
            Console.WriteLine($"      {d.Avant ?? "—"} → {d.Apres ?? "—"} : {d.Phrase}");
        }

    if (mesures.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"   {mesures.Count} valeur(s) mesuree(s) ont bouge sans changer de jugement — "
                          + "c'est attendu :");
        foreach (var d in mesures)
            Console.WriteLine($"      {d.Domaine,-10} {d.Nom,-24} {d.Avant ?? "—"} → {d.Apres ?? "—"}");
    }
}

var ecrit = journal.Enregistrer(photoActuelle);
if (!ecrit.IsOk) Console.WriteLine($"   {ecrit.Error!.Value.Message}");

Console.WriteLine();
Console.WriteLine("== FINAL ==");
Console.WriteLine($"   {tri.Phrase}.");
Console.WriteLine($"   {constats.Count(c => c.Action == RepairAction.ReparationAutomatique)} reparation(s) automatique(s) · "
                  + $"{constats.Count(c => c.Statut == RepairStatus.KnownHarmful)} etat(s) connu(s) comme nuisible(s)");
Console.WriteLine();
Console.WriteLine("   « Different » n'est pas « endommage ». Un audit qui ne repare rien peut");
Console.WriteLine("      etre excellent — et un audit qui repare un choix casse une machine.");
return 0;

/// <summary>Un reglage de carte, rendu en constat. `Unknown` quand la carte n'expose pas le
/// mot-cle : une absence de reglage n'est pas un reglage desactive.</summary>
static RepairFinding Reglage(string quoi, TriState etat, string explication) => etat switch
{
    TriState.On => new RepairFinding("RESEAU", quoi, "actif", null, RepairStatus.Observed, explication),
    TriState.Off => new RepairFinding("RESEAU", quoi, "inactif", null, RepairStatus.Observed, explication),
    _ => new RepairFinding("RESEAU", quoi, null, null, RepairStatus.Unknown,
        "cette carte n'expose pas ce mot-cle NDIS — ce n'est PAS « desactive »"),
};

/// <summary>
/// Les pertes a l'interface PENDANT une fenetre, jamais depuis le demarrage.
///
/// Le contrat de `NicErrorCounters` est explicite : ces compteurs sont des totaux depuis le
/// boot, et « 1 553 paquets ignores sur 52 millions » ne dit rien de la partie en cours. Seule la
/// difference entre deux instants a un sens — c'est pour ca que l'API n'expose que `Diff`.
/// </summary>
static RepairFinding MesurerPertes(string interfaceId)
    // 10/09/2026 : la regle vit dans le produit (`PertesInterface`) ; l'outil et l'App classent pareil.
    // Ici, une fenetre calme de 3 s ; l'App, elle, mesure pendant la partie.
    => PertesInterface.Mesurer(new SystemNicCounterSource(), interfaceId, TimeSpan.FromSeconds(3));

/// <summary>
/// La charge processeur moyenne, pour dater la photo dans un ETAT et pas seulement dans le temps.
///
/// Via `Win32_PerfFormattedData_PerfOS_Processor`, dont les noms de proprietes ne sont PAS
/// localises — contrairement aux compteurs de performance nommes, qui ont deja fait echouer en
/// silence un temoin de charge le 08/09. Trois releves espaces, parce qu'un instantane de la
/// charge processeur ne veut rien dire.
///
/// Rend <c>null</c> si rien ne repond : « non lu » n'est pas « machine au repos ».
/// </summary>
static double? ChargeProcesseur()
{
    try
    {
        var releves = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            using var chercheur = new System.Management.ManagementObjectSearcher(
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor "
                + "WHERE Name='_Total'");
            foreach (var o in chercheur.Get())
                if (o["PercentProcessorTime"] is { } v && double.TryParse(v.ToString(), out var d))
                    releves.Add(d);
            if (i < 2) Thread.Sleep(400);
        }
        return releves.Count > 0 ? releves.Average() : null;
    }
    catch { return null; }
}

static string? Wmi(string requete, string propriete)
{
    try
    {
        using var chercheur = new System.Management.ManagementObjectSearcher(requete);
        foreach (var o in chercheur.Get()) return o[propriete]?.ToString();
    }
    catch { /* WMI absent ou refuse : non lu, jamais une valeur fabriquee */ }
    return null;
}

static string Symbole(RepairStatus s) => s switch
{
    RepairStatus.ExpectedDefault => "",
    RepairStatus.KnownHarmful => "",
    RepairStatus.Suspicious => "",
    RepairStatus.Unknown => "?",
    _ => "·",
};

static string Decrire(MemoryModule m) =>
    $"{(string.IsNullOrWhiteSpace(m.Manufacturer) ? "?" : m.Manufacturer)} "
    + $"{(string.IsNullOrWhiteSpace(m.PartNumber) ? "?" : m.PartNumber)} "
    + $"{(m.ConfiguredMhz > 0 ? m.ConfiguredMhz + " MT/s" : "MT/s non lu")}"
    + (m.Ranks > 0 ? $" {m.Ranks}R" : "")
    + (m.DeviceLocator is { Length: > 0 } d ? $" [{d}]" : "");

/// <summary>Le controleur memoire d'un emplacement (« Controller0-DIMMA2 » → « Controller0 »).
/// Deux controleurs DISTINCTS prouvent le double canal ; le nombre de barrettes ne prouve rien.</summary>
static string? Controleur(string? deviceLocator)
{
    if (string.IsNullOrWhiteSpace(deviceLocator)) return null;
    var tiret = deviceLocator.IndexOf('-');
    return tiret > 0 ? deviceLocator[..tiret] : deviceLocator;
}

