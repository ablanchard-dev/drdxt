using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Tests de base de <see cref="BiosAdvisor"/> : détecter → comparer sur le HardwareInventory
/// et le SettingsState du scan (il ne redétecte jamais, n'écrit jamais).
/// L'honnêteté est la colonne vertébrale : un réglage illisible = un constat « non lu » honnête,
/// jamais un état fabriqué ; une machine bien réglée = une liste vide (résultat valide).
/// Chaque constat porte sa classe de matrice.
/// </summary>
public class BiosAdvisorTests
{
    private static readonly IBiosAdvisor Advisor = new BiosAdvisor();

    // ── Constructeurs : un bureau volontairement mal réglé, muté par test. ──

    private static HardwareInventory Hw(
        bool isX3D = false,
        int ccdCount = 1,
        string gpuVendor = "NVIDIA",
        TriState gpuReBar = TriState.Off,
        ChassisType chassis = ChassisType.Desktop,
        bool isUefi = true,
        int configuredMhz = 4800,
        int ratedMhz = 6000,
        DateTimeOffset? biosDate = null)
        => new(
            Cpu: new CpuInfo("Test CPU", "GenuineIntel", 8, 16, isX3D, ccdCount),
            Gpus: new[] { new GpuInfo("Test GPU", gpuVendor, 8L * 1024 * 1024 * 1024, "999.99", gpuReBar) },
            Memory: new[] { new MemoryModule("PN", "Mfr", configuredMhz, ratedMhz) },
            Board: new MotherboardInfo("ASUSTeK", "ROG STRIX B650-A"),
            Bios: new BiosInfo("1.0", biosDate ?? DateTimeOffset.UtcNow, isUefi),
            Chassis: chassis);

    private static SettingsState State(
        TriState xmp = TriState.Off,
        TriState reBar = TriState.Off)
        => new(
            XmpExpo: xmp,
            ReBar: reBar,
            PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e",
            Hags: TriState.On,
            GameMode: TriState.On,
            ParkedCoreIndexes: Array.Empty<int>(),
            Vrr: TriState.On,
            UncleanShutdowns: Array.Empty<DateTimeOffset>(),
            IncidentsDeStabilite: Array.Empty<DateTimeOffset>());

    private static IReadOnlyList<BiosFinding> Advise(HardwareInventory hw, SettingsState state)
    {
        var r = Advisor.Advise(hw, state);
        r.IsOk.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    // ════════════════ XMP / EXPO ════════════════

    [Fact] // A1
    public void XmpOff_emits_Major_Guide_Enable_finding()
    {
        var f = Advise(Hw(), State(xmp: TriState.Off))
            .Single(x => x.Kind == FirmwareSettingKind.XmpExpo);

        f.Severity.Should().Be(Severity.Major);
        f.MatrixClass.Should().Be(BiosMatrixClass.Guide);
        f.CurrentState.Should().Be(TriState.Off);
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Enable);
        f.StateNotReadable.Should().BeFalse();
        // Plus de fourchette (voir No_bios_gain_shows_a_range_nothing_public_holds).
        f.Gain.Should().NotBeNull();
        f.Gain!.MinPct.Should().BeNull();
        f.Gain.SourceKey.Should().NotBeNullOrWhiteSpace();
        f.Gain.CaveatKey.Should().NotBeNullOrWhiteSpace();
        f.TitleKey.Should().NotBeNullOrWhiteSpace();
        f.RationaleKey.Should().NotBeNullOrWhiteSpace();
        f.RecommendedActionKey.Should().NotBeNullOrWhiteSpace();
    }

    // Un profil mémoire pousse la machine : sur une machine qui s'arrête, ou dont les arrêts n'ont pas été lus, le
    // conseil d'activer devient « d'abord stabiliser », sans action ni gain.
    [Fact]
    public void XmpOff_n_est_jamais_conseille_sur_une_machine_instable_ou_non_lue()
    {
        foreach (var incidents in new[] { null, new[] { DateTimeOffset.UtcNow.AddDays(-3) } })
        {
            var f = Advise(Hw(), State(xmp: TriState.Off) with { IncidentsDeStabilite = incidents })
                .Single(x => x.Kind == FirmwareSettingKind.XmpExpo);
            f.RecommendedAction.Should().Be(BiosRecommendedAction.None);
            f.TitleKey.Should().Be("Bios.Xmp.Off.Unstable.Title");
            f.Gain.Should().BeNull();
        }
        Advise(Hw(), State(xmp: TriState.Off) with { IncidentsDeStabilite = [DateTimeOffset.UtcNow.AddDays(-60)] })
            .Single(x => x.Kind == FirmwareSettingKind.XmpExpo).RecommendedAction.Should().Be(BiosRecommendedAction.Enable,
                "le conseil revient seul après la fenêtre sans arrêt");
    }

