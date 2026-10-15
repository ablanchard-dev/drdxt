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
///   • sur un démarrage non UEFI, l'étape de désactivation du CSM est retirée (garde contre le
///     non-démarrage) et <see cref="RecoverySection.CsmAdviceSuppressed"/> est posé ;
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
        // Le perimetre V1 est reduit. Le cahier exige « la precision par-carte est le produit » ; cette
        // exigence n'est pas tenable par un dev solo avant la premiere vente, et la bibliotheque compte
        // 9 guides dont zero verifie sur une vraie carte. Un guide vague qui envoie un client dans son
        // BIOS, c'est le SAV qui explose.
        var actionable = findings
            .Where(f => f.MatrixClass == BiosMatrixClass.Guide)
            .Where(f => V1Settings.Contains(f.Kind))
            .OrderBy(f => OrderIndex(f.Kind))
            .ToList();

        // Le CSM ne fait pas partie du pas a pas, mais on regarde si l'advisor en a emis
        // un : c'est le signal que ReBAR est desactive, et ca decide de ce qu'on dit ensuite.
        var csmWasAdvisable = findings.Any(f =>
            f.MatrixClass == BiosMatrixClass.Guide && f.Kind == FirmwareSettingKind.Csm);

        // Le meme garde-fou. Sur un demarrage Legacy, conseiller de couper le CSM risque d'empecher la
        // machine de demarrer. Le pas a pas ne le propose a personne ; ce booleen decide lequel des deux
        // messages sort :
        //   Legacy  -> « laissez le CSM tel quel » (la note existante, toujours vraie) ;
        //   UEFI    -> « si ReBAR refuse, c'est probablement le CSM » (la ligne de secours).
        // Sans condition, cette ligne de secours conseillerait le CSM a un utilisateur en Legacy :
        // exactement le non-demarrage que ce garde-fou existe pour eviter.
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
            : ComposeExactValue(path);

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
    private static string ComposeExactValue(GuideSettingPath path)
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

        // Le CSM ne fait pas partie du pas a pas (risque de non-demarrage), mais il reste
        // la cause n°1 d'un ReBAR qui refuse de s'activer. Le nommer sans le faire faire, c'est
        // la difference entre « on a retire une etape » et « on a retire une etape et laisse le
        // client en plan ».
        //
        // Uniquement en demarrage UEFI. En Legacy, cette phrase enverrait quelqu'un couper un
        // CSM dont sa machine a besoin pour demarrer, et la note « laissez le CSM tel quel »
        // sort a la place.
        if (mentionCsm)
            steps.Add(Recovery(6, BiosGuideStringKeys.RecoveryRebarNeedsCsmOff));

        return steps;
    }

    private static GuideStep Recovery(int order, string key)
        => new(order, key, ExactValue: null, Setting: FirmwareSettingKind.BiosVersion, IsRecoveryRelated: true);

    /// <summary>
    /// Les seuls reglages que la V1 fait faire a un client. Ensemble ferme et volontairement
    /// petit : chaque etape en plus est un ticket de support en plus, sur des chemins de menu que
    /// personne n'a verifies sur une vraie carte.
    ///
    /// Ce qui est dehors, et pourquoi :
    ///
    /// • <b>CSM</b> : l'etape la plus dangereuse de tout le guide. Mal suivie, la machine ne
    ///   demarre plus. Et elle serait conseillee a l'aveugle : `SettingsState.Csm` vaut Unknown en
    ///   permanence (le lecteur refuse d'affirmer), on demanderait donc de desactiver quelque chose dont
    ///   on ignore l'etat. Hors du guide ; le filet de recuperation la nomme quand meme, pour que
    ///   quelqu'un dont ReBAR refuse de s'activer sache ou regarder.
    ///
    /// • <b>Routage CPPC / X3D</b> : un levier separe, hors du perimetre « XMP et ReBAR », et
    ///   dont la detection de parking reste fragile.
    ///
    /// <b>Above 4G reste</b>, et c'est un ecart assume par rapport a la regle (« limites a
    /// XMP et ReBAR ») : ce n'est pas une etape de plus, c'est la moitie mecanique de ReBAR. Sur
    /// la plupart des cartes ReBAR ne s'active pas sans elle, et elle vit dans le meme menu. Un
    /// pas a pas ReBAR qui ne peut pas aboutir est pire que pas de pas a pas.
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
