namespace DrDxt.Core;

/// <summary>
/// Ce que DrDXT sait faire d'un reglage BIOS, sur cette plateforme. Doctrine : aucune ecriture BIOS generique ou non
/// qualifiee (pas « aucune pour toujours ») ; ecrire seulement la ou une methode fiable a ete qualifiee. Il n'existe
/// exprès aucune valeur `GenericBiosWrite` : l'ajouter devrait se faire explicitement, et se verrait.
/// </summary>
public enum BiosCapabilityLevel
{
    /// <summary>Aucune voie sure connue sur cette plateforme. → PDF et QR uniquement.</summary>
    Unsupported,

    /// <summary>DrDXT connait le reglage et la procedure, sans pouvoir l'ecrire surement. → PDF et QR, puis validation
    /// apres redemarrage. Le repli universel, qui ne disparait jamais.</summary>
    Guided,

    /// <summary>Une interface officielle du constructeur lit et ecrit. → automatisable apres qualification.</summary>
    VendorSupportedWrite,

    /// <summary>Une voie firmware/UEFI validee pour une carte, une version de BIOS et un reglage exacts. →
    /// automatisable uniquement dans cette matrice.</summary>
    QualifiedFirmwareWrite,
}

/// <summary>
/// La reversibilite d'un reglage BIOS ne se deduit pas de l'ancienne valeur : si la machine ne passe plus le POST,
/// Windows ne s'execute pas et `Revert()` ne tourne jamais. Aucun code ne peut ecrire « reversible » depuis un snapshot.
/// </summary>
public enum BiosRecoverability
{
    /// <summary>On ne sait pas. Le defaut, et il interdit toute ecriture automatique.</summary>
    RecoveryUnknown,

    /// <summary>Windows peut annuler seul : la machine demarre toujours.</summary>
    SoftwareRevertPossible,

    /// <summary>Le firmware sait revenir en arriere seul : retour automatique apres un POST rate,
    /// profil de secours, double BIOS.</summary>
    FirmwareRevertPossible,

    /// <summary>Seule une intervention physique recupere la machine (cavalier Clear CMOS, pile, bouton de secours) :
    /// quelqu'un doit ouvrir le boitier, et le joueur doit le savoir avant.</summary>
    PhysicalRecoveryAvailable,
}

/// <summary>
/// Le risque de ne plus demarrer (« peut-on y arriver ? »), distinct de `BiosRecoverability` (« comment revient-on ? »).
/// Un XMP peut laisser un ecran noir sur une carte parfaitement recuperable : le joueur doit le savoir avant de cliquer.
/// </summary>
public enum NoPostRisk
{
    /// <summary>Pas etabli. Le defaut, et il n'autorise rien.</summary>
    NoPostRiskUnknown,

    /// <summary>Le reglage n'agit qu'apres l'initialisation materielle.</summary>
    CannotPreventPost,

    /// <summary>Le reglage touche l'initialisation memoire, le bus PCIe ou la sequence de demarrage : un ecran noir est
    /// un resultat possible et normal.</summary>
    CanPreventPost,
}

