using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// Le pilote RTCore, dont Microsoft bloque des versions : absent, installé ou chargé, lu dans la clé Services du registre
// (la même énumération que l'inventaire de la machine), sans requête WMI.
public sealed class PiloteRTCoreTests
{
    private static readonly (string Nom, string Image)[] Ordinaires = [("Tcpip", @"System32\drivers\tcpip.sys"), ("Spooler", @"C:\WINDOWS\System32\spoolsv.exe")];

    [Fact]
    public void Absent_rien_a_redire()
        => PiloteRTCore.Juger(Ordinaires, _ => false).Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Installe_ou_charge_donne_un_constat_avec_son_etape()
    {
        (string, string)[] avec = [.. Ordinaires, ("RTCore64", @"\??\C:\Program Files (x86)\Outil\RTCore64.sys")];
        var installe = PiloteRTCore.Juger(avec, _ => false);
        installe.Statut.Should().Be(RepairStatus.Suspicious);
        installe.Action.Should().Be(RepairAction.Recommandation);
        installe.ValeurCourante.Should().Contain("RTCore64.sys");
        PiloteRTCore.Juger(avec, f => f.Equals("RTCore64.sys", StringComparison.OrdinalIgnoreCase)).Explication.Should().NotBe(installe.Explication);
    }

    // Reconnu à son fichier même sous un autre nom de service.
    [Fact]
    public void Reconnu_a_son_fichier_sous_un_autre_nom()
        => PiloteRTCore.Juger([("OutilCarte", @"C:\Outil\RTCore32.sys")], _ => false).Statut.Should().Be(RepairStatus.Suspicious);

    [Fact]
    public void Non_lu_reste_inconnu()
        => PiloteRTCore.Juger(null, _ => false).Statut.Should().Be(RepairStatus.Unknown);

    // Sans droits, Windows peut masquer les adresses du noyau : la liste des pilotes chargés ne se lit pas, et « pas chargé »
    // serait une affirmation sans lecture.
    [Fact]
    public void Un_chargement_non_lu_ne_se_dit_pas_absent()
    {
        var avec = new[] { ("RTCore64", @"\SystemRoot\System32\drivers\RTCore64.sys") };
        var nonLu = PiloteRTCore.Juger(avec, _ => null);
        nonLu.Statut.Should().Be(RepairStatus.Suspicious, "le pilote est installé, lu dans la clé Services");
        nonLu.Explication.Should().Be(DrDxt.Core.Docteur.T("RTC.InstalleChargeNonLu"))
            .And.NotBe(PiloteRTCore.Juger(avec, _ => false).Explication);
    }
}
