using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>La lecture WMI bornée : un winmgmt qui ne rend jamais rend la valeur « figé » au délai, sans figer l'appelant.</summary>
public sealed class LectureWmiBorneeTests
{
    [Fact]
    public void Une_lecture_qui_ne_rend_jamais_rend_la_valeur_fige_au_delai()
    {
        using var jamais = new ManualResetEventSlim(false);
        var chrono = System.Diagnostics.Stopwatch.StartNew();

        var r = LectureWmiBornee.Lire(() => { jamais.Wait(); return "lu"; }, () => "figé", TimeSpan.FromMilliseconds(200));

        r.Should().Be("figé");
        chrono.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        jamais.Set();
    }

    [Fact]
    public void Une_lecture_qui_repond_rend_sa_valeur()
        => LectureWmiBornee.Lire(() => "lu", () => "figé").Should().Be("lu");

    [Fact]
    public void Le_delai_est_de_dix_secondes()
        => LectureWmiBornee.Delai.Should().Be(TimeSpan.FromSeconds(10));

    // Figée dans WMI, la lecture garde son fil jusqu'au retour de winmgmt : ce fil n'est pas pris au pool, que d'autres tâches
    // (un scan en parallèle) attendent.
    [Fact]
    public void La_lecture_tourne_sur_un_fil_hors_du_pool()
        => LectureWmiBornee.Lire(() => Thread.CurrentThread.IsThreadPoolThread, () => true).Should().BeFalse();
}
