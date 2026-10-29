using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Assemble le <see cref="PersonalizedGuide"/> (contenu seulement, aucun rendu) à partir des
/// constats actionnables + de l'identité carte / BIOS.
///
/// Règles imposées ici :
///   • seuls les constats <see cref="BiosMatrixClass.Guide"/> produisent des étapes d'écriture
///     (les constats lecture / auto / labo / interdits ne portent aucune navigation de menu :
///     honnêteté + sécurité) ;
///   • les chemins de menu exacts viennent de <see cref="IGuideLibrary"/> ; une carte non couverte
///     donne un guide étiqueté <see cref="GuideConfidence.UnverifiedFallback"/> dont les étapes
///     portent quand même le nom exact du réglage (ne jamais laisser l'utilisateur en plan) ;
///   • l'ordre de dépendance du ReBAR est Above-4G → CSM → Re-Size BAR ;
///   • le CSM n'est jamais une étape ; <see cref="RecoverySection.CsmAdviceSuppressed"/> est posé quand un constat CSM
///     arrive sur un démarrage non UEFI (garde contre le non-démarrage). BiosAdvisor n'émet ce constat qu'en UEFI : en
///     production, la garde ne se déclenche donc pas ;
///   • l'échelle de récupération obligatoire est émise en haut et en bas.
///
/// N'écrit jamais le BIOS : un guide est du texte / des étapes pour un humain.
/// </summary>
public sealed class GuideGenerator : IGuideGenerator
{
    private readonly IGuideLibrary _library;

    // L'ordre canonique de tri des étapes émises : la chaîne ReBAR reste Above-4G → CSM → Re-Size BAR ;
    // XMP/EXPO passe en tête (indépendant, plus fort impact) ; X3D et les types en lecture seule
    // ferment la marche. Tout ce qui n'est pas listé passe en dernier, de façon stable.
    private static readonly FirmwareSettingKind[] StepOrder =
    {
        FirmwareSettingKind.XmpExpo,
        FirmwareSettingKind.Above4G,
        FirmwareSettingKind.Csm,
        FirmwareSettingKind.ReBar,
        FirmwareSettingKind.X3dCppc,
        FirmwareSettingKind.PcieGen,
        FirmwareSettingKind.BiosVersion,
    };

    public GuideGenerator(IGuideLibrary library)
        => _library = library ?? throw new ArgumentNullException(nameof(library));

    /// <inheritdoc/>
    public Result<PersonalizedGuide> Generate(HardwareInventory hw, IReadOnlyList<BiosFinding> findings)
    {
        if (hw is null)
            return Result<PersonalizedGuide>.Fail(ErrorCodes.BiosInputInvalid, "null hardware inventory");
        findings ??= Array.Empty<BiosFinding>();

        var vendor = BiosVendorMapper.FromManufacturer(hw.Board.Manufacturer);
        var uiGen = BiosUiGenerationDetector.Detect(vendor, hw.Bios);
        var bootMode = BootModeResolver.Resolve(hw.Bios);

        // Résoudre avec la clé de base de connaissance carte + version BIOS : seule une entrée vérifiée
        // carte + version donne GuideConfidence.Verified. Une correspondance constructeur / génération
        // d'interface se résout en meilleur repli connu (IsVerified=false) → UnverifiedFallback.
        var templateResult = _library.Resolve(vendor, uiGen, hw.Board.Product, hw.Bios.Version);
        if (!templateResult.IsOk)
            return Result<PersonalizedGuide>.Fail(templateResult.Error!.Value.Code, templateResult.Error!.Value.Message);
        var template = templateResult.Value!;

        var confidence = template.IsVerified
            ? GuideConfidence.Verified
            : GuideConfidence.UnverifiedFallback;

        // Ne garder que les constats actionnables (classe Guide) ; trier selon l'ordre canonique pour que
        // la chaîne ReBAR se lise correctement. Stable, déterministe.
        //
        // Perimetre V1 reduit : aucun guide de la bibliotheque n'est verifie sur une vraie carte.
        var actionable = findings
            .Where(f => f.MatrixClass == BiosMatrixClass.Guide)
            .Where(f => V1Settings.Contains(f.Kind))
            .OrderBy(f => OrderIndex(f.Kind))
            .ToList();

        // Un constat CSM de l'advisor signale un ReBAR desactive, et decide de ce qu'on dit ensuite.
        var csmWasAdvisable = findings.Any(f =>
            f.MatrixClass == BiosMatrixClass.Guide && f.Kind == FirmwareSettingKind.Csm);

        // En Legacy, couper le CSM risque d'empecher la machine de demarrer. Ce booleen choisit le message :
        //   Legacy  -> « laissez le CSM tel quel » ;
        //   UEFI    -> « si ReBAR refuse, c'est probablement le CSM » (la ligne de secours).
        var csmSuppressed = csmWasAdvisable && bootMode != BootMode.Uefi;
        var mentionCsmInRecovery = csmWasAdvisable && bootMode == BootMode.Uefi;

        var steps = new List<GuideStep>();
        var order = 1;
        foreach (var finding in actionable)
            steps.Add(BuildStep(order++, finding, template));

        var recovery = BuildRecovery(csmSuppressed, mentionCsmInRecovery);

        var guide = new PersonalizedGuide(
            Vendor: vendor,
            BiosUiGeneration: uiGen,
            DetectedBoardLabel: BoardLabel(hw.Board),
            Confidence: confidence,
            BootMode: bootMode,
            Findings: actionable,
            Steps: steps,
            Recovery: recovery,
            // Les touches d'entrée / d'enregistrement viennent directement du modèle résolu (la bibliothèque
            // les valide) : les transmettre telles quelles ; ne jamais inventer de touche.
            EnterKey: template.EnterKey,
            SaveKey: template.SaveKey,
            BoardProduct: (hw.Board.Product ?? string.Empty).Trim());

        return Result<PersonalizedGuide>.Ok(guide);
    }

