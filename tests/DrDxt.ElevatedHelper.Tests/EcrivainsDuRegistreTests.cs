using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le registre ne s'écrit que par ces chemins-là. Le helper (son tube et sa
/// désinstallation), le service (manifeste signé), et le client de la session de l'utilisateur pour sa ruche HKCU.
/// Un nouvel écrivain doit être ajouté ici, donc relu.
/// </summary>
public sealed class EcrivainsDuRegistreTests
{
    [Fact]
    public void Seuls_les_chemins_relus_construisent_un_ecrivain_du_registre()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var src = Path.Combine(d!.FullName, "src");

        var ecrivains = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("new Win32RegistryActions(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        ecrivains.Should().Equal(
            "DrDxt.ElevatedHelper/ClientSessionUtilisateur.cs",
            "DrDxt.ElevatedHelper/ModeDesinstallation.cs",
            "DrDxt.ElevatedHelper/Program.cs",
            "DrDxt.ReconcilerHost/HostServiceCollectionExtensions.cs");
    }
}
