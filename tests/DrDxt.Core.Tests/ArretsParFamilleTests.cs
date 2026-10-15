using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Les arrêts brutaux classés par famille, et le réglage de la carte fermé machine par machine
/// et famille par famille. Les arrêts ne bloquent pas tout : une coupure en veille
/// ne ferme rien, un écran bleu graphique récent ferme la carte.
/// </summary>
public class ArretsParFamilleTests
{
    private static readonly DateTimeOffset Maintenant = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(0, true, FamilleDArret.Veille)]
    [InlineData(0, false, FamilleDArret.SansCode)]
    [InlineData(0x116, false, FamilleDArret.EcranBleuGraphique)]
    [InlineData(0x117, false, FamilleDArret.EcranBleuGraphique)]
    [InlineData(0x141, false, FamilleDArret.EcranBleuGraphique)]
    [InlineData(0x193, true, FamilleDArret.EcranBleuGraphique)]
    [InlineData(0xEF, true, FamilleDArret.EcranBleu)]
    [InlineData(0x154, false, FamilleDArret.EcranBleu)]
    public void Chaque_arret_a_sa_famille_et_le_code_prime_sur_la_transition(int code, bool transition, FamilleDArret attendue)
        => ClasseurDArrets.Classer(code, transition).Should().Be(attendue);

    private static ArretClasse Arret(FamilleDArret f, int jours, int code = 0) => new(Maintenant.AddDays(-jours), f, code);

    private static DiagnosticMachine Machine(params ArretClasse[] arrets)
        => DiagnosticDeScan.Construire(goulot: null, mesure: null, instabiliteGpu: null, arretsBrutaux: arrets);

    private static CandidateAppraisal Carte(DiagnosticMachine d)
        => JugementApply.Juger(d, new[] { "gpu.power_limit" }, jeuEnCours: true).Appreciations.Single();

    [Fact]
    public void Des_coupures_en_veille_recentes_ne_ferment_ni_la_carte_ni_le_processeur()
    {
        var d = Machine(Arret(FamilleDArret.Veille, 1), Arret(FamilleDArret.Veille, 2), Arret(FamilleDArret.Veille, 3));

        Carte(d).Retenu.Should().BeTrue("une coupure en veille ne dit rien de la stabilité de la carte en charge");
        JugementApply.Juger(d, new[] { "fixit.power_plan" }, jeuEnCours: true).VetoDesArrets.Should().BeNull();
        CandidateSelector.PhraseReglageCarte(d, Maintenant).Should().StartWith("Réglage de la carte ouvert chez toi")
            .And.Contain("3 « coupure pendant la veille ou la sortie de veille »")
            .And.Contain("gain mesuré");
    }

    // Un écran bleu graphique récent ne ferme plus la
    // carte, il la met sous garde (un arrêt après la pose la rend seule) ; la phrase nomme toujours sa famille.
    [Fact]
    public void Un_ecran_bleu_graphique_recent_ouvre_la_carte_sous_garde_et_la_phrase_nomme_sa_famille()
    {
        var d = Machine(Arret(FamilleDArret.EcranBleuGraphique, 3, 0x116));

        Carte(d).Retenu.Should().BeTrue();
        CandidateSelector.PhraseReglageCarte(d, Maintenant).Should().StartWith("Réglage de la carte ouvert sous garde")
            .And.Contain("1 arrêt brutal de la famille « écran bleu graphique »")
            .And.Contain("le rend seul");
    }

    [Fact]
    public void Un_ecran_bleu_graphique_ANCIEN_n_est_plus_un_veto()
    {
        var d = Machine(Arret(FamilleDArret.EcranBleuGraphique, 100, 0x116));

        Carte(d).Retenu.Should().BeTrue();
        CandidateSelector.PhraseReglageCarte(d, Maintenant).Should().Contain("aucun arrêt brutal depuis 45 jours");
    }

    [Fact]
    public void Des_arrets_secs_sans_code_repetes_n_empechent_plus_la_carte()
    {
        // Sous garde, comme le reste.
        Carte(Machine(Arret(FamilleDArret.SansCode, 1), Arret(FamilleDArret.SansCode, 5))).Retenu.Should().BeTrue();
        Carte(Machine(Arret(FamilleDArret.SansCode, 1))).Retenu.Should().BeTrue();
    }

    [Fact]
    public void Des_ecrans_bleus_d_un_autre_code_ne_ferment_plus_rien()
    {
        // Le cas relevé sur le PC de référence (0xEF le 04/09, 0x154 le 02/09) ; la garde d'incident remplace le refus.
        var d = Machine(Arret(FamilleDArret.EcranBleu, 2, 0xEF), Arret(FamilleDArret.EcranBleu, 4, 0x154));

        Carte(d).Retenu.Should().BeTrue();
        JugementApply.Juger(d, new[] { "fixit.power_plan" }, jeuEnCours: true).Appreciations.Single().Retenu.Should().BeTrue();
    }

    [Fact]
    public void Non_lu_n_ouvre_rien_de_faux_et_le_dit()
        => CandidateSelector.PhraseReglageCarte(DiagnosticDeScan.Construire(null, null, null, null), Maintenant)
            .Should().Contain("le journal des arrêts n'a pas pu être lu");
}
