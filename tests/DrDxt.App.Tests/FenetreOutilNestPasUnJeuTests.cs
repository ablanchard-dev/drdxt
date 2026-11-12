using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Une fenêtre d'outil (WS_EX_TOOLWINDOW) n'a ni bouton dans la barre des tâches ni place dans Alt+Tab : le voile de
// capture d'écran de Windows couvre tout l'écran au premier plan, comme un jeu sans bordure, et le relevé des parties le
// prenait pour une partie. Un jeu auquel on revient par la barre des tâches n'est jamais une fenêtre d'outil.
public sealed class FenetreOutilNestPasUnJeuTests
{
    [Theory]
    [InlineData(0x00200188, true)]  // outil Capture de Windows 11, relevé sur la machine de référence : TOOLWINDOW | TOPMOST
    [InlineData(0x08000000, true)]  // overlay qui ne passe jamais au premier plan (WS_EX_NOACTIVATE)
    [InlineData(0x00000080, true)]  // WS_EX_TOOLWINDOW seul
    [InlineData(0x00040000, false)] // WS_EX_APPWINDOW, un jeu sans bordure
    [InlineData(0x00000008, false)] // WS_EX_TOPMOST seul, un jeu en plein écran exclusif
    [InlineData(0, false)]
    public void Une_fenetre_d_outil_ou_d_overlay_n_est_pas_un_jeu(int styleEtendu, bool outil)
        => Win32GameLocator.EstUnOutilOuUnOverlay(styleEtendu).Should().Be(outil);
}
