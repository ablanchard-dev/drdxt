using DrDxt.Application.DependencyInjection;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Sa machine
/// a déjà tout ce que DrDXT sait poser (7 lus sur 7). Ici, un Windows 11 sorti de boîte, bureau Intel + NVIDIA, avec les
/// défauts de Windows et du pilote : Mode Jeu allumé, accélération du pointeur allumée, enregistrement en arrière-plan
/// éteint, « optimisations pour les jeux en fenêtre » jamais réglées, plan Équilibré, boost par défaut, mode NVIDIA
/// « Normal », fichier d'échange en place, rien d'écrit par DrDXT. Compte PRUDENT : HAGS et la fréquence d'écran varient
/// d'une machine à l'autre, ils sont mis « déjà bons ». Ce test fixe ce qu'Optimiser pose au premier clic sans jeu
/// ouvert, et ce qu'il essaie en jeu ensuite.
/// </summary>
public sealed class WindowsSortiDeBoiteTests
{
    private const string Equilibre = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private static IReadOnlyList<ITweak> VraisLeviers()
    {
        // Sorti de boîte : aucune des valeurs qu'un joueur ou DrDXT écrit n'existe encore dans le registre.
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(Arg.Any<RegistryHiveKind>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Result<uint?>.Ok(null));
        registre.ReadString(Arg.Any<RegistryHiveKind>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Result<string?>.Ok(null));
        registre.ReadMultiString(Arg.Any<RegistryHiveKind>(), Arg.Any<string>(), Arg.Any<string>()).Returns(Result<string?>.Ok(null));
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDiagnosticsLog>());
        services.AddSingleton(Substitute.For<IBenchmarkRunner>());
        services.AddSingleton(registre);
        services.AddDrDxtApplication();
        return services.BuildServiceProvider().GetRequiredService<ITweakCatalog>().All().ToList();
    }

    private static (HardwareInventory, SettingsState) SortiDeBoite()
    {
        var hw = MachinesEnregistreesTests.Charger("nvidia-rtx3060-i5-12400f").Inventaire;
        var etat = new SettingsState(
            XmpExpo: TriState.Off, ReBar: TriState.Unknown, PowerPlanGuid: Equilibre, Hags: TriState.On, GameMode: TriState.On,
            ParkedCoreIndexes: [], Vrr: TriState.Off, VrrCapableDisplay: TriState.On, PagefileConfigured: TriState.On,
            MemoryConfiguredMhz: 2133, DisplayRefresh: new RefreshHeadroom(TriState.Off, 144, 144),
            NvidiaPreferMaxPerf: TriState.Off, HagsEffective: TriState.On,
            ModeDeBoost: new ModeDeBoostLu(Guid.Parse(Equilibre), 2, 2), AccelerationSouris: TriState.On,
            // Un i5-12400F livré à sa puissance de base (65 W) sous une pointe de 117 W (Intel), sans verrou, aucune partie
            // encore relevée : la machine sort de sa boîte.
            LimitePuissance: new LimiteDePuissanceLue(65, 117, 65, false, CopieMmioPl1W: 0));
        return (hw, etat);
    }

    [Fact]
    public void Au_premier_clic_sans_jeu_ouvert_Optimiser_pose_deux_reglages_et_en_garde_deux_pour_le_jeu()
    {
        var (hw, etat) = SortiDeBoite();
        var ouverts = VraisLeviers().Where(t => t.AppliesTo(hw, etat)).Select(t => t.Id).ToList();
        var diagnostic = DiagnosticDeScan.Construire(null, null, null, null);

        var sansJeu = JugementApply.Juger(diagnostic, ouverts, jeuEnCours: false);
        sansJeu.RienATenter.Should().BeFalse("un Windows sorti de boîte a toujours quelque chose à poser");
        sansJeu.AuRedemarrage.Should().BeEquivalentTo(["latency.mouse_acceleration_off", "latency.vrrFlipModelOn"],
            "l'accélération du pointeur et les optimisations pour les jeux en fenêtre sont les défauts de Windows");

        var enJeu = JugementApply.Juger(diagnostic, ouverts, jeuEnCours: true);
        enJeu.LotAChaud.Select(c => c.Id).Should().BeEquivalentTo(["latency.prefer_max_perf", "gpu.power_limit"],
            "le mode NVIDIA et la limite de puissance de la carte se jugent en jeu, une partie avec, une sans");
        // Sans aucune partie relevée, la chaleur du processeur n'est pas connue : sa limite ne monte pas, et c'est dit.
        var pl1 = VraisLeviers().Single(t => t.Id == "fixit.cpu_power_limit");
        ouverts.Should().NotContain(pl1.Id);
        pl1.EtatNonLu(etat).Should().BeTrue();
    }

    [Fact]
    public void Ce_qui_est_deja_bon_sur_ce_Windows_n_est_pas_propose()
    {
        var (hw, etat) = SortiDeBoite();
        var ouverts = VraisLeviers().Where(t => t.AppliesTo(hw, etat)).Select(t => t.Id).ToList();

        ouverts.Should().NotContain(["latency.gameModeOn", "latency.gameDvrBackgroundOff", "fixit.sane_pagefile", "fixit.mode_boost",
            "latency.hagsOn", "fixit.ecran_frequence", "fixit.x3d_ccd"], "DrDXT ne touche pas à ce qui est déjà à sa place");
    }
}