/// <summary>
/// Une entree de la matrice de qualification. Le firmware fait partie de l'empreinte : valide sur « carte A / BIOS X »
/// ne vaut pas sur « carte A / BIOS Y ». Une ecriture automatique n'existe que pour une entree qualifiee en entier.
/// </summary>
/// <param name="Board">La carte exacte.</param>
/// <param name="FirmwareVersion">La version de BIOS exacte.</param>
/// <param name="Setting">Le reglage, tel que le firmware le nomme.</param>
/// <param name="CurrentValue">Ce qui est lu, ou <c>null</c> si illisible.</param>
/// <param name="TargetValue">Ce qui serait ecrit.</param>
/// <param name="Provider">Qui saurait l'ecrire.</param>
/// <param name="Capability">Ce que cette plateforme autorise.</param>
/// <param name="Recoverability">Comment on revient en arriere si ca tourne mal.</param>
/// <param name="RequiresReboot">Presque toujours vrai ; explicite pour ne pas l'oublier dans un parcours.</param>
/// <param name="Validated">Reellement validee sur cette matrice ? Faux par defaut.</param>
/// <param name="BoardRevision">Deux revisions d'un meme nom commercial n'ont pas forcement le meme firmware ni les
/// memes cavaliers.</param>
/// <param name="MenuPath">Ou cliquer, tel que le firmware l'affiche. <c>null</c> = inconnu.</param>
/// <param name="PossibleValues">Ce que le reglage accepte. <c>null</c> = jamais releve.</param>
/// <param name="DefaultValue">Ce que le firmware y met d'usine. <c>null</c> = inconnu, jamais « Auto » suppose.</param>
/// <param name="NoPost">Ce reglage peut-il laisser un ecran noir ? Voir <see cref="NoPostRisk"/>.</param>
/// <param name="ReadMechanism">Comment on lit la valeur courante. <c>null</c> = illisible, d'ou un
/// <paramref name="CurrentValue"/> nul.</param>
/// <param name="DocumentationSource">D'ou vient cette connaissance. Exigee des qu'une entree se declare validee : une
/// validation sans source n'est verifiable par personne.</param>
public sealed record BiosQualification(
    string Board,
    string FirmwareVersion,
    string Setting,
    string? CurrentValue,
    string TargetValue,
    string Provider,
    BiosCapabilityLevel Capability,
    BiosRecoverability Recoverability,
    bool RequiresReboot = true,
    bool Validated = false,
    string? BoardRevision = null,
    string? MenuPath = null,
    IReadOnlyList<string>? PossibleValues = null,
    string? DefaultValue = null,
    NoPostRisk NoPost = NoPostRisk.NoPostRiskUnknown,
    string? ReadMechanism = null,
    string? DocumentationSource = null,
    // Ou le reglage vit dans la NVRAM, releve dans l'IFR du BIOS officiel de cette
    // version. null = jamais releve : la valeur courante reste illisible.
    NvramLocation? Nvram = null)
{
    /// <summary>
    /// Une validation sans source n'est pas une validation : `Validated` est le seul champ qui debloque l'ecriture
    /// automatique, et une supposition ne doit pas pouvoir l'autoriser. La regle vit dans le constructeur et dans les
    /// accesseurs init (un `with` passe par eux, jamais par les initialiseurs).
    /// </summary>
    private readonly bool _validated = ValidationSourcee(Validated, DocumentationSource, Board, FirmwareVersion, Setting);
    public bool Validated
    {
        get => _validated;
        init => _validated = ValidationSourcee(value, _documentationSource, Board, FirmwareVersion, Setting);
    }

    private readonly string? _documentationSource = DocumentationSource;
    public string? DocumentationSource
    {
        get => _documentationSource;
        init { ValidationSourcee(_validated, value, Board, FirmwareVersion, Setting); _documentationSource = value; }
    }

    private static bool ValidationSourcee(bool validated, string? source, string board, string firmware, string setting) =>
        validated && string.IsNullOrWhiteSpace(source)
            ? throw new ArgumentException(
                $"l'entree « {board} / {firmware} / {setting} » se declare validee sans "
                + "DocumentationSource: a validation without a source cannot be checked",
                nameof(Validated))
            : validated;

    /// <summary>
    /// DrDXT a-t-il le droit d'ecrire ceci tout seul ? Cinq conditions, une seule manquante renvoie au guide : capacite
    /// qualifiee, validation reelle, recuperation connue, risque de non-demarrage etabli, valeur courante lisible (sans
    /// elle on ne saurait pas dire ce qu'on a change).
    /// </summary>
    public bool EcritureAutomatiqueAutorisee =>
        Capability is BiosCapabilityLevel.VendorSupportedWrite or BiosCapabilityLevel.QualifiedFirmwareWrite
        && Validated
        && Recoverability != BiosRecoverability.RecoveryUnknown
        && NoPost != NoPostRisk.NoPostRiskUnknown
        && CurrentValue is not null;

    /// <summary>
    /// Tout ce qui manque, pas seulement le premier (meme regle que `CandidateAppraisal.Motifs`) : sur la carte de
    /// reference, « aucune voie qualifiee » cacherait la recuperation et la valeur illisible, trois obstacles et non un.
    /// </summary>
    public IReadOnlyList<string> CeQuiManqueTout
    {
        get
        {
            var manques = new List<string>();

            if (Capability is BiosCapabilityLevel.Unsupported or BiosCapabilityLevel.Guided)
                manques.Add(Docteur.T("BC.SansVoie"));

            if (!Validated)
                manques.Add(Docteur.T("BC.NonValidee"));

            if (Recoverability == BiosRecoverability.RecoveryUnknown)
                manques.Add(Docteur.T("BC.SansRecuperation"));

            if (NoPost == NoPostRisk.NoPostRiskUnknown)
                manques.Add(Docteur.T("BC.NoPostInconnu"));

            if (CurrentValue is null)
                manques.Add(Docteur.T("BC.ValeurIllisible"));

            return manques;
        }
    }

    /// <summary>Ce qui manque, en une ligne, ou <c>null</c> quand l'ecriture est permise.</summary>
    public string? CeQuiManque =>
        CeQuiManqueTout.Count == 0 ? null : string.Join(" · ", CeQuiManqueTout);
}

/// <summary>
/// L'emplacement d'un reglage BIOS dans une variable UEFI, tel que l'IFR le declare, pour une carte et une version (l'offset
/// bouge). <see cref="VarSize"/> est le garde-fou : une variable d'une autre taille n'est pas ce firmware, rien n'est lu.
/// <see cref="Values"/> ferme les valeurs : un octet hors de la liste invalide la lecture.
/// </summary>
public sealed record NvramLocation(
    string VarName,
    string VendorGuid,
    int VarSize,
    int Offset,
    IReadOnlyDictionary<byte, string> Values,
    byte? Default,
    // Un reglage numerique (limites de puissance) : taille en octets (petit-boutiste),
    // bornes de l'IFR, unite, et 0 qui vaut « Auto » quand l'IFR le dit. Values vide dans ce cas.
    int Taille = 1,
    long Min = 0,
    long Max = 255,
    string? Unite = null,
    bool ZeroVautAuto = false);

/// <summary>Un reglage BIOS LU dans la NVRAM, pas deduit. <see cref="Libelle"/> est le texte que
/// le menu du BIOS affiche pour cette valeur.</summary>
public sealed record BiosSettingReading(string Setting, long Raw, string Libelle, bool IsDefault);
