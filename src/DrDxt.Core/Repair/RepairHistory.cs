namespace DrDxt.Core;

/// <summary>L'etat d'un constat a un instant, reduit a ce qu'une comparaison a besoin de savoir.</summary>
public sealed record RepairEtat(string Domaine, string Nom, string? Valeur, RepairStatus Statut);

/// <summary>
/// Une photo de la machine, prise par une version donnee du produit.
/// </summary>
/// <param name="Quand">L'instant de la photo.</param>
/// <param name="DomainesExamines">Les familles que le produit savait lire ce jour-la : quand l'audit en gagne une
/// (MMCSS, services, reseau, thermique), ses constats ne ressortent pas « apparus » ; c'est le produit qui a appris a
/// regarder, pas la machine qui a change.</param>
/// <param name="Etats">Ce qui a ete lu.</param>
/// <param name="Conditions">Dans quel etat etait la machine pendant la lecture. <c>null</c> pour
/// les photos prises avant que le produit sache le mesurer.</param>
/// <param name="EmpreinteLecteur">L'identifiant du module qui a lu. La facon de lire peut changer a familles egales :
/// un lecteur d'arrets qui apprend a exclure les appuis longs ferait lire « arrets brutaux 11 → 10 » comme une machine
/// qui s'arrange. <c>null</c> pour les photos prises avant que le produit sache la noter.</param>
public sealed record RepairSnapshot(
    DateTimeOffset Quand,
    IReadOnlyList<string> DomainesExamines,
    IReadOnlyList<RepairEtat> Etats,
    ConditionsDeMesure? Conditions = null,
    string? EmpreinteLecteur = null,
    bool? Eleve = null,
    // Quel programme a pris la photo (« appli », « outil ») : les deux écrivent dans le même journal, et l'outil lit
    // aussi la RAM, le réseau et le BIOS ; comparée à une photo de l'outil, l'appli annoncerait des familles entières
    // « disparues ». null = photo écrite avant le champ.
    string? Lecteur = null);

/// <summary>
/// L'etat de la machine pendant l'audit, sans quoi un diff accuse la machine a tort. Mesure du 09/09/2026 :
/// `RAM / mode memoire : ExpectedDefault → Unknown` se lirait « double canal perdu », alors que des compilations
/// avaient fait tomber le debit a 18,7 GB/s, sous la borne qui prouve le multi-canal. Trois lectures d'un compteur.
/// </summary>
/// <param name="ChargeProcesseurPct">Charge processeur moyenne pendant la passe, ou <c>null</c>
/// si elle n'a pas pu etre lue, et « non lue » n'est pas « machine au repos ».</param>
/// <param name="DispersionDebitMemoirePct">L'ecart-type relatif du banc memoire, en pourcent. La charge processeur ne
/// suffit pas (mesure : deux passes a 19 % et 15 %, « comparables », ont fait basculer `RAM / mode memoire` de
/// `Unknown` a `ExpectedDefault`) : le banc est limite par la bande passante, qu'un autre programme sature sans faire
/// monter le CPU. La dispersion voit les passes vraiment agitees (12,6 a 26,6 %) ; les 8,2 % d'une passe pendant des
/// compilations restent dans la plage calme (voir DispersionQuiCompte). Une condition de mesure se choisit pour la
/// grandeur qu'elle qualifie.</param>
public sealed record ConditionsDeMesure(
    double? ChargeProcesseurPct,
    double? DispersionDebitMemoirePct = null);

/// <summary>Ce qui a bouge entre deux photos.</summary>
public enum RepairChangement
{
    /// <summary>Le constat existe maintenant et n'existait pas avant, dans une famille deja
    /// examinee. C'est le seul cas ou « apparu » veut dire quelque chose sur la machine.</summary>
    Apparu,

    /// <summary>Le constat n'est plus produit : repare, ou lecteur en echec cette fois. Jamais une preuve de
    /// reparation.</summary>
    Disparu,

    /// <summary>La valeur lue a change, le statut non.</summary>
    ValeurChangee,

    /// <summary>Le statut a change : c'est le jugement du produit qui a bouge.</summary>
    StatutChange,

    /// <summary>La famille n'etait pas examinee dans la photo precedente. Le produit a appris,
    /// la machine n'a rien fait.</summary>
    FamilleNouvelle,
}

/// <summary>Un changement, avec de quoi le lire sans se tromper.</summary>
public sealed record RepairDelta(
    string Domaine,
    string Nom,
    RepairChangement Quoi,
    string? Avant,
    string? Apres,
    string Phrase);

