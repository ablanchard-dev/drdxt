using System.Reflection;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le helper eleve charge les DLL des pilotes par leur nom (nvapi64, amdadlx64, nvml).
/// Sur une carte NVIDIA, amdadlx64.dll manque dans System32 : la recherche par defaut de Windows
/// continuait dans le dossier courant puis le PATH. Sur la machine de reference, 15 dossiers du PATH
/// sont modifiables par l'utilisateur (WinGet\Links, Python, .cargo...) : une DLL posee la
/// s'executait en administrateur. Ces tests tiennent la restriction a System32.
/// </summary>
public class DllChargeesDepuisSystem32Tests
{
    public static TheoryData<string> Assemblies => new()
    {
        typeof(OpAllowlist).Assembly.GetName().Name!,
        typeof(DrDxt.Detection.Gpu.Adlx.AdlxNative).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Les_P_Invoke_de_l_assembly_ne_cherchent_que_dans_System32(string nom)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == nom);
        var attribut = assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
        attribut.Should().NotBeNull($"{nom} charge des DLL dans un processus eleve");
        attribut!.Paths.Should().Be(DllImportSearchPath.System32);
    }

    [Fact]
    public void Aucun_chargement_dynamique_par_nom_seul_dans_le_code_eleve()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        d.Should().NotBeNull();

        // NativeLibrary.TryLoad/Load a deux arguments suit l'ordre de recherche de Windows, PATH
        // compris : l'attribut d'assembly ne le couvre pas.
        var motif = new System.Text.RegularExpressions.Regex(@"NativeLibrary\.(Try)?Load\(""[^""\\/]+"",\s*out\b");
        var lus = new[] { "DrDxt.ElevatedHelper", "DrDxt.GpuNative" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(d!.FullName, "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        // Temoin : un fichier qui charge une DLL native est bien dans ce qui a ete lu.
        lus.Where(f => Path.GetFileName(f) == "NvmlNative.cs").Should().NotBeEmpty();
        lus.Where(f => motif.IsMatch(File.ReadAllText(f))).Should().BeEmpty();
    }
}
