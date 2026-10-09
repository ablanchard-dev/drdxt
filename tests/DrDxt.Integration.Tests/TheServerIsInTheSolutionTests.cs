using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Le backend est dans la solution, et il doit y rester.
///
/// Constate le 07/09/2026 : `server/DrDxt.Server/DrDxt.Server.csproj` affirmait en commentaire
/// etre « **intentionally NOT part of DrDXT.sln** ». Il y est — lui et ses tests — et ses **51
/// tests** tournent a chaque `dotnet test DrDXT.sln`.
///
/// L'etat reel est le bon : un backend que personne ne compile est un backend dont personne ne
/// sait qu'il est casse. C'est le commentaire qui avait tort, et un commentaire faux est pire
/// qu'absent — qui l'aurait lu en aurait conclu que le serveur n'etait pas teste, et se serait
/// abstenu de le toucher.
///
/// Ce test tient les deux moities ensemble : le projet dans la solution, et le commentaire qui ne
/// dit plus le contraire.
/// </summary>
public sealed class TheServerIsInTheSolutionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    private static string Lire(params string[] parties) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parties).ToArray()));

    [Fact]
    public void Le_backend_ET_ses_tests_sont_dans_la_solution()
    {
        var sln = Lire("DrDXT.sln");

        sln.Should().Contain("DrDxt.Server.csproj", "un backend hors solution n'est compile par personne");
        sln.Should().Contain("DrDxt.Server.Tests.csproj", "et ses tests ne tourneraient nulle part");
    }

    [Fact]
    public void Le_csproj_ne_pretend_plus_le_contraire()
    {
        // La garde qui empeche le commentaire de repartir en arriere. Un commentaire faux est pire
        // qu'absent : il fait prendre des decisions sur un etat qui n'existe pas.
        Lire("server", "DrDxt.Server", "DrDxt.Server.csproj")
            .Should().NotContain("NOT part of DrDXT.sln");
    }
}
