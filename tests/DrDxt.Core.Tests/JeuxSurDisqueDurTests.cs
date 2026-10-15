using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

// Des jeux posés sur un disque dur se chargent bien plus lentement.
public sealed class JeuxSurDisqueDurTests
{
    [Fact]
    public void Une_bibliotheque_sur_un_disque_dur_se_dit_avec_le_geste()
    {
        var c = JeuxSurDisqueDur.Juger([new("Steam", @"C:\Steam", JeuxSurDisqueDur.Ssd), new("Steam", @"D:\Jeux", JeuxSurDisqueDur.DisqueDur)]);
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain(@"D:\Jeux").And.NotContain(@"C:\Steam").And.Contain("DrDXT ne déplace rien");
    }

    [Fact]
    public void Tout_sur_ssd_est_attendu_et_un_type_non_dit_n_est_jamais_juge()
    {
        JeuxSurDisqueDur.Juger([new("Epic", @"C:\Epic", JeuxSurDisqueDur.Ssd)]).Statut.Should().Be(RepairStatus.ExpectedDefault);
        JeuxSurDisqueDur.Juger([new("Epic", @"E:\Epic", 0)]).Statut.Should().Be(RepairStatus.Unknown);
        JeuxSurDisqueDur.Juger(null).Statut.Should().Be(RepairStatus.Unknown);
        JeuxSurDisqueDur.Juger([]).Statut.Should().Be(RepairStatus.Observed);
    }

    // Un type non lu ne s'affiche pas « aucun », et seuls les dossiers lus sont dits sur un SSD.
    [Fact]
    public void Rien_de_lu_ne_s_affiche_pas_aucun_et_le_compte_des_ssd_est_juste()
    {
        JeuxSurDisqueDur.Juger([new("Epic", @"E:\Epic", 0)]).ValeurCourante.Should().BeNull();
        JeuxSurDisqueDur.Juger([new("Epic", @"C:\Epic", JeuxSurDisqueDur.Ssd), new("Steam", @"E:\Steam", 0)]).Explication
            .Should().StartWith("1 dossier(s) de jeux sur un SSD").And.Contain("1 sur un disque dont Windows ne dit pas le type");
    }
}
