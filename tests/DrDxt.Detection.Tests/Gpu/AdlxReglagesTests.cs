using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.Detection.Network;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Settings;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Detection.Tests.Gpu;

// A1 (06/10/2026) : les réglages AMD lus par ADLX arrivent là où le produit lit déjà ceux de NVIDIA (ReBAR, couture
// de latence, limite d'images, constats de santé). Non lus, ils restent « non lu » nommé.
public sealed class AdlxReglagesTests
{
    private static readonly AdlxReglagesLus RienLu = AdlxReglagesLus.RienLu(AdlxReglages.DllAbsente);
    private static GpuInfo G(string modele) => new(modele, "", 8L << 30, "", TriState.Unknown);

    [Fact]
    public void DLL_absente_rien_n_est_lu_et_la_raison_est_nommee()
    {
        var lus = AdlxReglages.Lire(null);
        lus.Raison.Should().Be(AdlxReglages.DllAbsente);
        lus.Sam.Should().BeNull();
        lus.AntiLag.Should().BeNull();
        lus.LimiteImages.Should().BeNull();
        lus.TemperatureC.Should().BeNull();
    }

    [Fact]
    public void Un_natif_qui_leve_rend_non_lu()
    {
        var natif = Substitute.For<IAdlxNative>();
        natif.LireReglages().Returns(_ => throw new AccessViolationException());
        AdlxReglages.Lire(natif).Raison.Should().Contain("AccessViolationException");
    }

    [Theory]
    [InlineData(true, TriState.On)]
    [InlineData(false, TriState.Off)]
    [InlineData(null, TriState.Unknown)]
    public void SAM_lu_par_ADLX_devient_l_etat_ReBAR(bool? sam, TriState attendu)
        => new AdlxProvider(() => new AdlxReglagesLus(Sam: sam)).GetSmartAccessMemoryState().Should().Be(attendu);

    [Fact]
    public void Un_lecteur_SAM_qui_leve_rend_Unknown()
        => new AdlxProvider(() => throw new InvalidOperationException()).GetSmartAccessMemoryState().Should().Be(TriState.Unknown);

    [Fact]
    public void La_couture_de_latence_AMD_rend_Anti_Lag_et_la_limite_lus_ou_un_echec_nomme()
    {
        var lue = new AdlxLatencyReader(() => new AdlxReglagesLus(AntiLag: false, FrtcActif: true, FrtcFps: 141, ChillActif: false));
        lue.IsAvailable.Should().BeTrue();
        lue.GetAntiLagState().Value.Should().Be(0);
        lue.GetChillActif().Value.Should().BeFalse();
        lue.GetFrameRateTarget().Value.Should().Be(141);
        new AdlxLatencyReader(() => new AdlxReglagesLus(FrtcActif: false, FrtcFpsReglee: 60)).GetFrameRateTarget().Value
            .Should().Be(-60, "éteint : moins la cible réglée, pour la rendre exacte");
        new AdlxLatencyReader(() => new AdlxReglagesLus(FrtcActif: false)).GetFrameRateTarget().IsOk
            .Should().BeFalse("cible réglée non lue : on ne saurait pas la rendre");
        lue.GetFrameRateLimit().Value.Should().Be(141);

        var absente = new AdlxLatencyReader(() => RienLu);
        absente.IsAvailable.Should().BeFalse();
        absente.GetAntiLagState().IsOk.Should().BeFalse();
        absente.GetChillActif().IsOk.Should().BeFalse();
        absente.GetVsyncMode().IsOk.Should().BeFalse();
        absente.GetEnhancedSyncActif().IsOk.Should().BeFalse();
        var vsync = new AdlxLatencyReader(() => new AdlxReglagesLus(VsyncMode: 2, EnhancedSyncActif: false));
        vsync.GetVsyncMode().Value.Should().Be(2);
        vsync.GetEnhancedSyncActif().Value.Should().BeFalse();
        absente.GetAntiLagState().Error!.Value.Message.Should().Be(AdlxReglages.DllAbsente);
        absente.GetFrameRateLimit().Error!.Value.Code.Should().Be(ErrorCodes.TweakSdkUnavailable);
    }

    // ── la limite d'images AMD dans l'état des réglages (FrameCap), là où NVIDIA rend la sienne ──

