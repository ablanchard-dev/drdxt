using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// Le fichier d'échange sur un disque dur alors qu'un SSD est là.
public sealed class FichierDEchangeSurDisqueDurTests
{
    private const int Hdd = JeuxSurDisqueDur.DisqueDur, Ssd = JeuxSurDisqueDur.Ssd;

    [Fact]
    public void Sur_un_disque_dur_avec_un_ssd_present_se_dit_avec_le_geste()
    {
        var c = FichierDEchangeSurDisqueDur.Juger([(@"D:\pagefile.sys", Hdd)], ssdPresent: true);
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.ValeurCourante.Should().Be(@"D:\pagefile.sys");
        c.Explication.Should().Contain("Mémoire virtuelle").And.Contain("DrDXT ne le déplace pas");
    }

    [Fact]
    public void Sur_un_disque_dur_sans_ssd_rien_n_est_reproche()
        => FichierDEchangeSurDisqueDur.Juger([(@"C:\pagefile.sys", Hdd)], ssdPresent: false).Statut.Should().Be(RepairStatus.Observed);

    [Fact]
    public void Sur_un_ssd_c_est_attendu()
        => FichierDEchangeSurDisqueDur.Juger([(@"C:\pagefile.sys", Ssd)], ssdPresent: true).Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Un_type_non_dit_ou_une_lecture_ratee_n_est_jamais_juge()
    {
        FichierDEchangeSurDisqueDur.Juger([(@"C:\pagefile.sys", 0)], ssdPresent: true).Statut.Should().Be(RepairStatus.Unknown);
        FichierDEchangeSurDisqueDur.Juger(null, ssdPresent: true).Statut.Should().Be(RepairStatus.Unknown);
        FichierDEchangeSurDisqueDur.Juger([], ssdPresent: true).Statut.Should().Be(RepairStatus.Observed);
    }

    [Fact]
    public void Un_seul_fichier_sur_disque_dur_parmi_plusieurs_suffit()
        => FichierDEchangeSurDisqueDur.Juger([(@"C:\pagefile.sys", Ssd), (@"D:\pagefile.sys", Hdd)], ssdPresent: true)
            .Statut.Should().Be(RepairStatus.Suspicious);

    // Lecture incomplète, cas mixte.
    [Fact]
    public void Un_disque_au_type_non_dit_n_affirme_pas_qu_il_n_y_a_pas_de_ssd()
        => FichierDEchangeSurDisqueDur.Juger([(@"D:\pagefile.sys", Hdd)], ssdPresent: null).Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Le_cas_mixte_ne_nomme_que_le_fichier_sur_disque_dur()
    {
        var c = FichierDEchangeSurDisqueDur.Juger([(@"C:\pagefile.sys", Ssd), (@"D:\pagefile.sys", Hdd)], ssdPresent: true);
        c.ValeurCourante.Should().Be(@"D:\pagefile.sys");
        c.Explication.Should().Contain(@"D:\pagefile.sys").And.NotContain(@"C:\pagefile.sys");
    }
}
