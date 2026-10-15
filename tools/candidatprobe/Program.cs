using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using Microsoft.Win32;

// ============================================================================================
// QUEL CANDIDAT CETTE MACHINE MERITE-T-ELLE, AUJOURD'HUI ?
//
// Le catalogue de leviers n'avait jamais ete confronte a l'etat REEL de cette machine avant
// qu'on depense quatre minutes et demie de jeu sur l'un d'eux. Ce probe lit ce qui est lisible
// SANS ELEVATION et sans rien ecrire, puis dit — levier par levier — ce que la machine autorise
// et ce qu'elle disqualifie.
//
// Sa premiere version annoncait « 2 candidats retenus » alors que les DEUX etaient
// inapplicables : elle jugeait sur les grandeurs touchees et ignorait le `AppliesTo` de chaque
// tweak. Un selecteur qui surestime ce qu'il peut essayer est pire qu'un selecteur absent — il
// promet du temps de jeu qui ne rapportera rien.
//
// Il n'ecrit RIEN. C'est un diagnostic, pas une application.
//
//   dotnet run --project tools/candidatprobe
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("== ETAT REEL DE LA MACHINE, LU PAR LES LECTEURS DU PRODUIT ==");

// ── Le parking de cœurs : la condition de `SanePowerPlanTweak.AppliesTo` ────────────────
// null et liste vide ne se confondent pas : « je n'ai pas pu lire » n'est pas « aucun coeur
// n'est parque ». Cette distinction est ce qui evite de basculer un plan d'alimentation sur une
// absence de mesure.
var parques = new RegistryProvider().GetParkedCoreIndexes();
Console.WriteLine(parques is null
    ? "   coeurs parques    : NON LU (ce n'est pas « aucun »)"
    : parques.Count == 0
        ? "   coeurs parques    : aucun (lecture aboutie)"
        : $"   coeurs parques    : {parques.Count} — {string.Join(", ", parques)}");

// ── Le plan d'alimentation actif ────────────────────────────────────────────────────────
var plan = new PowerProvider().GetActiveSchemeGuid();
var planGuid = plan is { IsOk: true, Value: { } lu } ? lu.Trim('{', '}').ToLowerInvariant() : null;
Console.WriteLine($"   plan actif        : {planGuid ?? "NON LU"}"
                  + (planGuid is not null && NomDePlan(planGuid) is { } nom ? $"  ({nom})" : ""));

