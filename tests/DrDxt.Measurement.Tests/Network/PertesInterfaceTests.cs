using DrDxt.Core;
using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

/// <summary>Les pertes a l'interface, classees pareil par l'outil et par l'App.</summary>
public class PertesInterfaceTests
{
    private sealed class Source(params Result<NicCounterSnapshot>[] releves) : INicCounterSource
    {
        private int _i;
        public Result<NicCounterSnapshot> Read(string interfaceId) => releves[Math.Min(_i++, releves.Length - 1)];
    }

    private static Result<NicCounterSnapshot> R(long ignores, long erreurs, long recus) =>
        Result<NicCounterSnapshot>.Ok(new NicCounterSnapshot(ignores, erreurs, 0, 0, recus, 1000));

    private static RepairFinding Mesure(params Result<NicCounterSnapshot>[] releves) =>
        PertesInterface.Mesurer(new Source(releves), "id", TimeSpan.FromSeconds(20), _ => { });

    [Fact]
    public void Une_fenetre_propre_est_un_constat_verifie()
        => Mesure(R(5, 0, 1000), R(5, 0, 9000)).Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Des_trames_jetees_PENDANT_la_fenetre_sont_suspectes()
    {
        var c = Mesure(R(5, 0, 1000), R(40, 2, 9000));

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Contain("35 ignorés").And.Contain("2 erreurs");
    }

    // Seuls les compteurs d'émission bougent : la valeur ne se lit pas « 0 ignorés / 0 erreurs en réception » sous un statut
    // suspect, elle dit l'émission.
    [Fact]
    public void Des_pertes_en_emission_seule_se_disent_en_emission()
    {
        var c = PertesInterface.Classer(new NicErrorDelta(0, 0, 0, 3, 8000, 4000, false), TimeSpan.FromSeconds(20));

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.ValeurCourante.Should().Be(Docteur.T("PI.PertesEmission", 0, 3)).And.NotContain(Docteur.T("PI.Pertes", 0, 0));
    }

    // Une trame ignorée en 20 s donnait « suspect » : Windows ignore aussi les trames d'un protocole qu'il ne connaît pas.
    // Quelques-unes sont observées ; une erreur compte dès la première.
    [Fact]
    public void Quelques_trames_ignorees_sont_observees_une_erreur_reste_suspecte()
    {
        PertesInterface.Classer(new NicErrorDelta(1, 0, 0, 0, 8000, 4000, false), TimeSpan.FromSeconds(20))
            .Statut.Should().Be(RepairStatus.Observed);
        PertesInterface.Classer(new NicErrorDelta(0, 1, 0, 0, 8000, 4000, false), TimeSpan.FromSeconds(20))
            .Statut.Should().Be(RepairStatus.Suspicious);
        PertesInterface.Classer(new NicErrorDelta(40, 0, 0, 0, 8000, 4000, false), TimeSpan.FromSeconds(20))
            .Statut.Should().Be(RepairStatus.Suspicious, "40 sur 8000 : 0,5 %");
    }

    [Fact]
    public void Un_compteur_qui_recule_n_est_jamais_rien_a_signaler()
        => Mesure(R(50, 0, 9000), R(5, 0, 1000)).Statut.Should().Be(RepairStatus.Unknown);

    [Fact]
    public void Des_compteurs_illisibles_ne_valent_pas_zero()
        => Mesure(Result<NicCounterSnapshot>.Fail("X", "carte absente")).Statut.Should().Be(RepairStatus.Unknown);
}
