using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// 14/09/2026 — la desinstallation relit un reglage DRS au profil du jeu.
///
/// `LectureDesCiblesWin32` ne relisait le DRS qu'au profil de base. Un reglage pose au profil d'un
/// jeu (PreferMaxPerf depuis le 10/09, RenderQueueCap et VrrVsyncCap depuis `5619aa8`) etait donc
/// « illisible » : la desinstallation ne le rendait jamais, et il restait dans le pilote apres DrDXT.
///
/// Le piege : relu au profil d'un jeu qui ne le possede plus, le pilote rend la valeur HERITEE du
/// global. Elle peut valoir exactement ce que DrDXT avait pose — et la desinstallation rendrait alors
/// un reglage que le jeu n'a plus. Seul un reglage POSSEDE par le profil du jeu est une valeur lue.
/// </summary>
public sealed class LectureDesCiblesDrsAuJeuTests
{
    private const string Jeu = @"C:\Games\Call of Duty\cod.exe";
    private const uint Reglage = 0x1057EB71u;
    private const uint ProfilCourant = 0, ProfilGlobal = 1;

    private static LectureDesCiblesWin32 Lecture(bool lu, uint valeur, uint emplacement)
    {
        var nv = Substitute.For<INvApiNative>();
        nv.IsAvailable.Returns(true);
        nv.TryReadDrsSettingForApplication(Jeu, Reglage, out Arg.Any<DrsReading>())
            .Returns(x =>
            {
                x[2] = new DrsReading(valeur, emplacement, IsCurrentPredefined: 0, IsPredefinedValid: 0);
                return lu;
            });
        return new LectureDesCiblesWin32(nv);
    }

    private static Result<string?> Lire(LectureDesCiblesWin32 l) =>
        l.Lire(new ApplyNvApiDrsSettingOp(Reglage, 1, Jeu));

    [Fact]
    public void Un_reglage_POSSEDE_par_le_profil_du_jeu_se_relit_a_sa_valeur()
    {
        var r = Lire(Lecture(lu: true, valeur: 1, emplacement: ProfilCourant));

        r.IsOk.Should().BeTrue();
        r.Value.Should().Be("1");
    }

    [Fact]
    public void Un_reglage_seulement_HERITE_se_lit_absent_jamais_a_la_valeur_du_global()
    {
        // La valeur heritee vaut ici 1, exactement ce que DrDXT avait pose : la comparer rendrait un
        // reglage que le jeu ne possede plus.
        var r = Lire(Lecture(lu: true, valeur: 1, emplacement: ProfilGlobal));

        r.IsOk.Should().BeTrue();
        r.Value.Should().BeNull();
    }

    [Fact]
    public void Une_lecture_ratee_est_illisible_et_rien_n_est_touche()
    {
        Lire(Lecture(lu: false, valeur: 0, emplacement: ProfilCourant)).IsOk.Should().BeFalse();
    }
}
