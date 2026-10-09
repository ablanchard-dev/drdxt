using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Scoring;

/// <summary>
/// Le cablage de la mesure memoire dans le scan. Sans lui, le banc serait un instrument que le
/// produit n'appelle pas -- exactement le defaut qu'il vient reparer.
/// </summary>
public class MeasuredMemoryClosesTheUnknownTests
{
    // Deux barrettes dont la carte ne nomme pas les emplacements : `MemoryTopologyAdvisor` ne
    // peut rien conclure et le produit affiche « topologie illisible ». C'est notre lacune.
    private static HardwareInventory CarteQuiNeNommeRien() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        Array.Empty<GpuInfo>(),
        new[]
        {
            new MemoryModule("PN", "Kingston", 3800, 4000, DeviceLocator: null),
            new MemoryModule("PN", "Kingston", 3800, 4000, DeviceLocator: null),
        },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    private static SettingsState Etat(int canauxProuves = 0) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On,
        MemoryProvenChannels: canauxProuves);

    [Fact]
    public void Sans_mesure_le_produit_avoue_qu_il_ne_sait_pas_lire_la_topologie()
    {
        ScoringRules.Evaluate(CarteQuiNeNommeRien(), Etat())
            .Should().Contain(f => f.TitleKey == "Finding.Memory.ChannelTopologyUnknown");
    }

    [Fact]
    public void Deux_canaux_PROUVES_par_la_mesure_ferment_cet_aveu()
    {
        // Le debit a depasse le plafond theorique d'un canal : la topologie n'est plus inconnue,
        // elle est mesuree. Continuer a dire « je ne sais pas » serait faux -- et le client lit
        // cette ligne comme un trou de notre couverture.
        ScoringRules.Evaluate(CarteQuiNeNommeRien(), Etat(canauxProuves: 2))
            .Should().NotContain(f => f.TitleKey == "Finding.Memory.ChannelTopologyUnknown");
    }

    [Fact]
    public void Une_mesure_qui_ne_prouve_qu_UN_canal_ne_ferme_RIEN()
    {
        // 1 veut dire « au moins un », c'est-a-dire rien de prouve : un seul fil d'execution ne
        // sature pas forcement deux canaux. L'aveu doit rester.
        ScoringRules.Evaluate(CarteQuiNeNommeRien(), Etat(canauxProuves: 1))
            .Should().Contain(f => f.TitleKey == "Finding.Memory.ChannelTopologyUnknown");
    }

    // Deux barrettes dont la carte nomme les emplacements, mais A L'identique : l'heuristique
    // conclut au simple canal et dit au client d'ouvrir son boitier.
    private static HardwareInventory CarteQuiNommePareil() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        Array.Empty<GpuInfo>(),
        new[]
        {
            new MemoryModule("PN", "Kingston", 3800, 4000, DeviceLocator: "Controller0-DIMMA1"),
            new MemoryModule("PN", "Kingston", 3800, 4000, DeviceLocator: "Controller0-DIMMA2"),
        },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    [Fact]
    public void Sans_mesure_le_produit_conclut_au_simple_canal_et_fait_ouvrir_le_boitier()
    {
        ScoringRules.Evaluate(CarteQuiNommePareil(), Etat())
            .Should().Contain(f => f.TitleKey == "Finding.Memory.SingleChannelPopulation");
    }

    [Fact]
    public void Deux_canaux_PROUVES_demontent_le_conseil_d_ouvrir_le_boitier()
    {
        // Le defaut que ce test ferme : la mesure ne faisait taire que l'aveu « je ne sais pas »
        // (Info, inoffensif) et laissait passer le conseil « tes deux barrettes sont sur le meme
        // canal » (Minor) -- fonde sur la meme heuristique de chaines, et dementi par la mesure.
        // Le client demontait sa machine pour rien.
        ScoringRules.Evaluate(CarteQuiNommePareil(), Etat(canauxProuves: 2))
            .Should().NotContain(f => f.TitleKey == "Finding.Memory.SingleChannelPopulation");
    }

    [Fact]
    public void Une_SEULE_barrette_reste_signalee_meme_avec_deux_canaux_prouves()
    {
        // Le contre-controle. Une seule barrette est le simple canal, aucune chaine n'a besoin
        // d'etre comprise pour le dire -- et un debit eleve ne le contredit pas. Faire taire ce
        // constat-la serait echanger un faux positif contre un faux negatif.
        var uneSeule = CarteQuiNommePareil() with
        {
            Memory = new[] { new MemoryModule("PN", "Kingston", 3800, 4000, DeviceLocator: "Controller0-DIMMA1") },
        };

        ScoringRules.Evaluate(uneSeule, Etat(canauxProuves: 2))
            .Should().Contain(f => f.TitleKey == "Finding.Memory.SingleChannelOneModule");
    }
}
