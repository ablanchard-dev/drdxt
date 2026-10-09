using DrDxt.Application.Support;
using FluentAssertions;

namespace DrDxt.Application.Tests.Support;

public class RemesureAuReposTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static EtatDuPoste Repos(
        TimeSpan? sansSaisie = null, bool jeuOuvert = false, double? chargeCpu = 3,
        bool referenceSuspecte = true, DateTimeOffset? dernierEssai = null)
        => new(sansSaisie ?? TimeSpan.FromMinutes(6), jeuOuvert, chargeCpu, referenceSuspecte, dernierEssai);

    [Fact]
    public void Poste_au_repos_et_reference_suspecte_on_remesure()
        => RemesureAuRepos.Decider(Repos(), T0).Should().Be(DecisionRemesure.Mesurer);

    [Fact]
    public void Une_reference_saine_ne_se_remesure_jamais_seule()
        => RemesureAuRepos.Decider(Repos(referenceSuspecte: false), T0).Should().Be(DecisionRemesure.RienASuspecter);

    [Fact]
    public void Un_jeu_ouvert_meme_reduit_interdit_la_mesure()
        => RemesureAuRepos.Decider(Repos(jeuOuvert: true), T0).Should().Be(DecisionRemesure.PasAuRepos);

    [Fact]
    public void Quelqu_un_devant_le_poste_interdit_la_mesure()
        => RemesureAuRepos.Decider(Repos(sansSaisie: TimeSpan.FromMinutes(4)), T0).Should().Be(DecisionRemesure.PasAuRepos);

    [Fact]
    public void Un_processeur_occupe_interdit_la_mesure()
        => RemesureAuRepos.Decider(Repos(chargeCpu: 25), T0).Should().Be(DecisionRemesure.PasAuRepos);

    [Fact]
    public void Une_charge_inconnue_n_est_pas_un_repos()
        => RemesureAuRepos.Decider(Repos(chargeCpu: null), T0).Should().Be(DecisionRemesure.PasAuRepos);

    [Fact]
    public void Un_essai_par_heure_au_plus()
    {
        RemesureAuRepos.Decider(Repos(dernierEssai: T0.AddMinutes(-30)), T0).Should().Be(DecisionRemesure.TropTot);
        RemesureAuRepos.Decider(Repos(dernierEssai: T0.AddMinutes(-61)), T0).Should().Be(DecisionRemesure.Mesurer);
    }

    [Fact]
    public void Les_seuils_aux_bornes()
    {
        RemesureAuRepos.Decider(Repos(sansSaisie: RemesureAuRepos.SansSaisieMin), T0).Should().Be(DecisionRemesure.Mesurer);
        RemesureAuRepos.Decider(Repos(chargeCpu: RemesureAuRepos.ChargeCpuMaxPourcent), T0).Should().Be(DecisionRemesure.PasAuRepos);
    }
}
