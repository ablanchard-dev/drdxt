using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Les lignes du scan disent en mots ce qu'un nombre ou un code ne dit pas, chaque fois d'après une
/// page Microsoft relue.
///
/// « Mode de boost : 2 » ne dit rien. La table vient de learn.microsoft.com (PERFBOOSTMODE) : 3 = 1,
/// 4 = 2, et 2 n'est « agressif » qu'en CPPC non autonome ; pas de valeurs 5 ou 6.
/// </summary>
public sealed class LignesDuScanLisiblesTests
{
    [Theory]
    [InlineData(0u, "0 (désactivé)")]
    [InlineData(1u, "1 (activé)")]
    [InlineData(3u, "3 (activé)")]
    [InlineData(2u, "2 (activé, agressif si Windows choisit la fréquence)")]
    [InlineData(4u, "4 (activé, agressif si Windows choisit la fréquence)")]
    [InlineData(5u, "5")]   // hors de la table publiée : le nombre seul, rien d'inventé
    public void Le_mode_de_boost_dit_ce_que_la_documentation_dit(uint valeur, string attendu)
        => RepairAuditService.Lisible("mode de boost", valeur, "").Should().Be(attendu);

    [Fact]
    public void La_preference_energie_performance_dit_son_sens()
        => RepairAuditService.Lisible("preference energie/perf", 5, "").Should().Be("5 (0 = performance, 100 = économie)");

    // Bug Check 0x144 (Microsoft) : 0x1009 = commande xHCI non aboutie après abandon.
    [Fact]
    public void Un_incident_USB3_0x1009_dit_sa_cause_publiee()
        => RepairAuditService.CauseUsb3(0x144, "1009").Should()
            .Contain("contrôleur USB 3").And.Contain("abandon sur délai");

    [Theory]
    [InlineData(0x144u, "1001", "contrôleur USB 3")]
    [InlineData(0x144u, "3003", "concentrateur USB 3")]
    [InlineData(0x144u, "1", "")]      // hors des vidages à chaud publiés : rien d'inventé
    [InlineData(0x193u, "1009", "")]   // autre code : rien
    [InlineData(0x144u, null, "")]
    public void Seules_les_familles_publiees_sont_nommees(uint code, string? p, string attendu)
    {
        var phrase = RepairAuditService.CauseUsb3(code, p);
        if (attendu.Length == 0) phrase.Should().BeEmpty();
        else phrase.Should().Contain(attendu).And.NotContain("abandon sur délai");
    }

    // La liste « sans geste proposé » n'affiche que la valeur : la famille doit y être.
    [Theory]
    [InlineData(0x144u, "1009", ", contrôleur USB 3 (vidage à chaud)")]
    [InlineData(0x144u, "3000", ", concentrateur USB 3 (vidage à chaud)")]
    [InlineData(0x144u, "1", "")]
    [InlineData(0x193u, "80e", "")]
    public void La_famille_USB3_entre_dans_la_valeur_affichee(uint code, string p, string attendu)
        => RepairAuditService.FamilleUsb3(code, p).Should().Be(attendu);

    // Photo (processeur) : « Minimum processeur : 100% ». En français l'unité se sépare du nombre ;
    // le reste de l'écran écrit déjà « 0 % », « 30,0 % ».
    [Fact]
    public void Les_autres_reglages_gardent_leur_unite_separee_du_nombre()
        => RepairAuditService.Lisible("minimum processeur", 100, "%").Should().Be("100 %");

    [Fact]
    public void Sans_unite_le_nombre_reste_seul()
        => RepairAuditService.Lisible("autre", 3, "").Should().Be("3");
}
