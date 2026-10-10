using DrDxt.Core;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.Detection.Settings;

/// <summary>Une valeur de plan d'alimentation, comparee au defaut de ce plan.</summary>
/// <param name="Nom">Le reglage, nomme comme l'utilisateur peut le retrouver.</param>
/// <param name="ValeurAc">La valeur active sur secteur, ou <c>null</c> si non lue.</param>
/// <param name="DefautAc">Le defaut de ce plan pour ce reglage, ou <c>null</c> si non lu.</param>
/// <param name="Unite">« % », « ms »… pour l'affichage. Vide quand la valeur est un code.</param>
public sealed record PowerSettingValue(string Nom, uint? ValeurAc, uint? DefautAc, string Unite)
{
    /// <summary>La valeur s'ecarte-t-elle du defaut de son propre plan ? <c>null</c> quand l'un
    /// des deux n'a pas ete lu — et « je n'ai pas lu » n'est pas « identique ».</summary>
    public bool? EcarteDuDefaut =>
        ValeurAc is { } v && DefautAc is { } d ? v != d : null;
}

/// <summary>
/// Les valeurs d'un plan d'alimentation, et le defaut de ce meme plan.
///
/// Juger un plan par son GUID ou son nom ne prouve rien : **deux plans nommes « Equilibre »
/// peuvent porter des reglages completement differents.** Un script d'optimisation qui pousse le
/// minimum processeur a 100 % laisse un plan qui s'appelle toujours « Equilibre » et qui n'a plus
/// rien du defaut Windows. C'est exactement le degat qu'on cherche, et l'audit precedent ne
/// pouvait pas le voir.
///
/// Windows expose la reponse officiellement : <c>PowerReadACValueIndex</c> rend la valeur active,
/// <c>PowerReadACDefaultIndex</c> rend **le defaut de ce plan pour ce reglage**. La difference des
/// deux est un fait, pas une heuristique — et elle n'exige aucune elevation.
///
/// Un ecart au defaut reste un ECART. Il ne dit pas que la valeur est mauvaise, ni que
/// quelqu'un l'a posee par erreur. Le classement vit ailleurs (<see cref="DrDxt.Core.RepairStatus"/>).
///
/// FAIL-CLEAN : ne leve jamais ; toute lecture ratee rend <c>null</c>, jamais zero. « Non lu » et
/// « vaut zero » sont deux reponses differentes, et les confondre ferait accuser un plan sain.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerSettingsReader
{
    // GUID_PROCESSOR_SETTINGS_SUBGROUP — le sous-groupe processeur (powrprof.h).
    private static readonly Guid SousGroupeProcesseur = new("54533251-82be-4824-96c1-47b60b740d00");

    /// <summary>Les reglages processeur qui comptent pour le jeu, avec leur GUID officiel.
    ///
    /// Cette liste est volontairement COURTE. Un audit qui rend quarante lignes dont trente-cinq
    /// sans effet connu noie les cinq qui comptent, et personne ne le lit deux fois.</summary>
    public static readonly (string Nom, Guid Guid, string Unite)[] ReglagesProcesseur =
    {
        // PROCTHROTTLEMIN / PROCTHROTTLEMAX : le plancher et le plafond de frequence, en pourcent.
        // Le plancher a 100 % est la signature la plus courante des scripts « gaming » : il
        // interdit au processeur de redescendre, ce qui coute des watts et de la chaleur sans
        // qu'aucune mesure ne l'ait jamais justifie sur cette machine.
        ("minimum processeur", new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"), "%"),
        ("maximum processeur", new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"), "%"),

        // PERFEPP — Energy Performance Preference. 0 = performance pure, 100 = economie.
        ("preference energie/perf", new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"), ""),

        // PERFBOOSTMODE — la politique de boost (0 = desactive, 2 = agressif…).
        ("mode de boost", new Guid("be337238-0d82-4146-a960-4f3749d470c7"), ""),

        // CPMINCORES / CPMAXCORES — le parking de coeurs, en pourcent des coeurs disponibles.
        // C'est la CONFIGURATION du parking, a ne pas confondre avec le nombre de coeurs
        // parques a un instant donne : le second bouge en permanence selon la charge.
        ("parking : coeurs min", new Guid("0cc5b647-c1df-4637-891a-dec35c318583"), "%"),
        ("parking : coeurs max", new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028"), "%"),
    };

    /// <summary>
    /// Lit les reglages processeur du plan actif, chacun avec le defaut de sa PERSONNALITE.
    ///
    /// Piege d'API paye ici : le second parametre de <c>PowerReadACDefaultIndex</c> n'est pas le
    /// plan, c'est sa **personnalite** (Equilibre / Performances elevees / Economie). Lui passer le
    /// GUID du plan, ou nul, rend soit une erreur soit la valeur d'autre chose — et on comparerait
    /// une valeur au defaut d'un plan qui n'est pas le sien.
    /// </summary>
    /// <param name="personnalite">Le GUID de personnalite du plan actif. Pour les trois plans
    /// Windows, il est egal au GUID du plan lui-meme.</param>
    /// <param name="planActif">Le GUID du plan dont on lit les valeurs. Sur les plans Windows il
    /// est egal a la personnalite, mais un plan personnalise a les deux differents.</param>
    public IReadOnlyList<PowerSettingValue> LireProcesseur(Guid personnalite, Guid? planActif = null)
    {
        planActif ??= personnalite;
        var sortie = new List<PowerSettingValue>();
        foreach (var (nom, guid, unite) in ReglagesProcesseur)
        {
            sortie.Add(new PowerSettingValue(nom,
                LireValeur(guid, planActif),
                LireDefaut(guid, personnalite),
                unite));
        }
        return sortie;
    }

    private static uint? LireValeur(Guid reglage, Guid? plan, Guid? sousGroupeLu = null)
    {
        try
        {
            var sousGroupe = sousGroupeLu ?? SousGroupeProcesseur;
            var schema = plan ?? Guid.Empty;
            uint valeur = 0;
            // Un plan NUL ne marche pas ici, contrairement a l'hypothese de depart : mesure du
            // 08/09/2026, les six lectures rendaient « non lu » pendant que les defauts, eux,
            // sortaient. Il faut nommer le plan dont on lit la valeur.
            return PowerReadACValueIndex(IntPtr.Zero, ref schema, ref sousGroupe, ref reglage, ref valeur) == 0
                ? valeur
                : null;   // ERROR_SUCCESS ou rien : jamais un zero fabrique.
        }
        catch { return null; }
    }

    /// <summary>07/10/2026 (L-CPU) : PERFBOOSTMODE sur secteur dans ce plan, et le défaut de SA personnalité (un plan de
    /// constructeur dérivé d'Équilibré garde le défaut d'Équilibré). null = l'un des deux non lu, jamais un zéro.</summary>
    public static ModeDeBoostLu? LireModeDeBoost(Guid plan) =>
        LireValeur(ModeDeBoostLu.Reglage, plan) is { } valeur && LireDefaut(ModeDeBoostLu.Reglage, Personnalite(plan) ?? plan) is { } defaut
            ? new ModeDeBoostLu(plan, valeur, defaut)
            : null;

    /// <summary>B3 (10/10/2026) : « Mode d'économie d'énergie » de la carte sans fil, sur secteur, dans ce plan. null = non lu.</summary>
    public static EconomieSansFilLue? LireEconomieSansFil(Guid plan) =>
        LireValeur(EconomieSansFilLue.Reglage, plan, EconomieSansFilLue.SousGroupe) is { } valeur ? new EconomieSansFilLue(plan, valeur) : null;

    /// <summary>07/10/2026 (L-USB) : la suspension sélective USB sur secteur dans ce plan, et le défaut de sa personnalité.
    /// Lecture seule : aucune source ne justifie de l'écrire pour le jeu (voir <see cref="Repair.SuspensionUsb"/>).</summary>
    public static PowerSettingValue LireSuspensionUsb(Guid plan) =>
        new("suspension sélective USB (secteur)",
            LireValeur(Repair.SuspensionUsb.Reglage, plan, Repair.SuspensionUsb.SousGroupe),
            LireDefaut(Repair.SuspensionUsb.Reglage, Personnalite(plan) ?? plan, Repair.SuspensionUsb.SousGroupe), "");

    /// <summary>06/10/2026 (revue F-12) : la personnalité d'un plan (Équilibré, Hautes performances, Économie) :
    /// un plan de constructeur dérivé d'Équilibré garde la personnalité Équilibré. GUID_POWERSCHEME_PERSONALITY sous
    /// NO_SUBGROUP_GUID (winnt.h). null = non lue.</summary>
    public static Guid? Personnalite(Guid plan)
    {
        try
        {
            var sousGroupe = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");
            var reglage = new Guid("245d8541-3943-4422-b025-13a784f679b7");
            var tampon = new byte[16];
            uint taille = 16;
            return PowerReadACValue(IntPtr.Zero, ref plan, ref sousGroupe, ref reglage, out _, tampon, ref taille) == 0 && taille == 16
                ? new Guid(tampon) : null;
        }
        catch { return null; }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValue(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid,
        ref Guid powerSettingGuid, out uint type, byte[] buffer, ref uint bufferSize);

    private static uint? LireDefaut(Guid reglage, Guid personnalite, Guid? sousGroupeLu = null)
    {
        try
        {
            var sousGroupe = sousGroupeLu ?? SousGroupeProcesseur;
            uint valeur = 0;
            return PowerReadACDefaultIndex(IntPtr.Zero, ref personnalite, ref sousGroupe, ref reglage, ref valeur) == 0
                ? valeur
                : null;
        }
        catch { return null; }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid powerSettingGuid,
        ref uint acValueIndex);

    // CINQ parametres : (RootPowerKey, SchemePersonalityGuid, SubGroup, Setting, out Index).
    //
    // Une premiere version en oubliait le premier. Le decalage a produit une VIOLATION D'ACCES
    // qui a tue le processus — et que le `try/catch` autour de l'appel n'a pas rattrapee : une
    // AccessViolationException est une exception d'etat corrompu, le CLR ne la livre pas au code
    // managé. Le contrat fail-clean de ce fichier ne protege donc que des erreurs de RETOUR, pas
    // d'une signature fausse. La seule protection contre une signature fausse est de la verifier.
    //
    // Le second parametre est la PERSONNALITE du plan (Equilibre / Performances / Economie), pas
    // le plan : la confondre comparerait une valeur au defaut de quelqu'un d'autre.
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACDefaultIndex(
        IntPtr rootPowerKey, ref Guid schemePersonalityGuid, ref Guid subGroupGuid,
        ref Guid powerSettingGuid, ref uint acDefaultIndex);
}
