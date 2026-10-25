using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// L'Instant Replay de NVIDIA, lu dans les réglages de l'overlay
// (forme relevée sur la machine de référence, ShareSettings.json du 07/07/2026).
public sealed class EnregistrementNvidiaTests
{
    private const string Allume = """{"settings":{"globalhighlights":{"enabled":true},"video":{"irEnabled":true}}}""";

    [Fact]
    public void Instant_Replay_allume_se_dit_a_regarder_avec_le_moyen_de_le_juger()
    {
        var c = EnregistrementNvidia.Juger(Allume).Should().ContainSingle().Subject;
        c.Domaine.Should().Be("GPU DRIVER");
        c.Statut.Should().Be(RepairStatus.UserChoiceProbable, "c'est un choix du joueur, pas une panne");
        c.Explication.Should().Contain("coûte des images").And.NotContain("Alt+Z", "le geste a sa place à part");
        c.Remede.Should().Contain("1) Dans un jeu, Alt+Z").And.Contain("parties");
    }

    [Theory]
    [InlineData("""{"settings":{"video":{"irEnabled":false}}}""")]
    [InlineData("""{"settings":{"video":{}}}""")]
    [InlineData("pas du json")]
    [InlineData(null)]
    public void Eteint_absent_ou_illisible_ne_dit_rien(string? json)
        => EnregistrementNvidia.Juger(json).Should().BeEmpty();

    [Fact]
    public void Le_fichier_reel_se_lit_sans_erreur_s_il_existe()
    {
        var json = EnregistrementNvidia.LireReglages();
        if (json is null) return; // machine sans overlay NVIDIA
        var act = () => EnregistrementNvidia.Juger(json).ToList();
        act.Should().NotThrow();
    }

    // La valeur visible dit ce qu'il fait, sans chiffre : aucune page NVIDIA à jour ne publie son coût, et un chiffre
    // affiché porte son adresse.
    [Fact]
    public void La_valeur_visible_dit_ce_qu_il_fait_sans_chiffre_sans_source()
        => EnregistrementNvidia.Juger(Allume).Single().ValeurCourante.Should().Contain("en continu").And.NotContain("%");

    // Une forme inattendue vaut « non lu » et ne lève pas : le relevé des autres réglages suivis ne saute plus.
    [Theory]
    [InlineData("[]")]
    [InlineData("""{"settings":1}""")]
    [InlineData("""{"settings":{"video":1}}""")]
    public void Une_forme_inattendue_vaut_non_lu(string json)
        => EnregistrementNvidia.EtatInstantReplay(json).Should().BeNull();

    // Fichier tiers : au-delà du plafond, il n'est pas lu.
    [Fact]
    public void Un_fichier_trop_gros_n_est_pas_lu()
    {
        var f = Path.Combine(Path.GetTempPath(), $"share-{Guid.NewGuid():N}.json");
        File.WriteAllText(f, Allume + new string(' ', (int)DrDxt.Detection.FichierTiers.OctetsMax));
        try
        {
            EnregistrementNvidia.LireReglages(f).Should().BeNull();
        }
        finally { File.Delete(f); }
    }
}
