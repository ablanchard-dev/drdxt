using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Convert.ToUInt64(null) rend 0 : un compteur absent des deux relevés donnait un delta nul, dit « 0 % » comme une lecture.
public sealed class CompteurWmiTests
{
    [Fact]
    public void Un_compteur_absent_n_est_pas_zero()
        => FluentActions.Invoking(() => LectureWmiBornee.Compteur(null)).Should().Throw<InvalidDataException>();

    [Fact]
    public void Un_compteur_present_se_lit_qu_il_vienne_en_nombre_ou_en_texte()
    {
        LectureWmiBornee.Compteur(42UL).Should().Be(42UL);
        LectureWmiBornee.Compteur("18446744073709551615").Should().Be(ulong.MaxValue); // les uint64 bruts de WMI arrivent en texte
    }
}
