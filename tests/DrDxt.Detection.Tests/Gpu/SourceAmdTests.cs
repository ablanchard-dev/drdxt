using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// « il doit regarder la marque et savoir où chercher ». AMD comparé comme NVIDIA, depuis sa page de
// téléchargement ; la version installée au format d'AMD vient du registre.
public sealed class SourceAmdTests
{
    private static string Page()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "tests", "DrDxt.Detection.Tests", "Gpu", "amd-pilotes-2026-10-03.html"));
    }

    private static SourceAmd Source() => new((_, _) => Task.FromResult(Page()));
    private static readonly SourceNvidia NvidiaMuette = new((_, _) => Task.FromResult(""));
    private static GpuInfo Radeon(string modele) => new(modele, "AMD", 16L << 30, "32.0.21025.10016", TriState.On);

    // La page d'AMD se lit avec l'en-tête d'un navigateur ; rien n'y dit à AMD que cette adresse utilise DrDXT.
    [Fact]
    public void La_requete_a_amd_ne_nomme_pas_drdxt() =>
        SourceAmd.EnTeteNavigateur.Should().StartWith("Mozilla/5.0").And.NotContainEquivalentOf("DrDXT");

    [Fact]
    public void La_page_reelle_d_AMD_donne_la_derniere_version_et_sa_date() =>
        SourceAmd.Lire(Page()).Should().Be(new VersionPubliee("26.8.1", new DateOnly(2026, 8, 18)));

    [Fact]
    public async Task Une_Radeon_recente_en_retard_est_dite_sans_compter_les_versions()
    {
        var c = await VersionDuPilote.VerifierAsync([Radeon("AMD Radeon RX 7800 XT")], NvidiaMuette, Source(), _ => "26.5.1");

        // En retard = observé, avec son geste.
        c!.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("ton pilote AMD 26.5.1 n'est pas le dernier : la dernière publiée est la 26.8.1 du 18/08/2026")
            .And.NotContain("versions de retard", "AMD ne publie que la dernière : le nombre de versions entre les deux n'est pas lu");
    }

    // Une APU avec une Radeon dédiée : Windows liste souvent la puce intégrée d'abord, parfois avec une réserve UMA de
    // 2 Go. La version comparée est celle de la carte dédiée, pas celle de la puce.
    [Theory]
    [InlineData(512L << 20)]
    [InlineData(2L << 30)]
    public async Task Sur_une_APU_la_version_est_lue_sur_la_carte_dediee(long memoirePuce)
    {
        var puce = new GpuInfo("AMD Radeon(TM) Graphics", "AMD", memoirePuce, "31.0.0.1", TriState.Unknown);
        var c = await VersionDuPilote.VerifierAsync([puce, Radeon("AMD Radeon RX 7800 XT")], NvidiaMuette, Source(),
            m => m == "AMD Radeon RX 7800 XT" ? "26.5.1" : null);

        c!.Explication.Should().Contain("ton pilote AMD 26.5.1 n'est pas le dernier");
    }

    [Fact]
    public async Task Une_Radeon_a_jour_est_dite_a_jour()
    {
        var c = await VersionDuPilote.VerifierAsync([Radeon("AMD Radeon RX 9070 XT")], NvidiaMuette, Source(), _ => "26.8.1");
        c!.Statut.Should().Be(RepairStatus.ExpectedDefault);
    }

    [Theory]
    [InlineData("AMD Radeon RX 580 Series")]
    [InlineData("AMD Radeon RX Vega 64")]
    [InlineData("AMD Radeon(TM) Graphics")]
    public async Task Une_Radeon_hors_suite_Adrenalin_courante_est_renvoyee_vers_AMD(string modele)
    {
        var c = await VersionDuPilote.VerifierAsync([Radeon(modele)], NvidiaMuette, Source(), _ => "23.9.1");
        c!.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("Radeon RX 5000 à 9000").And.NotContain("n'est pas le dernier");
    }

    [Fact]
    public async Task Sans_version_AMD_dans_le_registre_rien_n_est_compare()
    {
        var c = await VersionDuPilote.VerifierAsync([Radeon("AMD Radeon RX 6700 XT")], NvidiaMuette, Source(), _ => null);
        c!.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("ne sait pas encore lire");
    }

    // Site muet = l'endroit où vérifier, jamais le silence (avant, l'utilisateur AMD avait au moins cela).
    [Fact]
    public async Task Un_site_muet_dit_ou_verifier()
    {
        var muette = new SourceAmd((_, _) => throw new HttpRequestException("hors ligne"));
        var c = await VersionDuPilote.VerifierAsync([Radeon("AMD Radeon RX 7800 XT")], NvidiaMuette, muette, _ => "26.5.1");
        c!.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("n'a pas répondu").And.Contain("amd.com/fr/support");

        var nv = await VersionDuPilote.VerifierAsync([new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "32.0.16.1714", TriState.On)],
            NvidiaMuette, muette, _ => null);
        nv!.Explication.Should().Contain("n'a pas répondu").And.Contain("617.14");
    }

    [Fact]
    public async Task Intel_dit_pourquoi_rien_n_est_compare()
    {
        var c = await VersionDuPilote.VerifierAsync([new GpuInfo("Intel(R) Arc(TM) A770", "Intel", 16L << 30, "32.0.101.6979", TriState.On)],
            NvidiaMuette, Source(), _ => null);
        c!.Explication.Should().Contain("refuse la lecture par un programme");
    }

    // La page en ligne, aujourd'hui : elle doit encore se lire, sur une version au moins aussi récente.
    [Fact]
    public async Task La_vraie_page_d_AMD_se_lit_en_ligne()
    {
        var v = await new SourceAmd().DerniereAsync();
        if (v is null) return; // hors ligne : rien à vérifier
        VersionDuPilote.Comparer(VersionDuPilote.Groupes(v.Version)!, [26, 8, 1]).Should().BeGreaterThanOrEqualTo(0);
        v.Sortie.Should().NotBeNull();
    }

    [Fact]
    public void Sur_cette_machine_sans_carte_AMD_le_registre_ne_rend_rien() =>
        VersionDuPilote.RadeonSoftwareVersion("NVIDIA GeForce RTX 4060").Should().BeNull("la machine de référence a une carte NVIDIA");
}
