using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Un constat d'audit (<see cref="RepairAuditService"/>) pret a afficher. Il ne juge pas : le tri est a
/// <see cref="RepairTriage"/>, la contrainte statut/action au constructeur de <see cref="RepairFinding"/>.
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
        // Une vingtaine de textes du moteur portent « (s) » ; ils s'accordent ici, une fois, au moment
        // d'être affichés. Le constat gardé pour l'historique reste tel quel (aucun faux « changement »).
        Explication = Pluriels.Accorder(f.Explication, Docteur.Langue);
        Remede = f.Remede is { } remede ? Pluriels.Accorder(remede, Docteur.Langue) : null;
        Cible = f.Cible;

        // `null` = pas lisible, et ca se dit. Rendre une chaine vide ferait passer un angle
        // mort pour une absence de probleme : la faute exacte que RepairStatus.Unknown existe
        // pour empecher.
        ValeurCourante = ValeurAffichee(f) ?? Docteur.T("RF.NonLisible");
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

    /// <summary>La forme commune, ce que l'écran affiche.</summary>
    public DrDxt.Core.Constat Constat { get; }

    public string Domaine { get; }

    /// <summary>La clé brute du domaine (celle du journal), pour ranger le constat sur la carte.</summary>
    public string DomaineCle { get; }
    public string NomCle { get; }
    public string Nom { get; }
    public string Explication { get; }

    /// <summary>Comment corriger, quand le constat le porte à part ; null sinon.</summary>
    public string? Remede { get; }

    /// <summary>Ce que le geste de DrDXT viserait, tel que lu ; null quand aucun geste ne s'y rattache.</summary>
    public string? Cible { get; }
    public string ValeurCourante { get; }

    /// <summary>Faux quand l'etat n'a pas pu etre lu : l'angle mort se voit au lieu de se taire.</summary>
    public bool ValeurLue { get; }

    /// <summary>Le default ou known-good, vide quand nous n'avons pas de reference honnete.</summary>
    public string ValeurAttendue { get; }

    public string Statut { get; }
    public string StatutLabel { get; }
    public string ActionLabel { get; }
    public string Provenance { get; }
    public bool NecessiteUneDecisionHumaine { get; }

    /// <summary>Reutilise l'echelle de gravite du scan pour que la barre de couleur du panneau
    /// soit la meme que partout ailleurs : un joueur n'apprend pas deux codes couleur.</summary>
    public Severity Severity { get; }

    // Les libellés suivent la langue du docteur, comme les phrases des constats.
    internal static string LibelleStatut(RepairStatus s) => RepairLibelles.Statut(s);

    // Les mots vivent dans Core (RepairLibelles) : le rapport de diagnostic dit les memes.
    internal static string LibelleDomaine(string cle) => RepairLibelles.Domaine(cle);

    /// <summary>La valeur à l'écran, accordée ; une valeur BCD dit ce qu'elle veut dire, son nom technique
    /// entre parenthèses. <c>null</c> = non lue. La valeur d'origine (comparée par l'historique) ne change pas.</summary>
    internal static string? ValeurAffichee(RepairFinding f) => f.ValeurCourante is not { } v ? null
        : f.Nom == "bootstatuspolicy" && SensDuDemarrage.TryGetValue(v, out var sens) ? $"{Docteur.T(sens)} ({v})"
        // L'incident du pilote graphique se dit en mots ; le code d'arrêt reste entre parenthèses pour qui le cherche.
        : f.Nom == "incident noyau conserve" && IncidentGraphique(v) is { } incident
            ? Docteur.T("RF.Incident.Graphique.Valeur", incident.Fois, incident.Code)
        // La version installée et la disponible sur une seule ligne.
        : f.Nom == DrDxt.Detection.Gpu.VersionDuPilote.Nom && f.ValeurAttendue is { Length: > 0 } dispo ? Docteur.T("RF.VersionInstalleeDisponible", v, dispo)
        : Pluriels.Accorder(v, Docteur.Langue);

    /// <summary>« 3x 0x193 VIDEO_DXGKRNL_LIVEDUMP (…) » : le nombre et le code, si le code est celui du pilote graphique.
    /// Le code se lit par <see cref="CodeDArret"/>, comme pour le titre : seul le nombre de fois se lit ici.</summary>
    private static (int Fois, string Code)? IncidentGraphique(string valeur) =>
        System.Text.RegularExpressions.Regex.Match(valeur, @"^(\d+)x ") is { Success: true } m
        && CodeDArret(valeur) is { } code && DrDxt.Detection.Hardware.LiveKernelReport.CodeGraphique(code)
            ? (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), $"0x{code:X}")
            : null;

    // Doc Microsoft « BCDEdit /set » (bootstatuspolicy), traduite au plus près.
    private static readonly Dictionary<string, string> SensDuDemarrage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IgnoreAllFailures"] = "RF.Bcd.IgnoreAll",
        ["IgnoreShutdownFailures"] = "RF.Bcd.IgnoreShutdown",
        ["IgnoreBootFailures"] = "RF.Bcd.IgnoreBoot",
        ["DisplayAllFailures"] = "RF.Bcd.DisplayAll",
    };

    /// <summary>Deux incidents noyau différents (USB 3, carte graphique) partagent un nom. Le titre
    /// affiché dit lequel, d'après ce que le constat dit déjà ; la clé (comparée par l'historique) ne change pas.</summary>
    internal static string TitreAffiche(RepairFinding f) => f.Nom switch
    {
        // Reconnus au code d'arrêt (« 3x 0x193 VIDEO_… »), pas à la phrase : « carte graphique » échouait en anglais.
        "incident noyau conserve" when CodeDArret(f.ValeurCourante) == 0x144 => Docteur.T("RF.IncidentUsb3"),
        "incident noyau conserve" when CodeDArret(f.ValeurCourante) is { } code && DrDxt.Detection.Hardware.LiveKernelReport.CodeGraphique(code)
            => Docteur.T("RF.IncidentGraphique"),
        _ => RepairLibelles.Nom(f.Domaine, f.Nom),
    };

    private static uint? CodeDArret(string? valeur) =>
        valeur is null ? null
        : System.Text.RegularExpressions.Regex.Match(valeur, @"0x([0-9A-Fa-f]+)") is { Success: true } m
          && uint.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : null;

    private static string LibelleAction(RepairAction a) => a switch
    {
        RepairAction.ReparationAutomatique => Docteur.T("RF.Action.Reparer"),
        RepairAction.GuidedBios            => Docteur.T("RF.Action.Bios"),
        RepairAction.Recommandation        => Docteur.T("RF.Action.Recommandation"),
        _                                  => string.Empty,
    };

}
