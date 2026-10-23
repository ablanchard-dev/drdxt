using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Porte AMD fermée (PawnIO absent, ou génération jamais vérifiée sur une vraie machine) : une seule ligne qui le dit, au
// lieu d'une ligne « non lu » par lecture. Porte ouverte : chaque constat reprend sa ligne.
public sealed class LecturesAmdProfondesTests
{
    private static readonly GenerationAmd.Ligne Zen4 = GenerationAmd.Table.First(l => l.Generation == "Zen 4");
    private static readonly IReadOnlySet<string> Zen4Validee = new HashSet<string> { "Zen 4" };

    [Fact]
    public void Sans_PawnIO_une_seule_ligne_qui_nomme_les_lectures()
    {
        var appels = 0;
        var c = LecturesAmdProfondes.Juger(Zen4, pawnIoInstalle: false, () => { appels++; return []; }, Zen4Validee).ToList();

        c.Should().ContainSingle();
        c[0].Statut.Should().Be(RepairStatus.Unknown);
        c[0].Explication.Should().Be(Docteur.T("LAP.SansPawnIo", Docteur.T("LAP.Liste")));
        appels.Should().Be(0, "porte fermée : les constats détaillés ne sont même pas calculés");
    }

    // Aucune génération n'est validée : sans rapport sur une vraie machine, rien de ce qui est lu n'est montré.
    [Fact]
    public void Generation_non_validee_porte_fermee_meme_avec_PawnIO()
    {
        var appels = 0;
        var c = LecturesAmdProfondes.Juger(Zen4, pawnIoInstalle: true, () => { appels++; return []; }).ToList();

        LecturesAmdProfondes.ValideesEnVie.Should().BeEmpty("aucun rapport de validation AMD n'est encore archivé");
        c.Should().ContainSingle().Which.Explication.Should().Be(Docteur.T("LAP.NonValidee", Docteur.T("LAP.Liste"), "Zen 4"));
        appels.Should().Be(0);
    }

    [Fact]
    public void Porte_ouverte_les_constats_normaux_reprennent_leur_ligne()
    {
        var timings = new RepairFinding("RAM", "timings appliques", "A 5800 MT/s", null, RepairStatus.Observed, "lus");
        var pm = new RepairFinding("CPU", "limites PPT/TDC/EDC (AMD)", "PPT 0 %", null, RepairStatus.Observed, "lue");

        LecturesAmdProfondes.Juger(Zen4, pawnIoInstalle: true, () => [timings, pm], Zen4Validee).Should().Equal(timings, pm);
    }

    [Fact]
    public void Hors_AMD_rien()
        => LecturesAmdProfondes.Juger(null, pawnIoInstalle: false, () => []).Should().BeEmpty();

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Les_lignes_et_leur_liste_existent_dans_les_deux_langues(string langue)
    {
        using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo(langue));
        Docteur.T("LAP.SansPawnIo", Docteur.T("LAP.Liste")).Should().NotContain("LAP.");
        Docteur.T("LAP.NonValidee", Docteur.T("LAP.Liste"), "Zen 4").Should().NotContain("LAP.").And.Contain("Zen 4");
    }
}