    private static HardwareInventory Hw() => new(
        new CpuInfo("AMD Ryzen 7 7800X3D", "AuthenticAMD", 8, 16, true, 1),
        new[] { G("AMD Radeon RX 9070 XT") },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("ASUS", "B650"),
        new BiosInfo("1", DateTimeOffset.UtcNow, true),
        ChassisType.Desktop);

    private static async Task<SettingsState> LireEtat(IAdlx adlx)
    {
        var power = Substitute.For<IPowerProvider>();
        power.GetActiveSchemeGuid().Returns(Result<string>.Ok("381b4222-f694-41f0-9685-ff5bb260df2e"));
        var reg = Substitute.For<IRegistryProvider>();
        reg.GetParkedCoreIndexes().Returns(Array.Empty<int>());
        var net = Substitute.For<INetworkAdapterReader>();
        net.Read().Returns(Result<IReadOnlyList<NetworkAdapterState>>.Fail(ErrorCodes.WmiFail, "non lu"));
        var ecran = Substitute.For<IDisplayInfo>();
        ecran.GetAllDisplays().Returns(Result<IReadOnlyList<DisplayInfo>>.Ok(new List<DisplayInfo>
        {
            new(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144, MaxRefreshHz: 144),
        }));
        var r = await new SettingsStateReader(power, reg, net, nvApi: null, display: ecran, adlx: adlx).ReadAsync(Hw());
        return r.Value!;
    }

    [Fact]
    public async Task La_limite_FRTC_lue_par_ADLX_est_jugee_comme_celle_de_NVIDIA()
    {
        var etat = await LireEtat(new AdlxLatencyReader(() => new AdlxReglagesLus(FrtcActif: true, FrtcFps: 141, ChillActif: false)));
        etat.FrameCap!.Value.Verdict.Should().Be(FrameCapVerdict.Coherent, "141 = 144 - 3");
        etat.FrameCap.Value.Origin.Should().BeNull("ADLX ne dit pas qui a posé la limite");
    }

    [Fact]
    public async Task Une_limite_AMD_non_lue_reste_indeterminee()
    {
        var etat = await LireEtat(new AdlxLatencyReader(() => RienLu));
        etat.FrameCap!.Value.Verdict.Should().Be(FrameCapVerdict.Indeterminate);
    }

    // ── constats de santé : la vraie valeur remplace le « non lu » AMD ──

    [Fact]
    public void Une_carte_AMD_non_lue_est_nommee_avec_la_raison_d_ADLX()
        => RepairAuditService.TemperatureCarteNonLue([G("AMD Radeon RX 7800 XT")], RienLu)
            .Should().Contain("AMD Radeon RX 7800 XT").And.Contain("amdadlx64.dll absente").And.Contain("HWiNFO");

    // Revue T3+T4 (F-23) : APU + Radeon dédiée, l'ordre de Windows met la puce intégrée d'abord ; la phrase nomme la
    // carte dédiée (la plus grosse mémoire).
    [Fact]
    public void Avec_une_puce_integree_en_premier_la_carte_dediee_est_nommee()
        => RepairAuditService.TemperatureCarteNonLue(
                [new GpuInfo("AMD Radeon(TM) Graphics", "", 512L << 20, "", TriState.Unknown), G("AMD Radeon RX 7800 XT")], RienLu)
            .Should().Contain("AMD Radeon RX 7800 XT");

    // La machine enregistrée `amd-radeon-synthetique` porte ces deux phrases (carte trouvée, valeur non rendue) : elles
    // doivent rester celles que le code écrit.
    [Fact]
    public void Les_phrases_de_la_machine_AMD_enregistree_sont_celles_du_code()
    {
        var aucune = new AdlxReglagesLus();
        RepairAuditService.TemperatureCarteNonLue([G("AMD Radeon RX 7800 XT")], aucune).Should().Be(
            "DrDXT n'a pas pu lire la température de ta carte (AMD Radeon RX 7800 XT) par le pilote AMD. Le logiciel AMD ou HWiNFO la montre.");
        RepairAuditService.LimiteImagesHorsNvidia([G("AMD Radeon RX 7800 XT")], aucune).Single().Explication.Should().Be(
            "DrDXT n'a pas pu lire la limite d'images de ta carte (AMD Radeon RX 7800 XT) par le pilote AMD. Avec un écran à fréquence "
            + "variable, une limite juste sous sa fréquence maximale garde le rafraîchissement variable actif ; elle se règle dans "
            + "le logiciel AMD ou dans le jeu.");
    }

