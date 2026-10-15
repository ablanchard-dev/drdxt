using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using DrDxt.Profiles;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// `fixit.sane_pagefile` et `latency.prefer_max_perf` étaient enregistrés,
/// testés un par un, et absents de tout profil : « Optimiser » ne les proposait jamais. Ici, les VRAIS leviers passent par
/// les vrais profils et le vrai consommateur du scan : proposés là où l'état lu le justifie, nulle part ailleurs.
/// Sources et limites : research/north-star/leviers-injoignables.md.
/// </summary>
public sealed class LeviersInjoignablesTests
{
    private const string Pagefile = "fixit.sane_pagefile";
    private const string PMax = "latency.prefer_max_perf";
    private const string Ecran = "fixit.ecran_frequence";

    private static ITweakCatalog Catalogue()
    {
        var elevated = Substitute.For<IElevatedClient>();
        ITweak[] vrais = [new SanePagefileTweak(Substitute.For<IRegistryReader>(), elevated),
                          new PreferMaxPerfTweak(Substitute.For<INvApi>(), elevated),
                          new EcranFrequenceTweak(Substitute.For<IDisplayInfo>(), Substitute.For<IModeEcran>(),
                              Substitute.For<IConfirmationEcran>())];
        var c = Substitute.For<ITweakCatalog>();
        c.Get(Arg.Any<string>()).Returns(_ => Result<ITweak>.Fail("TWEAK.NOT_FOUND", "absent du test"));
        foreach (var t in vrais) c.Get(t.Id).Returns(Result<ITweak>.Ok(t));
        c.All().Returns(vrais);
        return c;
    }

    private static IReadOnlyList<string> Plan(HardwareInventory hw, SettingsState st)
        => new ScanProfileConsumer(new ChipProfileMatcher(new ChipProfileCatalog()), Catalogue(),
               new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90)))
           .Consume(hw, st).Plan.TweakIds;

    private static HardwareInventory Pc(string vendeurGpu = "NVIDIA", ChassisType chassis = ChassisType.Desktop) => new(
        new CpuInfo("Intel Core i7-14700F", "GenuineIntel", 20, 28, false, 1),
        [new GpuInfo(vendeurGpu == "NVIDIA" ? "NVIDIA GeForce RTX 4060" : "AMD Radeon RX 7800 XT", vendeurGpu, 8L << 30, "1", TriState.On)],
        [new MemoryModule("p", "m", 3800, 4000)],
        new MotherboardInfo("b", "p"), new BiosInfo("1", null, true), chassis);

    private static SettingsState Etat(TriState pagefile, TriState pmax) => new(
        TriState.On, TriState.On, "g", TriState.On, TriState.On, [], TriState.On,
        PagefileConfigured: pagefile, NvidiaPreferMaxPerf: pmax);

    [Fact]
    public void Sur_un_PC_qui_en_a_besoin_Optimiser_propose_les_deux()
        => Plan(Pc(), Etat(TriState.Off, TriState.Off)).Should().Contain([Pagefile, PMax]);

    [Fact]
    public void Sur_un_PC_deja_sain_ni_l_un_ni_l_autre()
        => Plan(Pc(), Etat(TriState.On, TriState.On)).Should().NotContain(Pagefile).And.NotContain(PMax);

    [Fact]
    public void Non_lu_n_est_pas_un_besoin()
        => Plan(Pc(), Etat(TriState.Unknown, TriState.Unknown)).Should().NotContain(Pagefile).And.NotContain(PMax);

    [Fact]
    public void Sur_AMD_le_fichier_d_echange_oui_l_etat_P_NVIDIA_jamais()
        => Plan(Pc("AMD"), Etat(TriState.Off, TriState.Off)).Should().Contain(Pagefile).And.NotContain(PMax);

    [Fact]
    public void Sur_un_portable_l_etat_P_jamais()
        => Plan(Pc(chassis: ChassisType.Laptop), Etat(TriState.Off, TriState.Off)).Should().NotContain(PMax);

    [Theory]
    [MemberData(nameof(MachinesEnregistreesTests.Noms), MemberType = typeof(MachinesEnregistreesTests))]
    public void Les_machines_enregistrees_ne_recoivent_aucune_proposition_fausse(string nom)
    {
        var m = MachinesEnregistreesTests.Charger(nom);
        var plan = Plan(m.Inventaire, m.Reglages);

        // Les cinq ont un fichier d'échange lu « présent » et un état P non lu : rien à proposer.
        plan.Contains(Pagefile).Should().Be(m.Reglages.PagefileConfigured == TriState.Off);
        plan.Contains(PMax).Should().Be(m.Reglages.NvidiaPreferMaxPerf == TriState.Off
            && m.Inventaire.Chassis == ChassisType.Desktop && m.Inventaire.Gpus.Any(g => g.Vendor == "NVIDIA"));
        // L'écran n'est proposé que s'il est LU sous sa fréquence max ; une fixture sans la lecture n'invente rien.
        plan.Contains(Ecran).Should().Be(m.Reglages.DisplayRefresh?.HasHeadroom == TriState.On);
    }

    [Fact]
    public void Un_ecran_lu_sous_sa_frequence_max_est_propose_un_ecran_au_max_ou_non_lu_non()
    {
        var st = Etat(TriState.On, TriState.On);
        Plan(Pc(), st with { DisplayRefresh = new RefreshHeadroom(TriState.On, 60, 144) }).Should().Contain(Ecran);
        Plan(Pc("AMD"), st with { DisplayRefresh = new RefreshHeadroom(TriState.On, 60, 144) }).Should().Contain(Ecran);
        Plan(Pc(), st with { DisplayRefresh = new RefreshHeadroom(TriState.Off, 144, 144) }).Should().NotContain(Ecran);
        Plan(Pc(), st with { DisplayRefresh = null }).Should().NotContain(Ecran);
    }

    // Le fichier d'échange agit au redémarrage : sa bascule se date et s'affiche sur les parties. 07/10/2026 (revue
    // F-9) : mais il n'est plus relié à un levier, donc jamais retiré sur les images — c'est un levier de fiabilité.
    [Fact]
    public void Le_fichier_d_echange_est_suivi_et_relie_a_son_levier()
    {
        var st = Etat(TriState.On, TriState.Unknown);
        ScanViewModel.EtatsSuivis(st)["memoire.fichier_echange"].Should().Be("on");
        ScanViewModel.EtatsSuivis(st with { PagefileConfigured = TriState.Unknown }).Should().NotContainKey("memoire.fichier_echange");
        ScanViewModel.LevierDuReglage.Should().NotContainKey("memoire.fichier_echange");
    }
}
