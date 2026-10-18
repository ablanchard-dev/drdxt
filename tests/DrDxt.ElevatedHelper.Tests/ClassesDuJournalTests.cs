using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Journal;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ElevatedHelper.Tests;

// Le journal d'écriture anticipée décide op par op. Une op ajoutée sans décision partirait sans filet, ou serait refusée
// en silence : le test parcourt toutes les ops connues, et confronte chaque op journalisée à la lecture qui doit la relire.
public sealed class ClassesDuJournalTests
{
    private static IEnumerable<Type> ToutesLesOps()
        => typeof(PrivilegedOp).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(PrivilegedOp).IsAssignableFrom(t));

    [Fact]
    public void Chaque_op_privilegiee_a_une_classe_du_journal()
        => ToutesLesOps().Except(ClassesDuJournal.TypesClasses).Select(t => t.Name).Should().BeEmpty();

    [Fact]
    public void La_table_ne_classe_que_des_ops_qui_existent()
        => ClassesDuJournal.TypesClasses.Except(ToutesLesOps()).Should().BeEmpty();

    [Fact]
    public void Chaque_genre_du_reglage_ADLX_a_une_classe()
        => Enum.GetValues<AdlxTuningKind>().Except(ClassesDuJournal.GenresAdlxClasses).Should().BeEmpty();

    // Une op de chaque type journalisé, et de chaque portée ou genre qui se classe à part.
    private static readonly PrivilegedOp[] ExemplesJournalises =
    [
        new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "2"),
        new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance),
        new SetProcessorBoostModeOp(KnownPowerPlans.Balanced, 2),
        new SetWirelessPowerSavingOp(KnownPowerPlans.Balanced, 0),
        new SetCpuPackagePowerLimitOp(1, 2),
        new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start),
        new ApplyNvApiDrsSettingOp(0x007BA09E, 1, OpAllowlist.BaseProfileName),
        new ApplyNvApiDrsSettingOp(0x007BA09E, 1, @"C:\Games\Call of Duty\cod.exe"),
        new ApplyNvApiPowerLimitOp(0, 90),
        new ApplyAdlxTuningOp(0, AdlxTuningKind.PowerLimit, 90),
        new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 1),
        new ApplyAdlxTuningOp(0, AdlxTuningKind.FrameRateTarget, 141),
        new ApplyAdlxTuningOp(0, AdlxTuningKind.VerticalRefresh, 1),
    ];

    public static TheoryData<PrivilegedOp> Journalisees() => new(ExemplesJournalises);

    // Journaliser exige l'image avant : une op journalisée sans lecture serait refusée par le journal, et le levier cassé.
    [Theory]
    [MemberData(nameof(Journalisees))]
    public void Une_op_journalisee_a_une_lecture_de_sa_cible(PrivilegedOp op)
    {
        ClassesDuJournal.De(op).Should().Be(ClasseDuJournal.Journalisee);
        new LectureDesCiblesWin32().Lecteur(op).Should().NotBeNull($"{op} est journalisée sans lecture de sa cible");
    }

    [Fact]
    public void Chaque_type_journalise_a_son_exemple()
        => ClassesDuJournal.TypesClasses.Where(t => ClassesDuJournal.DuType(t) == ClasseDuJournal.Journalisee)
            .Except(ExemplesJournalises.Select(op => op.GetType())).Should().BeEmpty();

    // La sous-tension AMD persiste au redémarrage, et rien ne relit encore sa MaxFreq : elle garde le comportement actuel,
    // jamais un refus qui casserait le levier.
    [Theory]
    [InlineData(AdlxTuningKind.Undervolt)]
    [InlineData(AdlxTuningKind.RemiseUsine)]
    public void Un_genre_ADLX_sans_lecture_attend_son_lecteur(AdlxTuningKind genre)
    {
        var op = new ApplyAdlxTuningOp(0, genre, 2500);
        ClassesDuJournal.De(op).Should().Be(ClasseDuJournal.LecteurAVenir);
        new LectureDesCiblesWin32().Lecteur(op).Should().BeNull();
    }

    // Le ventilateur est rangé volatil parce qu'il ne s'écrit qu'en vitesse fixe par NVML, perdue au redémarrage : une
    // vraie courbe (plusieurs points), qui pourrait persister, n'est jamais exécutée. Si elle le devenait, cette classe
    // serait fausse.
    [Fact]
    public async Task Une_courbe_de_ventilateur_a_plusieurs_points_n_est_jamais_executee()
    {
        var ventilateur = Substitute.For<IVentilateurActions>();
        var executeur = new PrivilegedOpExecutor(Substitute.For<IRegistryActions>(), Substitute.For<IPowerPlanActions>(),
            Substitute.For<IRestorePointActions>(), Substitute.For<IServiceActions>(), Substitute.For<INvApiDrsActions>(),
            Substitute.For<INvApiTuningActions>(), Substitute.For<IADLXTuningActions>(), ventilateur: ventilateur);

        var r = await executeur.ExecuteAsync(new ApplyGpuFanCurveOp(0, [new FanCurvePointDto(40, 45), new FanCurvePointDto(80, 90)]),
            CancellationToken.None);

        r.IsOk.Should().BeFalse();
        ventilateur.DidNotReceiveWithAnyArgs().Poser(default, default);
    }

    // Une valeur de registre journalisée doit se relire quel que soit son type : sinon le journal refuserait l'écriture.
    // Chaque type, avec la valeur que Windows rend pour lui.
    private static readonly IReadOnlyDictionary<RegistryValueType, object> ValeurRendueParWindows = new Dictionary<RegistryValueType, object>
    {
        [RegistryValueType.DWord] = 2,
        [RegistryValueType.QWord] = 2L,
        [RegistryValueType.String] = "AutoHDREnable=2097;",
        [RegistryValueType.MultiString] = new[] { @"D:\pagefile.sys 0 0" },
    };

    [Fact]
    public void Chaque_type_de_valeur_de_registre_se_relit()
        => Enum.GetValues<RegistryValueType>()
            .Where(type => !ValeurRendueParWindows.TryGetValue(type, out var brute) || !LectureDesCiblesWin32.Forme(type, "Valeur", brute).IsOk)
            .Should().BeEmpty("un type de registre écrit sans relecture ferait refuser son écriture par le journal");

    // Le plafond d'une partie est rangé volatil parce que sa pose ne crée que des politiques du magasin actif de Windows, que le
    // redémarrage efface. Une pose persistante rendrait cette classe fausse : ce test tomberait.
    [Fact]
    public void Le_plafond_d_une_partie_ne_pose_que_dans_le_magasin_actif()
    {
        var poses = System.Text.RegularExpressions.Regex.Matches(
            DrDxt.ElevatedHelper.Execution.Win32NetworkQosActions.ScriptDePose("cod.exe", 20_000_000), @"New-NetQosPolicy\s+-[^;]*");

        poses.Should().HaveCount(2, "la politique générale et l'exemption du jeu");
        poses.Select(p => p.Value).Should().OnlyContain(p => p.Contains("-PolicyStore ActiveStore") && !p.Contains("PersistentStore"));
        ClassesDuJournal.De(new ApplyUploadCapOp("cod.exe", 20_000_000)).Should().Be(ClasseDuJournal.Volatile);
    }
}
