using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Un constat d'audit, pret a afficher.
///
/// Ce type existe parce que l'audit ne sortait nulle part. Le 09/09/2026, onze types de
/// lecture — <see cref="RepairAuditService"/> compris — n'etaient references ni par
/// <c>DrDxt.App</c> ni par <c>DrDxt.Application</c> : deux jours de travail prouve, testes,
/// et invisibles de quiconque ouvre le produit. Un outil prouve qu'un lecteur fonctionne ;
/// il ne met rien entre les mains de quelqu'un.
///
/// Ce VM ne juge pas : le tri appartient a <see cref="RepairTriage"/>, la contrainte
/// statut/action au constructeur de <see cref="RepairFinding"/>. Ici on ne fait que mettre en
/// forme — sinon la regle vivrait a deux endroits et divergerait au premier ajout.
/// </summary>
public sealed class RepairFindingViewModel
{
    public RepairFindingViewModel(RepairFinding f)
    {
        ArgumentNullException.ThrowIfNull(f);

        Domaine = LibelleDomaine(f.Domaine);
        DomaineCle = f.Domaine;
        NomCle = f.Nom;
        Nom = TitreAffiche(f);
        // UI1 (08/10/2026) : une vingtaine de textes du moteur portent « (s) » ; ils s'accordent ici, une fois, au moment
        // d'être affichés. Le constat gardé pour l'historique reste tel quel (aucun faux « changement »).
        Explication = Pluriels.Accorder(f.Explication, Francais);

        // `null` = pas LISIBLE, et ca se dit. Rendre une chaine vide ferait passer un angle
        // mort pour une absence de probleme — la faute exacte que RepairStatus.Unknown existe
        // pour empecher.
        ValeurCourante = ValeurAffichee(f) ?? "non lisible";
        ValeurLue = f.ValeurCourante is not null;

        // Le default n'est affiche que quand on en a un defendable. Fabriquer une reference
        // pour remplir la ligne donnerait au joueur un ecart qui n'existe pas.
        ValeurAttendue = f.ValeurAttendue ?? string.Empty;

        Statut = f.Statut.ToString();
        StatutLabel = LibelleStatut(f.Statut);
        ActionLabel = LibelleAction(f.Action);
        NecessiteUneDecisionHumaine = f.NecessiteUneDecisionHumaine;

        // Provenance : affichee seulement si elle a ete reellement identifiee. Nommer un
        // coupable qu'on n'a pas identifie donne une certitude fausse.
        Provenance = f.Provenance ?? string.Empty;

        Severity = DrDxt.Core.Constat.GraviteDe(f.Statut);
        Constat = DrDxt.Core.Constat.Depuis(f);
    }

    /// <summary>chantier B0 : la forme commune, ce que l'écran affiche.</summary>
    public DrDxt.Core.Constat Constat { get; }

    public string Domaine { get; }

    /// <summary>La clé brute du domaine (celle du journal), pour ranger le constat sur la carte.</summary>
    public string DomaineCle { get; }
    public string NomCle { get; }
    public string Nom { get; }
    public string Explication { get; }
    public string ValeurCourante { get; }

    /// <summary>Faux quand l'etat n'a pas pu etre lu — l'angle mort se voit au lieu de se taire.</summary>
    public bool ValeurLue { get; }

    /// <summary>Le default ou known-good, vide quand nous n'avons pas de reference honnete.</summary>
    public string ValeurAttendue { get; }

    public string Statut { get; }
    public string StatutLabel { get; }
    public string ActionLabel { get; }
    public string Provenance { get; }
    public bool NecessiteUneDecisionHumaine { get; }

    /// <summary>Reutilise l'echelle de gravite du scan pour que la barre de couleur du panneau
    /// soit la meme que partout ailleurs — un joueur n'apprend pas deux codes couleur.</summary>
    public Severity Severity { get; }

    // Simplification voulue : libelles en dur, comme `RepairFinding.Explication` et `RepairTriage.Phrase` qui
    // sont deja de la prose francaise dans Core. Passer par le resx ici et pas la donnerait une
    // moitie de section traduite et l'autre non. Le jour ou l'audit passe au resx, il y passe en
    // entier — Core compris.
    internal static string LibelleStatut(RepairStatus s) => s switch
    {
        RepairStatus.KnownHarmful      => "nuisible connu",
        RepairStatus.Suspicious        => "suspect",
        RepairStatus.NonDefault        => "différent du défaut",
        RepairStatus.UserChoiceProbable => "choix probable de ta part",
        RepairStatus.ExpectedDefault   => "valeur par défaut",
        RepairStatus.Unknown           => "non lu",
        _                              => "observé",
    };

    // Les mots vivent dans Core (RepairLibelles) : le rapport de diagnostic dit les memes.
    internal static string LibelleDomaine(string cle) => RepairLibelles.Domaine(cle);
    internal static string LibelleNom(string cle) => RepairLibelles.Nom(cle);

    /// <summary>UI1 (08/10/2026) : deux incidents noyau différents (USB 3, carte graphique) partageaient un titre. Le titre
    /// affiché dit lequel, d'après ce que le constat dit déjà ; la clé (comparée par l'historique) ne change pas.</summary>
    /// <summary>UI1 (08/10/2026) : la valeur à l'écran, accordée ; une valeur BCD dit ce qu'elle veut dire, son nom technique
    /// entre parenthèses. <c>null</c> = non lue. La valeur d'origine (comparée par l'historique) ne change pas.</summary>
    internal static string? ValeurAffichee(RepairFinding f) => f.ValeurCourante is not { } v ? null
        : f.Nom == "bootstatuspolicy" && SensDuDemarrage.TryGetValue(v, out var sens) ? $"{sens} ({v})"
        // UI1 (critique du 08/10, défaut 11) : la version installée et la disponible sur une seule ligne.
        : f.Nom == DrDxt.Detection.Gpu.VersionDuPilote.Nom && f.ValeurAttendue is { Length: > 0 } dispo ? $"{v} installé → {dispo} disponible"
        : Pluriels.Accorder(v, Francais);

    // Les textes du moteur sont écrits en français, quelle que soit la langue de l'interface.
    private static readonly System.Globalization.CultureInfo Francais = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    // Doc Microsoft « BCDEdit /set » (bootstatuspolicy), traduite au plus près.
    private static readonly Dictionary<string, string> SensDuDemarrage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IgnoreAllFailures"] = "erreurs de démarrage ignorées",
        ["IgnoreShutdownFailures"] = "réglage par défaut",
        ["IgnoreBootFailures"] = "échecs de démarrage ignorés",
        ["DisplayAllFailures"] = "écran de récupération toujours affiché",
    };

    internal static string TitreAffiche(RepairFinding f) => f.Nom switch
    {
        "incident noyau conserve" when f.ValeurCourante?.Contains("USB 3", StringComparison.Ordinal) == true
            => "Incident du pilote USB 3, conservé",
        "incident noyau conserve" when f.Explication.Contains("carte graphique", StringComparison.Ordinal)
            => "Incident du pilote graphique, conservé",
        _ => LibelleNom(f.Nom),
    };

    private static string LibelleAction(RepairAction a) => a switch
    {
        RepairAction.ReparationAutomatique => "DrDXT peut le réparer",
        RepairAction.GuidedBios            => "passage guidé par le BIOS",
        RepairAction.Recommandation        => "recommandation",
        _                                  => string.Empty,
    };

}
