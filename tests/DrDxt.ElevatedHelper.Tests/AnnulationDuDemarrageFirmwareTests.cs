using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// « shutdown /a /fw » rendait OK sur n'importe quel code : un refus (droits, autre erreur) laissait le redémarrage dans le
// firmware en attente pendant que l'App effaçait son marqueur. Seuls « annulé » (0) et « rien en attente » (1116,
// ERROR_NO_SHUTDOWN_IN_PROGRESS, mesuré le 12/10 sur la machine de référence) disent que plus rien n'attend.
public sealed class AnnulationDuDemarrageFirmwareTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1116, true)]
    [InlineData(5, false)]     // accès refusé
    [InlineData(1, false)]
    [InlineData(1190, false)]  // un arrêt déjà en cours qu'on ne peut pas annuler
    public void Seuls_annule_et_rien_en_attente_valent_une_annulation(int code, bool aboutie) =>
        Win32FirmwareBootActions.AnnulationAboutie(code).Should().Be(aboutie);
}
