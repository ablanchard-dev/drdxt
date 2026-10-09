using System.IO;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// T4 (08/10/2026) : une preuve débloque son matériel, et rien d'autre ne le fait. Une architecture « validée en vie » et un
/// profil « Tested » exigent un rapport de vérification réel dans tests/Shared/validations, avec une écriture rendue.
/// </summary>
public sealed class UnePreuveDebloqueSonMaterielTests
{
    private static string Validations()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        return Path.Combine(d.FullName, "tests", "Shared", "validations");
    }

    private static bool Prouvee(string architecture) =>
        Directory.GetFiles(Validations(), architecture + "-*.txt").Any(f =>
        {
            var texte = File.ReadAllText(f);
            return texte.Contains("rendu        : oui", StringComparison.Ordinal)
                   && !texte.Contains("PAS REMIS D'ORIGINE", StringComparison.Ordinal);
        });

    [Fact]
    public void Chaque_architecture_validee_a_son_rapport()
    {
        Directory.Exists(Validations()).Should().BeTrue("le dossier des preuves existe, avec son mode d'emploi");
        Enum.GetValues<GpuArchitecture>().Should().NotBeEmpty();
        GpuTuningCoverage.ValideesEnVie.Where(a => !Prouvee(a.ToString())).Should().BeEmpty(
            "une architecture ne se dit validée qu'avec son rapport réel, écriture rendue");
    }

    [Fact]
    public void Aucun_profil_ne_se_dit_teste_sans_rapport()
        => new DrDxt.Profiles.ChipProfileCatalog().All().Where(p => p.Tested).Select(p => p.Id).Should().BeEmpty(
            "un profil Tested exigera son rapport dans tests/Shared/validations ; aucun n'en a encore");
}
