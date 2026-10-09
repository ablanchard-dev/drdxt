using DrDxt.Core;
using DrDxt.Measurement;
using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// Le journal des controles n'efface plus ce qu'il n'a pas su lire (L2, plan du 10/09).
///
/// Une lecture ratee ou un JSON illisible donnait une liste vide, puis `Ajouter` ecrasait le
/// fichier avec les seules observations du jour. Des semaines de controles no-op — la seule base
/// qui dira un jour si le produit est surconfiant — disparaissaient sans un mot.
/// </summary>
public class NoOpCalibrationStoreTests
{
    private sealed class Fichier : IPerformanceJournalFile
    {
        public string? Contenu;
        public bool EchecLecture;
        public int Ecritures;

        public Result WriteAtomic(string json)
        {
            Ecritures++;
            Contenu = json;
            return Result.Ok();
        }

        public Result<string?> Read() => EchecLecture
            ? Result<string?>.Fail(ErrorCodes.CaptureFail, "fichier verrouille")
            : Result<string?>.Ok(Contenu);
    }

    private static NoOpObservation Observation(int jour) =>
        new(new DateTimeOffset(2026, 9, jour, 12, 0, 0, TimeSpan.Zero), "firing-range", 10, 24, 0.99,
            GameMetricKind.Moyenne, 1.2, 5.4);

    [Fact]
    public void Une_LECTURE_ratee_n_ecrase_pas_le_journal()
    {
        var f = new Fichier { Contenu = "[historique precieux]", EchecLecture = true };

        var r = new NoOpCalibrationStore(f).Ajouter(new[] { Observation(10) });

        r.IsOk.Should().BeFalse("ajouter sans avoir lu, c'est effacer");
        f.Ecritures.Should().Be(0);
        f.Contenu.Should().Be("[historique precieux]");
    }

    [Fact]
    public void Un_journal_ILLISIBLE_n_est_pas_ecrase_non_plus()
    {
        var f = new Fichier { Contenu = "{ pas du json" };

        new NoOpCalibrationStore(f).Ajouter(new[] { Observation(10) }).IsOk.Should().BeFalse();

        f.Ecritures.Should().Be(0);
        f.Contenu.Should().Be("{ pas du json");
    }

    [Fact]
    public void Un_journal_ABSENT_se_cree_et_les_existants_se_gardent()
    {
        var f = new Fichier();
        var magasin = new NoOpCalibrationStore(f);

        magasin.Ajouter(new[] { Observation(9) }).IsOk.Should().BeTrue();
        magasin.Ajouter(new[] { Observation(10) }).IsOk.Should().BeTrue();

        magasin.Lire().Should().HaveCount(2);
    }

    [Fact]
    public void Lire_garde_son_repli_une_mesure_du_jour_n_est_jamais_bloquee()
    {
        new NoOpCalibrationStore(new Fichier { EchecLecture = true }).Lire().Should().BeEmpty();
    }

    // 26/09/2026 : une resolution infinie (zero degre de liberte) faisait lever la serialisation, et le
    // lot du jour etait perdu. Elle s'ecrit, se relit, et le juge l'ecarte deja (IsFinite).
    [Fact]
    public void Une_resolution_infinie_se_consigne_au_lieu_de_perdre_le_lot()
    {
        var magasin = new NoOpCalibrationStore(new Fichier());
        var infinie = Observation(3) with { ResolutionPct = double.PositiveInfinity };

        magasin.Ajouter([Observation(2), infinie]).IsOk.Should().BeTrue();

        magasin.Lire().Should().HaveCount(2);
    }
}
