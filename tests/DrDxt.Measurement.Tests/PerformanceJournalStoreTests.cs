using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Measurement.Tests;

/// <summary>
/// Le magasin du journal — trois choses seulement : ajouter, relire, elaguer.
///
/// Ce qui est verifie ici n'est pas « le JSON fait un aller-retour » mais les decisions qui
/// touchent le client : un historique perdu ne doit **jamais** empecher de mesurer aujourd'hui, une
/// ecriture qui echoue doit se dire (sinon le produit croit accumuler un historique qui n'existe
/// pas), et l'elagage ne doit pas detruire la reference la plus ancienne.
/// </summary>
public class PerformanceJournalStoreTests
{
    private sealed class FichierFake : IPerformanceJournalFile
    {
        public string? Contenu;
        public bool EchecEcriture;
        public bool EchecLecture;
        public int Ecritures;

        public Result WriteAtomic(string json)
        {
            Ecritures++;
            if (EchecEcriture) return Result.Fail(ErrorCodes.CaptureFail, "disque plein");
            Contenu = json;
            return Result.Ok();
        }

        public Result<string?> Read() => EchecLecture
            ? Result<string?>.Fail(ErrorCodes.CaptureFail, "illisible")
            : Result<string?>.Ok(Contenu);
    }

    private static readonly DateTimeOffset J0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Le JEU fait partie de l'identite d'une mesure : sans lui, le juge refuse de
    /// comparer — deux titres n'ont pas la meme echelle de FPS.</summary>
    private static PerformanceSample Mesure(int jour, double avgFps) =>
        new(J0.AddDays(jour), "PC-A", avgFps, 2.0, Runs: 3, KeptFraction: 1.0, GameId: "warzone");

    [Fact]
    public void Une_mesure_ajoutee_se_relit_a_l_identique()
    {
        var f = new FichierFake();
        var magasin = new PerformanceJournalStore(f);

        magasin.Ajouter(Mesure(0, 200)).IsOk.Should().BeTrue();

        var relu = magasin.Lire();
        relu.Should().ContainSingle();
        relu[0].AvgFps.Should().Be(200);
        relu[0].MachineId.Should().Be("PC-A");
        relu[0].KeptFraction.Should().Be(1.0);
    }

    // 26/09/2026 : une mesure relue sans « MarginOfError » vaut 0. Elle passait pour la plus precise, devenait
    // l'etalon, et la resolution de la derive retrecissait d'autant.
    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void Une_mesure_relue_sans_marge_n_est_pas_gardee(double marge)
    {
        var f = new FichierFake();
        var magasin = new PerformanceJournalStore(f);
        magasin.Ajouter(Mesure(0, 140)).IsOk.Should().BeTrue();
        f.Contenu = f.Contenu!.Replace("\"MarginOfError\":2", $"\"MarginOfError\":{marge.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        magasin.Lire().Should().BeEmpty();
    }

    // 26/09/2026 : la marge vaut +infini sous trois passes (voir PerformanceSample). Le JSON par defaut
    // refuse d'ecrire l'infini : la mesure doit se journaliser, ou echouer en le disant, jamais lever.
    [Fact]
    public void Une_mesure_a_marge_infinie_ne_fait_pas_lever_le_journal()
    {
        var magasin = new PerformanceJournalStore(new FichierFake());
        var act = () => magasin.Ajouter(Mesure(0, 140) with { MarginOfError = double.PositiveInfinity, Runs = 2 });
        act.Should().NotThrow();
        magasin.Lire().Should().ContainSingle().Which.MarginOfError.Should().Be(double.PositiveInfinity);
    }

    [Fact]
    public void Le_journal_s_ACCUMULE_entre_deux_sessions()
    {
        // Toute la raison d'etre : deux lancements separes doivent laisser deux traces.
        var f = new FichierFake();
        new PerformanceJournalStore(f).Ajouter(Mesure(0, 200));
        new PerformanceJournalStore(f).Ajouter(Mesure(30, 170));

        var derive = PerformanceJournal.Derive(new PerformanceJournalStore(f).Lire());
        derive.RegressionEnJeuProuvee.Should().BeTrue("200 → 170 fps depasse largement le bruit");
    }

    [Fact]
    public void Un_journal_ILLISIBLE_n_empeche_pas_de_mesurer_aujourd_hui()
    {
        // Ne pas avoir d'historique est un etat NORMAL (premier lancement). Faire echouer un
        // scan pour un fichier abime serait punir l'utilisateur d'un probleme qui n'est pas le sien.
        var f = new FichierFake { EchecLecture = true };

        new PerformanceJournalStore(f).Lire().Should().BeEmpty();
    }

    [Fact]
    public void Un_journal_CORROMPU_repart_a_vide_au_lieu_de_lever()
    {
        var f = new FichierFake { Contenu = "{ ceci n'est pas du JSON" };

        new PerformanceJournalStore(f).Lire().Should().BeEmpty();
    }

    [Fact]
    public void Une_LECTURE_ratee_n_ecrase_pas_la_reference_de_derive()
    {
        // L2 : l'echec de lecture donnait un journal vide, puis Ajouter ecrasait le fichier —
        // la plus ancienne mesure, la reference de toute derive, disparaissait en silence.
        var f = new FichierFake { Contenu = "[reference]", EchecLecture = true };

        new PerformanceJournalStore(f).Ajouter(Mesure(3, 140)).IsOk.Should().BeFalse();

        f.Ecritures.Should().Be(0);
        f.Contenu.Should().Be("[reference]");
    }

    [Fact]
    public void Un_journal_CORROMPU_n_est_pas_ecrase_par_la_mesure_suivante()
    {
        var f = new FichierFake { Contenu = "{ ceci n'est pas du JSON" };

        new PerformanceJournalStore(f).Ajouter(Mesure(3, 140)).IsOk.Should().BeFalse();

        f.Ecritures.Should().Be(0);
    }

    [Fact]
    public void Une_ECRITURE_qui_echoue_se_DIT()
    {
        // L'asymetrie voulue : une lecture ratee se tait, une ecriture ratee parle. Sinon le
        // produit croirait accumuler un historique qui n'existe pas, et annoncerait un jour une
        // « premiere mesure » apres six mois d'usage.
        var f = new FichierFake { EchecEcriture = true };

        var r = new PerformanceJournalStore(f).Ajouter(Mesure(0, 200));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Une_mesure_INEXPLOITABLE_n_est_pas_journalisee_du_tout()
    {
        // Zero FPS n'est pas une mesure : la garder polluerait la reference pour toujours.
        var f = new FichierFake();

        new PerformanceJournalStore(f).Ajouter(Mesure(0, 0)).IsOk.Should().BeFalse();
        f.Ecritures.Should().Be(0, "on n'ecrit meme pas");
    }

    [Fact]
    public void Le_journal_reste_borne_SANS_perdre_la_reference_la_plus_ancienne()
    {
        // Tronquer par la fin donnerait un historique glissant, qui ne voit jamais une
        // degradation lente : chaque comparaison se ferait contre un passe deja degrade.
        var f = new FichierFake();
        var magasin = new PerformanceJournalStore(f);
        for (var i = 0; i < PerformanceJournalStore.MaximumEntrees + 25; i++)
            magasin.Ajouter(Mesure(i, 200 - i * 0.01));

        var journal = magasin.Lire();

        journal.Should().HaveCount(PerformanceJournalStore.MaximumEntrees);
        journal[0].TakenUtc.Should().Be(J0, "la toute premiere mesure est l'etalon");
    }
}
