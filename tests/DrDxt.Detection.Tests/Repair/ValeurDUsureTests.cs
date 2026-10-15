using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Photo (stockage) : « Usure · Samsung SSD 990 PRO 2TB : 0 %, 40 °C ». Sous le mot « usure », le
// second chiffre n'était nommé nulle part. La température dit ce qu'elle est.
public sealed class ValeurDUsureTests
{
    [Fact]
    public void La_temperature_est_nommee_a_cote_de_l_usure()
        => RepairAuditService.ValeurDUsure(0, 40).Should().Be("0 %, température 40 °C");

    [Fact]
    public void Sans_temperature_publiee_l_usure_reste_seule()
        => RepairAuditService.ValeurDUsure(12, null).Should().Be("12 %");

    // Une clé USB publiait « 0 °C » ; zéro n'est pas une température de disque en marche.
    [Fact]
    public void Une_temperature_a_zero_n_est_pas_dite()
        => RepairAuditService.ValeurDUsure(0, 0).Should().Be("0 %");
}
