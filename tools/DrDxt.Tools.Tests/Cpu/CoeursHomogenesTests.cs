using DrDxt.Tools.Coeurs;
using FluentAssertions;

namespace DrDxt.Tools.Tests.Cpu;

/// <summary>
/// UN SEUL CŒUR BRUYANT NE DOIT PAS AVEUGLER LA COMPARAISON DES AUTRES.
///
/// La premiere version prenait la PLUS GRANDE dispersion comme plancher de bruit. Mesure du
/// 09/09/2026 sur huit cœurs : sept sous 1,3 % et un seul a 5,86 %. Le seuil montait a 17,6 %, et
/// un cœur a 6,6 % sous la mediane passait inapercu. La garde etait aveugle pour tout le monde a
/// cause d'une seule mesure ratee — et au tour suivant, ce meme cœur mesurait a 0,02 %.
/// </summary>
public class CoeursHomogenesTests
{
    private static DebitParCoeur C(int cpu, double mops, double dispersion = 0.3)
        => new(cpu, mops, dispersion);

    [Fact]
    public void Un_seul_coeur_bruyant_n_empeche_pas_de_voir_un_coeur_lent()
    {
        // Sept cœurs sains, un bruyant, et un lent. Avec le maximum comme plancher, le lent
        // disparaissait derriere le bruyant.
        var verdict = CoeursHomogenes.Analyser(
        [
            C(2, 1000), C(4, 1000), C(6, 1000), C(8, 1000), C(10, 1000, dispersion: 6.0),
            C(12, 1000), C(14, 900),
        ]);

        verdict.MeriteUnRegard.Should().BeTrue("un cœur a 10 % sous la mediane doit se voir");
        verdict.CoeurLePlusLent.Should().Be(14);
        verdict.BruitPercent.Should().BeApproximately(0.3, 0.01, "la mediane des dispersions");
        verdict.PireDispersionPercent.Should().BeApproximately(6.0, 0.01,
            "le cœur bruyant reste signale, il n'est simplement plus le plancher");
    }

    [Fact]
    public void Des_coeurs_homogenes_ne_declenchent_rien()
    {
        var verdict = CoeursHomogenes.Analyser(
            [C(0, 1000), C(2, 1002), C(4, 998), C(6, 1001)]);

        verdict.MeriteUnRegard.Should().BeFalse();
        verdict.Lisible.Should().Contain("aucun cœur ne se distingue");
    }

    [Fact]
    public void Le_processeur_0_est_nomme_AVEC_la_raison_pour_laquelle_il_rend_moins()
    {
        // Le cas reel de la machine de reference : le processeur 0 rend 3,7 % de moins, et ce
        // n'est PAS un defaut — Windows y dirige une part des interruptions et des DPC.
        var verdict = CoeursHomogenes.Analyser(
            [C(0, 963), C(2, 1000), C(4, 1000), C(6, 1001)]);

        verdict.CoeurLePlusLent.Should().Be(0);
        verdict.Lisible.Should().Contain("processeur 0 : Windows",
            "le nommer sans dire ca enverrait quelqu'un soupconner son processeur pour un "
            + "comportement voulu par le systeme");
    }

    [Fact]
    public void La_phrase_ne_dit_JAMAIS_qu_un_coeur_est_degrade()
    {
        var verdict = CoeursHomogenes.Analyser([C(2, 1000), C(4, 1000), C(6, 800)]);

        verdict.MeriteUnRegard.Should().BeTrue();
        verdict.Lisible.Should().Contain("pas un diagnostic",
            "une dispersion entre cœurs est ATTENDUE : Turbo Boost Max designe des favoris, et "
            + "l'appeler degradation accuserait le silicium d'un defaut mis la expres");
        verdict.Lisible.Should().NotContain("degrade,");
    }

    [Fact]
    public void Moins_de_deux_coeurs_ne_produit_aucune_comparaison()
    {
        CoeursHomogenes.Analyser([C(0, 1000)]).Lisible.Should().Contain("rien a comparer");
        CoeursHomogenes.Analyser([]).CoeurLePlusLent.Should().Be(-1);
        CoeursHomogenes.Analyser(null).MeriteUnRegard.Should().BeFalse();
    }

    [Fact]
    public void Un_coeur_sans_debit_est_ecarte_au_lieu_de_passer_pour_le_plus_lent()
    {
        // Un epinglage refuse rend 0 Mops : le compter ferait crier « ce cœur est mort ».
        var verdict = CoeursHomogenes.Analyser(
            [C(0, 0), C(2, 1000), C(4, 1001), C(6, 999)]);

        verdict.CoeurLePlusLent.Should().NotBe(0);
        verdict.MeriteUnRegard.Should().BeFalse();
    }
}
