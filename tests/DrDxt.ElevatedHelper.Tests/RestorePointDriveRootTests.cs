using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// 25/09/2026 — l'allowlist accepte toute forme que NormalizeDriveLetter ramene a la lettre systeme.
// La racine passee a WMI et a vssadmin doit etre la meme pour chacune de ces formes.
public sealed class RestorePointDriveRootTests
{
    [Theory]
    [InlineData("C:")]
    [InlineData("C")]
    [InlineData("c")]
    [InlineData(" C: ")]
    [InlineData("C:\\")]
    [InlineData("C:/")]
    public void Toute_forme_admise_par_l_allowlist_donne_la_meme_racine(string lettre)
        => Win32RestorePointActions.DriveRoot(lettre).Should().Be("C:\\");

    // 07/10/2026 (revue F-13) : la reserve des points de restauration n'est fixee a 5 % que quand aucune n'a de
    // place. Une reserve deja reglee (2 %, illimitee) n'est plus ecrasee ; non lue, on ne touche a rien.
    [Fact]
    public void La_reserve_n_est_fixee_que_quand_aucune_n_existe()
    {
        Win32RestorePointActions.DoitRedimensionner([]).Should().BeTrue();
        Win32RestorePointActions.DoitRedimensionner([0UL]).Should().BeTrue();
        Win32RestorePointActions.DoitRedimensionner([10UL << 30]).Should().BeFalse();
        Win32RestorePointActions.DoitRedimensionner([0UL, ulong.MaxValue]).Should().BeFalse();
        Win32RestorePointActions.DoitRedimensionner(null).Should().BeFalse();
    }
}
