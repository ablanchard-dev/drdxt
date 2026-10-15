using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Le produit dit les echecs de creation de vidage (volmgr 161), sans nommer de cause qu'il n'a pas
/// etablie. Releve sur la machine de reference : 10 arrets brutaux, 2 echecs, aucun vidage garde.
/// </summary>
public class CreationDeVidageTests
{
    private sealed class Vidages(Result<EtatDesVidages> reponse) : IDumpCreationLog
    {
        public Result<EtatDesVidages> Lire(DateTimeOffset depuis) => reponse;
    }

    private static RepairFinding Constat(Result<EtatDesVidages> reponse)
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(),
                new Vidages(reponse), new WmiMuet())
            .Lire().Single(c => c.Nom == "creation de vidage");

    private static readonly DateTimeOffset Le2Septembre = new(2026, 9, 2, 3, 9, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Le4Septembre = new(2026, 9, 4, 14, 44, 0, TimeSpan.Zero);

    [Fact]
    public void Des_echecs_sans_aucun_vidage_garde_meritent_l_attention_sans_cause_inventee()
    {
        var c = Constat(Result<EtatDesVidages>.Ok(new EtatDesVidages([Le2Septembre, Le4Septembre], 0, false)));

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Be("2 échec(s), aucun vidage gardé");
        c.Explication.Should().Contain("02/09").And.Contain("04/09")
            .And.Contain("n'est pas établie").And.Contain("type de vidage ne réglerait rien");
        RepairTriage.Trier([c]).Attention.Should().ContainSingle();
    }

    // Comme le test memoire : la phrase de Windows est rendue telle quelle, sans verdict tire du code.
    [Fact]
    public void La_phrase_de_Windows_sur_le_dernier_echec_est_rendue_telle_quelle()
    {
        const string phrase = "La création du fichier de vidage a échoué en raison d’une erreur lors de la création de l’image mémoire. BugCheckProgress était : 0x00040049";
        var c = Constat(Result<EtatDesVidages>.Ok(new EtatDesVidages([Le2Septembre, Le4Septembre], 0, false, phrase)));

        c.Explication.Should().Contain($"Windows a noté : « {phrase} »");
    }

    [Fact]
    public void Des_vidages_presents_sont_un_inventaire_sans_jugement()
    {
        var c = Constat(Result<EtatDesVidages>.Ok(new EtatDesVidages([Le2Septembre], 3, false)));

        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().Be("3 petit(s) vidage(s), 1 échec(s)");
    }

    [Fact]
    public void Un_journal_illisible_est_un_angle_mort_pas_une_machine_saine()
    {
        var c = Constat(Result<EtatDesVidages>.Fail(ErrorCodes.DetectionFailed, "refus"));

        c.Statut.Should().Be(RepairStatus.Unknown);
        c.ValeurCourante.Should().BeNull();
    }
}
