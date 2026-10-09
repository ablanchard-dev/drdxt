using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// PHASE 18 F2 — la decision sur HAGS, testee sans carte. Les bits viennent de
/// `D3DKMT_WDDM_2_7_CAPS` : 1 supporte, 2 active, 4 active par defaut.
/// </summary>
public class HagsEffectiveReaderTests
{
    private static TriState Decider(params AdaptateurHags[] a) => D3dkmtHagsReader.Decider(a);

    [Fact]
    public void La_valeur_MESUREE_sur_la_machine_de_reference_rend_On()
        // 10/09/2026 : 0x0B sur l'adaptateur qui affiche (supporte + active + active par defaut).
        => Decider(new AdaptateurHags(Sources: 2, Lu: true, Caps: 0x0B)).Should().Be(TriState.On);

    [Fact]
    public void Supporte_mais_eteint_rend_Off_et_seulement_la_un_candidat_existe()
        => Decider(new AdaptateurHags(2, true, 0x1)).Should().Be(TriState.Off);

    [Fact]
    public void Non_supporte_rend_Unknown_car_on_ne_peut_pas_l_activer()
        => Decider(new AdaptateurHags(2, true, 0x0)).Should().Be(TriState.Unknown);

    [Fact]
    public void Une_requete_qui_ECHOUE_rend_Unknown_jamais_Off()
        => Decider(new AdaptateurHags(2, Lu: false, Caps: 0)).Should().Be(TriState.Unknown);

    [Fact]
    public void Un_adaptateur_SANS_sortie_ne_compte_pas()
        // Un rendu hors ecran (ou un adaptateur logiciel) ne dit rien de ce que le jeu obtient.
        => Decider(new AdaptateurHags(2, true, 0x0B), new AdaptateurHags(0, true, 0x1))
            .Should().Be(TriState.On);

    [Fact]
    public void Deux_adaptateurs_d_affichage_qui_se_CONTREDISENT_rendent_Unknown()
        => Decider(new AdaptateurHags(1, true, 0x3), new AdaptateurHags(1, true, 0x1))
            .Should().Be(TriState.Unknown);

    [Fact]
    public void Rien_d_enumere_rend_Unknown()
        => Decider().Should().Be(TriState.Unknown);
}
