using System.Text.RegularExpressions;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// =======================================================================
// Un gate qui choisit ses leviers ne garantit que ceux-la.
//
// La premiere phrase du README promet : « il n'applique jamais un levier qu'il ne peut pas
// defaire ». `RevertRestoresStateTests` construit sa propre liste, ecrite a la main : elle ne
// couvre pas tous les leviers que le produit enregistre dans `ApplicationServiceCollectionExtensions`.
// Les oublies seraient souvent des ecritures registre MACHINE qui ne prennent effet qu'au
// redemarrage : celles dont l'inverse est le moins souvent joue pour de vrai. Et sans comparaison
// des deux listes, un levier ajoute demain naitrait HORS du gate, en silence.
//
// Ici : le banc construit tous les leviers, la comparaison avec l'enregistrement du produit est
// DEDUITE du code, et chaque levier fait le tour complet -- appliquer, annuler, machine
// identique octet pour octet.
// =======================================================================
public sealed class ChaqueLevierDuProduitSaitSeDefaireTests
{
    private const string Ecran = @"\\.\DISPLAY1";

    // --- Un socle ou chaque levier a quelque chose a ecrire, et quelque chose a remettre. ---
    private static FakeSystemState Socle()
    {
        var s = new FakeSystemState
        {
            NvApiAvailable = true,
            ActivePowerPlan = KnownPowerPlans.Balanced,
            // Le boost coupé sur le plan actif, son défaut à 2.
            ModeBoostSecteur = 0,
            ModeBoostDefaut = 2,
            NvLowLatencyMode = 0,
            NvVsyncMode = 0x60925292, // PASSIVE, le défaut du pilote
            NvFrameRateLimit = 0,
            PrimaryGameDisplay = new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144,
                MaxRefreshHz: 165, DeviceName: Ecran),
            // Un écran 165 Hz laissé à 144, le mode d'avant enregistré tel quel.
            ModeEcranCourant = new ModeEcran(Ecran, 2560, 1440, 32, 144),
            ModeEcranEnregistre = new ModeEcran(Ecran, 2560, 1440, 32, 144),
        };
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode", 0);
        s.SetDrs(0x00A879CF, 0x60925292);   // VSYNCMODE_ID = PASSIVE, le défaut du pilote
        s.SetDrs(0x10835002, 0);   // FRL_FPS_ID
        s.SetDrs(0x1057EB71, 0);   // PREFERRED_PSTATE_ID — sinon « absent » puis « 0 » differeraient
        // MAX_PRERENDERED_FRAMES, ensemencee a la valeur que la couture rapporte (3).
        //
        // Limite declaree du banc, et elle est reelle : le modele ne distingue pas une
        // feuille DRS absente d'une feuille posee a la valeur par defaut. Sur une vraie
        // machine, ce n'est pas la meme chose -- l'une laisse le pilote decider, l'autre
        // fige un choix utilisateur, et l'annulation « restaure » alors une valeur que
        // personne n'avait jamais ecrite. Ce que le tour complet prouve ici est donc :
        // « la valeur d'avant revient », pas « rien de neuf n'apparait ». Le second
        // demanderait de modeliser l'origine DRS (GetFrameRateLimitOrigin la connait).
        s.SetDrs(0x007ba09e, 3);
        s.SetService("AMD3DVCacheSvc", ServiceState.Stopped);
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable", 0);

        // Les feuilles des quatre leviers « effet au redemarrage », avec une valeur
        // PREEXISTANTE : un inverse de suppression ne suffirait donc pas a restaurer.
        s.SetRegistry(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 1);
        s.SetRegistry(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 0);
        s.SetRegistry(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 0);
        s.SetRegistry(RegistryHiveKind.CurrentUser,
            @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2);
        s.SetRegistry(RegistryHiveKind.CurrentUser,
            @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1);
        s.SetRegistry(RegistryHiveKind.CurrentUser,
            @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 0);
        s.SetRegistry(RegistryHiveKind.CurrentUser,
            @"System\GameConfigStore", "GameDVR_EFSEFeatureFlags", 0);

