using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les traces du registre : lues avec leur date, jamais écrites.
public sealed class TracesDuRegistreTests
{
    [Theory]
    [InlineData(20, "20")]
    [InlineData(-1, "4294967295")]
    public void Un_DWORD_se_lit_comme_Windows_l_affiche(int brut, string attendu) => TracesDuRegistre.Texte(brut).Should().Be(attendu);

    [Fact]
    public void Des_octets_se_lisent_en_hexadecimal() => TracesDuRegistre.Texte(new byte[] { 0x0F, 0x00 }).Should().Be("0x0F00");

    // Un refus d'accès sur la clé d'un seul périphérique ne fait pas passer toutes les traces en « non lu ».
    [Fact]
    public void Un_refus_sur_un_seul_peripherique_ne_coute_que_lui()
        => TracesDuRegistre.ParPeripherique(["VEN_10DE", "VEN_8086", "VEN_1022"],
                p => p == "VEN_8086" ? throw new System.Security.SecurityException("refus") : new[] { p })
            .Should().Equal("VEN_10DE", "VEN_1022");

    [Fact]
    [Trait("Category", "Machine")] // lit le vrai registre de la machine
    public void La_cle_MMCSS_se_lit_avec_sa_date_sur_ce_PC()
    {
        var lectures = TracesDuRegistre.Lire();
        lectures.Should().NotBeNull();
        var mmcss = lectures!.Should().ContainSingle(l => l.Id == "mmcss").Subject;
        mmcss.Ecrite.Should().NotBeNull("RegQueryInfoKeyW rend la date de la clé");
        mmcss.Ecrite!.Value.Should().BeBefore(DateTimeOffset.Now.AddMinutes(1));
        mmcss.Valeurs.Should().ContainKey("SystemResponsiveness");
    }
}
