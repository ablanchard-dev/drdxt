using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks;

// Après une mise à jour de Windows, ce que DrDXT a posé est relu et dit, jamais reposé ; le premier scan pose le repère.
public sealed class ApresMiseAJourDeWindowsTests
{
    private sealed class RegistreVide : IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) => Result<uint?>.Ok(null);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
    }

    [Fact]
    public void Le_premier_scan_pose_le_repere_et_seul_un_changement_de_build_parle()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"apres-maj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        try
        {
            var journal = new JournalDesLeviersAppliques(Path.Combine(dossier, "leviers.json"));
            var build = Path.Combine(dossier, "build.json");

            journal.ApresMiseAJour(new RegistreVide(), "26200.6899", build).Should().BeNull("le premier scan pose le repère");
            journal.ApresMiseAJour(new RegistreVide(), "26200.6899", build).Should().BeNull("même build");
            journal.ApresMiseAJour(new RegistreVide(), "26200.7000", build).Should().BeNull("rien de posé, rien à relire");
            File.ReadAllText(build).Should().Contain("26200.7000");
        }
        finally { Directory.Delete(dossier, true); }
    }

    [Fact]
    public void Un_repere_illisible_se_repose_au_lieu_de_faire_tomber_le_scan()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"apres-maj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dossier);
        try
        {
            var build = Path.Combine(dossier, "build.json");
            File.WriteAllText(build, "{\"pas\":\"une chaine\"}");
            new JournalDesLeviersAppliques(Path.Combine(dossier, "leviers.json"))
                .ApresMiseAJour(new RegistreVide(), "26200.6899", build).Should().BeNull("un repère illisible vaut un premier scan");
            File.ReadAllText(build).Should().Contain("26200.6899");
        }
        finally { Directory.Delete(dossier, true); }
    }
}
