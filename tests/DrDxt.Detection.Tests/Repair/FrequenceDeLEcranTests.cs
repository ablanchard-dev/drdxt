using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Win32 rend 0 ou 1 pour « fréquence par défaut du matériel » (bureau à distance, dalle qui l'annonce) : DrDXT la lit 0, non
// lue. Comparée au maximum, elle donnait « l'écran sait 200 Hz, tourne à 0 Hz », avec une recommandation.
public sealed class FrequenceDeLEcranTests
{
    [Fact]
    public void Une_frequence_non_lue_se_dit_non_lue_sans_recommandation()
    {
        var f = RepairAuditService.FrequenceDeLEcran(new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: false, RefreshHz: 0, MaxRefreshHz: 200));

        f.Statut.Should().Be(RepairStatus.Unknown);
        f.Action.Should().NotBe(RepairAction.Recommandation);
        f.Explication.Should().Be(Docteur.T("RA.Ecran.FrequenceNonLue"));
    }

    [Theory]
    [InlineData(144, 144, RepairStatus.ExpectedDefault)]
    [InlineData(60, 144, RepairStatus.NonDefault)]
    [InlineData(144, 0, RepairStatus.Observed)]
    public void Les_frequences_lues_se_jugent_comme_avant(int courante, int max, RepairStatus attendu)
        => RepairAuditService.FrequenceDeLEcran(new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: false, RefreshHz: courante,
            MaxRefreshHz: max)).Statut.Should().Be(attendu);
}
