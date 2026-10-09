using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Mesuré ici le 07/09/2026, sur deux services jetables reellement crees puis supprimes.
///
/// Des que le `binPath` porte des ARGUMENTS, il contient un chemin entre guillemets suivi
/// d'options -- et l'ecriture naive casse. Les deux formes ont ete soumises a `sc.exe` :
///
///   A. binPath= "&lt;g&gt;C:\Program Files\...\PresentMonService.exe&lt;g&gt; --control-pipe ..."
///      → **exit 1639** (ERROR_INVALID_COMMAND_LINE), sc affiche son aide, **service jamais cree**.
///   B. binPath= "\&lt;g&gt;C:\Program Files\...\PresentMonService.exe\&lt;g&gt; --control-pipe ..."
///      → exit 0, et `ImagePath` relu dans le registre vaut exactement
///        `"C:\Program Files\...\PresentMonService.exe" --control-pipe ...`
///
/// (&lt;g&gt; = un guillemet double.) La difference tient aux deux antislash devant les guillemets
/// internes : `CommandLineToArgvW` doit rendre a sc.exe **un seul** argument contenant des
/// guillemets. Sans eux, le `""` ouvrant est lu comme une chaine vide et la ligne se disloque.
///
/// Le produit ne pose plus d'arguments (voir <see cref="PresentMonNaming.ServiceArguments"/>,
/// vide depuis que les mesures ont montre qu'ils n'ont aucun effet). L'echappement reste teste
/// parce qu'il est correct dans les deux cas et que c'est la forme qui survit au jour ou un
/// argument revient — le cas ou l'echec serait SILENCIEUX cote produit : « sc.exe a echoue », le
/// service inexistant, et le diagnostic accusant PresentMon.
/// </summary>
public class PresentMonScEscapingTests
{
    private const string CheminAvecEspace = @"C:\Program Files\DrDXT\PresentMonService.exe";

    [Fact]
    public void Les_guillemets_internes_sont_echappes_par_un_antislash()
    {
        var argument = PresentMonServicePlan.BinPathArgument(CheminAvecEspace);

        // La forme B, celle qui a rendu exit 0. Litteralement.
        var attendu = "binPath= \"\\\"" + CheminAvecEspace + "\\\""
            + (PresentMonNaming.ServiceArguments.Length == 0 ? "" : " " + PresentMonNaming.ServiceArguments)
            + "\"";
        argument.Should().Be(attendu);
    }

    [Fact]
    public void La_forme_qui_a_rendu_1639_ne_peut_plus_etre_produite()
    {
        // `binPath= ""C:\...` : deux guillemets colles, sans antislash. C'est la forme A.
        PresentMonServicePlan.BinPathArgument(CheminAvecEspace).Should().NotContain("binPath= \"\"");
    }

    [Fact]
    public void Le_binPath_enregistre_est_celui_que_le_registre_a_effectivement_rendu()
    {
        // Ce que `reg query ... /v ImagePath` a affiche apres la creation reussie. C'est aussi
        // exactement la chaine que `PresentMonServicePlan.Decide` relit pour dire StartOnly.
        var attendu = "\"" + CheminAvecEspace + "\""
            + (PresentMonNaming.ServiceArguments.Length == 0 ? "" : " " + PresentMonNaming.ServiceArguments);
        PresentMonServicePlan.BinPath(CheminAvecEspace).Should().Be(attendu);
    }
}
