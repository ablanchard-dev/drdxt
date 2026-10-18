using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// La vérification de la machine essaie chaque levier de sa liste : posé, relu, rendu, relu. Un levier absent de la liste
// n'est jamais vérifié, et l'App ne peut jamais le dire « vérifié ici ».
public sealed class ValidationDeLaMachineLeviersTests : IDisposable
{
    private readonly string _journal = Path.Combine(Path.GetTempPath(), $"drdxt-vm-leviers-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_journal); } catch (IOException) { } }

    private static readonly Guid Plan = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");

    private static readonly HardwareInventory Pc = new(new CpuInfo("Core i7-14700F", "Intel", 20, 28, false, 1),
        [new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "32.0.16.1742", TriState.On)],
        [new MemoryModule("PN", "K", 3200, 3200)], new MotherboardInfo("M", "B"), new BiosInfo("1", null, true), ChassisType.Desktop);

    private static readonly SettingsState Base = new(TriState.On, TriState.On, Plan.ToString(), TriState.On, TriState.On, [], TriState.On);

    // Le plan d'alimentation tel que le helper l'écrit et que la relecture le relit.
    private sealed class Alimentation : IPowerPlanReader
    {
        public uint SansFil { get; set; } = 2;
        public Result<Guid> GetActivePowerPlan() => Result<Guid>.Ok(Plan);
        public Result<EconomieSansFilLue> LireEconomieSansFil(Guid plan) => Result<EconomieSansFilLue>.Ok(new EconomieSansFilLue(plan, SansFil));
    }

    // Le registre tel que le helper l'écrit et que la relecture le relit (DWORD seulement).
    private sealed class Registre : IRegistryReader
    {
        public Dictionary<string, uint> Valeurs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name)
            => Result<uint?>.Ok(Valeurs.TryGetValue(name, out var v) ? v : null);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
    }

    private sealed class Helper(Alimentation? alimentation = null, Registre? registre = null) : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is SetWirelessPowerSavingOp w && w.PlanGuid == Plan && alimentation is not null) alimentation.SansFil = w.AcValue;
            if (op is SetRegistryValueOp { Kind: RegistryValueType.DWord } r && registre is not null) registre.Valeurs[r.Name] = uint.Parse(r.Value);
            return Task.FromResult(Result.Ok());
        }
    }

    private async Task<ValidationComplete.Resultat> Valider(ITweak levier, SettingsState etat, IPowerPlanReader alimentation,
        IRegistryReader? registre = null)
    {
        var catalogue = Substitute.For<ITweakCatalog>();
        catalogue.Get(Arg.Any<string>()).Returns(Result<ITweak>.Fail("ABSENT", "hors du test"));
        catalogue.Get(levier.Id).Returns(Result<ITweak>.Ok(levier));
        var detecteur = Substitute.For<IHardwareDetector>();
        detecteur.DetectAsync().Returns(Result<HardwareInventory>.Ok(Pc));
        var lecteur = Substitute.For<ISettingsStateReader>();
        lecteur.ReadAsync(Arg.Any<HardwareInventory>()).Returns(Result<SettingsState>.Ok(etat));

        return await new ValidationDeLaMachine(catalogue, detecteur, lecteur, registre ?? Substitute.For<IRegistryReader>(), Substitute.For<INvApi>(),
                alimentation, Substitute.For<IAdlxGpuTuning>(), Substitute.For<IAdlx>(), Substitute.For<INvApiGpuTuning>(), () => null)
            .ValiderAsync(new FichierRevertEnAttente(_journal));
    }

    [Fact]
    public async Task La_carte_sans_fil_est_posee_relue_rendue_relue()
    {
        var alimentation = new Alimentation();
        var wifi = new NetworkAdapterState("Wi-Fi", "Intel Wi-Fi 6E AX211", IsWired: false, TriState.Unknown, TriState.Unknown, TriState.Unknown);
        var etat = Base with { NetworkAdapters = [wifi], EconomieSansFil = new EconomieSansFilLue(Plan, 2) };

        var r = await Valider(new EconomieSansFilTweak(alimentation, new Helper(alimentation: alimentation)), etat, alimentation);

        r.ValidesIds.Should().Contain("fixit.wifi_performances");
        alimentation.SansFil.Should().Be(2u, "rendue à son origine");
    }

    // Heures d'activité de 15 h à 8 h (la machine de référence), réglées à la main, et une partie de 10 h à 11 h hors d'elles.
    [Fact]
    public async Task Les_heures_d_activite_sont_posees_relues_rendues_relues()
    {
        var registre = new Registre();
        (registre.Valeurs[HeuresActives.NomDebut], registre.Valeurs[HeuresActives.NomFin], registre.Valeurs[HeuresActives.NomAutomatique]) = (15, 8, 0);
        IReadOnlyList<PlageDeJeu> parties = [new PlageDeJeu(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 6, 10, 50, 0, TimeSpan.Zero))];
        var etat = Base with { HeuresActives = new HeuresActivesLues(15, 8, false), HeuresDeJeu = parties };

        var r = await Valider(new HeuresActivesTweak(registre, new Helper(registre: registre), () => parties, TimeZoneInfo.Utc),
            etat, new Alimentation(), registre);

        r.ValidesIds.Should().Contain("fixit.heures_actives");
        (registre.Valeurs[HeuresActives.NomDebut], registre.Valeurs[HeuresActives.NomFin]).Should().Be((15u, 8u), "rendues à leur origine");
    }
}
