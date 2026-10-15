using DrDxt.App.ViewModels;
using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// La section « ton passage dans le BIOS », moitie memoire. Ce que ces tests tiennent :
// une phrase que le client lit comme une preuve ne s'affiche que quand l'instrument l'a
// prouvee, et le pourcentage voyage toujours avec les deux valeurs qui le fabriquent.
// -----------------------------------------------------------------------
public sealed class MemoryAbViewModelTests
{
    // Le localiseur de test rend « CLE :: {0} » pour les refus : le test voit alors deux choses
    // que l'un ou l'autre seul ne montrerait pas -- QUELLE phrase a ete choisie (donc quel conseil
    // le client va lire) et si le motif y arrive.
    private static string Localize(string key)
        => key is MemoryAbStringKeys.NoVerdictBusy
                or MemoryAbStringKeys.NoVerdictNoRoom
                or MemoryAbStringKeys.NoVerdictFailed
                or MemoryAbStringKeys.NoVerdictStale
                or MemoryAbStringKeys.NoVerdictGame
            ? key + " :: {0}"
            : key is MemoryAbStringKeys.NotSignificant ? key + " :: {0}" : key;

    private static MemoryAbVerdict Verdict(
        bool hasVerdict = true, bool significant = true, string? noVerdictReason = null) => new(
        HasVerdict: hasVerdict,
        NoVerdictReason: noVerdictReason,
        BandwidthDeltaPct: 79.06,
        LatencyDeltaPct: -25.0,
        BandwidthSignificant: significant,
        LatencySignificant: significant,
        BandwidthBeforeGBs: 19.1,
        BandwidthAfterGBs: 34.2,
        LatencyBeforeNs: 88.0,
        LatencyAfterNs: 66.0);

    [Fact]
    public void Rien_a_comparer_ne_produit_aucune_section()
    {
        MemoryAbViewModel.From(null, Localize).Should().BeNull();
    }

    [Fact]
    public void Un_gain_prouve_montre_les_deux_valeurs_ET_le_pourcentage()
    {
        var vm = MemoryAbViewModel.From(Verdict(), Localize)!;

        vm.Lines.Should().HaveCount(2);
        vm.Lines[0].Before.Should().Contain("19");
        vm.Lines[0].After.Should().Contain("34");
        vm.Lines[0].Delta.Should().Contain("79");
        vm.Lines[0].IsSignificant.Should().BeTrue();
    }

    [Fact]
    public void Un_ecart_sous_le_bruit_ne_dit_JAMAIS_leger_gain()
    {
        var vm = MemoryAbViewModel.From(Verdict(significant: false), Localize)!;

        vm.Conclusion.Should().StartWith("Memory.Ab.NotSignificant");
        vm.Lines.Should().OnlyContain(l => !l.IsSignificant);
    }

