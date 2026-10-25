using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les arrêts brutaux et les incidents USB 3 se disent en anglais ; la famille USB 3 se lit dans le paramètre, pas dans
// le texte (qui change de langue).
public sealed class StabiliteEnAnglaisTests
{
    [Fact]
    public void La_repartition_des_arrets_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
        // Heure de la machine : les dates s'affichent dans son fuseau, le jour attendu ne change pas avec lui.
        var quand = new DateTimeOffset(new DateTime(2026, 10, 3, 20, 0, 0, DateTimeKind.Local));

        RepairAuditService.Repartition([new ArretBrutal(quand, 0, false, false), new ArretBrutal(quand.AddDays(2), 0, false, false)], 1)
            .Should().Be("2 shutdowns with no code at all (freeze, power cut or power supply; from 10/03 to 10/05), "
                         + "1 long press on the power button, not counted");
    }

    [Theory]
    [InlineData("1009", ", USB 3 controller (live dump)")]
    [InlineData("3001", ", USB 3 hub (live dump)")]
    [InlineData("2000", "")]
    public void La_famille_usb3_se_lit_dans_le_parametre(string parametre, string attendu)
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
        RepairAuditService.FamilleUsb3(0x144, parametre).Should().Be(attendu);
    }
}