    // Construit une étape d'écriture : la clé de l'instruction humaine + la valeur de navigation
    // exacte (chemin de menu « → » nom du réglage « → » valeur cible) tirée du modèle de la bibliothèque.
    private static GuideStep BuildStep(int order, BiosFinding finding, GuideTemplate template)
    {
        var path = template.Paths.FirstOrDefault(p => p.Setting == finding.Kind);
        var exact = path is null
            ? null
            : ValeurExacte(path);

        return new GuideStep(
            Order: order,
            InstructionKey: InstructionKeyFor(finding.Kind),
            ExactValue: exact,
            Setting: finding.Kind,
            IsRecoveryRelated: false);
    }

    // La valeur exacte que l'interface affiche telle quelle : « <chemin de menu> → <nom du réglage> →
    // <cible> ». Pour un repli sur le seul nom (chemin de menu vide), elle se réduit à « <nom du
    // réglage> → <cible> » : le client a toujours le nom exact à chercher.
    public static string ValeurExacte(GuideSettingPath path)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(path.MenuPath))
            parts.Add(path.MenuPath);
        parts.Add(path.SettingName);
        parts.Add($"= {path.TargetValue}");
        return string.Join("  →  ", parts);
    }

    private static RecoverySection BuildRecovery(bool csmSuppressed, bool mentionCsm)
        => new(
            Top: RecoverySteps(mentionCsm),
            Bottom: RecoverySteps(mentionCsm),
            CsmAdviceSuppressed: csmSuppressed);

    // L'échelle de récupération obligatoire, dans l'ordre exact exigé. Des listes neuves pour que Top
    // et Bottom soient des instances indépendantes (aucune référence modifiable partagée).
    private static IReadOnlyList<GuideStep> RecoverySteps(bool mentionCsm)
    {
        var steps = new List<GuideStep>
        {
            Recovery(1, BiosGuideStringKeys.RecoveryNoBootNormal),
            Recovery(2, BiosGuideStringKeys.RecoveryMemoryTraining),
            Recovery(3, BiosGuideStringKeys.RecoveryClearCmos),
            Recovery(4, BiosGuideStringKeys.RecoveryLoadDefaults),
            Recovery(5, BiosGuideStringKeys.RecoveryOneChangeAtATime),
        };

        // Hors du pas a pas (risque de non-demarrage), le CSM reste la cause n°1 d'un ReBAR qui refuse de s'activer : on
        // le nomme sans le faire faire. En UEFI seulement : en Legacy, la machine en a besoin pour demarrer, et la note
        // « laissez le CSM tel quel » sort a la place.
        if (mentionCsm)
            steps.Add(Recovery(6, BiosGuideStringKeys.RecoveryRebarNeedsCsmOff));

        return steps;
    }

    private static GuideStep Recovery(int order, string key)
        => new(order, key, ExactValue: null, Setting: FirmwareSettingKind.BiosVersion, IsRecoveryRelated: true);

    /// <summary>
    /// Les seuls reglages que la V1 fait faire a un client : ensemble ferme et petit, sur des chemins de menu que personne
    /// n'a verifies sur une vraie carte. Dehors :
    /// • <b>CSM</b> : mal suivie, la machine ne demarre plus, et `SettingsState.Csm` vaut toujours Unknown (le lecteur
    ///   refuse d'affirmer) : on ferait desactiver a l'aveugle. Le filet de recuperation la nomme quand meme ;
    /// • <b>Routage CPPC / X3D</b> : un levier separe, hors « XMP et ReBAR », a la detection de parking fragile.
    /// <b>Above 4G reste</b>, ecart assume : la moitie mecanique de ReBAR, qui sur la plupart des cartes ne s'active pas
    /// sans elle, dans le meme menu.
    /// </summary>
    private static readonly IReadOnlySet<FirmwareSettingKind> V1Settings =
        new HashSet<FirmwareSettingKind>
        {
            FirmwareSettingKind.XmpExpo,
            FirmwareSettingKind.Above4G,
            FirmwareSettingKind.ReBar,
        };

    private static int OrderIndex(FirmwareSettingKind kind)
    {
        var i = Array.IndexOf(StepOrder, kind);
        return i < 0 ? int.MaxValue : i;
    }

    private static string InstructionKeyFor(FirmwareSettingKind kind) => kind switch
    {
        FirmwareSettingKind.XmpExpo => BiosGuideStringKeys.StepXmp,
        FirmwareSettingKind.Above4G => BiosGuideStringKeys.StepAbove4G,
        FirmwareSettingKind.Csm => BiosGuideStringKeys.StepCsm,
        FirmwareSettingKind.ReBar => BiosGuideStringKeys.StepRebar,
        FirmwareSettingKind.X3dCppc => BiosGuideStringKeys.StepX3dCppc,
        FirmwareSettingKind.PcieGen => BiosGuideStringKeys.StepPcie,
        FirmwareSettingKind.BiosVersion => BiosGuideStringKeys.StepBiosVersion,
        _ => BiosGuideStringKeys.StepGeneric,
    };

    private static string BoardLabel(MotherboardInfo board)
    {
        var manufacturer = (board.Manufacturer ?? string.Empty).Trim();
        var product = (board.Product ?? string.Empty).Trim();
        return string.Join(" ", new[] { manufacturer, product }.Where(s => s.Length > 0));
    }
}
