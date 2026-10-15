using System.IO;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Une preuve débloque son matériel, et rien d'autre ne le fait. Une architecture « validée en vie » et un
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

    // Les leviers de carte qu'un rapport archivé a validés (écrit, relu, rendu, relu), par architecture (le nom du rapport).
    // L'écran les dit « vérifiés sur une carte de la même famille » : la table du code et les rapports ne divergent pas.
    [Fact]
    public void Les_leviers_valides_en_vie_sont_ceux_des_rapports_archives()
    {
        var valides = Directory.GetFiles(Validations(), "*-*.txt")
            .GroupBy(f => Path.GetFileName(f).Split('-')[0])
            .ToDictionary(g => g.Key, g => g
                .SelectMany(f => File.ReadAllText(f).Split("=== ", StringSplitOptions.RemoveEmptyEntries).Skip(1))
                .Where(bloc => bloc.Contains("verdict      : validé", StringComparison.Ordinal))
                .Select(bloc => bloc.Split('\n')[0].Trim())
                .Where(id => id.StartsWith("gpu.", StringComparison.Ordinal))
                .ToHashSet())
            .Where(kv => kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        valides.Should().ContainKey("AdaOrOlder").WhoseValue.Should().Contain("gpu.power_limit", "témoin : le crible lit bien le rapport de la RTX 4060");
        PreuvesArchivees.LeviersParArchitecture.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToHashSet())
            .Should().BeEquivalentTo(valides);
    }

    [Fact]
    public void La_preuve_d_une_architecture_ne_vaut_pas_pour_une_autre()
    {
        PreuvesArchivees.Prouve(GpuArchitecture.AdaOrOlder, "gpu.power_limit").Should().BeTrue();
        Enum.GetValues<GpuArchitecture>().Where(a => a != GpuArchitecture.AdaOrOlder)
            .Should().OnlyContain(a => !PreuvesArchivees.Prouve(a, "gpu.power_limit"));
    }

    [Fact]
    public void Aucun_profil_ne_se_dit_teste_sans_rapport()
        => new DrDxt.Profiles.ChipProfileCatalog().All().Where(p => p.Tested).Select(p => p.Id).Should().BeEmpty(
            "un profil Tested exigera son rapport dans tests/Shared/validations ; aucun n'en a encore");
}
