using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Un SSD en USB ne compte pas ; un disque au type non dit empêche d'affirmer « aucun SSD ».
public class SsdInterneTests
{
    [Theory]
    [InlineData(4, 17, true)]   // NVMe interne
    [InlineData(4, 7, false)]   // SSD en USB, seul avec un disque dur
    [InlineData(0, 11, null)]   // type non dit
    public void Le_ssd_interne_se_lit_parmi_tous_les_disques(int type, int bus, bool? attendu)
        => RepairAuditService.SsdInterneParmi([(type, bus), (3, 11)]).Should().Be(attendu);
}