    // La même source que la garde Curve Optimizer : un vidage écrit (écran bleu) sans aucun arrêt brutal noté suffit.
    [Fact]
    public void XmpOff_est_retenu_par_un_vidage_recent_meme_sans_arret_brutal()
    {
        var f = Advise(Hw(), State(xmp: TriState.Off) with { UncleanShutdowns = [], IncidentsDeStabilite = [DateTimeOffset.UtcNow.AddDays(-2)] })
            .Single(x => x.Kind == FirmwareSettingKind.XmpExpo);

        f.TitleKey.Should().Be("Bios.Xmp.Off.Unstable.Title");
        f.RecommendedAction.Should().Be(BiosRecommendedAction.None);
    }

    [Fact] // Honnêteté : déjà actif → aucun constat XMP (pas de besoin fabriqué)
    public void XmpOn_emits_no_XmpExpo_finding()
    {
        Advise(Hw(), State(xmp: TriState.On))
            .Should().NotContain(x => x.Kind == FirmwareSettingKind.XmpExpo);
    }

    [Fact] // Unknown → Info « non lu » honnête, n'affirme jamais un état
    public void XmpUnknown_emits_Info_Read_NotReadable_finding()
    {
        var f = Advise(Hw(), State(xmp: TriState.Unknown))
            .Single(x => x.Kind == FirmwareSettingKind.XmpExpo);

        f.Severity.Should().Be(Severity.Info);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read);
        f.CurrentState.Should().Be(TriState.Unknown);
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Verify);
        f.StateNotReadable.Should().BeTrue();
        f.Gain.Should().BeNull();
    }

    // ════════════════ ReBAR et prérequis ordonnés ════════════════

    [Fact] // ReBar désactivé (UEFI) → prérequis ordonnés Above-4G → CSM → ReBAR
    public void ReBarOff_uefi_emits_ordered_prerequisites()
    {
        var findings = Advise(Hw(gpuReBar: TriState.Off, isUefi: true), State(reBar: TriState.Off));

        var kinds = findings.Select(f => f.Kind).ToList();
        kinds.Should().Contain(FirmwareSettingKind.Above4G);
        kinds.Should().Contain(FirmwareSettingKind.Csm);
        kinds.Should().Contain(FirmwareSettingKind.ReBar);

        // Ordre exact de la chaîne : Above-4G activé → CSM désactivé → Re-Size BAR activé.
        kinds.IndexOf(FirmwareSettingKind.Above4G)
            .Should().BeLessThan(kinds.IndexOf(FirmwareSettingKind.Csm));
        kinds.IndexOf(FirmwareSettingKind.Csm)
            .Should().BeLessThan(kinds.IndexOf(FirmwareSettingKind.ReBar));

        findings.Single(f => f.Kind == FirmwareSettingKind.Above4G)
            .RecommendedAction.Should().Be(BiosRecommendedAction.Enable);
        findings.Single(f => f.Kind == FirmwareSettingKind.Csm)
            .RecommendedAction.Should().Be(BiosRecommendedAction.Disable);
        findings.Single(f => f.Kind == FirmwareSettingKind.ReBar)
            .RecommendedAction.Should().Be(BiosRecommendedAction.Enable);

        // chaque prérequis porte une classe de matrice (V1 = Guide)
        findings.Where(f => f.Kind is FirmwareSettingKind.Above4G
                                 or FirmwareSettingKind.Csm
                                 or FirmwareSettingKind.ReBar)
            .Should().OnlyContain(f => f.MatrixClass == BiosMatrixClass.Guide);
    }

    [Fact] // ReBar déjà actif → aucun constat ReBar/Above4G/CSM
    public void ReBarOn_emits_no_rebar_chain()
    {
        var findings = Advise(Hw(gpuReBar: TriState.On), State(reBar: TriState.On));
        findings.Select(f => f.Kind).Should()
            .NotContain(new[] { FirmwareSettingKind.ReBar, FirmwareSettingKind.Above4G, FirmwareSettingKind.Csm });
    }

    [Fact] // ReBar inconnu sur NVIDIA → recommander avec réserve, Info, jamais majeur ni forcé
    public void ReBarUnknown_nvidia_emits_Info_caveat_finding()
    {
        var f = Advise(Hw(gpuVendor: "NVIDIA", gpuReBar: TriState.Unknown), State(reBar: TriState.Unknown))
            .Single(x => x.Kind == FirmwareSettingKind.ReBar);

        f.Severity.Should().Be(Severity.Info);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read);
        f.CurrentState.Should().Be(TriState.Unknown);
        f.StateNotReadable.Should().BeTrue();
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Verify);
        // Unknown ne doit pas entraîner d'étapes d'écriture CSM/Above-4G.
        Advise(Hw(gpuVendor: "NVIDIA", gpuReBar: TriState.Unknown), State(reBar: TriState.Unknown))
            .Select(x => x.Kind).Should()
            .NotContain(new[] { FirmwareSettingKind.Above4G, FirmwareSettingKind.Csm });
    }

    [Fact] // ReBar désactivé, connu (AMD) → constat Guide normal, sans réserve « inconnu »
    public void ReBarOff_amd_emits_normal_finding_not_caveat()
    {
        var f = Advise(Hw(gpuVendor: "AMD", gpuReBar: TriState.Off), State(reBar: TriState.Off))
            .Single(x => x.Kind == FirmwareSettingKind.ReBar);

        f.MatrixClass.Should().Be(BiosMatrixClass.Guide);
        f.StateNotReadable.Should().BeFalse();
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Enable);
    }

    // ════════════════ Mode de démarrage / garde CSM (sécurité de démarrage) ════════════════

    [Fact] // Démarrage Legacy + ReBar désactivé → ne pas conseiller de couper le CSM (risque de non-démarrage)
    public void Legacy_boot_suppresses_csm_off_recommendation()
    {
        var findings = Advise(Hw(gpuReBar: TriState.Off, isUefi: false), State(reBar: TriState.Off));

        findings.Should().NotContain(f => f.Kind == FirmwareSettingKind.Csm,
            "advising CSM-off on a Legacy/MBR system risks no-boot");
        // Le constat ReBar remonte toujours, juste sans l'étape CSM dangereuse.
        findings.Should().Contain(f => f.Kind == FirmwareSettingKind.ReBar);
    }

    // ════════════════ X3D / CPPC ════════════════

    [Fact] // X3D multi-CCD → un constat à vérifier (côté Windows d'abord), pas une écriture BIOS majeure
    public void MultiCcd_X3D_emits_verify_finding_not_a_bios_write()
    {
        var f = Advise(Hw(isX3D: true, ccdCount: 2), State())
            .Single(x => x.Kind == FirmwareSettingKind.X3dCppc);

        // Adouci (le correctif principal est côté Windows, CPPC du BIOS seulement « si mal réglé »).
        f.Severity.Should().Be(Severity.Minor);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read, "the primary routing fix is Windows-side, not a BIOS step");
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Verify);
        f.StateNotReadable.Should().BeTrue();
    }

    [Fact] // Hors X3D ou un seul CCD → aucun constat X3D
    public void NonX3D_emits_no_X3dCppc_finding()
    {
        Advise(Hw(isX3D: false, ccdCount: 1), State())
            .Should().NotContain(x => x.Kind == FirmwareSettingKind.X3dCppc);
        Advise(Hw(isX3D: true, ccdCount: 1), State())
            .Should().NotContain(x => x.Kind == FirmwareSettingKind.X3dCppc);
    }

    // ════════════════ Lien PCIe : « non lu » honnête ════════════════

    [Fact] // L'info de lien PCIe n'est pas dans l'inventaire du scan → constat « non lu » honnête, jamais fabriqué
    public void PcieGen_emits_honest_not_read_finding()
    {
        var f = Advise(Hw(), State())
            .Single(x => x.Kind == FirmwareSettingKind.PcieGen);

        f.Severity.Should().Be(Severity.Info);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read);
        f.CurrentState.Should().Be(TriState.Unknown);
        f.StateNotReadable.Should().BeTrue();
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Verify);
        f.Gain.Should().BeNull();
    }

    // ════════════════ Version du BIOS / AGESA ════════════════

    [Fact] // date de BIOS ancienne → suggestion informative de mise à jour (sans base = aucune affirmation dure)
    public void OldBios_emits_Info_update_suggestion()
    {
        var old = DateTimeOffset.UtcNow.AddYears(-4);
        var f = Advise(Hw(biosDate: old), State())
            .Single(x => x.Kind == FirmwareSettingKind.BiosVersion);

        f.Severity.Should().Be(Severity.Info);
        f.MatrixClass.Should().Be(BiosMatrixClass.Read);
        f.RecommendedAction.Should().Be(BiosRecommendedAction.Update);
    }

    [Fact] // BIOS récent → aucun constat de version
    public void RecentBios_emits_no_version_finding()
    {
        Advise(Hw(biosDate: DateTimeOffset.UtcNow.AddMonths(-3)), State())
            .Should().NotContain(x => x.Kind == FirmwareSettingKind.BiosVersion);
    }

    // ════════════════ Filtre par châssis ════════════════

    [Theory] // Le BIOS d'un portable ou d'une console portable est verrouillé → aucun constat firmware actionnable
    [InlineData(ChassisType.Laptop)]
    [InlineData(ChassisType.Handheld)]
    public void LockedChassis_emits_no_actionable_findings(ChassisType chassis)
    {
        var findings = Advise(Hw(chassis: chassis, gpuReBar: TriState.Off, isX3D: true, ccdCount: 2),
                              State(xmp: TriState.Off, reBar: TriState.Off));

        // Aucune recommandation d'écriture Guide/Forbidden sur une machine verrouillée.
        findings.Should().NotContain(f => f.MatrixClass == BiosMatrixClass.Guide);
        findings.Select(f => f.Kind).Should().NotContain(new[]
        {
            FirmwareSettingKind.XmpExpo, FirmwareSettingKind.ReBar,
            FirmwareSettingKind.Above4G, FirmwareSettingKind.Csm,
            FirmwareSettingKind.X3dCppc, FirmwareSettingKind.PcieGen,
        });
        // A single honest informational finding explaining non-applicability.
        findings.Should().ContainSingle()
            .Which.MatrixClass.Should().Be(BiosMatrixClass.Read);
        findings.Single().Severity.Should().Be(Severity.Info);
    }

    // ════════════════ Frontière défensive ════════════════

    [Fact] // Mémoire vide pendant l'inférence XMP → Result.Fail propre, pas d'exception
    public void EmptyMemory_fails_clean()
    {
        var hw = Hw() with { Memory = Array.Empty<MemoryModule>() };
        var r = Advisor.Advise(hw, State());

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosInputInvalid);
    }

    [Fact] // Aucune carte pendant la comparaison ReBar → Result.Fail propre
    public void NoGpu_fails_clean()
    {
        var hw = Hw() with { Gpus = Array.Empty<GpuInfo>() };
        var r = Advisor.Advise(hw, State());

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosInputInvalid);
    }

    // ════════════════ Intégration : chaque constat est honnête et classé ════════════════

    [Fact] // Bureau entièrement mal réglé → exactement l'ensemble actionnable attendu
    public void MisTunedDesktop_emits_expected_set()
    {
        var findings = Advise(
            Hw(isX3D: true, ccdCount: 2, gpuVendor: "AMD", gpuReBar: TriState.Off, isUefi: true),
            State(xmp: TriState.Off, reBar: TriState.Off));

        findings.Select(f => f.Kind).Should().Contain(new[]
        {
            FirmwareSettingKind.XmpExpo,
            FirmwareSettingKind.Above4G,
            FirmwareSettingKind.Csm,
            FirmwareSettingKind.ReBar,
            FirmwareSettingKind.X3dCppc,
        });
    }

    [Fact] // Bureau bien réglé → « rien à régler dans le BIOS » (seulement des infos « non lu » honnêtes)
    public void WellTunedDesktop_has_no_actionable_findings()
    {
        var findings = Advise(
            Hw(isX3D: false, ccdCount: 1, gpuVendor: "NVIDIA", gpuReBar: TriState.On,
               isUefi: true, configuredMhz: 6000, ratedMhz: 6000,
               biosDate: DateTimeOffset.UtcNow.AddMonths(-2)),
            State(xmp: TriState.On, reBar: TriState.On));

        // Aucun constat actionnable (Guide) : un résultat valide, supposé « déjà bon ».
        findings.Should().NotContain(f => f.MatrixClass == BiosMatrixClass.Guide);
        // Tout constat restant est purement informatif ou « non lu » honnête.
        findings.Should().OnlyContain(f =>
            f.Severity == Severity.Info && f.MatrixClass == BiosMatrixClass.Read);
    }

    [Fact] // global invariant: every finding carries a non-default matrix class + resx keys
    public void EveryFinding_carries_matrix_class_and_keys()
    {
        var findings = Advise(
            Hw(isX3D: true, ccdCount: 2, gpuVendor: "NVIDIA", gpuReBar: TriState.Off),
            State(xmp: TriState.Off, reBar: TriState.Off));

        findings.Should().NotBeEmpty();
        findings.Should().OnlyContain(f =>
            !string.IsNullOrWhiteSpace(f.TitleKey) &&
            !string.IsNullOrWhiteSpace(f.RationaleKey) &&
            !string.IsNullOrWhiteSpace(f.RecommendedActionKey));
        // Aucun constat ne peut annoncer une classe d'écriture que la base ne sait pas honorer.
        findings.Should().OnlyContain(f =>
            f.MatrixClass == BiosMatrixClass.Guide || f.MatrixClass == BiosMatrixClass.Read);
    }

    // ════════════════ Gains présentés honnêtement (fourchettes et réserves, pas de % nu) ════════════════

    [Fact] // ReBAR dépend du jeu (peut régresser) → conditionnel, aucun % ponctuel fabriqué
    public void ReBar_gain_is_conditional_with_no_fabricated_percent()
    {
        var f = Advise(Hw(gpuVendor: "AMD", gpuReBar: TriState.Off, isUefi: true), State(reBar: TriState.Off))
            .Single(x => x.Kind == FirmwareSettingKind.ReBar);

        f.Gain.Should().NotBeNull();
        f.Gain!.Conditional.Should().BeTrue("ReBAR is profiled per game, not a global win");
        f.Gain.MinPct.Should().BeNull("no honest global % exists for ReBAR");
        f.Gain.MaxPct.Should().BeNull();
        f.Gain.SourceKey.Should().NotBeNullOrWhiteSpace();
        f.Gain.CaveatKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact] // X3D : seulement si mal routé → conditionnel, et aucune fourchette à l'écran
    public void X3d_gain_is_conditional_with_no_range()
    {
        var f = Advise(Hw(isX3D: true, ccdCount: 2), State())
            .Single(x => x.Kind == FirmwareSettingKind.X3dCppc);

        f.Gain.Should().NotBeNull();
        f.Gain!.Conditional.Should().BeTrue("the gain is realized only if the CCD routing is mis-set");
        f.Gain.MinPct.Should().BeNull();
        f.Gain.MaxPct.Should().BeNull();
        f.Gain.SourceKey.Should().NotBeNullOrWhiteSpace();
        f.Gain.CaveatKey.Should().NotBeNullOrWhiteSpace();
    }

    // Une fourchette que rien de public ne tient ne s'affiche pas.
    //
    // Une plage comme « XMP : ≈ 11–25 % » ou « X3D : ≈ 0–10 % » tiree de nos notes ne tient pas. Les
    // mesures publiques disent autre chose : pour XMP/EXPO, de quelques % dans un jeu limite par la
    // carte graphique a ~42 % dans un jeu tres sensible a la memoire (TechSpot, XDA), et ~1 % sur un
    // X3D (AMD). Une fourchette unique ne decrit la machine de personne ; la note memoire du projet
    // (non publiee) le dit elle-meme : « non chiffrable de facon canonique : on mesure ». Le chiffre
    // de la personne vient de l'A/B du produit, pas d'une plage imprimee d'avance.
    [Fact]
    public void No_bios_gain_shows_a_range_nothing_public_holds()
    {
        var tout = Advise(Hw(isX3D: true, ccdCount: 2, gpuVendor: "AMD", gpuReBar: TriState.Off, isUefi: true),
                          State(xmp: TriState.Off, reBar: TriState.Off));

        tout.Where(f => f.Gain is not null).Should().HaveCountGreaterThanOrEqualTo(3, "temoin : XMP, ReBAR, X3D");
        tout.Where(f => f.Gain is { MinPct: not null } or { MaxPct: not null })
            .Select(f => f.Kind).Should().BeEmpty();
    }
}
