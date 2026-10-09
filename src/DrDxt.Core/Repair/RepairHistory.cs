namespace DrDxt.Core;

/// <summary>L'etat d'un constat a un instant, reduit a ce qu'une comparaison a besoin de savoir.</summary>
public sealed record RepairEtat(string Domaine, string Nom, string? Valeur, RepairStatus Statut);

/// <summary>
/// Une photo de la machine, prise par une VERSION donnee du produit.
/// </summary>
/// <param name="Quand">L'instant de la photo.</param>
/// <param name="DomainesExamines">Les familles que le produit savait lire ce jour-la.
///
/// Sans ce champ, la comparaison ment. Le 08/09/2026 l'audit a gagne les familles MMCSS,
/// SERVICES, reseau et thermique en une journee : une photo de la veille comparee a celle du soir
/// aurait affiche des dizaines de constats « APPARUS », comme si la machine s'etait degradee. Ce
/// n'est pas la machine qui a change, c'est le produit qui a appris a regarder.</param>
/// <param name="Etats">Ce qui a ete lu.</param>
/// <param name="Conditions">Dans quel etat etait la machine pendant la lecture. <c>null</c> pour
/// les photos prises avant que le produit sache le mesurer.</param>
/// <param name="EmpreinteLecteur">quel lecteur a pris la photo : l'identifiant du module qui lit.
///
/// 16/09/2026 : entre deux photos, le lecteur d'arrets a appris a exclure les appuis longs sur
/// le bouton, et le journal a lu « arrets brutaux 11 → 10 » comme une machine qui s'arrange. Les
/// familles examinees etaient les memes : seule la facon de lire avait change. <c>null</c> pour
/// les photos prises avant que le produit sache la noter.</param>
public sealed record RepairSnapshot(
    DateTimeOffset Quand,
    IReadOnlyList<string> DomainesExamines,
    IReadOnlyList<RepairEtat> Etats,
    ConditionsDeMesure? Conditions = null,
    string? EmpreinteLecteur = null,
    bool? Eleve = null,
    // 17/09/2026 (D21) : quel programme a pris la photo (« appli », « outil »). Les deux écrivent
    // dans le même journal et ne lisent pas les mêmes familles : l'outil lit aussi la RAM, le réseau
    // et le BIOS. Sans ce champ, l'appli se comparait à une photo de l'outil et annonçait des
    // familles entières « disparues ». null = photo écrite avant le champ.
    string? Lecteur = null);

/// <summary>
/// L'etat de la machine pendant l'audit — sans quoi un diff accuse la machine a tort.
///
/// Vu en vrai le 09/09/2026. Le journal a annonce `RAM / mode memoire : ExpectedDefault →
/// Unknown`, ce qui se lit comme « la machine a perdu son double canal ». Elle n'avait rien perdu :
/// le debit mesure etait tombe a 18,7 GB/s parce que des compilations tournaient, donc la borne
/// qui prouve le multi-canal ne tenait plus. Le constat etait juste et sa lecture etait fausse.
///
/// Ce qui distingue les deux n'est pas dans le constat, c'est autour : la CHARGE au moment de
/// la lecture. La porter dans la photo coute trois lectures d'un compteur et evite au produit
/// d'accuser une machine qui n'a rien fait.
/// </summary>
/// <param name="ChargeProcesseurPct">Charge processeur moyenne pendant la passe, ou <c>null</c>
/// si elle n'a pas pu etre lue — et « non lue » n'est pas « machine au repos ».</param>
/// <param name="DispersionDebitMemoirePct">L'ecart-type relatif du banc memoire, en pourcent.
///
/// Ajoute parce que la charge processeur ne suffit pas, et c'est mesure : deux passes a 19 %
/// et 15 % de charge — donc « comparables » selon ce critere — ont fait basculer
/// `RAM / mode memoire` de `Unknown` a `ExpectedDefault`. Le banc memoire est limite par la BANDE
/// PASSANTE, pas par le processeur : un autre programme peut saturer la memoire sans faire monter
/// la charge CPU. La dispersion du banc, elle, le voit — elle etait a 8,2 % sur la passe agitee.
///
/// La lecon generale : une condition de mesure doit etre choisie pour la grandeur qu'elle
/// qualifie, pas parce qu'elle est facile a lire.</param>
public sealed record ConditionsDeMesure(
    double? ChargeProcesseurPct,
    double? DispersionDebitMemoirePct = null);

/// <summary>Ce qui a bouge entre deux photos.</summary>
public enum RepairChangement
{
    /// <summary>Le constat existe maintenant et n'existait pas avant, dans une famille deja
    /// examinee. C'est le seul cas ou « apparu » veut dire quelque chose sur la machine.</summary>
    Apparu,

