using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Assembles the <see cref="PersonalizedGuide"/> (content only — NO rendering) from
/// the actionable findings + the board/BIOS identity.
///
/// Rules enforced here:
///   • only <see cref="BiosMatrixClass.Guide"/> findings produce write steps
///     (read/auto/lab/forbidden findings carry no menu navigation — honesty + safety);
///   • exact menu paths come from <see cref="IGuideLibrary"/>; an uncovered board
///     yields a labelled <see cref="GuideConfidence.UnverifiedFallback"/> guide whose
///     steps still carry the exact setting name (decision §9 — never strand the user);
///   • the ReBAR dependency order is Above-4G → CSM → Re-Size BAR;
///   • §16 / B2: on a non-UEFI boot the CSM-off step is SUPPRESSED (no-boot guard) and
///     <see cref="RecoverySection.CsmAdviceSuppressed"/> is set;
///   • the mandatory recovery ladder is emitted at the TOP and the BOTTOM.
///
/// Never writes the BIOS — a guide is text/steps for a human.
/// </summary>
public sealed class GuideGenerator : IGuideGenerator
{
    private readonly IGuideLibrary _library;

    // The canonical ordering used to sort the emitted steps: the ReBAR chain stays
    // Above-4G → CSM → Re-Size BAR; XMP/EXPO leads (independent, highest impact);
    // X3D and the read-only kinds trail. Anything unlisted sorts last, stably.
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

        // Resolve with the board + BIOS-version KB key (decision §9.1): only a verified
        // board+version KB hit yields GuideConfidence.Verified. A vendor/UI-generation
        // match resolves to a best-known fallback (IsVerified=false) → UnverifiedFallback.
        var templateResult = _library.Resolve(vendor, uiGen, hw.Board.Product, hw.Bios.Version);
        if (!templateResult.IsOk)
            return Result<PersonalizedGuide>.Fail(templateResult.Error!.Value.Code, templateResult.Error!.Value.Message);
        var template = templateResult.Value!;

        var confidence = template.IsVerified
            ? GuideConfidence.Verified
            : GuideConfidence.UnverifiedFallback;

        // Keep only actionable (Guide-class) findings; sort by the canonical order so
        // the ReBAR chain reads correctly. Stable, deterministic.
        //
        // D4 (lot L5, gate G7) — LE PERIMETRE V1 est REDUIT. Le cahier exigeait « la precision
        // par-carte est le produit » ; cette exigence n'est pas tenable par un dev solo avant la
        // premiere vente, et la bibliotheque compte 9 guides dont ZERO verifie sur une vraie
        // carte. Un guide vague qui envoie un client dans son BIOS, c'est le SAV qui explose.
        var actionable = findings
            .Where(f => f.MatrixClass == BiosMatrixClass.Guide)
            .Where(f => V1Settings.Contains(f.Kind))
            .OrderBy(f => OrderIndex(f.Kind))
            .ToList();

        // Le CSM ne fait plus partie du pas a pas (D4), mais on regarde si l'advisor en a emis
        // un : c'est le signal que ReBAR est desactive, et ca decide de ce qu'on dit ensuite.
        var csmWasAdvisable = findings.Any(f =>
            f.MatrixClass == BiosMatrixClass.Guide && f.Kind == FirmwareSettingKind.Csm);

        // §16 / B2 — le meme garde-fou, deplace. Sur un demarrage Legacy, conseiller de couper
        // le CSM risque d'empecher la machine de demarrer. Le pas a pas ne le propose plus a
        // personne ; ce booleen decide desormais lequel des deux messages sort :
        //   Legacy  -> « laissez le CSM tel quel » (la note existante, toujours vraie) ;
        //   UEFI    -> « si ReBAR refuse, c'est probablement le CSM » (la ligne de secours).
        // Ecrite d'abord sans condition, cette ligne de secours conseillait le CSM a un
        // utilisateur en Legacy — exactement le non-demarrage que ce garde-fou existe pour eviter.
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
            // The enter/save keys come straight from the resolved template (the library
            // validates them) — pass them through verbatim; never invent a key.
            EnterKey: template.EnterKey,
            SaveKey: template.SaveKey,
            BoardProduct: (hw.Board.Product ?? string.Empty).Trim());

        return Result<PersonalizedGuide>.Ok(guide);
    }

    // Builds one write step: the human instruction key + the exact navigation value
    // (menu path "→" setting name "→" target value) drawn from the library template.
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

    // The exact value the UI shows verbatim: "<menu path> → <setting name> → <target>".
    // For a name-only fallback (empty menu path) it degrades to "<setting name> → <target>"
    // so the client still gets the exact name to look for (decision §9).
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

    // The mandatory recovery ladder, in the exact order D2 requires. Fresh lists so
    // Top and Bottom are independent instances (no shared mutable reference).
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

        // D4 : le CSM ne fait plus partie du pas a pas (risque de non-demarrage), mais il reste
        // la cause n°1 d'un ReBAR qui refuse de s'activer. Le NOMMER sans le faire faire, c'est
        // la difference entre « on a retire une etape » et « on a retire une etape et laisse le
        // client en plan ».
        //
        // Uniquement en demarrage UEFI. En Legacy, cette phrase enverrait quelqu'un couper un
        // CSM dont sa machine a besoin pour demarrer — et la note « laissez le CSM tel quel »
        // sort a la place.
        if (mentionCsm)
            steps.Add(Recovery(6, BiosGuideStringKeys.RecoveryRebarNeedsCsmOff));

        return steps;
    }

    private static GuideStep Recovery(int order, string key)
        => new(order, key, ExactValue: null, Setting: FirmwareSettingKind.BiosVersion, IsRecoveryRelated: true);

    /// <summary>
    /// Les seuls reglages que la V1 fait faire a un client (D4). Ensemble ferme et volontairement
    /// petit : chaque etape en plus est un ticket de support en plus, sur des chemins de menu que
    /// personne n'a verifies sur une vraie carte.
    ///
    /// Ce qui est DEHORS, et pourquoi :
    ///
    /// • <b>CSM</b> — l'etape la plus dangereuse de tout le guide. Mal suivie, la machine NE
    ///   DEMARRE plus. Et on la conseillait a l'aveugle : `SettingsState.Csm` vaut Unknown en
    ///   permanence (le lecteur refuse d'affirmer), donc on demandait de desactiver quelque chose
    ///   dont on ignorait l'etat. Sortie du guide ; le filet de recuperation la nomme quand meme,
    ///   pour que quelqu'un dont ReBAR refuse de s'activer sache ou regarder.
    ///
    /// • <b>Routage CPPC / X3D</b> — un levier separe, hors du perimetre « XMP et ReBAR », et
    ///   dont la detection de parking reste fragile.
    ///
    /// <b>Above 4G reste</b>, et c'est un ecart assume par rapport a la lettre de D4 (« limites a
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
