using System.IO;
using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// DrDXT dit quand un pilote NVIDIA a du retard. La réponse réelle de la source NVIDIA,
// relevée le 03/10/2026 (617.14, 616.92, 616.64), et le pilote réellement installé sur la machine (616.56).
public sealed class VersionDuPiloteTests
{
    private static string Reponse()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "tests", "DrDxt.Detection.Tests", "Gpu", "nvidia-reponse-2026-10-03.json"));
    }

    [Fact]
    public void La_reponse_reelle_de_NVIDIA_se_lit()
    {
        var v = SourceNvidia.Lire(Reponse())!;
        v.Select(x => x.Version).Should().Equal("617.14", "616.92", "616.64");
        v[0].Sortie.Should().Be(new DateOnly(2026, 9, 22));
    }

    [Fact]
    public void Le_pilote_installe_a_trois_versions_de_retard()
    {
        var c = VersionDuPilote.Juger("NVIDIA", "616.56", SourceNvidia.Lire(Reponse())!)!;
        // Être en retard n'est pas un défaut ; observé, avec son geste.
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("3 versions de retard").And.Contain("617.14 du 22/09/2026").And.Contain("ne l'installe pas")
            .And.NotContain("Mets-le à jour");
        c.Remede.Should().StartWith("1) Ouvre l'application NVIDIA").And.Contain("2) Installe la 617.14");
        c.ValeurCourante.Should().Be("NVIDIA 616.56");
    }

    [Theory]
    [InlineData("617.14")]
    [InlineData("617.20")]
    public void Un_pilote_au_niveau_de_la_derniere_est_a_jour(string installee) =>
        VersionDuPilote.Juger("NVIDIA", installee, SourceNvidia.Lire(Reponse())!)!.Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Une_version_seule_de_retard_est_dite_au_singulier() =>
        VersionDuPilote.Juger("NVIDIA", "616.92", SourceNvidia.Lire(Reponse())!)!.Explication.Should().Contain("une version de retard");

    [Fact]
    public void Une_reponse_illisible_ne_fabrique_rien() => SourceNvidia.Lire("{\"Success\":\"0\"}").Should().BeNull();

    // Le vrai réseau, la vraie source : si la machine est en ligne, la dernière version publiée est lue et n'est pas plus
    // ancienne que celle relevée le 03/10/2026.
    [Fact]
    public async Task La_vraie_source_NVIDIA_repond_en_ligne()
    {
        var v = await new SourceNvidia().DernieresAsync();
        if (v is null) return; // hors ligne : rien à vérifier
        VersionDuPilote.Comparer(VersionDuPilote.Groupes(v[0].Version)!, [617, 14]).Should().BeGreaterThanOrEqualTo(0);
    }

    // Si NVML ne répond pas, Windows donne « 32.0.16.1656 » : c'est la 616.56, jamais une version « 32 » en retard.
    [Theory]
    [InlineData("32.0.16.1656", "616.56")]
    [InlineData("32.0.15.7270", "572.70")]
    [InlineData("616.56", "616.56")]
    public void La_forme_Windows_se_decode_en_version_NVIDIA(string brute, string nvidia) =>
        VersionDuPilote.VersionNvidia(brute).Should().Be(nvidia);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060", true)]
    [InlineData("NVIDIA GeForce RTX 3070 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce RTX 5090", true)]
    [InlineData("NVIDIA GeForce RTX 2060 SUPER", true)]
    [InlineData("NVIDIA GeForce GTX 1660 Ti", true)]
    [InlineData("NVIDIA GeForce MX450", true)]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", false)]
    [InlineData("NVIDIA GeForce GTX 970", false)]
    [InlineData("NVIDIA GeForce MX250", false)]
    [InlineData("NVIDIA RTX A4000", false)]
    [InlineData("NVIDIA Quadro P2000", false)]
    public void Seules_les_GeForce_au_pilote_Game_Ready_courant_sont_comparees(string modele, bool comparee) =>
        VersionDuPilote.SuitLesPilotesGameReady(modele).Should().Be(comparee);

    [Fact]
    public async Task Une_GTX_1060_n_est_pas_declaree_en_retard_sur_des_versions_qui_ne_la_prennent_pas()
    {
        var cartes = new[] { new GpuInfo("NVIDIA GeForce GTX 1060 6GB", "NVIDIA", 6L << 30, "32.0.15.8180", TriState.Off) };
        var c = await VersionDuPilote.VerifierAsync(cartes, new SourceNvidia((_, _) => Task.FromResult(Reponse())));
        c!.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("branche 580").And.NotContain("retard");
    }

    [Fact]
    public async Task La_machine_reelle_est_jugee_de_bout_en_bout()
    {
        var cartes = new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "32.0.16.1656", TriState.On) };
        var c = await VersionDuPilote.VerifierAsync(cartes, new SourceNvidia((_, _) => Task.FromResult(Reponse())));
        c!.Explication.Should().Contain("616.56").And.Contain("3 versions de retard");
    }
}
