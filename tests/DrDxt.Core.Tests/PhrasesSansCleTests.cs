using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Des phrases écrites en dur dans le code ne suivaient pas la langue de l'App : du français en anglais (virgule décimale,
// espace avant le deux-points, « Mo »), de l'anglais en français, et des noms d'énumération bruts dans les phrases.
public sealed class PhrasesSansCleTests
{
    private static IDisposable En() => Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
    private static IDisposable Fr() => Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));

    [Fact]
    public void Le_wifi_se_dit_en_anglais()
    {
        using var _ = En();
        var phrase = new QualiteWifi(10, 45, -77, 2437, 6).Phrase();
        phrase.Should().StartWith("signal 45% (-77 dBm)").And.Contain("2.4 GHz").And.NotContain("2,4");
    }

    [Fact]
    public void Le_p95_non_interpretable_se_dit_en_anglais()
    {
        using var _ = En();
        TailStatistic.PourquoiPasInterpretable(5)!.Should().Contain("5").And.NotContain("sous").And.NotContain(" : ");
    }

    [Fact]
    public void Les_caches_L3_se_disent_en_MB()
    {
        using var _ = En();
        var f = CachesL3.Juger([new CachesL3.Cache(32L << 20, [(0, 0)])]).Single();
        f.ValeurCourante.Should().Be("32 MB");
    }

    [Fact]
    public void Le_palier_instable_prend_le_deux_points_de_la_langue()
    {
        using var _ = En();
        RechercheSousTension.Nouvelle(DateTimeOffset.UnixEpoch).Apres(IssuePalier.Instable("x")).Raison
            .Should().MatchRegex(@"^-?\d+ mV: x$");
    }

    [Fact]
    public void L_origine_d_un_reglage_NVIDIA_se_dit_en_francais()
    {
        using var _ = Fr();
        foreach (var (lieu, predefini) in new[] { (0u, 0u), (0u, 1u), (1u, 0u), (2u, 0u), (3u, 0u), (9u, 0u) })
            DrsSettingOrigin.Describe(lieu, predefini, 1).Reason.Should().NotContain("This").And.NotContain(" the ");
    }

    [Fact]
    public void Ni_la_nature_ni_le_statut_ne_s_ecrivent_par_leur_nom_d_enumeration()
    {
        foreach (var langue in new[] { "fr-FR", "en-US" })
        {
            using var _ = Docteur.Portee(CultureInfo.GetCultureInfo(langue));
            foreach (var k in Enum.GetValues<CandidateKind>())
                Docteur.T("AE.Nature", RepairLibelles.Nature(k)).Should().NotContain(k.ToString());
            foreach (var s in Enum.GetValues<RepairStatus>())
                RepairLibelles.Statut(s).Should().NotBe(s.ToString()).And.NotStartWith("RF.");
        }
    }

    [Fact]
    public void Un_changement_de_jugement_se_dit_avec_les_mots_du_statut()
    {
        using var _ = Fr();
        RepairSnapshot Photo(RepairStatus s) => new(DateTimeOffset.Now, ["STABILITE"], [new RepairEtat("STABILITE", "arrets brutaux", "3", s)]);
        var delta = RepairHistory.Comparer(Photo(RepairStatus.Observed), Photo(RepairStatus.Suspicious)).Single();
        delta.Phrase.Should().Contain("suspect").And.NotContain("Suspicious");
    }

    [Fact]
    public void Les_motifs_des_noms_ont_une_limite_de_correspondance()
    {
        var motifs = (System.Collections.IEnumerable)((dynamic)typeof(Docteur)
            .GetField("Motifs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!).Value;
        var lus = motifs.Cast<object>().Select(m => (System.Text.RegularExpressions.Regex)m.GetType().GetField("Item2")!.GetValue(m)!).ToList();
        lus.Should().NotBeEmpty("témoin : les motifs sont lus");
        lus.Should().OnlyContain(r => r.MatchTimeout != System.Text.RegularExpressions.Regex.InfiniteMatchTimeout);
    }
}
