namespace DrDxt.Core;

/// <summary>
/// Ce que DrDXT sait faire d'un reglage BIOS, sur cette plateforme.
///
/// `GuidedBios` est l'etape actuelle, pas la finalite. Le produit doit progressivement savoir
/// ecrire certains reglages lui-meme : pas universellement, pas en devinant, pas par un hack
/// generique, mais sur les plateformes pour lesquelles une methode fiable a ete identifiee et
/// qualifiee.
///
/// La doctrine est donc : **aucune ecriture BIOS generique ou non qualifiee**, et non « aucune
/// ecriture BIOS pour toujours ».
///
/// Il n'existe volontairement aucune valeur `GenericBiosWrite`. Son absence est le garde-fou :
/// une future decision ne pourra pas s'y glisser sans ajouter explicitement un niveau, ce qui se
/// verra.
/// </summary>
public enum BiosCapabilityLevel
{
    /// <summary>Aucune voie sure connue sur cette plateforme. → PDF et QR uniquement.</summary>
    Unsupported,

    /// <summary>DrDXT connait le reglage et la procedure, mais ne peut pas l'ecrire assez surement.
    /// → PDF et QR, puis validation apres redemarrage. **Le repli universel, qui ne disparait
    /// jamais.**</summary>
    Guided,

    /// <summary>Une interface officiellement supportee par le constructeur permet lecture et
    /// ecriture. → automatisable apres qualification.</summary>
    VendorSupportedWrite,

    /// <summary>Une voie firmware/UEFI specifique a ete etudiee et validee pour une carte exacte,
    /// une version de BIOS exacte et un reglage exact. → automatisable uniquement dans cette
    /// matrice de compatibilite.</summary>
    QualifiedFirmwareWrite,
}

/// <summary>
/// La reversibilite d'un reglage BIOS ne se deduit pas de la connaissance de l'ancienne valeur.
///
/// Pour un reglage Windows : snapshot → apply → read-back → revert. Pour un reglage BIOS, si la
/// machine ne passe plus le POST, **Windows ne s'execute pas** et `Revert()` n'a aucune chance de tourner.
/// Connaitre l'ancienne valeur ne sert alors a rien.
///
/// Ce type existe pour qu'aucun code ne puisse ecrire « reversible » a partir d'un snapshot.
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

    /// <summary>Seule une intervention physique recupere la machine : cavalier Clear CMOS, retrait
    /// de la pile, bouton de secours. Cela reste une recuperation, mais elle exige que quelqu'un
    /// ouvre le boitier, et le joueur doit le savoir avant.</summary>
    PhysicalRecoveryAvailable,
}

/// <summary>
/// Le risque de ne plus demarrer, distinct de la recuperabilite.
///
/// Les deux se confondent facilement et ne disent pas la meme chose. `BiosRecoverability`
/// repond a « comment revient-on ? », celui-ci a « peut-on y arriver ? ». Un XMP peut echouer a
/// entrainer la memoire et laisser un ecran noir sur une carte parfaitement recuperable ; le
/// joueur doit le savoir avant de cliquer, meme quand la recuperation est facile.
/// </summary>
public enum NoPostRisk
{
    /// <summary>Pas etabli. Le defaut, et il n'autorise rien.</summary>
    NoPostRiskUnknown,

    /// <summary>Le reglage ne peut pas empecher le POST : il n'agit qu'apres l'initialisation
    /// materielle.</summary>
    CannotPreventPost,

    /// <summary>Le reglage touche l'initialisation memoire, le bus PCIe ou la sequence de
    /// demarrage : un ecran noir est un resultat possible et normal.</summary>
    CanPreventPost,
}

