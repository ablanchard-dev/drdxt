using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// Ce lecteur ne doit jamais dire « QUELQU'UN A MODIFIE CECI ».
///
/// La page Microsoft du MMCSS documente la SEMANTIQUE de chaque valeur et ne publie aucun defaut —
/// ni pour `SystemResponsiveness`, ni pour les champs de la tache `Games`. Tout le lecteur repose
/// donc sur un contournement : affirmer qu'une valeur est sans EFFET ne demande pas de savoir d'ou
/// elle vient.
///
/// La regression que ces tests empechent est precise : qu'un futur lot reintroduise un jugement
/// d'ecart (« different du defaut ») dans un lecteur qui n'a pas de defaut a comparer. C'est deja
/// arrive une fois dans ce lot meme — l'audit classait `SystemResponsiveness = 20` en
/// `ExpectedDefault`, une affirmation que rien ne soutenait.
/// </summary>
public class MmcssReaderTests
{
    /// <summary>La lecture reelle sur cette machine. Elle EXECUTE le lecteur : un test qui se
    /// contenterait d'un registre simule ne dirait rien de ce que Windows expose vraiment.</summary>
    private static IReadOnlyList<MmcssConstat> Lu => new MmcssReader().Lire();

    [Fact]
    public void LES_TROIS_CHAMPS_QUE_MICROSOFT_DECLARE_INUTILISES_SORTENT_EN_SANS_EFFET()
    {
        // Le service rendu. `GPU Priority`, `SFIO Priority` et `Clock Rate` sont recopies dans
        // des centaines de guides « gaming ». La documentation dit « not yet used », « not used »,
        // et « this guarantee was removed » depuis Windows 7. Les classer autrement laisserait
        // croire qu'y toucher rapporte quelque chose.
        foreach (var nom in new[] { "GPU Priority", "SFIO Priority", "Clock Rate" })
        {
            var c = Lu.SingleOrDefault(x => x.Nom == nom);
            c.Should().NotBeNull($"« {nom} » doit etre lu sur cette machine");
            c!.Effet.Should().Be(MmcssEffet.SansEffet);
        }
    }

    [Fact]
    public void AUCUN_CONSTAT_NE_PRETEND_CONNAITRE_UN_DEFAUT()
    {
        // La garde centrale. Aucune phrase de ce lecteur ne doit affirmer qu'une valeur est
        // celle d'origine, ni qu'elle en devie : la source ne le permet pas. Le jour ou quelqu'un
        // trouvera une source qui publie les defauts, ce test tombera — et la discussion aura lieu
        // au lieu que l'affirmation se glisse dans un diff.
        foreach (var c in Lu)
            c.Phrase.Should().NotContainAny("par défaut,", "valeur d'origine", "a été modifie",
                "quelqu'un l'a change");
    }

    [Fact]
    public void UNE_VALEUR_ABSENTE_RESSORT_EN_NON_LU_ET_JAMAIS_EN_CONFORME()
    {
        // « absent » n'est pas « a 20 ». Si un jour `SystemResponsiveness` disparait du registre,
        // le lecteur doit le dire au lieu de supposer le defaut qu'il ne connait pas.
        var c = Lu.Single(x => x.Nom == "SystemResponsiveness");

        if (c.Valeur is null) c.Effet.Should().Be(MmcssEffet.NonLu);
        else c.Effet.Should().BeOneOf(MmcssEffet.Agit, MmcssEffet.SansEffet, MmcssEffet.Desactive);
    }

    [Fact]
    public void CHAQUE_PHRASE_PORTE_DE_QUOI_LA_VERIFIER()
    {
        // Une affirmation sourcee dont la source ne voyage pas avec elle est invérifiable six mois
        // plus tard. La page est citee une fois, en constante, et le rapport la colle a chaque
        // ligne — ce qui evite qu'une phrase migre sans son origine.
        MmcssReader.Source.Should().Contain("learn.microsoft.com");
        Lu.Should().NotBeEmpty();
    }

    // « Values below 10 and above 100 are clamped to 20 » : 150 ne réserve pas 150 %, il est ramené à 20.
    [Theory]
    [InlineData(0, MmcssEffet.SansEffet)]
    [InlineData(150, MmcssEffet.SansEffet)]
    [InlineData(100, MmcssEffet.Desactive)]
    [InlineData(20, MmcssEffet.Agit)]
    [InlineData(null, MmcssEffet.NonLu)]
    public void Hors_de_10_a_100_la_valeur_est_ramenee_a_20(int? valeur, MmcssEffet effet)
        => MmcssReader.Reactivite(valeur).Effet.Should().Be(effet);

    // Au-dessus de 100, ce n'est pas le tweak « = 0 » des guides : la phrase le dit à part.
    [Fact]
    public void Au_dessus_de_100_la_phrase_ne_parle_pas_du_tweak_a_zero()
    {
        var phrase = MmcssReader.Reactivite(150).Phrase;
        phrase.Should().Be(DrDxt.Core.Docteur.T("MM.AuDessusDeCent", 150));
        phrase.Should().Contain("20").And.NotContain("= 0").And.NotContain("MM.", "témoin : la clé existe");
        MmcssReader.Reactivite(0).Phrase.Should().Be(DrDxt.Core.Docteur.T("MM.SousDix", 0));
    }
}
