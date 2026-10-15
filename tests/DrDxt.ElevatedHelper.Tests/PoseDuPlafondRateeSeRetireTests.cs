using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// La politique générale (tout l'envoi plafonné) est créée avant l'exemption du jeu. Une exemption refusée ou une relecture
// différente sortait en échec en laissant la générale : l'envoi de tout le PC restait bridé. Le script retire ses deux
// politiques avant de sortir en échec, et une erreur d'une cmdlet arrête le script au lieu de passer à la suite.
public sealed class PoseDuPlafondRateeSeRetireTests
{
    private static readonly string Script = Win32NetworkQosActions.ScriptDePose("cod.exe", 503_000_000);

    [Fact]
    public void Les_creations_s_arretent_a_la_premiere_erreur() =>
        Script.Should().Contain("New-NetQosPolicy -Name 'DrDXT-upload-cap' ").And.Contain("-ErrorAction Stop");

    [Fact]
    public void Chaque_sortie_en_echec_retire_d_abord_les_deux_politiques()
    {
        var sorties = Script.Split("exit 1").Length - 1;
        sorties.Should().BeGreaterThan(0);
        Script.Split(Win32NetworkQosActions.Retirer + " exit 1").Length.Should().Be(sorties + 1,
            "aucune sortie en échec ne laisse une politique derrière elle");
    }
}