/// <summary>
/// Ce qui a change depuis la derniere fois : le pilier maintenir (une trajectoire, pas seulement un etat).
/// Deux facons de mentir avec un diff, fermees :
/// 1. « le produit a appris » lu comme « la machine s'est degradee » : la photo porte ses familles examinees, et un
///    constat d'une famille inconnue de la photo precedente ressort en `FamilleNouvelle` ;
/// 2. « disparu » lu comme « repare » (le lecteur a pu echouer) : le type ne dit jamais « repare », la phrase porte
///    l'ambiguite, et une famille entiere disparue designe d'abord le lecteur.
/// </summary>
public static class RepairHistory
{
    /// <summary>Au-dela de cet ecart de charge processeur, les deux photos n'ont pas ete prises dans le meme monde.
    /// **Non chiffré** : vingt points separent repos et travail sans se declencher sur un antivirus qui passe.</summary>
    public const double EcartDeChargeQuiCompte = 20.0;

    /// <summary>
    /// Les deux photos ont-elles ete prises dans des conditions comparables ? Rend la phrase a afficher quand ce n'est
    /// pas le cas, <c>null</c> sinon. Un avertissement sur la lecture du diff, jamais l'explication d'un constat precis.
    /// Une charge non lue (d'un cote ou des deux) ne declenche rien : « on ne sait pas » n'est pas « c'etait different ».
    /// </summary>
    public static string? AvertissementDeConditions(RepairSnapshot avant, RepairSnapshot apres)
    {
        var raisons = new List<string>();

        if (avant.Conditions?.ChargeProcesseurPct is { } a
            && apres.Conditions?.ChargeProcesseurPct is { } b
            && Math.Abs(a - b) >= EcartDeChargeQuiCompte)
            raisons.Add(Docteur.T("RH.ChargeProcesseur", a, b));

        // La dispersion est la qualite de chaque photo, pas un ecart entre elles : une seule passe agitee rend le diff
        // douteux (le cas vu sur la machine).
        foreach (var (photo, quand) in new[] { (avant, "RH.BancAgite.Avant"), (apres, "RH.BancAgite.Apres") })
            if (photo.Conditions?.DispersionDebitMemoirePct is { } d && d >= DispersionQuiCompte)
                raisons.Add(Docteur.T(quand, d));

        return raisons.Count == 0
            ? null
            : Docteur.T("RH.EtatsDifferents", string.Join(" · ", raisons));
    }

    /// <summary>
    /// Les deux photos ont-elles ete prises par le meme lecteur ? Rend la phrase a afficher quand
    /// non, <c>null</c> sinon. Une empreinte absente ne declenche rien : une photo d'avant cette
    /// notation ne dit pas qu'elle venait d'un autre lecteur.
    /// </summary>
    public static string? AvertissementDeLecteur(RepairSnapshot avant, RepairSnapshot apres)
        => avant.EmpreinteLecteur is { } a && apres.EmpreinteLecteur is { } b
           && !string.Equals(a, b, StringComparison.Ordinal)
            ? Docteur.T("RH.MisAJour")
            : null;

    /// <summary>Les deux photos ont-elles ete prises avec les memes droits ? Sans administrateur, la seconde lecture est
    /// refusee : la famille BCD « cesse de repondre », la latence des pilotes devient inconnue, sans que rien n'ait
    /// change. Des droits inconnus d'un cote ne declenchent rien.</summary>
    public static string? AvertissementDeDroits(RepairSnapshot avant, RepairSnapshot apres)
        => avant.Eleve is { } a && apres.Eleve is { } b && a != b
            ? (a
                ? Docteur.T("RH.DroitsPerdus")
                : Docteur.T("RH.DroitsGagnes"))
            : null;

    /// <summary>Au-dela de cette dispersion, le banc memoire a mesure l'agitation de la machine plus que sa memoire.
    /// Seuil mesure le 17/09/2026 sur 27 photos du journal avec dispersion (machine de reference).
    /// Machine calme (charge < 10 %) : 15 photos, dispersion mediane 3,9 %, maximum 8,7 %. Machine
    /// chargee : 12 photos, mediane 6,9 %, maximum 26,6 %. Ce que chaque seuil aurait fait :
    ///   5 % → avertit 15/27, dont 7 des 15 photos calmes   (avertit presque toujours)
    ///   8 % → avertit 8/27, dont 2 calmes
    ///  10 % → avertit 4/27, dont 0 calme, 4 des 12 chargees
    ///  15 % → avertit 2/27
    /// Dix est le premier seuil qui ne se declenche sur aucune photo calme et garde les passes
    /// vraiment agitees (12,6 · 21,7 · 26,6 %). Le 8,2 % « pendant des compilations » tombe dans
    /// la plage calme (max 8,7) : il n'est separable a aucun seuil. Reversible : une constante.</summary>
    public const double DispersionQuiCompte = 10.0;

