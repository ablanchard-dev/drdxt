using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Le message d'un échec (souvent l'anglais technique du helper) se collait aux phrases que l'Application compose pour
// l'écran (le démarrage, le ventilateur resté fixe). Une phrase de l'Application ne reçoit jamais Error.Message, sauf :
//   * un message rendu par Result.Fail : l'écran le passe par RaisonLisible, il n'y arrive jamais ;
//   * le rapport de vérification de la machine : un fichier pour le support, où le détail technique est utile.
public sealed class AucunMessageTechniqueDansLesPhrasesTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static readonly Regex Message = new(@"Error(\?|!)?\.(Value\.)?Message");

    private static readonly Dictionary<string, string> Rapport = new()
    {
        ["ValidationComplete.cs"] = "rapport de vérification de la machine : fichier pour le support",
        ["ValidationDeLaMachine.cs"] = "rapport de vérification de la machine : fichier pour le support",
    };

    /// <summary>Les lignes d'une source où une phrase (Docteur.T) reçoit le message d'un échec hors d'un Result.Fail.</summary>
    private static IEnumerable<int> Fautives(string[] lignes)
    {
        for (var i = 0; i < lignes.Length; i++)
        {
            var l = lignes[i];
            if (!l.Contains("Docteur.T(", StringComparison.Ordinal) || !Message.IsMatch(l)) continue;
            var precedente = i > 0 ? lignes[i - 1] : "";
            if (l.Contains("Fail(", StringComparison.Ordinal) || precedente.Contains("Fail(", StringComparison.Ordinal))
                continue;
            yield return i + 1;
        }
    }

    [Fact]
    public void Le_crible_voit_une_phrase_qui_recoit_le_message_d_un_echec()
    {
        Fautives(["return Docteur.T(\"RV.RenduEchoue\", nom, rendu.Error?.Message);"]).Should().Equal(1);
        Fautives(["return Result.Fail(code, Docteur.T(\"GX.BailCarte\", hb.Error.Value.Message));"]).Should().BeEmpty();
        Fautives(["return Result.Fail(ErrorCodes.TweakSdkUnavailable,", "    Docteur.T(\"GX.SansFilet\", pose.Error?.Message));"]).Should().BeEmpty();
        Fautives(["return Result<int>.Fail(", "    r.Error?.Code, r.Error?.Message ?? Docteur.T(\"GX.BailNonArme\"));"]).Should().BeEmpty();
    }

    [Fact]
    public void Aucune_phrase_de_l_Application_ne_recoit_le_message_d_un_echec()
    {
        var sources = FichiersDuDepot.Sous(Path.Combine(Racine(), "src", "DrDxt.Application"), "*.cs").ToList();
        var lues = sources.Where(f => File.ReadAllText(f).Contains("Docteur.T(", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();

        lues.Should().Contain("RevertEnAttente.cs", "témoin : le crible lit les phrases de l'Application");
        sources.Where(f => !Rapport.ContainsKey(Path.GetFileName(f)))
            .SelectMany(f => Fautives(File.ReadAllLines(f)).Select(n => $"{Path.GetFileName(f)}:{n}"))
            .Should().BeEmpty();
    }
}
