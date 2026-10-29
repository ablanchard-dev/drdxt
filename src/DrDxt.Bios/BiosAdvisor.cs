using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Fondation du BIOS Doctor : un conseiller pur, lecture + écart. Il consomme le
/// <see cref="HardwareInventory"/> + le <see cref="SettingsState"/> déjà produits par le scan (il ne
/// les redétecte jamais, ne relance jamais l'inférence XMP, n'écrit jamais le BIOS) et n'émet que les
/// constats firmware <see cref="BiosFinding"/> qui concernent cette machine.
///
/// Honnêteté :
///   • un réglage lisible et mal réglé → un constat <see cref="BiosMatrixClass.Guide"/> actionnable
///     (V1 = guide, jamais d'écriture) ;
///   • un réglage illisible → un constat honnête <see cref="BiosMatrixClass.Read"/> marqué
///     <see cref="BiosFinding.StateNotReadable"/>, jamais un état fabriqué ;
///   • une machine déjà bien réglée → aucun constat actionnable (un résultat valide, assumé).
///
/// Sécurité (plan B2) : la désactivation du CSM n'est jamais conseillée sur un démarrage Legacy
/// (risque de non-démarrage). Filtre : le BIOS d'un portable / d'une console portable est verrouillé
/// → aucun constat actionnable.
/// </summary>
public sealed class BiosAdvisor : IBiosAdvisor
{
    // Un BIOS plus ancien est signalé pour information (« envisager une mise à jour ») : sans base de connaissance des
    // cartes mères, jamais une affirmation sur l'AGESA ou la version.
    private static readonly TimeSpan OldBiosThreshold = TimeSpan.FromDays(365 * 2);

    public Result<IReadOnlyList<BiosFinding>> Advise(HardwareInventory hw, SettingsState state)
    {
        // ── Defensive boundary: reject internally incoherent input, fail clean. ──
        if (hw.Memory is null || hw.Memory.Count == 0)
            return Result<IReadOnlyList<BiosFinding>>.Fail(
                ErrorCodes.BiosInputInvalid, "Empty memory inventory; cannot diff XMP/EXPO.");
        if (hw.Gpus is null || hw.Gpus.Count == 0)
            return Result<IReadOnlyList<BiosFinding>>.Fail(
                ErrorCodes.BiosInputInvalid, "No GPU in inventory; cannot diff ReBAR/PCIe.");

        var findings = new List<BiosFinding>();

        // Le BIOS d'un portable / d'une console portable est en général verrouillé : un constat d'information, pas d'étapes.
        if (hw.Chassis is ChassisType.Laptop or ChassisType.Handheld)
        {
            findings.Add(new BiosFinding(
                Kind: FirmwareSettingKind.BiosVersion,
                Severity: Severity.Info,
                TitleKey: BiosStringKeys.NotApplicableTitle,
                RationaleKey: BiosStringKeys.NotApplicableRationale,
                MatrixClass: BiosMatrixClass.Read,
                CurrentState: TriState.Unknown,
                RecommendedAction: BiosRecommendedAction.None,
                RecommendedActionKey: BiosStringKeys.NoneAction,
                Gain: null,
                StateNotReadable: false));
            return Result<IReadOnlyList<BiosFinding>>.Ok(findings);
        }

        AddXmpExpo(findings, hw, state);
        // Une puce intégrée n'a ni BAR à agrandir ni slot PCIe : sans carte dédiée, rien à vérifier au BIOS pour elles.
        var carte = CartesGraphiques.CarteDediee(hw);
        if (carte is not null) AddReBarChain(findings, hw, carte, state);
        AddX3dCppc(findings, hw);
        if (carte is not null) AddPcieGen(findings);
        AddBiosVersion(findings, hw);

        return Result<IReadOnlyList<BiosFinding>>.Ok(findings);
    }

