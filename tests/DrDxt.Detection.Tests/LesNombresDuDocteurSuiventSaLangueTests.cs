using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Diffusion;
using DrDxt.Detection.Display;
using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

// Le docteur parle la langue de l'affichage de Windows ; les formats régionaux peuvent en être une autre (Windows en
// anglais, formats français). Un nombre dans une phrase du docteur suit la phrase, pas les formats régionaux.
public sealed class LesNombresDuDocteurSuiventSaLangueTests
{
    private static T EnAnglaisSurFormatsFrancais<T>(Func<T> f)
    {
        var avant = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
            return f();
        }
        finally { CultureInfo.CurrentCulture = avant; }
    }

    [Fact]
    public void Les_frequences_d_un_ecran_duplique_prennent_le_point()
    {
        var constat = EnAnglaisSurFormatsFrancais(() => EcransDupliques.Juger(
        [
            new CheminsDAffichage.Chemin(1, 0, 1, 1, 0, 143.5, "A"),
            new CheminsDAffichage.Chemin(1, 0, 1, 2, 0, 59.94, "B"),
        ]));

        constat.ValeurCourante.Should().Contain("143.5").And.Contain("59.94");
        constat.Explication.Should().Contain("143.5");
    }

    [Fact]
    public void Le_debit_d_un_programme_prend_le_point()
    {
        var constat = EnAnglaisSurFormatsFrancais(() =>
            EnvoisParProgramme.Juger(new Dictionary<int, long> { [42] = 1_000 }, TimeSpan.FromSeconds(3), _ => "jeu"));

        constat.ValeurCourante.Should().Be("jeu 2.7 kbit/s");
    }

    [Fact]
    public void Les_pertes_d_obs_prennent_le_point()
    {
        var constats = EnAnglaisSurFormatsFrancais(() => ObsStudio.Juger(true, new ObsStudio.Reglages(null, "x264", 6000, null, null),
            [new ObsStudio.Session(DateTime.Now, 2.5, 0, 0, true)]).ToList());

        constats.Single(c => c.Nom == "pertes en stream").ValeurCourante.Should().Contain("2.5");
    }
}