/// <summary>
/// Une entree de la matrice de qualification.
///
/// **Le firmware fait partie de l'empreinte materielle du profil.** Une modification qui
/// fonctionne sur « MSI carte A / BIOS X » ne devient pas valide sur « MSI carte A / BIOS Y »,
/// encore moins sur « MSI carte B ». Une ecriture automatique n'existe que pour une entree
/// qualifiee : carte, version, reglage, valeurs, fournisseur, relecture, redemarrage, risque,
/// recuperation, statut de validation.
/// </summary>
/// <param name="Board">La carte exacte.</param>
/// <param name="FirmwareVersion">La version de BIOS exacte.</param>
/// <param name="Setting">Le reglage, tel que le firmware le nomme.</param>
/// <param name="CurrentValue">Ce qui est lu, ou <c>null</c> si illisible.</param>
/// <param name="TargetValue">Ce qui serait ecrit.</param>
/// <param name="Provider">Qui saurait l'ecrire.</param>
/// <param name="Capability">Ce que cette plateforme autorise.</param>
/// <param name="Recoverability">Comment on revient en arriere si ca tourne mal.</param>
/// <param name="RequiresReboot">Presque toujours vrai, et le dire explicitement evite de
/// l'oublier dans un parcours.</param>
/// <param name="Validated">Cette entree a-t-elle ete reellement validee sur cette matrice ?
/// Faux par defaut. « Ca devrait marcher » n'est pas une validation.</param>
/// <param name="BoardRevision">La revision de la carte. Deux revisions d'un meme nom commercial
/// n'ont pas forcement le meme firmware ni les memes cavaliers.</param>
/// <param name="MenuPath">Ou cliquer, tel que le firmware l'affiche. <c>null</c> = inconnu.</param>
/// <param name="PossibleValues">Ce que le reglage accepte. <c>null</c> = jamais releve.</param>
/// <param name="DefaultValue">Ce que le firmware y met d'usine. <c>null</c> = inconnu, et
/// surtout pas « Auto » suppose.</param>
/// <param name="NoPost">Ce reglage peut-il laisser un ecran noir ? Voir <see cref="NoPostRisk"/>.</param>
/// <param name="ReadMechanism">Comment on lit la valeur courante. <c>null</c> = on ne sait pas la
/// lire, ce qui explique un <paramref name="CurrentValue"/> nul.</param>
/// <param name="DocumentationSource">D'ou vient cette connaissance. Exigee des qu'une entree se
/// declare validee : « ca devrait marcher » n'est pas une source, et une validation sans source
/// n'est pas verifiable par quelqu'un d'autre.</param>
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
    /// Une validation sans source n'est pas une validation.
    ///
    /// Le champ `Validated` est le seul qui debloque l'ecriture automatique. Le poser sans dire
    /// d'ou vient la certitude rend la matrice invérifiable par quelqu'un d'autre, et c'est
    /// exactement ainsi qu'une supposition finit par autoriser une ecriture dans un firmware.
    /// La regle est donc dans le constructeur, pas dans une revue de code.
    /// </summary>
    public bool Validated { get; init; } =
        Validated && string.IsNullOrWhiteSpace(DocumentationSource)
            ? throw new ArgumentException(
                $"l'entree « {Board} / {FirmwareVersion} / {Setting} » se declare validee sans "
                + "DocumentationSource: a validation without a source cannot be checked",
                nameof(Validated))
            : Validated;

    /// <summary>
    /// DrDXT a-t-il le droit d'ecrire ceci tout seul ?
    ///
    /// Quatre conditions, et il en manque une suffit a renvoyer au parcours guide :
    /// une capacite d'ecriture qualifiee, une validation reelle sur cette matrice, une voie de
    /// recuperation connue, et une valeur courante lisible : sans elle on ne saurait meme pas
    /// dire ce qu'on a change.
    /// </summary>
    public bool EcritureAutomatiqueAutorisee =>
        Capability is BiosCapabilityLevel.VendorSupportedWrite or BiosCapabilityLevel.QualifiedFirmwareWrite
        && Validated
        && Recoverability != BiosRecoverability.RecoveryUnknown
        && CurrentValue is not null;

    /// <summary>
    /// Tout ce qui manque, pas seulement le premier.
    ///
    /// Un seul motif masquerait les autres (meme regle que `CandidateAppraisal.Motifs`) : sur la
    /// carte de reference, « aucune voie d'ecriture qualifiee » cacherait que le manque a lever en
    /// premier est la voie de recuperation, et que la valeur courante n'est pas lisible non plus.
    /// N'en dire qu'un donne une feuille de route fausse : on croit un obstacle la ou il y en a trois.
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
/// L'emplacement d'un reglage BIOS dans une variable UEFI, tel que l'IFR du firmware le declare.
///
/// Il ne vaut que pour une carte et une version de BIOS : l'offset bouge d'une version a l'autre.
/// <see cref="VarSize"/> est le garde-fou : la variable lue doit avoir exactement la taille que
/// l'IFR declare, sinon la table ne decrit pas ce firmware et rien n'est lu.
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