    /// <summary>Le constat n'est plus produit. Cela PEUT vouloir dire repare — et cela peut
    /// tout autant vouloir dire que le lecteur a echoue cette fois. Jamais une preuve de
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
/// Ce qui a change depuis la derniere fois — le pilier maintenir, qui etait vide.
///
/// L'audit recalculait tout a chaque passe et ne gardait rien. « Est-ce que quelque chose a
/// change depuis hier ? » n'avait pas de reponse, et une reparation ne pouvait pas etre prouvee
/// dans le temps : on voyait un etat, jamais une trajectoire.
///
/// ══ les deux facons de mentir avec un diff, et comment elles sont fermees ══
///
/// **1. Confondre « le produit a appris » avec « la machine s'est degradee ».** Le 08/09/2026,
/// l'audit a gagne quatre familles en une journee. Un diff naif aurait affiche des dizaines
/// d'« APPARUS » alarmants. La photo porte donc la liste des familles EXAMINEES, et un constat
/// dans une famille inconnue de la photo precedente ressort en `FamilleNouvelle`.
///
/// **2. Confondre « disparu » avec « repare ».** Un constat qui n'est plus produit peut avoir ete
/// corrige — ou son lecteur peut avoir echoue. Les deux se ressemblent parfaitement. Le type ne
/// dit donc jamais « repare », et la phrase porte l'ambiguite. Quand une famille ENTIERE
/// disparait, la seconde explication devient la plus probable, et c'est dit.
/// </summary>
public static class RepairHistory
{
    /// <summary>Au-dela de cet ecart de charge processeur, les deux photos n'ont pas ete prises
    /// dans le meme monde.
    ///
    /// **NON CHIFFRÉ.** Vingt points separent une machine au repos d'une machine qui travaille,
    /// sans se declencher sur le bruit d'un antivirus qui passe. Ecrit ici pour etre discute.</summary>
    public const double EcartDeChargeQuiCompte = 20.0;

    // 12/09/2026 : la phrase est francaise, ses nombres aussi — la culture du poste n'y entre pas
    // (un runner en-US rendait « 8.2 % »). Meme regle que KeepPolicy et PlafondGpu.
    private static readonly System.Globalization.CultureInfo Fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>
    /// Les deux photos ont-elles ete prises dans des conditions comparables ? Rend la phrase a
    /// afficher quand NON, <c>null</c> quand rien ne s'y oppose.
    ///
    /// C'est un AVERTISSEMENT sur la lecture du diff, jamais une explication d'un changement
    /// precis. Le produit ne sait pas dire quel constat a bouge a cause de la charge — il sait
    /// dire que la question se pose, et c'est deja ce qui manquait pour ne pas accuser la machine
    /// a tort.
    ///
    /// Deux charges NON lues ne declenchent rien : « on ne sait pas » n'est pas « c'etait
    /// different ». Une seule lue non plus — comparer un nombre a une absence n'a pas de sens.
    /// </summary>
    public static string? AvertissementDeConditions(RepairSnapshot avant, RepairSnapshot apres)
    {
        var raisons = new List<string>();

        if (avant.Conditions?.ChargeProcesseurPct is { } a
            && apres.Conditions?.ChargeProcesseurPct is { } b
            && Math.Abs(a - b) >= EcartDeChargeQuiCompte)
            raisons.Add($"charge processeur {a.ToString("0", Fr)} % puis {b.ToString("0", Fr)} %");

        // La dispersion n'est pas une DIFFERENCE entre les deux photos : c'est la qualite de
        // chacune. Une seule passe agitee suffit a rendre le diff douteux, meme si l'autre etait
        // parfaite — et c'est ce cas-la qui s'est produit sur la machine.
        foreach (var (photo, quand) in new[] { (avant, "du scan précédent"), (apres, "de celui-ci") })
            if (photo.Conditions?.DispersionDebitMemoirePct is { } d && d >= DispersionQuiCompte)
                raisons.Add($"le banc mémoire {quand} était agité ({d.ToString("0.0", Fr)} % de dispersion)");

        return raisons.Count == 0
            ? null
            : "Les deux scans n'ont pas été faits dans le même état : "
              + string.Join(" · ", raisons)
              + ". Un changement de jugement peut venir de là, et non de la machine : en "
              + "particulier pour les constats dont la conclusion repose sur une mesure (débit "
              + "mémoire, fréquence effective, pertes réseau).";
    }

