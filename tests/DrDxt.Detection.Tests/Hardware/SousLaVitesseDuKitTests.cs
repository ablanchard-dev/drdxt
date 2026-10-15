using DrDxt.Core;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>
/// Remarque du joueur : « a pas vu que dans mon BIOS mon vrai profil XMP pas activé ».
///
/// Sa mémoire (kit prévu pour 4000 MT/s) tourne à 3800. Le produit la classait « XMP actif » et se
/// taisait. Le fait mesuré est simple : la vitesse réglée est sous celle du kit, donc le profil du
/// kit n'est pas appliqué tel quel. Le constat le dit, sans chiffre de gain et sans pousser à
/// remonter : une vitesse abaissée sert souvent à stabiliser une machine.
/// </summary>
public sealed class SousLaVitesseDuKitTests
{
    private const string Cle = "Finding.Memory.SousLaVitesseDuKit";

    private static HardwareInventory Machine(int configuree, int kit, ChassisType chassis = ChassisType.Desktop) => new(
        new CpuInfo("Intel Core i7-14700F", "GenuineIntel", 20, 28, false, 1),
        Array.Empty<GpuInfo>(),
        new[]
        {
            new MemoryModule("F4-4000C18-32GTES", "G.Skill", configuree, kit, SmbiosMemoryType: 26, Ranks: 2,
                ConfiguredVoltageMv: 1350, DeviceLocator: "Controller0-DIMMA2"),
            new MemoryModule("F4-4000C18-32GTES", "G.Skill", configuree, kit, SmbiosMemoryType: 26, Ranks: 2,
                ConfiguredVoltageMv: 1350, DeviceLocator: "Controller1-DIMMB2"),
        },
        new MotherboardInfo("MSI", "PRO B760-P WIFI DDR4"),
        new BiosInfo("1.A0", DateTimeOffset.UnixEpoch, true),
        chassis);

    private static SettingsState Etat(TriState xmp) => new(
        xmp, TriState.Unknown, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        TriState.On, TriState.On, Array.Empty<int>(), TriState.Unknown);

    [Fact]
    public void La_machine_de_reference_a_3800_sur_un_kit_4000_le_dit()
    {
        var f = ScoringRules.Evaluate(Machine(3800, 4000), Etat(TriState.On)).Single(x => x.TitleKey == Cle);

        f.Severity.Should().Be(Severity.Info, "une vitesse abaissée n'est pas une faute : elle stabilise souvent");
        f.EstimatedGainPct.Should().BeNull("aucun gain n'est mesuré pour 200 MT/s de plus");
    }

    // Pas « Si des arrêts brutaux sont relevés sur ce PC » sous une carte qui en compte dix : quand ils sont
    // relevés, la variante l'affirme. Et deux constats orange à la suite, « au-dessus de JEDEC » puis « sous la
    // vitesse du kit », passeraient pour contradictoires. Avec des arrêts relevés, un seul constat dit les trois faits ensemble.
    [Fact]
    public void Avec_des_arrets_brutaux_releves_un_seul_constat_dit_les_trois_faits()
    {
        var etat = Etat(TriState.On) with { UncleanShutdowns = new[] { DateTimeOffset.UtcNow.AddDays(-3) } };
        var memoire = ScoringRules.Evaluate(Machine(3800, 4000), etat).Where(x => x.Category == "Memory").Select(x => x.TitleKey).ToList();

        memoire.Should().Equal("Finding.Memory.XmpOnSousLeKitAvecArrets");
        ScoringRules.Evaluate(Machine(3800, 4000), Etat(TriState.On)).Select(x => x.TitleKey)
            .Should().Contain(Cle, "sans arrêt relevé, le texte au conditionnel reste juste");
    }

    [Fact]
    public void A_la_vitesse_du_kit_avec_des_arrets_le_constat_d_origine_reste_seul()
    {
        var etat = Etat(TriState.On) with { UncleanShutdowns = new[] { DateTimeOffset.UtcNow.AddDays(-3) } };
        ScoringRules.Evaluate(Machine(4000, 4000), etat).Where(x => x.Category == "Memory").Select(x => x.TitleKey)
            .Should().Equal("Finding.Memory.XmpOnWithUncleanShutdowns");
    }

    [Fact]
    public void A_la_vitesse_du_kit_rien_n_est_dit() =>
        ScoringRules.Evaluate(Machine(4000, 4000), Etat(TriState.On)).Should().NotContain(x => x.TitleKey == Cle);

    [Fact]
    public void Sous_le_standard_JEDEC_c_est_un_autre_constat() =>
        ScoringRules.Evaluate(Machine(2133, 4000), Etat(TriState.Off)).Should().NotContain(x => x.TitleKey == Cle);

    [Fact]
    public void Vitesses_non_lues_rien_n_est_dit() =>
        ScoringRules.Evaluate(Machine(0, 0), Etat(TriState.Unknown)).Should().NotContain(x => x.TitleKey == Cle);

    [Fact]
    public void Un_portable_dont_le_BIOS_est_verrouille_n_a_pas_ce_conseil() =>
        ScoringRules.Evaluate(Machine(3800, 4000, ChassisType.Laptop), Etat(TriState.On)).Should().NotContain(x => x.TitleKey == Cle);
}
