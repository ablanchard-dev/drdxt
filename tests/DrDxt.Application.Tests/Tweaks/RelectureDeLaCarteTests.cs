using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>
/// Les réglages de la carte et la limite du processeur se relisent par ce qui les lit vraiment (NVAPI, ADLX, le registre
/// 0x610). Sans eux, l'autopilote rendrait chaque pose en la croyant non prise. Les valeurs se comparent dans
/// l'unité que l'API rend : un pourcentage entier, des MHz entiers, le champ PL1 brut ; aucune conversion ne s'y glisse.
/// </summary>
public sealed class RelectureDeLaCarteTests
{
    private static string Fil(PrivilegedOp op) => PrivilegedOpCodec.Serialize(op).Value!;
    private static TweakSnapshot Pose(PrivilegedOp origine, PrivilegedOp posee) => new("levier", Fil(origine), Fil(posee));
    private static Result<int> Lu(int v) => Result<int>.Ok(v);
    private static readonly Result<int> Refus = Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, "pilote muet");

    private static EtatApplique Verdict(TweakSnapshot s, AppliedStateVerifier.LecteursCarte? carte)
        => AppliedStateVerifier.Verifier(s, Substitute.For<IRegistryReader>(), carte: carte).Verdict;

    // La limite se pose telle que la carte la relit après l'écriture (89 % quand 90 % était demandé) : la relecture la
    // retrouve, jamais « autre valeur » pour un pas de pilote.
    [Theory]
    [InlineData(89, EtatApplique.Applied)]
    [InlineData(100, EtatApplique.NotApplied)]
    [InlineData(80, EtatApplique.Drifted)]
    public void La_limite_NVIDIA_se_relit_en_pourcentage_entier(int lu, EtatApplique attendu)
    {
        var nv = Substitute.For<INvApiGpuTuning>();
        nv.GetPowerLimitPct(0).Returns(Lu(lu));
        var s = Pose(new ApplyNvApiPowerLimitOp(0, 100), new ApplyNvApiPowerLimitOp(0, 89));

        Verdict(s, new(Nvidia: nv)).Should().Be(attendu);
    }

    [Theory]
    [InlineData(AdlxTuningKind.PowerLimit, 100, 85)]
    [InlineData(AdlxTuningKind.AntiLag, 0, 1)]
    [InlineData(AdlxTuningKind.FrameRateTarget, -60, 141)]
    [InlineData(AdlxTuningKind.Undervolt, 2900, 2850)]
    public void Un_reglage_AMD_se_relit_par_son_genre(AdlxTuningKind genre, int origine, int posee)
    {
        var amd = Substitute.For<IAdlxGpuTuning>();
        var pilote = Substitute.For<IAdlx>();
        void Rendre(int v)
        {
            amd.GetPowerLimitPct(0).Returns(Lu(v));
            amd.GetGfxMaxFreqMhz(0).Returns(Lu(v));
            pilote.GetAntiLagState().Returns(Lu(v));
            pilote.GetFrameRateTarget().Returns(Lu(v));
        }
        var s = Pose(new ApplyAdlxTuningOp(0, genre, origine), new ApplyAdlxTuningOp(0, genre, posee));
        var carte = new AppliedStateVerifier.LecteursCarte(Amd: amd, PiloteAmd: pilote);

        Rendre(posee);
        Verdict(s, carte).Should().Be(EtatApplique.Applied);
        Rendre(origine);
        Verdict(s, carte).Should().Be(EtatApplique.NotApplied);
    }

    // La Vsync du pilote AMD et la FRTC se posent ensemble : chaque feuille relue par son genre.
    [Fact]
    public void Le_composite_Vsync_et_FRTC_AMD_se_relit_feuille_par_feuille()
    {
        var pilote = Substitute.For<IAdlx>();
        pilote.GetVsyncMode().Returns(Lu(3));
        pilote.GetFrameRateTarget().Returns(Lu(141));
        var origine = CompositeSnapshotCodec.Encode([new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, -60),
            new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 1)]).Value!;
        var posee = CompositeSnapshotCodec.Encode([new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 3),
            new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, 141)]).Value!;
        var s = new TweakSnapshot("latency.vrr_vsync_cap", origine, posee);
        var carte = new AppliedStateVerifier.LecteursCarte(PiloteAmd: pilote);

        Verdict(s, carte).Should().Be(EtatApplique.Applied);
        pilote.GetVsyncMode().Returns(Lu(1));
        Verdict(s, carte).Should().Be(EtatApplique.Drifted, "une feuille rendue, l'autre posée : ni l'un ni l'autre");
    }

    private const ulong Origine65W = 0x00DD8000_00DC8208UL, Pose219W = 0x00DD8000_00DC86D8UL;

    // Le champ PL1 (bits 14:0) en unités du registre : le verrou, l'activation et la PL2, que la carte peut réécrire, ne
    // comptent pas.
    [Theory]
    [InlineData(Pose219W, EtatApplique.Applied)]
    [InlineData(Pose219W | (1UL << 63) | (1000UL << 32), EtatApplique.Applied)]
    [InlineData(Origine65W, EtatApplique.NotApplied)]
    [InlineData((Pose219W & ~0x7FFFUL) | 0x6D9UL, EtatApplique.Drifted)]
    public void La_limite_PL1_se_relit_par_son_champ_brut(ulong registre, EtatApplique attendu)
    {
        var cpu = Substitute.For<ICpuPowerLimitReader>();
        cpu.LireRegistres().Returns(Result<LectureLimiteCpu>.Ok(new LectureLimiteCpu(new RegistresCpuLus(0xA0E03, registre, 0),
            new LimiteDePuissanceLue(0, 0, 65, false))));
        var s = Pose(new SetCpuPackagePowerLimitOp(Origine65W, Pose219W), new SetCpuPackagePowerLimitOp(Pose219W, Origine65W));

        Verdict(s, new(Cpu: cpu)).Should().Be(attendu);
    }

    [Fact]
    public void Une_lecture_qui_echoue_ne_conclut_rien_et_sans_lecteur_rien_n_est_affirme()
    {
        var nv = Substitute.For<INvApiGpuTuning>();
        nv.GetPowerLimitPct(0).Returns(Refus);
        var s = Pose(new ApplyNvApiPowerLimitOp(0, 100), new ApplyNvApiPowerLimitOp(0, 90));

        Verdict(s, new(Nvidia: nv)).Should().Be(EtatApplique.Unknown);
        Verdict(s, null).Should().Be(EtatApplique.Unavailable);
        Verdict(s, new(Amd: Substitute.For<IAdlxGpuTuning>())).Should().Be(EtatApplique.Unavailable, "le lecteur NVIDIA manque");
    }
}
