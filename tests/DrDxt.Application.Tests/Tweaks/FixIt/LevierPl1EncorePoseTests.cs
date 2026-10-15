using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// « encore posé ? » décide si un levier de PL1 sort du journal (et perd son retour). Deux défauts :
// la comparaison portait sur les 64 bits du registre (un autre outil qui touche PL2 faisait croire à un redémarrage), et un
// plafond posé par-dessus une montée faisait lire la montée « rendue » : son retour vers la PL1 du BIOS était perdu.
public sealed class LevierPl1EncorePoseTests
{
    private const ulong Unite = 0x3; // huitièmes de watt
    private const ulong Bios125 = (2024UL << 32) | (1UL << 47) | (1UL << 15) | 1000UL; // PL1 125 W, PL2 253 W
    private static readonly ulong Monte253 = RegistreDeLimiteCpu.ComposerPl1(Bios125, 253, Unite);
    private static readonly ulong Plafond160 = RegistreDeLimiteCpu.ComposerPl1(Monte253, 160, Unite);

    private static TweakSnapshot Levier(string id, ulong origine, ulong pose) => new(id,
        PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(origine, pose)).Value!,
        PrivilegedOpCodec.Serialize(new SetCpuPackagePowerLimitOp(pose, origine)).Value!);

    private static readonly TweakSnapshot Montee = Levier("fixit.cpu_power_limit", Bios125, Monte253);
    private static readonly TweakSnapshot Plafond = Levier("fixit.cpu_power_cap", Monte253, Plafond160);

    [Fact]
    public void Seul_le_champ_PL1_compte_un_autre_outil_qui_touche_PL2_ne_rend_pas_le_levier()
    {
        var pl2Change = (Monte253 & ~(0x7FFFUL << 32)) | (1800UL << 32);
        LevierPl1TweakBase.EncorePose(Montee, [Montee], pl2Change).Should().BeTrue();
        LevierPl1TweakBase.EncorePose(Montee, [Montee], Bios125).Should().BeFalse("le BIOS a remis sa PL1 : rendu");
    }

    [Fact]
    public void Une_montee_recouverte_par_un_plafond_reste_posee_et_le_redemarrage_rend_les_deux()
    {
        TweakSnapshot[] journal = [Montee, Plafond];
        LevierPl1TweakBase.EncorePose(Plafond, journal, Plafond160).Should().BeTrue();
        LevierPl1TweakBase.EncorePose(Montee, journal, Plafond160).Should().BeTrue("le retour du plafond la rend : recouverte, pas rendue");

        LevierPl1TweakBase.EncorePose(Montee, journal, Bios125).Should().BeFalse();
        LevierPl1TweakBase.EncorePose(Plafond, journal, Bios125).Should().BeFalse();
        LevierPl1TweakBase.EncorePose(Montee, [Montee], Plafond160).Should().BeFalse("sans le plafond au journal, rien ne la recouvre");
    }

    [Fact]
    public void Un_levier_illisible_rend_null()
        => LevierPl1TweakBase.EncorePose(Montee with { AppliedValueJson = null }, [Montee], Monte253).Should().BeNull();
}
