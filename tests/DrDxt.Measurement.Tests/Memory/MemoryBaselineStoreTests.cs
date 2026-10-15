using DrDxt.Core;
using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

public class MemoryBaselineStoreTests
{
    // Un fichier en memoire : le magasin se teste sans toucher le disque, comme le magasin BIOS.
    private sealed class FausFichier : IMemoryBaselineFile
    {
        public string? Contenu;
        public bool Casse;

        public Result WriteAtomic(string json)
        {
            if (Casse) return Result.Fail("io", "disque en panne");
            Contenu = json;
            return Result.Ok();
        }

        public Result<string?> Read()
            => Casse ? Result<string?>.Fail("io", "disque en panne") : Result<string?>.Ok(Contenu);
    }

    private static MemoryBaseline Reference(string machineId = "MACHINE-A") => new(
        MachineId: machineId,
        TakenUtc: new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero),
        ConfiguredMts: 3800,
        Measurement: new MemoryMeasurement(34.20, 3.34, 71.33, 4.53,
            new[] { new StaircasePoint(32 * 1024, 0.95), new StaircasePoint(128 * 1024 * 1024, 75.33) }));

    [Fact]
    public void Une_reference_ecrite_se_relit_a_l_identique()
    {
        var fichier = new FausFichier();
        var magasin = new MemoryBaselineStore(fichier);

        magasin.Save(Reference()).IsOk.Should().BeTrue();
        var relue = magasin.Read("MACHINE-A");

        relue.IsOk.Should().BeTrue();
        relue.Value!.Measurement.BandwidthGBs.Should().BeApproximately(34.20, 0.001);
        relue.Value!.Measurement.Staircase.Should().HaveCount(2);
        relue.Value!.ConfiguredMts.Should().Be(3800);
    }

    [Fact]
    public void Aucune_reference_enregistree_n_est_pas_une_erreur()
    {
        new MemoryBaselineStore(new FausFichier()).Read("MACHINE-A")
            .Should().Match<Result<MemoryBaseline?>>(r => r.IsOk && r.Value == null);
    }

    [Fact]
    public void Une_reference_prise_sur_une_AUTRE_machine_est_ignoree()
    {
        // Le disque voyage : image systeme, restauration, deuxieme PC. Comparer la memoire d'une
        // machine a la reference d'une autre rendrait un delta invente de toutes pieces.
        var fichier = new FausFichier();
        new MemoryBaselineStore(fichier).Save(Reference("MACHINE-A"));

        new MemoryBaselineStore(fichier).Read("MACHINE-B").Value.Should().BeNull();
    }

    [Theory]
    // Du JSON parfaitement VALIDE, mais amputé. `JsonSerializer` remplit un record sans se
    // plaindre d'un champ absent : la mesure vaut null, l'escalier vaut null, et le premier
    // appelant qui les dereference fait tomber le scan. Le refus doit vivre ici, la ou tous
    // les appelants passent, pas dans chacun d'eux.
    [InlineData("{\"MachineId\":\"MACHINE-A\",\"ConfiguredMts\":3800}")]
    [InlineData("{\"MachineId\":\"MACHINE-A\",\"ConfiguredMts\":3800,\"Measurement\":{\"BandwidthGBs\":34.2}}")]
    [InlineData("null")]
    public void Une_reference_amputee_est_refusee_au_lieu_de_faire_tomber_le_scan(string json)
    {
        var lu = new MemoryBaselineStore(new FausFichier { Contenu = json }).Read("MACHINE-A");

        lu.IsOk.Should().BeFalse();
    }

    // Une reference sans ses dispersions se relisait avec sigma = 0. Le seuil de
    // MemoryVerdict (3 sigma) tombait alors a 0, et n'importe quel ecart de bruit devenait
    // « significatif ». Un banc de dix passes ne rend jamais une dispersion nulle.
    [Theory]
    [InlineData("\"BandwidthSigmaPct\":0,")]
    [InlineData("\"LatencySigmaPct\":0,")]
    [InlineData("\"BandwidthSigmaPct\":-1,")]
    public void Une_reference_sans_dispersion_est_refusee(string champ)
    {
        var json = """
            {"MachineId":"MACHINE-A","TakenUtc":"2026-09-06T10:00:00+00:00","ConfiguredMts":3800,
             "Measurement":{"BandwidthGBs":34.2,"BandwidthSigmaPct":3.34,"LatencyNs":71.33,"LatencySigmaPct":4.53,
             "Staircase":[{"WindowBytes":32768,"LatencyNs":0.95},{"WindowBytes":134217728,"LatencyNs":75.33}]}}
            """;
        var nom = champ.Split(':')[0];
        var altere = System.Text.RegularExpressions.Regex.Replace(json, nom + @":[0-9.]+,", champ);
        altere.Should().NotBe(json);

        new MemoryBaselineStore(new FausFichier { Contenu = altere }).Read("MACHINE-A").IsOk.Should().BeFalse();
        // Temoin : le meme fichier, intact, se relit.
        new MemoryBaselineStore(new FausFichier { Contenu = json }).Read("MACHINE-A").IsOk.Should().BeTrue();
    }

    [Fact]
    public void Une_panne_disque_echoue_proprement_des_deux_cotes()
    {
        var enPanne = new FausFichier { Casse = true };
        var magasin = new MemoryBaselineStore(enPanne);

        magasin.Save(Reference()).IsOk.Should().BeFalse();
        magasin.Read("MACHINE-A").IsOk.Should().BeFalse();
    }

    [Fact]
    public void Un_fichier_illisible_echoue_proprement_et_ne_leve_jamais()
    {
        var fichier = new FausFichier { Contenu = "{ ceci n'est pas du json" };

        var lu = new MemoryBaselineStore(fichier).Read("MACHINE-A");

        lu.IsOk.Should().BeFalse();
    }
}
