using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Porte AMD fermée (module de lecture absent) : une seule ligne qui le dit, au lieu de quatre « non lu ». Porte ouverte :
// chaque constat reprend sa ligne.
public sealed class LecturesAmdProfondesTests
{
    private static readonly GenerationAmd.Ligne Zen4 = GenerationAmd.Table.First(l => l.Generation == "Zen 4");

    [Fact]
    public void Porte_fermee_une_seule_ligne_qui_nomme_les_quatre_lectures()
    {
        var appels = 0;
        var c = LecturesAmdProfondes.Juger(Zen4, porteOuverte: false, () => { appels++; return []; }).ToList();

        c.Should().ContainSingle();
        c[0].Statut.Should().Be(RepairStatus.Unknown);
        c[0].Explication.Should().Be(Docteur.T("LAP.Fermee", Docteur.T("LAP.Liste")));
        appels.Should().Be(0, "porte fermée : les constats détaillés ne sont même pas calculés");
    }

    [Fact]
    public void Porte_ouverte_les_constats_normaux_reprennent_leur_ligne()
    {
        var timings = new RepairFinding("RAM", "timings appliques", "A 5800 MT/s", null, RepairStatus.Observed, "lus");
        var pm = new RepairFinding("CPU", "limites PPT/TDC/EDC (AMD)", "PPT 0 %", null, RepairStatus.Observed, "lue");

        LecturesAmdProfondes.Juger(Zen4, porteOuverte: true, () => [timings, pm]).Should().Equal(timings, pm);
    }

    [Fact]
    public void Hors_AMD_rien()
        => LecturesAmdProfondes.Juger(null, porteOuverte: false, () => []).Should().BeEmpty();

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void La_ligne_et_sa_liste_existent_dans_les_deux_langues(string langue)
    {
        using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo(langue));
        Docteur.T("LAP.Fermee", Docteur.T("LAP.Liste")).Should().NotContain("LAP.");
    }
}