    [Fact]
    public void Une_mesure_rejetee_affiche_le_refus_et_AUCUN_chiffre()
    {
        var vm = MemoryAbViewModel.From(
            Verdict(hasVerdict: false, noVerdictReason: "Q-03 (apres)"), Localize)!;

        vm.Lines.Should().BeEmpty();
        vm.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictBusy);
        vm.Conclusion.Should().Contain("Q-03");
    }

    [Fact]
    public void La_latence_qui_baisse_est_un_progres_et_le_signe_le_montre()
    {
        var vm = MemoryAbViewModel.From(Verdict(), Localize)!;

        vm.Lines[1].Delta.Should().StartWith("-").And.Contain("25");
    }

    [Fact]
    public void Machine_occupee_et_panne_d_instrument_ne_donnent_PAS_le_meme_conseil()
    {
        // « Relance la machine au repos » est actionnable quand la machine est occupee (Q-03).
        // Sur un banc indisponible ou une faute d'instrument, c'est une fausse piste : le client
        // ferme des programmes pour un probleme qui n'a rien a voir.
        // Ce test opposait « machine occupee » a « banc indisponible » jusqu'a ce que la revue
        // montre que le banc indisponible est lui aussi un manque de memoire. Il oppose desormais
        // la machine occupee a une vraie faute d'instrument (Q-04, on mesure du cache).
        var occupee = MemoryAbViewModel.From(
            Verdict(hasVerdict: false, noVerdictReason: "Q-03 (apres)"), Localize)!;
        var panne = MemoryAbViewModel.From(
            Verdict(hasVerdict: false, noVerdictReason: "Q-04 (apres)"), Localize)!;

        // Ce n'est pas le motif interpole qui doit differer -- ca, c'est gratuit. C'est la PHRASE,
        // donc le conseil.
        occupee.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictBusy);
        panne.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictFailed);
        occupee.Conclusion.Should().Contain("Q-03");
        panne.Conclusion.Should().Contain("Q-04");
        panne.Lines.Should().BeEmpty();
    }

    [Fact]
    public void Un_banc_qui_n_a_pas_pu_DEMARRER_ne_se_range_pas_avec_les_fautes_d_instrument()
    {
        // La faute de logique que ce test ferme : « banc indisponible » veut dire que
        // l'allocation de 384 Mo a echoue -- c'est-a-dire, le plus souvent, qu'il manque de la
        // memoire libre. Le ranger avec les fautes d'instrument lui collait la phrase « ce n'est
        // pas une question de programmes ouverts », alors que fermer des programmes est
        // Exactement ce qui repare ce cas-la. Trois causes, trois conseils.
        var manqueDeRam = MemoryAbViewModel.From(
            Verdict(hasVerdict: false, noVerdictReason: "banc indisponible (apres)"), Localize)!;
        var instrument = MemoryAbViewModel.From(
            Verdict(hasVerdict: false, noVerdictReason: "Q-04 (apres)"), Localize)!;
        var occupee = MemoryAbViewModel.From(
            Verdict(hasVerdict: false, noVerdictReason: "Q-03 (apres)"), Localize)!;

        manqueDeRam.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictNoRoom);
        instrument.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictFailed);
        occupee.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictBusy);

        new[] { manqueDeRam.Conclusion, instrument.Conclusion, occupee.Conclusion }
            .Distinct().Should().HaveCount(3, "trois causes differentes, trois conseils differents");
    }

    [Fact]
    public void Aucune_difference_mesurable_dit_SOUS_QUEL_SEUIL_elle_est_restee()
    {
        // « Aucune différence mesurable » sans son seuil se lit « ça ne sert à rien ».
        // Sur cette machine, monter la mémoire de 3 800 à 4 000 MT/s promet +5,3 % quand le banc
        // ne distingue pas moins de 7,8 % : la phrase honnête est « je ne sais pas voir si petit »,
        // pas « il n'y a rien ». Le tableau de bord le dit déjà pour le GPU (Scan.Drift.None).
        var verdict = Verdict(significant: false) with
        {
            BandwidthResolutionPct = 7.76,
            LatencyResolutionPct = 14.64,
        };

        var vm = MemoryAbViewModel.From(verdict, Localize)!;

        vm.Conclusion.Should().StartWith(MemoryAbStringKeys.NotSignificant);
        vm.Conclusion.Should().Contain(7.76.ToString(System.Globalization.CultureInfo.CurrentCulture));
    }

    // Une mesure d'avant qui ne date pas du depart n'est ni une machine occupee ni une
    // faute d'instrument ; la phrase le dit, et dit que le prochain passage par DrDXT le reglera.
    [Fact]
    public void Une_mesure_d_avant_perimee_a_sa_propre_phrase()
    {
        var vm = MemoryAbViewModel.From(Verdict(hasVerdict: false,
            noVerdictReason: DrDxt.Measurement.Memory.MemoryBaselineService.AvantPerime), Localize)!;

        vm.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictStale);
        vm.Lines.Should().BeEmpty();
    }

    [Fact]
    public void Un_jeu_lance_au_retour_a_sa_propre_phrase()
    {
        var vm = MemoryAbViewModel.From(Verdict(hasVerdict: false,
            noVerdictReason: DrDxt.Measurement.Memory.MemoryBaselineService.JeuLance), Localize)!;

        vm.Conclusion.Should().StartWith(MemoryAbStringKeys.NoVerdictGame);
        vm.Lines.Should().BeEmpty();
    }
}