    /// <summary>
    /// La photo à laquelle comparer : la plus récente prise par le même lecteur. Une photo sans `Lecteur` n'est gardée
    /// que si la photo actuelle lit toutes ses familles (sinon, de fausses disparitions). Aucune comparable ⇒ null.
    /// </summary>
    public static RepairSnapshot? PhotoComparable(IReadOnlyList<RepairSnapshot> photos, RepairSnapshot actuelle)
    {
        var famillesActuelles = new HashSet<string>(actuelle.DomainesExamines, StringComparer.OrdinalIgnoreCase);
        return photos
            .OrderByDescending(p => p.Quand)
            .FirstOrDefault(p => p.Lecteur is not null && actuelle.Lecteur is not null
                ? string.Equals(p.Lecteur, actuelle.Lecteur, StringComparison.Ordinal)
                // Une famille absente parce que les droits administrateur ont été perdus reste le même
                // lecteur : ces disparitions-là sont déjà écartées par l'avertissement de droits.
                : p.DomainesExamines.All(famillesActuelles.Contains) || (p.Eleve == true && actuelle.Eleve == false));
    }

    /// <summary>Compare deux photos. L'ordre compte : <paramref name="avant"/> est la plus ancienne.</summary>
    public static IReadOnlyList<RepairDelta> Comparer(RepairSnapshot avant, RepairSnapshot apres)
    {
        var famillesAvant = new HashSet<string>(avant.DomainesExamines, StringComparer.OrdinalIgnoreCase);
        var famillesApres = new HashSet<string>(apres.DomainesExamines, StringComparer.OrdinalIgnoreCase);

        // Le couple (domaine, nom) n'est pas unique : l'audit rend deux « BIOS / interface constructeur » (une par
        // interface WMI), et `ToDictionary` leverait « An item with the same key has already been added ». On groupe,
        // et un groupe de plusieurs entrees se compare comme un ensemble de valeurs.
        var avantParCle = avant.Etats.ToLookup(Cle, StringComparer.OrdinalIgnoreCase);
        var apresParCle = apres.Etats.ToLookup(Cle, StringComparer.OrdinalIgnoreCase);

        var deltas = new List<RepairDelta>();

        foreach (var groupe in apresParCle)
        {
            var e = groupe.First();

            if (!famillesAvant.Contains(e.Domaine))
            {
                deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.FamilleNouvelle,
                    null, e.Valeur,
                    Docteur.T("RH.FamilleNouvelle")));
                continue;
            }

            var vieuxGroupe = avantParCle[groupe.Key].ToList();
            if (vieuxGroupe.Count == 0)
            {
                deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.Apparu,
                    null, e.Valeur,
                    Docteur.T("RH.Apparu")));
                continue;
            }

            // Le cas simple, et de loin le plus frequent : une entree de chaque cote.
            if (vieuxGroupe.Count == 1 && groupe.Count() == 1)
            {
                var vieux = vieuxGroupe[0];
                if (vieux.Statut != e.Statut)
                    deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.StatutChange,
                        vieux.Statut.ToString(), e.Statut.ToString(),
                        Docteur.T("RH.JugementChange", RepairLibelles.Statut(vieux.Statut), RepairLibelles.Statut(e.Statut))));
                else if (vieux.Valeur != e.Valeur)
                    deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.ValeurChangee,
                        vieux.Valeur, e.Valeur, Docteur.T("RH.ValeurChangee")));
                continue;
            }

            // Plusieurs constats du meme nom n'ont pas d'identite : les apparier par ordre serait un tirage. On ne dit
            // que ce qui entre et ce qui sort de l'ensemble des valeurs.
            var valeursAvant = vieuxGroupe.Select(x => x.Valeur ?? "").ToHashSet(StringComparer.Ordinal);
            var valeursApres = groupe.Select(x => x.Valeur ?? "").ToHashSet(StringComparer.Ordinal);

            foreach (var neuve in valeursApres.Except(valeursAvant, StringComparer.Ordinal))
                deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.Apparu, null, neuve,
                    Docteur.T("RH.PlusieursMemeNom")));

            foreach (var partie in valeursAvant.Except(valeursApres, StringComparer.Ordinal))
                deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.Disparu, partie, null,
                    Docteur.T("RH.ValeurDisparue")));
        }

        // Les disparitions, traitees a part : c'est la moitie ambigue du diff.
        var famillesDisparues = famillesAvant.Except(famillesApres, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var groupeVieux in avantParCle)
        {
            if (apresParCle.Contains(groupeVieux.Key)) continue;
            var vieux = groupeVieux.First();

            // Une famille entiere qui se tait accuse le lecteur, pas des reparations en serie.
            var familleMuette = famillesDisparues.Contains(vieux.Domaine);

            deltas.Add(new RepairDelta(vieux.Domaine, vieux.Nom, RepairChangement.Disparu,
                vieux.Valeur, null,
                familleMuette
                    ? Docteur.T("RH.FamilleMuette")
                    : Docteur.T("RH.ConstatDisparu")));
        }

        return deltas;
    }

    private static string Cle(RepairEtat e) => e.Domaine + "\0" + e.Nom;
}