    /// <summary>
    /// Les deux photos ont-elles ete prises par le meme lecteur ? Rend la phrase a afficher quand
    /// non, <c>null</c> sinon. Une empreinte absente ne declenche rien : une photo d'avant cette
    /// notation ne dit pas qu'elle venait d'un autre lecteur.
    /// </summary>
    public static string? AvertissementDeLecteur(RepairSnapshot avant, RepairSnapshot apres)
        => avant.EmpreinteLecteur is { } a && apres.EmpreinteLecteur is { } b
           && !string.Equals(a, b, StringComparison.Ordinal)
            ? "DrDXT a été mis à jour entre les deux scans. Un changement de valeur ou de "
              + "jugement peut venir de sa nouvelle façon de lire, et non de la machine."
            : null;

    /// <summary>
    /// Les deux photos ont-elles ete prises avec les memes droits ? 16/09/2026 : une photo prise en
    /// administrateur puis une sans a fait dire au journal que la famille BCD avait « cesse de
    /// repondre » et que la latence des pilotes etait devenue inconnue. Rien n'avait change : la
    /// seconde lecture avait ete refusee. Des droits inconnus d'un cote ne declenchent rien.
    /// </summary>
    public static string? AvertissementDeDroits(RepairSnapshot avant, RepairSnapshot apres)
        => avant.Eleve is { } a && apres.Eleve is { } b && a != b
            ? (a
                ? "Le scan précédent a été fait avec les droits administrateur, celui-ci sans. "
                  + "Ce qui exige l'administrateur n'a pas pu être relu : ce n'est pas un changement de la machine."
                : "Le scan précédent a été fait sans les droits administrateur, celui-ci avec. "
                  + "Ce qui apparaît dans les lectures réservées à l'administrateur était peut-être déjà là.")
            : null;

    /// <summary>Au-dela de cette dispersion, le banc memoire a mesure l'agitation de la machine
    /// plus que sa memoire.
    ///
    /// **NON CHIFFRÉ.** Le banc rend habituellement 1 a 3 % sur cette machine ; il est monte a
    /// 8,2 % pendant des compilations. Cinq separe les deux regimes sans se declencher sur le
    /// bruit ordinaire. Ecrit ici pour etre discute.
    ///
    /// Dementi par le journal le 17/09/2026. Sur les 13 dernieres photos qui portent une
    /// dispersion (09/09 → 17/09, machine de reference), elle vaut 3,1 a 26,6 % et **9 sur 13 sont
    /// au-dessus de 5**, dont des passes a 2,3 et 2,7 % de charge processeur (6,1 et 7,0 %). Le « 1 a
    /// 3 % habituel » ne se retrouve pas : la plage ordinaire est plutot 3 a 8 %, et les passes
    /// vraiment agitees montent a 21-27 %. A 5, l'avertissement s'affiche presque a chaque
    /// comparaison et cesse d'avertir. Le seuil n'est pas change ici : 8,2 % a ete mesure pendant des
    /// compilations, donc aucun seuil ne separe proprement les deux regimes sur ces 13 points.
    /// Decision ouverte (D18 du registre de boucle).
    ///
    /// Tranche sur mesure le 17/09/2026 (04h45), 27 photos du journal avec dispersion (09/09 → 17/09).
    /// Machine calme (charge < 10 %) : 15 photos, dispersion mediane 3,9 %, maximum 8,7 %. Machine
    /// chargee : 12 photos, mediane 6,9 %, maximum 26,6 %. Ce que chaque seuil aurait fait :
    ///   5 % → avertit 15/27, dont 7 des 15 photos calmes   (le seuil d'origine : illisible)
    ///   8 % → avertit 8/27, dont 2 calmes
    ///  10 % → avertit 4/27, dont 0 calme, 4 des 12 chargees
    ///  15 % → avertit 2/27
    /// Dix est le premier seuil qui ne se declenche sur aucune photo calme et garde les passes
    /// vraiment agitees (12,6 · 21,7 · 26,6 %). Le 8,2 % « pendant des compilations » tombe dans
    /// la plage calme (max 8,7) : il n'etait pas separable, a aucun seuil. Reversible : une constante.</summary>
    public const double DispersionQuiCompte = 10.0;

    /// <summary>Compare deux photos. L'ordre compte : <paramref name="avant"/> est la plus ancienne.</summary>
    /// <summary>
    /// La photo à laquelle comparer : la plus récente prise par le même lecteur. Une photo écrite
    /// avant le champ `Lecteur` n'est gardée que si toutes ses familles sont lues par la photo
    /// actuelle ; sinon elle vient d'un lecteur qui en lit davantage, et la comparer ferait parler
    /// de disparitions qui n'en sont pas. Aucune photo comparable ⇒ null : on ne dit rien plutôt
    /// que de comparer deux lecteurs.
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

