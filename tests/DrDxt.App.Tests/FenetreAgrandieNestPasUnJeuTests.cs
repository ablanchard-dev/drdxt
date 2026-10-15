using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Une appli agrandie sur un écran sans barre des tâches (Discord, un navigateur) en couvre toute la surface, comme un jeu
// sans bordure. Ce qui la distingue : elle est agrandie par Windows et porte le bouton « Agrandir ». Un jeu en plein écran
// ou sans bordure est une fenêtre popup, sans ce bouton.
public sealed class FenetreAgrandieNestPasUnJeuTests
{
    [Theory]
    [InlineData(0x15030000, true)]  // Discord agrandi, relevé sur la machine de référence : WS_MAXIMIZE | WS_MAXIMIZEBOX
    [InlineData(0x14CF0000, false)] // fenêtre classique non agrandie
    [InlineData(unchecked((int)0x96000000), false)] // WS_POPUP plein écran, le cas d'un jeu
    [InlineData(unchecked((int)0x95000000), false)] // WS_POPUP agrandi sans bouton « Agrandir » : reste un jeu possible
    public void Une_appli_agrandie_n_est_pas_un_jeu(int style, bool appli)
        => Win32GameLocator.EstUneAppliAgrandie(style).Should().Be(appli);
}
