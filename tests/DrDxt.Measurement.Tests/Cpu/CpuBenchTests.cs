using DrDxt.Measurement.Cpu;
using FluentAssertions;
using Xunit;

namespace DrDxt.Measurement.Tests.Cpu;

/// <summary>
/// Ce qui doit etre vrai d'un banc, sous peine qu'il mente en silence.
///
/// Le mode d'echec redoute n'est pas le plantage, c'est la boucle SUPPRIMEE. Une charge
/// arithmetique dont personne ne lit le resultat, le compilateur a le droit de l'effacer : le banc
/// rend alors des debits magnifiques, parfaitement stables, et totalement faux. La somme de
/// controle est la seule chose qui distingue « a travaille » de « n'a rien fait ».
/// </summary>
public class CpuBenchTests
{
    [Fact]
    public void Deux_executions_identiques_rendent_la_meme_somme()
    {
        var a = CpuBench.Measure(threads: 1, passes: 1);
        var b = CpuBench.Measure(threads: 1, passes: 1);

        a.Somme.Should().Be(b.Somme, "la charge est deterministe : une somme qui bouge signale "
                                     + "que le travail effectue n'est pas le meme");
        a.Somme.Should().NotBe(0, "une somme nulle est le symptome d'une boucle supprimee");
    }

    [Fact]
    public void Les_deux_charges_ne_font_pas_le_meme_travail()
    {
        var legere = CpuBench.Measure(1, 1, ChargeCpu.Legere);
        var lourde = CpuBench.Measure(1, 1, ChargeCpu.Lourde);

        lourde.Somme.Should().NotBe(legere.Somme, "sans cela, `--lourd` serait un drapeau decoratif "
                                                  + "et l'on croirait mesurer la puissance en mesurant le boost");
        lourde.Lourde.Should().BeTrue();
        legere.Lourde.Should().BeFalse();
    }

    [Fact]
    public void Un_debit_est_rendu_avec_sa_dispersion()
    {
        var r = CpuBench.Measure(threads: 1, passes: 3);

        r.MopsParThread.Should().BeGreaterThan(0);
        r.Passes.Should().Be(3, "la passe d'echauffement est jetee, pas comptee");
        double.IsFinite(r.EcartTypePourcent).Should().BeTrue();
        r.EcartTypePourcent.Should().BeGreaterThanOrEqualTo(0);
    }

    // 27/09/2026 : sorti de la CI. Un runner partage a rendu 63 % de dispersion sur trois passes : il
    // mesurait ses voisins, pas le code. Le seuil garde son sens sur une machine au repos.
    [Fact]
    [Trait("Category", "Machine")]
    public void Sur_une_machine_au_repos_la_dispersion_reste_exploitable()
    {
        var r = CpuBench.Measure(threads: 1, passes: 3);

        r.EcartTypePourcent.Should().BeLessThan(50, "une dispersion enorme veut dire que la mesure n'est pas exploitable");
    }

    [Fact]
    public void L_epinglage_est_verifie_et_non_suppose()
    {
        var r = CpuBench.Measure(threads: 1, passes: 1, processeurs: new[] { 0 });

        r.EpinglesReussis.Should().Be(1, "un banc qui croit epingler sans y parvenir rend une "
                                         + "dispersion inexplicable et aucune trace de la cause");
    }

    [Fact]
    public void Un_processeur_inexistant_est_signale_au_lieu_d_etre_tu()
    {
        var r = CpuBench.Measure(threads: 1, passes: 1, processeurs: new[] { 9999 });

        r.EpinglesReussis.Should().Be(0, "l'epinglage a echoue, et le resultat doit le DIRE");
        r.MopsParThread.Should().BeGreaterThan(0, "la mesure se fait quand meme, non epinglee");
    }

    [Fact]
    public void Un_nombre_de_threads_absurde_est_ramene_a_une_mesure_valide()
    {
        var r = CpuBench.Measure(threads: 0, passes: 1);

        r.Threads.Should().Be(1, "on prefere mesurer un thread que rendre une division par zero");
        r.MopsParThread.Should().BeGreaterThan(0);
    }
}