        // Une chaine posee par WINDOWS (AutoHDR) : l'annulation doit la rendre intacte.
        s.SetRegistryValue(RegistryHiveKind.CurrentUser,
            @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings",
            RegistryValueType.String, "AutoHDREnable=2097;");
        // Et une MULTI-chaine : la configuration du fichier d'echange, desactivee.
        s.SetRegistryValue(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "PagingFiles", RegistryValueType.MultiString, string.Empty);
        // Les heures d'activité relevées sur la machine de référence le 06/10 (15 h à 8 h).
        s.SetRegistry(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomDebut, 15);
        s.SetRegistry(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomFin, 8);
        // Le levier n'écrit qu'en mode manuel lu.
        s.SetRegistry(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomAutomatique, 0);
        return s;
    }

    /// <summary>Le socle, plus l'état qu'un levier vient réparer quand il contredit celui des autres : le plan Économie
    /// n'est quitté que s'il est actif, et le reste du socle part d'Équilibré.</summary>
    private static FakeSystemState SocleDe(string id)
    {
        var s = Socle();
        if (id == "fixit.plan_economie") s.ActivePowerPlan = KnownPowerPlans.PowerSaver;
        if (id == "fixit.pagefile_ssd")
            s.SetRegistryValue(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                "PagingFiles", RegistryValueType.MultiString, @"D:\pagefile.sys 0 0");
        return s;
    }

    /// <summary>La partie relevée sur la machine de référence (cod.exe, 04/10 de 8:23 à 8:44, heure de Paris).</summary>
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.CreateCustomTimeZone("paris-ete", TimeSpan.FromHours(2), "paris-ete", "paris-ete");
    private static IReadOnlyList<PlageDeJeu> PartieDu0410() =>
        [new(new DateTimeOffset(2026, 10, 4, 8, 23, 48, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 10, 4, 8, 44, 3, TimeSpan.FromHours(2)))];

    /// <summary>Les quinze leviers du produit, construits contre les coutures du banc.
    /// Cette liste est la seule ecrite a la main ici — et le test plus bas la confronte a
    /// l'enregistrement reel, donc elle ne peut pas deriver en silence.</summary>
    internal static IReadOnlyList<ITweak> TousLesLeviers(FakeSystemState etat, FakeElevatedClient client)
    {
        var nv = new FakeNvApi(etat);
        var nvTuning = new FakeNvApiGpuTuning(etat);
        var adlx = new FakeAdlx(etat);
        var adlxTuning = new FakeAdlxGpuTuning(etat);
        var reg = new FakeRegistryReader(etat);
        var power = new FakePowerPlanReader(etat);
        var svc = new FakeServiceStateReader(etat);
        var display = new FakeDisplayInfo(etat);
        var cibles = new GpuTuningTargetSource(new GpuTuningTarget(0, -100, 0), new GpuPowerLimitTarget(0, 90));

        return new ITweak[]
        {
            new NvidiaUltraLowLatencyTweak(nv, reg, client),
            new RenderQueueCapTweak(nv, adlx, client),
            new VrrVsyncCapConfigTweak(nv, display, client),
            new HagsOnTweak(reg, client),
            new GameModeOnTweak(reg, client),
            new GameDvrBackgroundOffTweak(reg, client),
            new AccelerationSourisOffTweak(reg, client),
            new VrrFlipModelOnTweak(reg, client),
            new PreferMaxPerfTweak(nv, client),
            new GpuUndervoltTweak(nvTuning, adlxTuning, new StableStabilityTester(), client, cibles),
            new GpuPowerLimitTweak(nvTuning, adlxTuning, client, cibles),
            new GpuFanCurveTweak(nvTuning, adlxTuning, client, cibles, new GpuFanCurveTarget(0,
                new FanCurve(new[] { new FanCurvePoint(40, 35), new FanCurvePoint(80, 70), new FanCurvePoint(90, 90) }))),
            new X3dCcdParkingFixTweak(svc, reg, client),
            new SanePowerPlanTweak(power, client),
            new ModeBoostTweak(power, client),
            new EconomieSansFilTweak(power, client),
            new PlanEconomieTweak(power, client),
            new LimitePuissanceCpuTweak(new FakeCpuPowerLimitReader(etat), client),
            new PlafondThermiqueCpuTweak(new FakeCpuPowerLimitReader(etat, plafondThermique: 50), client),
            new SanePagefileTweak(reg, client),
            new PagefileSurSsdTweak(reg, client),
            new HeuresActivesTweak(reg, client, PartieDu0410, Paris),
            new EcranFrequenceTweak(display, new FakeModeEcran(etat), new ConfirmationQuiGarde()),
        };
    }

    public static TheoryData<string> Leviers()
    {
        var etat = Socle();
        var data = new TheoryData<string>();
        foreach (var t in TousLesLeviers(etat, new FakeElevatedClient(etat)))
            data.Add(t.Id);
        return data;
    }

    // =================== le tour complet, levier par levier ===================

    [Theory]
    [MemberData(nameof(Leviers))]
    public async Task Applique_puis_annule_rend_la_machine_identique(string id)
    {
        var etat = SocleDe(id);
        var client = new FakeElevatedClient(etat);
        var levier = TousLesLeviers(etat, client).Single(t => t.Id == id);

        var avant = etat.Snapshot();
        var applique = await levier.ApplyAsync();
        applique.IsOk.Should().BeTrue($"{id} doit savoir s'appliquer : {applique.Error?.Message}");

        var annule = await levier.RevertAsync(applique.Value!);

        annule.IsOk.Should().BeTrue($"{id} doit savoir se defaire : {annule.Error?.Message}");
        etat.Snapshot().Should().Be(avant,
            $"le README promet qu'aucun levier ne s'applique sans savoir se defaire, et {id} "
            + "laisse la machine differente de son etat d'avant");
    }

    // =================== les deux contrepoids ===================

    [Theory]
    [MemberData(nameof(Leviers))]
    public async Task Un_levier_qui_n_ecrit_rien_ne_prouve_rien_et_doit_le_declarer(string id)
    {
        // Sans ce contrepoids, le tour complet ci-dessus serait vert pour quinze no-ops :
        // ne rien ecrire restaure toujours. On exige donc que l'application CHANGE la
        // machine — sauf pour le seul levier qui, par contrat, n'ecrit jamais.
        var etat = SocleDe(id);
        var client = new FakeElevatedClient(etat);
        var levier = TousLesLeviers(etat, client).Single(t => t.Id == id);

        var avant = etat.Snapshot();
        await levier.ApplyAsync();
        var aEcrit = etat.Snapshot() != avant;

        // Le guide DDU, seul levier qui n'ecrivait rien, est retire ; tous ecrivent desormais.
        aEcrit.Should().BeTrue($"{id} n'a rien ecrit sur le banc : son tour complet est un vert vide");
    }

    [Fact]
    public void Le_banc_construit_TOUS_les_leviers_que_le_produit_enregistre()
    {
        var etat = Socle();
        var auBanc = TousLesLeviers(etat, new FakeElevatedClient(etat))
            .Select(t => t.GetType().Name)
            .ToHashSet(StringComparer.Ordinal);

        var enregistres = LeviersEnregistresParLeProduit();

        enregistres.Should().HaveCountGreaterThan(10, "un crible qui ne lit rien serait vert sur le vide");
        enregistres.Except(auBanc).Should().BeEmpty(
            "ces leviers partent chez les gens sans avoir jamais fait le tour appliquer/annuler "
            + "sur le banc : le gate ne dit rien d'eux");
        auBanc.Except(enregistres).Should().BeEmpty(
            "ces leviers sont testes mais plus enregistres : le banc prouve un produit qui n'existe pas");
    }

    /// <summary>Les leviers que le produit enregistre vraiment, lus dans le code de
    /// l'injection de dependances — pas dans une liste tenue a la main.</summary>
    private static HashSet<string> LeviersEnregistresParLeProduit()
    {
        var racine = new DirectoryInfo(AppContext.BaseDirectory);
        while (racine is not null && !File.Exists(Path.Combine(racine.FullName, "DrDXT.sln")))
            racine = racine.Parent;

        var source = File.ReadAllText(Path.Combine(racine!.FullName, "src", "DrDxt.Application",
            "DependencyInjection", "ApplicationServiceCollectionExtensions.cs"));

        return Regex.Matches(source, @"AddSingleton<ITweak,\s*(\w+)>")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