    // ── XMP / EXPO (consomme l'état inféré par le scan ; jamais de nouvelle inférence ici). ──
    private static void AddXmpExpo(List<BiosFinding> findings, HardwareInventory hw, SettingsState state)
    {
        switch (state.XmpExpo)
        {
            // Un profil mémoire est un surcadençage : jamais conseillé sur une machine instable (les incidents de la garde
            // Curve Optimizer : arrêt brutal, écran bleu, vidage, erreur grave, arrêt dangereux du SSD), ou non lue (null).
            // Le conseil revient seul après la fenêtre de la garde sans incident, aucune levée à la main.
            case TriState.Off when state.IncidentsDeStabilite is not { } incidents
                                   || incidents.Any(d => DateTimeOffset.UtcNow - d <= CandidateSelector.FenetreInstabilite):
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.XmpExpo, Severity.Info,
                    BiosStringKeys.XmpOffUnstableTitle, BiosStringKeys.XmpOffUnstableRationale,
                    BiosMatrixClass.Read, TriState.Off,
                    BiosRecommendedAction.None, BiosStringKeys.NoneAction,
                    Gain: null, StateNotReadable: false));
                break;

            case TriState.Off:
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.XmpExpo, Severity.Major,
                    BiosStringKeys.XmpOffTitle, BiosStringKeys.XmpOffRationale,
                    BiosMatrixClass.Guide, TriState.Off,
                    BiosRecommendedAction.Enable, BiosStringKeys.XmpEnableAction,
                    // Pas de fourchette. Mesures publiques : quelques % (jeu limite par la carte graphique) a ~42 % (jeu
                    // tres sensible a la memoire), ~1 % sur un X3D (TechSpot, XDA, AMD) ; le chiffre vient de l'A/B.
                    Gain: new BiosGainEstimate(
                        MinPct: null, MaxPct: null, Conditional: false,
                        SourceKey: BiosStringKeys.GainXmpSource, CaveatKey: BiosStringKeys.GainXmpCaveat),
                    StateNotReadable: false));
                break;

            // Une barrette qui n'annonce aucun profil au-dessus de son socle JEDEC n'a rien à activer : la vérifier au BIOS
            // ne mène nulle part.
            case TriState.Unknown when hw.Memory.Any(SocleJedec.AnnonceUnProfil):
                // « Non lu » honnête : on rapporte l'inconnu, on n'affirme jamais « désactivé ».
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.XmpExpo, Severity.Info,
                    BiosStringKeys.XmpUnknownTitle, BiosStringKeys.XmpUnknownRationale,
                    BiosMatrixClass.Read, TriState.Unknown,
                    BiosRecommendedAction.Verify, BiosStringKeys.VerifyAction,
                    Gain: null, StateNotReadable: true));
                break;

            // On → nothing (no fabricated need).
        }
    }

    // ── ReBAR + prérequis ordonnés (Above-4G → CSM → Re-Size BAR). ──
    private static void AddReBarChain(List<BiosFinding> findings, HardwareInventory hw, GpuInfo primary, SettingsState state)
    {
        // Préférer la lecture ReBAR de la carte dédiée ; à défaut, la lecture au niveau des
        // réglages. Les deux viennent directement du scan : aucune redétection.
        var rebar = primary.ReBar != TriState.Unknown ? primary.ReBar : state.ReBar;
        var isNvidia = primary.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                       || primary.Vendor.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

        switch (rebar)
        {
            case TriState.Off:
                // Les prérequis ordonnés d'abord, pour que la chaîne se lise de haut en bas.
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.Above4G, Severity.Minor,
                    BiosStringKeys.Above4GTitle, BiosStringKeys.Above4GRationale,
                    BiosMatrixClass.Guide, TriState.Unknown,
                    BiosRecommendedAction.Enable, BiosStringKeys.Above4GEnableAction,
                    Gain: null, StateNotReadable: false));

                // Conseiller de désactiver le CSM n'est sûr que sur un démarrage UEFI.
                // Legacy/MBR → retirer l'étape (risque de non-démarrage), garder le reste.
                if (hw.Bios.IsUefi)
                    findings.Add(new BiosFinding(
                        FirmwareSettingKind.Csm, Severity.Minor,
                        BiosStringKeys.CsmTitle, BiosStringKeys.CsmRationale,
                        BiosMatrixClass.Guide, TriState.Unknown,
                        BiosRecommendedAction.Disable, BiosStringKeys.CsmDisableAction,
                        Gain: null, StateNotReadable: false));

                findings.Add(new BiosFinding(
                    FirmwareSettingKind.ReBar, Severity.Minor,
                    BiosStringKeys.ReBarOffTitle, BiosStringKeys.ReBarOffRationale,
                    BiosMatrixClass.Guide, TriState.Off,
                    BiosRecommendedAction.Enable, BiosStringKeys.ReBarEnableAction,
                    // Le ReBAR dépend du jeu et peut faire régresser certains titres : pas de % global honnête.
                    // Conditionnel, avec une réserve « vérifier jeu par jeu ».
                    Gain: new BiosGainEstimate(
                        MinPct: null, MaxPct: null, Conditional: true,
                        SourceKey: BiosStringKeys.GainReBarSource, CaveatKey: BiosStringKeys.GainReBarCaveat),
                    StateNotReadable: false));
                break;

            case TriState.Unknown:
                // Recommandation avec réserve, inconnu honnête, jamais Major, jamais forcé,
                // et sans jamais entraîner les étapes d'écriture CSM / Above-4G.
                findings.Add(new BiosFinding(
                    FirmwareSettingKind.ReBar, Severity.Info,
                    BiosStringKeys.ReBarUnknownTitle, BiosStringKeys.ReBarUnknownRationale,
                    BiosMatrixClass.Read, TriState.Unknown,
                    BiosRecommendedAction.Verify, BiosStringKeys.VerifyAction,
                    Gain: null, StateNotReadable: true));
                break;

            // On → nothing.
        }

        _ = isNvidia; // la distinction de constructeur est déjà portée par la lecture par GPU ou Unknown.
    }

    // ── Routage X3D / CPPC : ne compte que sur un AMD X3D à plusieurs CCD. ──
    // Le routage se corrige d'abord côté Windows (service AMD 3D V-Cache / Game Bar), le CPPC du BIOS seulement « s'il
    // est mal réglé » ; le scan ne lit pas l'état du routage : un constat Verify, pas une étape Major d'écriture BIOS.
    private static void AddX3dCppc(List<BiosFinding> findings, HardwareInventory hw)
    {
        if (hw.Cpu.IsX3D && hw.Cpu.CcdCount > 1)
            findings.Add(new BiosFinding(
                FirmwareSettingKind.X3dCppc, Severity.Minor,
                BiosStringKeys.X3dCppcTitle, BiosStringKeys.X3dCppcRationale,
                BiosMatrixClass.Read, TriState.Unknown,
                BiosRecommendedAction.Verify, BiosStringKeys.VerifyAction,
                // Gain seulement si le routage des CCD est mal regle (le plus souvent correct) ; aucune fourchette
                // faute de mesure publique, la mise en garde porte la nuance.
                Gain: new BiosGainEstimate(
                    MinPct: null, MaxPct: null, Conditional: true,
                    SourceKey: BiosStringKeys.GainX3dSource, CaveatKey: BiosStringKeys.GainX3dCaveat),
                StateNotReadable: true));
    }

    // ── Lien PCIe : absent de l'inventaire du scan → « non lu » honnête, jamais fabriqué. ──
    private static void AddPcieGen(List<BiosFinding> findings)
    {
        findings.Add(new BiosFinding(
            FirmwareSettingKind.PcieGen, Severity.Info,
            BiosStringKeys.PcieNotReadTitle, BiosStringKeys.PcieNotReadRationale,
            BiosMatrixClass.Read, TriState.Unknown,
            BiosRecommendedAction.Verify, BiosStringKeys.VerifyAction,
            Gain: null, StateNotReadable: true));
    }

    // ── Version BIOS / AGESA : indication douce et honnête seulement (pas de base de connaissance dans la fondation). ──
    private static void AddBiosVersion(List<BiosFinding> findings, HardwareInventory hw)
    {
        // Pas de date de publication → âge impossible à juger honnêtement → aucun constat (jamais fabriquer).
        if (hw.Bios.ReleaseDate is not { } released) return;

        var age = DateTimeOffset.UtcNow - released;
        if (age <= OldBiosThreshold) return;

        findings.Add(new BiosFinding(
            FirmwareSettingKind.BiosVersion, Severity.Info,
            BiosStringKeys.BiosOldTitle, BiosStringKeys.BiosOldRationale,
            BiosMatrixClass.Read, TriState.Unknown,
            BiosRecommendedAction.Update, BiosStringKeys.BiosUpdateAction,
            Gain: null, StateNotReadable: false));
    }
}