// ── La valeur d'origine dont `ReflexLowLatencyTweak` a besoin pour pouvoir revenir ──────
// Le tweak lit la valeur AVANT d'ecrire et refuse d'ecrire s'il ne peut pas la lire : sans elle,
// il ne saurait pas revenir en arriere. Absente = le levier ne s'appliquera pas.
var lowLatency = LireDword(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode");
Console.WriteLine($"   LowLatencyMode    : {(lowLatency is { } v ? v.ToString() : "ABSENT")}"
                  + "   (valeur d'origine exigee pour un revert fidele)");


// Les descripteurs viennent du catalogue du PRODUIT (`CatalogueDeCandidats`) : l'outil et l'App
// jugent un levier de la meme facon. `BlocageConstate` porte ce que le `AppliesTo` du tweak exige et
// que la machine vient de dementir — lu a l'instant, jamais suppose.
var catalogue = new (CandidateDescriptor D, string Source)[]
{
    (Decrire("gpu.core_offset"), "NvApiTuningActions"),
    (Decrire("gpu.power_limit"), "GpuPowerLimitTweak"),
    (Decrire("latency.prefer_max_perf"), "PreferMaxPerfTweak (DRS PREFERRED_PSTATE)"),
    (Decrire("latency.render_queue_cap", "AppliesTo exige un GPU de rendu AMD — cette machine est NVIDIA"),
        "RenderQueueCapTweak"),
    (Decrire("latency.reflex", lowLatency is null
            ? "NVTweak\\LowLatencyMode est ABSENT : le tweak refuse d'ecrire une valeur qu'il "
              + "ne saurait pas annuler"
            : null),
        "ReflexLowLatencyTweak"),
    // Ce levier est une politique d'alimentation CPU, plus un correctif de parking.
    (Decrire("fixit.power_plan", BlocagePlan(planGuid)), "SanePowerPlanTweak"),
    (Decrire("latency.hagsOn"), "HagsOnTweak"),
    (Decrire("latency.gameModeOn"), "GameModeOnTweak"),
    (Decrire("fixit.sane_pagefile"), "SanePagefileTweak"),
};

// Ce qu'une session Warzone a mesure le 08/09/2026, injecte tel quel.
var diagnostic = new DiagnosticMachine(
    Goulot: BottleneckState.MixedGpuLeaning,
    Bridage: GpuLimiterObserved.RienDeBridant,
    FrequenceMedianeMhz: 2715,
    FrequenceMinimaleMhz: 2715,
    // Plus de decision globale ; la carte se ferme par famille d'arret, lue ci-dessous.
    TuningGpuAutorise: true,
    // `PressionThread` reste volontairement `Unknown`, ET C'EST UNE DECISION, PAS UN OUBLI.
    // La session ETW du 08/09 a mesure la RESIDENCE du thread chaud de Warzone : `tid 97704` a
    // 98,3-98,9 % sur la classe de coeurs haute, sur trois mesures. Cela dit OU il a tourne, pas
    // COMBIEN il consomme — et `GameThreadPressure.Observee` parle de consommation. Les confondre
    // ferait remonter les familles CPU et Scheduler en tete du classement sur une grandeur qui ne
    // repond pas a la question.
    // Lu par `StabiliteLue`, la meme lecture que l'App.
    InstabiliteGpu: StabiliteLue.InstabiliteGpu(new WerLiveKernelReportLog()),
    // Les arrets brutaux REELS de cette machine, sur la fenetre du veto. Echec de
    // lecture ⇒ null ⇒ le veto ne ferme rien (et ne pretend pas « aucun arret »).
    ArretsBrutaux: StabiliteLue.ArretsBrutaux(new WindowsUnexpectedShutdownLog(), DateTimeOffset.UtcNow));

// ── OU CETTE MACHINE PERD-ELLE DE LA PERFORMANCE ? ──────────────────────────────────────
//
// `ClasserFamilles` etait calcule et lu par PERSONNE — seuls ses propres tests l'appelaient.
// C'est pourtant lui qui porte le renversement du produit : au lieu de demander « quel tweak
// avons-nous sous la main ? », il demande « ou cette machine perd-elle ? », et le catalogue vient
// apres.
//
// Une famille peut donc arriver en tete sans qu'aucun levier n'existe encore pour elle. Ce
// n'est pas un trou du classement : c'est son resultat le plus utile, puisqu'il dit QUOI
// CONSTRUIRE. Le laisser invisible revenait a se priver de sa propre feuille de route.
// Chaque arret lu avec sa famille, et ce que le joueur lit sur le reglage de sa carte.
Console.WriteLine("== ARRETS BRUTAUX PAR FAMILLE (45 jours, hors bouton) ==");
foreach (var a in diagnostic.ArretsBrutaux ?? [])
    Console.WriteLine($"   {a.Quand.ToLocalTime():dd/MM HH:mm}  {ClasseurDArrets.Nom(a.Famille)}"
                      + (a.CodeArret != 0 ? $" (0x{a.CodeArret:X})" : ""));
if (diagnostic.ArretsBrutaux is null) Console.WriteLine("   journal NON LU");
Console.WriteLine("   " + CandidateSelector.PhraseReglageCarte(diagnostic, DateTimeOffset.UtcNow));
Console.WriteLine();

Console.WriteLine("== OU CETTE MACHINE PERD-ELLE, DANS L'ORDRE ==");
Console.WriteLine();

var familles = CandidateSelector.ClasserFamilles(diagnostic);
if (familles.Count == 0)
{
    Console.WriteLine("   aucune famille ne ressort du diagnostic actuel.");
}
else
{
    var rang = 0;
    foreach (var (famille, motif) in familles)
        Console.WriteLine($"   {++rang}. {famille,-12}{motif}");
}
Console.WriteLine();
Console.WriteLine("   Un classement n'est PAS une preuve : il ordonne des depenses de temps de");
Console.WriteLine("      jeu, rien de plus. La preuve reste du ressort de l'experience appariee.");
if (familles.Count == 1)
{
    // Ce que le classement dit VRAIMENT quand il est court, et qu'il faut lire sans se mentir.
    Console.WriteLine();
    Console.WriteLine("   Une seule famille ressort, et ce n'est pas un defaut du classement :");
    Console.WriteLine("      les autres branches attendent une condition que RIEN n'a observee ici");
    Console.WriteLine("      — un plafond publie pour Thermal, une pression de validation memoire");
    Console.WriteLine("      pour Memory, un goulot purement latence pour Latency. Aucune n'a ete");
    Console.WriteLine("      inventee pour remplir la liste.");
}
Console.WriteLine();
Console.WriteLine("== CE QUE LE CATALOGUE PROPOSE, ET CE QUE LA MACHINE EN FAIT ==");
Console.WriteLine();

var appreciations = CandidateSelector.Evaluer(diagnostic, catalogue.Select(x => x.D).ToList());

foreach (var (descripteur, source) in catalogue)
{
    var a = appreciations.Single(x => x.Id == descripteur.Id);
    Console.WriteLine($"   {(a.Retenu ? "RETENU " : "ecarte ")}{descripteur.Id,-26}{source}");
    foreach (var motif in a.Motifs)
        Console.WriteLine($"   {"",-33}{motif}");
}

var retenus = appreciations.Count(a => a.Retenu);
Console.WriteLine();
Console.WriteLine($"   {retenus} candidat(s) retenu(s) sur {catalogue.Length}.");
if (retenus == 0)
{
    Console.WriteLine("   Aucun levier chaud n'a quelque chose a donner sur cette machine");
    Console.WriteLine("      aujourd'hui. Ce n'est pas un echec de l'autopilote : c'est son");
    Console.WriteLine("      diagnostic, et il vaut mieux que quatre minutes et demie perdues.");
}
return 0;

/// <summary>Ce qui empeche `SanePowerPlanTweak` de s'appliquer, COTE PLAN. Depuis
/// l'autre condition de son `AppliesTo` est le GOULOT MESURE (processeur limitant ou mixte), que
/// porte le diagnostic injecte ci-dessous ; le parking n'entre plus dans la decision (il est
/// identique entre Equilibre et Haute performance, mesure du 10/09/2026).</summary>
static string? BlocagePlan(string? planGuid)
{
    if (planGuid is null) return "plan d'alimentation NON LU";
    return planGuid != "381b4222-f694-41f0-9685-ff5bb260df2e"
        ? "le plan actif n'est pas Equilibre : il n'y a rien a monter"
        : null;
}

static CandidateDescriptor Decrire(string id, string? blocage = null) =>
    CatalogueDeCandidats.Decrire(id, blocage)
    ?? throw new InvalidOperationException($"« {id} » absent du catalogue du produit");

static uint? LireDword(string chemin, string nom)
{
    try
    {
        using var cle = Registry.LocalMachine.OpenSubKey(chemin);
        return cle?.GetValue(nom) is int v ? unchecked((uint)v) : null;
    }
    catch { return null; }
}

static string? NomDePlan(string g) => g switch
{
    "381b4222-f694-41f0-9685-ff5bb260df2e" => "Equilibre",
    "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "Hautes performances",
    "a1841308-3541-4fab-bc81-f71556f20b4a" => "Economie d'energie",
    "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Performances ultimes",
    _ => null,
};