    [Fact]
    public void Une_carte_NVIDIA_garde_la_phrase_NVML()
        => RepairAuditService.TemperatureCarteNonLue([G("NVIDIA GeForce RTX 4060")], RienLu).Should().Contain("NVML");

    [Fact]
    public void Une_temperature_AMD_lue_par_ADLX_devient_un_constat_observe()
    {
        var f = RepairAuditService.TemperatureAdlx([G("AMD Radeon RX 9070 XT")], new(TemperatureC: 61.4))!;
        f.Statut.Should().Be(RepairStatus.Observed);
        f.ValeurCourante.Should().Be("61 °C");
        f.Explication.Should().Contain("ADLX");
        RepairAuditService.TemperatureAdlx([G("AMD Radeon RX 9070 XT")], RienLu).Should().BeNull();
        RepairAuditService.TemperatureAdlx([G("NVIDIA GeForce RTX 4060")], new(TemperatureC: 61.4)).Should().BeNull();
    }

    // F-24 : une Intel Arc sans NVIDIA reçoit un « non lu », avec le logiciel Intel.
    [Fact]
    public void La_limite_d_images_d_une_Intel_Arc_se_dit_non_lue()
        => RepairAuditService.LimiteImagesHorsNvidia([G("Intel(R) Arc(TM) B580 Graphics")], RienLu)
            .Single().Explication.Should().Contain("Intel(R) Arc(TM) B580 Graphics").And.Contain("logiciel Intel");

    // chantier B5 a : la limite d'images d'une carte AMD non lue est un « non lu » dit, pas un silence.
    [Fact]
    public void La_limite_d_images_d_une_carte_AMD_non_lue_se_dit_non_lue_avec_la_raison()
    {
        var f = RepairAuditService.LimiteImagesHorsNvidia([G("AMD Radeon RX 7800 XT")], RienLu).Single();
        f.Statut.Should().Be(RepairStatus.Unknown);
        f.Explication.Should().Contain("amdadlx64.dll absente");
        RepairAuditService.LimiteImagesHorsNvidia([G("NVIDIA GeForce RTX 4060")], RienLu).Should().BeEmpty();
        RepairAuditService.LimiteImagesHorsNvidia([G("AMD Radeon RX 7800 XT"), G("NVIDIA GeForce RTX 4060")], RienLu)
            .Should().BeEmpty();
    }

    [Fact]
    public void La_limite_FRTC_lue_par_ADLX_remplace_le_non_lu()
    {
        var f = RepairAuditService.LimiteImagesHorsNvidia([G("AMD Radeon RX 7800 XT")],
            new(FrtcActif: true, FrtcFps: 141, ChillActif: false)).Single();
        f.Statut.Should().Be(RepairStatus.Observed);
        f.ValeurCourante.Should().Be("141 images/s");
        f.Explication.Should().Contain("Frame Rate Target Control");
    }

    [Fact]
    public void Les_deux_limites_lues_eteintes_disent_aucune()
        => RepairAuditService.LimiteImagesHorsNvidia([G("AMD Radeon RX 7800 XT")], new(FrtcActif: false, ChillActif: false))
            .Single().ValeurCourante.Should().Be("aucune");

    [Fact]
    public void Anti_Lag_lu_par_ADLX_se_dit_et_non_lu_ne_dit_rien()
    {
        RepairAuditService.AntiLagAmd([G("AMD Radeon RX 7800 XT")], new(AntiLag: true)).Single().ValeurCourante.Should().Be("activé");
        RepairAuditService.AntiLagAmd([G("AMD Radeon RX 7800 XT")], new(AntiLag: false)).Single().ValeurCourante.Should().Be("désactivé");
        RepairAuditService.AntiLagAmd([G("AMD Radeon RX 7800 XT")], RienLu).Should().BeEmpty();
        RepairAuditService.AntiLagAmd([G("NVIDIA GeForce RTX 4060")], new(AntiLag: true)).Should().BeEmpty();
    }
}
