using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// 17/09/2026 : le test memoire est le geste qui tranche les arrets brutaux (conseil JEDEC), et le
/// produit ne savait pas s'il avait ete fait. Il lit maintenant le journal du Diagnostic de memoire
/// Windows. Les sources se contredisent sur le sens des numeros d'evenements : le produit ne juge donc
/// pas le resultat, il rend la date et la phrase exacte de Windows, et dit « jamais lance » quand le
/// journal ne contient rien.
/// </summary>
public class TestMemoireWindowsTests
{
    private sealed class Journal(Result<IReadOnlyList<ResultatTestMemoire>> reponse) : IMemoryDiagnosticLog
    {
        public Result<IReadOnlyList<ResultatTestMemoire>> Lire() => reponse;
    }

    private static RepairFinding Constat(Result<IReadOnlyList<ResultatTestMemoire>> reponse)
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new Journal(reponse),
                new VidagesMuets(), new WmiMuet())
            .Lire().Single(c => c.Nom == "test memoire Windows");

    [Fact]
    public void Un_journal_vide_dit_que_le_test_n_a_jamais_ete_lance()
    {
        var c = Constat(Result<IReadOnlyList<ResultatTestMemoire>>.Ok([]));

        c.ValeurCourante.Should().Be("jamais lancé");
        c.Statut.Should().Be(RepairStatus.Observed);
    }

    [Fact]
    public void Un_resultat_est_rendu_avec_la_phrase_de_Windows_sans_verdict_invente()
    {
        var quand = new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero);
        var c = Constat(Result<IReadOnlyList<ResultatTestMemoire>>.Ok(
            [new ResultatTestMemoire(quand, 1201, "Le Diagnostic de mémoire Windows a testé la mémoire de l'ordinateur et n'a détecté aucune erreur")]));

        c.ValeurCourante.Should().Contain("20/09/2026");
        c.Explication.Should().Contain("n'a détecté aucune erreur");
        c.Explication.Should().NotContainAny("réussi", "sain", "valide");
    }

    private sealed class Arrets(int combien) : IUnexpectedShutdownLog
    {
        public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest)
            => Result<IReadOnlyList<DateTimeOffset>>.Ok(
                Enumerable.Range(1, combien).Select(i => DateTimeOffset.UtcNow.AddDays(-i * 5)).ToList());
    }

    private static RepairFinding ConstatAvecArrets(int arrets, Result<IReadOnlyList<ResultatTestMemoire>> reponse)
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new Arrets(arrets), new Journal(reponse),
                new VidagesMuets(), new WmiMuet())
            .Lire().Single(c => c.Nom == "test memoire Windows");

    [Fact]
    public void Jamais_lance_avec_des_arrets_repetes_arrive_dans_ce_qui_merite_l_attention()
    {
        // 17/09/2026 : rangee en « verifie », la ligne etait comptee mais jamais affichee. Avec des
        // arrets brutaux qui se repetent, c'est le geste qui tranche : il doit se voir.
        var c = ConstatAvecArrets(10, Result<IReadOnlyList<ResultatTestMemoire>>.Ok([]));

        RepairTriage.Trier([c]).Attention.Should().ContainSingle();
        c.Explication.Should().Contain("10 arrêts brutaux");
    }

    [Fact]
    public void Jamais_lance_sans_arret_repete_reste_une_observation()
    {
        var c = ConstatAvecArrets(1, Result<IReadOnlyList<ResultatTestMemoire>>.Ok([]));

        RepairTriage.Trier([c]).Attention.Should().BeEmpty();
    }

    [Fact]
    public void Un_journal_illisible_garde_le_meme_nom_et_se_dit_inconnu()
    {
        var c = Constat(Result<IReadOnlyList<ResultatTestMemoire>>.Fail(ErrorCodes.DetectionFailed, "refus"));

        c.Statut.Should().Be(RepairStatus.Unknown);
    }
}