    public static IReadOnlyList<RepairDelta> Comparer(RepairSnapshot avant, RepairSnapshot apres)
    {
        var famillesAvant = new HashSet<string>(avant.DomainesExamines, StringComparer.OrdinalIgnoreCase);
        var famillesApres = new HashSet<string>(apres.DomainesExamines, StringComparer.OrdinalIgnoreCase);

        // Un dictionnaire ici levait sur l'entree reelle. Le couple (domaine, nom) n'est pas
        // unique : l'audit rend deux « BIOS / interface constructeur » — une par interface WMI
        // trouvee — et `ToDictionary` a jete « An item with the same key has already been added ».
        // Trouve en EXECUTANT, pas en relisant : le code compilait et ses sept tests passaient,
        // parce qu'aucun ne contenait de doublon.
        //
        // On groupe donc, et un groupe a plusieurs entrees se compare comme un ensemble de
        // valeurs. Une bibliotheque qui leve sur une donnee legitime est un defaut, quelle que
        // soit la donnee.
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
                    "cette famille n'était pas examinée la fois précédente : c'est le "
                    + "produit qui a appris à la lire, pas la machine qui a changé"));
                continue;
            }

            var vieuxGroupe = avantParCle[groupe.Key].ToList();
            if (vieuxGroupe.Count == 0)
            {
                deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.Apparu,
                    null, e.Valeur,
                    "ce constat n'existait pas la fois précédente, dans une famille déjà examinée"));
                continue;
            }

            // Le cas simple, et de loin le plus frequent : une entree de chaque cote.
            if (vieuxGroupe.Count == 1 && groupe.Count() == 1)
            {
                var vieux = vieuxGroupe[0];
                if (vieux.Statut != e.Statut)
                    deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.StatutChange,
                        vieux.Statut.ToString(), e.Statut.ToString(),
                        $"le jugement du produit a changé : {vieux.Statut} → {e.Statut}"));
                else if (vieux.Valeur != e.Valeur)
                    deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.ValeurChangee,
                        vieux.Valeur, e.Valeur, "la valeur lue a changé, le jugement non"));
                continue;
            }

            // Plusieurs constats portent le meme nom : ils n'ont pas d'identite individuelle, et
            // les APPARIER par ordre serait un tirage. On compare donc l'ensemble des valeurs, et
            // on ne parle que de ce qui entre et de ce qui sort.
            var valeursAvant = vieuxGroupe.Select(x => x.Valeur ?? "").ToHashSet(StringComparer.Ordinal);
            var valeursApres = groupe.Select(x => x.Valeur ?? "").ToHashSet(StringComparer.Ordinal);

            foreach (var neuve in valeursApres.Except(valeursAvant, StringComparer.Ordinal))
                deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.Apparu, null, neuve,
                    "plusieurs constats portent ce nom : seule l'entrée et la sortie de valeurs "
                    + "sont comparables, pas leur appariement"));

            foreach (var partie in valeursAvant.Except(valeursApres, StringComparer.Ordinal))
                deltas.Add(new RepairDelta(e.Domaine, e.Nom, RepairChangement.Disparu, partie, null,
                    "cette valeur n'est plus rendue. Cela peut vouloir dire réparé, et cela peut "
                    + "tout autant vouloir dire que la lecture a échoué cette fois"));
        }

        // Les disparitions, traitees a part : c'est la moitie ambigue du diff.
        var famillesDisparues = famillesAvant.Except(famillesApres, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var groupeVieux in avantParCle)
        {
            if (apresParCle.Contains(groupeVieux.Key)) continue;
            var vieux = groupeVieux.First();

            // Une famille entiere qui se tait accuse le LECTEUR bien plus qu'elle ne prouve des
            // reparations en serie. Le dire evite qu'un utilisateur croie avoir tout repare.
            var familleMuette = famillesDisparues.Contains(vieux.Domaine);

            deltas.Add(new RepairDelta(vieux.Domaine, vieux.Nom, RepairChangement.Disparu,
                vieux.Valeur, null,
                familleMuette
                    ? "toute cette famille a cessé de répondre : c'est bien plus "
                      + "probablement le lecteur qui a échoué qu'une série de réparations"
                    : "ce constat n'est plus produit. Cela peut vouloir dire réparé, et cela "
                      + "peut tout autant vouloir dire que la lecture a échoué cette fois : les "
                      + "deux se ressemblent, et rien ici ne les distingue"));
        }

        return deltas;
    }

    private static string Cle(RepairEtat e) => e.Domaine + "\0" + e.Nom;
}
