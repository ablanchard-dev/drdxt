using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// L'identifiant et la portée d'un réglage DRS étaient vérifiés, jamais la valeur. La borne est l'énumération que NVIDIA
// documente (NvApiDriverSettings.h, github.com/NVIDIA/nvapi), pas une règle à nous : un inverse rejoue la valeur LUE chez le
// joueur (une synchro « adaptative demi-fréquence », un plafond bas), et une borne plus stricte la rendait irrejouable,
// laissant le réglage de DrDXT posé pour toujours. Le levier refuse d'écrire quand l'origine lue sort de cette énumération.
public sealed class ValeursDrsBorneesTests
{
    private const uint Frl = 0x10835002, Vsync = 0x00A879CF, Pstate = 0x1057EB71, PreRendu = 0x007BA09E;
    private readonly OpAllowlist _allow = new();

    private bool Admis(uint id, uint valeur) =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(id, valeur, OpAllowlist.BaseProfileName)).IsOk;

    [Theory]
    [InlineData(Frl, 0x400u)]        // au-delà de FRL_FPS_MAX
    [InlineData(Vsync, 1u)]          // hors de l'énumération VSYNCMODE
    [InlineData(Pstate, 6u)]         // au-delà de PREFERRED_PSTATE_MAX
    [InlineData(PreRendu, 0x100u)]   // au-delà de PRERENDERLIMIT_MAX
    public void Une_valeur_hors_de_l_enumeration_documentee_est_refusee(uint id, uint valeur) =>
        Admis(id, valeur).Should().BeFalse();

    [Theory]
    [InlineData(Frl, 0u)]
    [InlineData(Frl, 25u)]           // un plafond bas, réglé par le joueur
    [InlineData(Frl, 0x3FFu)]
    [InlineData(Vsync, 0x60925292u)] // PASSIVE
    [InlineData(Vsync, 0x32610244u)] // FLIPINTERVAL2 : « adaptative demi-fréquence » du panneau
    [InlineData(Vsync, 0x18888888u)] // VIRTUAL
    [InlineData(Pstate, 4u)]         // PREFER_MIN
    [InlineData(PreRendu, 8u)]
    public void Une_origine_documentee_reste_rejouable(uint id, uint valeur) =>
        Admis(id, valeur).Should().BeTrue();

    [Fact]
    public void Le_predicat_est_partage_avec_les_leviers() =>
        ValeursDrs.Documentee(Vsync, 0x32610244).Should().Be(Admis(Vsync, 0x32610244));
}
