using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Le magasin de marqueur de changement BIOS en attente : écriture atomique, lecture à échec propre
/// (absent, corrompu, E/S), et fidélité de l'aller-retour. Le marqueur est un fichier OS seulement,
/// jamais une écriture BIOS. Les horodatages viennent d'une horloge injectée, jamais DateTime.Now.
/// </summary>
public class PendingBiosChangeStoreTests
{
    // ── Une horloge fixe pour vérifier l'horodatage persisté, pas l'heure murale. ──
    private sealed class FixedBiosClock(DateTimeOffset now) : IBiosClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static readonly DateTimeOffset Now =
        new(2026, 6, 25, 10, 30, 0, TimeSpan.Zero);

    private static PendingBiosChange SampleChange(IBiosClock clock) => new(
        Targets: new[]
        {
            // ObservedBefore renseigne : sans lui, « round trips EVERY field » etait un nom
            // qui mentait -- le champ etait jete a l'ecriture et personne ne le voyait.
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On, ObservedBefore: 2133),
            new PendingSettingTarget(FirmwareSettingKind.ReBar, TriState.On),
        },
        CreatedUtc: clock.UtcNow,
        MachineId: "machine-abc");

    // ── Fichier de marqueur en mémoire : capture les octets, permet une sonde d'écriture déchirée et
    //    une faute d'E/S forcée, pour tester la logique du magasin sans vrai disque. ──
    private sealed class FakeMarkerFile : IBiosMarkerFile
    {
        public string? Content { get; set; }
        public bool FailIo { get; set; }
        public int Writes { get; private set; }

        public Result WriteAtomic(string json)
        {
            if (FailIo) return Result.Fail(ErrorCodes.BiosPendingChangeIo, "forced");
            // Atomique par contrat : le contenu publié bascule en une seule affectation.
            Content = json;
            Writes++;
            return Result.Ok();
        }

        public Result<string?> Read()
            => FailIo
                ? Result<string?>.Fail(ErrorCodes.BiosPendingChangeIo, "forced")
                : Result<string?>.Ok(Content);

        public Result Delete()
        {
            if (FailIo) return Result.Fail(ErrorCodes.BiosPendingChangeIo, "forced");
            Content = null;
            return Result.Ok();
        }
    }

    [Fact]
    public void Save_then_Load_round_trips_every_field()
    {
        var clock = new FixedBiosClock(Now);
        var file = new FakeMarkerFile();
        var store = new PendingBiosChangeStore(file);

        var change = SampleChange(clock);
        store.Save(change).IsOk.Should().BeTrue();

        var loaded = store.Load();
        loaded.IsOk.Should().BeTrue(loaded.Error?.Message);
        loaded.Value.Should().NotBeNull();
        loaded.Value!.MachineId.Should().Be("machine-abc");
        loaded.Value.CreatedUtc.Should().Be(Now);
        loaded.Value.Targets.Should().HaveCount(2);
        loaded.Value.Targets.Should().ContainEquivalentOf(
            new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On, ObservedBefore: 2133));
        loaded.Value.Targets.Should().ContainEquivalentOf(
            new PendingSettingTarget(FirmwareSettingKind.ReBar, TriState.On));
    }

    [Fact]
    public void Load_when_absent_is_ok_null_nothing_pending()
    {
        var store = new PendingBiosChangeStore(new FakeMarkerFile());

        var loaded = store.Load();

        loaded.IsOk.Should().BeTrue();
        loaded.Value.Should().BeNull();
    }

    [Fact]
    public void Load_corrupt_json_fails_clean_without_throwing()
    {
        var file = new FakeMarkerFile { Content = "{ this is not valid json" };
        var store = new PendingBiosChangeStore(file);

        var loaded = store.Load();

        loaded.IsOk.Should().BeFalse();
        loaded.Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeCorrupt);
    }

    [Fact]
    public void Load_marker_with_no_targets_is_corrupt()
    {
        var file = new FakeMarkerFile
        {
            Content = """{ "Targets": [], "CreatedUtc": "2026-06-25T10:30:00+00:00", "MachineId": "m" }""",
        };
        var store = new PendingBiosChangeStore(file);

        store.Load().Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeCorrupt);
    }

    [Fact]
    public void Load_marker_with_unknown_enum_is_corrupt()
    {
        // Énumérations en chaîne → un nom de réglage inconnu fait échouer la lecture proprement.
        var file = new FakeMarkerFile
        {
            Content = """
            { "Targets": [ { "Setting": "TotallyMadeUp", "ExpectedState": "On" } ],
              "CreatedUtc": "2026-06-25T10:30:00+00:00", "MachineId": "m" }
            """,
        };
        var store = new PendingBiosChangeStore(file);

        store.Load().Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeCorrupt);
    }

    [Fact]
    public void Save_rejects_a_change_with_an_unknown_expectation()
    {
        var store = new PendingBiosChangeStore(new FakeMarkerFile());
        var bad = new PendingBiosChange(
            new[] { new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.Unknown) },
            Now, "m");

        var r = store.Save(bad);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeCorrupt);
    }

    [Fact]
    public void Save_rejects_a_change_with_empty_machine_id()
    {
        var store = new PendingBiosChangeStore(new FakeMarkerFile());
        var bad = new PendingBiosChange(
            new[] { new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On) },
            Now, "   ");

        store.Save(bad).Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeCorrupt);
    }

    [Fact]
    public void Read_io_fault_fails_clean()
    {
        var store = new PendingBiosChangeStore(new FakeMarkerFile { FailIo = true });

        store.Load().Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeIo);
    }

    [Fact]
    public void Clear_removes_the_marker_and_is_idempotent()
    {
        var clock = new FixedBiosClock(Now);
        var file = new FakeMarkerFile();
        var store = new PendingBiosChangeStore(file);
        store.Save(SampleChange(clock));

        store.Clear().IsOk.Should().BeTrue();
        store.Load().Value.Should().BeNull();
        // Idempotent : effacer de nouveau quand c'est déjà absent reste Ok.
        store.Clear().IsOk.Should().BeTrue();
    }

    // ── Vrai système de fichiers : atomicité et vrai aller-retour par du JSON réel sur disque. ──

    [Fact]
    public void Real_filesystem_round_trip_and_atomic_publish()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DrDxtBiosTest_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sub", "pending-bios-change.json");
        try
        {
            var clock = new FixedBiosClock(Now);
            var store = new PendingBiosChangeStore(new FileSystemBiosMarkerFile(path));

            store.Save(SampleChange(clock)).IsOk.Should().BeTrue();

            // Publication atomique : le fichier durable existe, le fichier temporaire ne traîne pas.
            File.Exists(path).Should().BeTrue();
            File.Exists(path + ".tmp").Should().BeFalse();

            var loaded = store.Load();
            loaded.IsOk.Should().BeTrue(loaded.Error?.Message);
            loaded.Value!.CreatedUtc.Should().Be(Now);
            loaded.Value.Targets.Should().HaveCount(2);

            store.Clear().IsOk.Should().BeTrue();
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Real_filesystem_corrupt_file_fails_clean()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DrDxtBiosTest_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "pending-bios-change.json");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "<<< not json >>>");
            var store = new PendingBiosChangeStore(new FileSystemBiosMarkerFile(path));

            store.Load().Error!.Value.Code.Should().Be(ErrorCodes.BiosPendingChangeCorrupt);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DefaultPath_is_under_local_app_data_drdxt()
    {
        var p = PendingBiosChangeStore.DefaultPath();
        p.Should().Contain("DrDXT");
        p.Should().EndWith("pending-bios-change.json");
    }

    [Fact]
    public void The_measured_baseline_SURVIVES_the_disk_round_trip()
    {
        // Le test qui manquait, et son absence rendait la boucle INERTE en production.
        // Le DTO disque n'avait pas ce champ : la grandeur mesuree etait jetee a l'ecriture et
        // reconstruite a null a la relecture. Comme BiosLoopVerifier ne voit les cibles que par
        // Load(), le juge par delta ne se declenchait jamais apres un redemarrage -- le seul
        // moment ou la boucle tourne. Toute la suite delta etait verte parce qu'elle injecte la
        // cible par FakeStore et ne passe jamais par la serialisation.
        var store = new PendingBiosChangeStore(new FakeMarkerFile());

        store.Save(new PendingBiosChange(
            new[] { new PendingSettingTarget(FirmwareSettingKind.XmpExpo, TriState.On, ObservedBefore: 2133) },
            Now, "machine-abc")).IsOk.Should().BeTrue();

        var loaded = store.Load();

        loaded.Value!.Targets.Single().ObservedBefore.Should().Be(2133);
    }

    [Fact]
    public void A_marker_written_without_a_baseline_reloads_as_null_not_as_zero()
    {
        // Compatibilite ascendante : un marqueur ecrit par la version d'avant se relit avec
        // null, et le juge retombe sur la comparaison TriState. Jamais 0 -- qui serait lu comme
        // une mesure valant zero et produirait un delta fabrique.
        var store = new PendingBiosChangeStore(new FakeMarkerFile());

        store.Save(new PendingBiosChange(
            new[] { new PendingSettingTarget(FirmwareSettingKind.ReBar, TriState.On) },
            Now, "machine-abc")).IsOk.Should().BeTrue();

        store.Load().Value!.Targets.Single().ObservedBefore.Should().BeNull();
    }

    // Le marqueur vit sous %LOCALAPPDATA%, ecrivable par tout le compte, et il est relu au
    // retour de Windows. Un contenu abime ne fait jamais lever, et ne se lit jamais comme un changement.
    public static TheoryData<string> Abimes() => new()
    {
        "",
        "null",
        "{",
        "[]",
        "{\"Targets\":null,\"CreatedUtc\":\"2026-09-26T00:00:00Z\",\"MachineId\":\"m\"}",
        "{\"Targets\":[null],\"CreatedUtc\":\"2026-09-26T00:00:00Z\",\"MachineId\":\"m\"}",
        "{\"Targets\":[{\"Setting\":999,\"ExpectedState\":1}],\"CreatedUtc\":\"2026-09-26T00:00:00Z\",\"MachineId\":\"m\"}",
        "{\"Targets\":[{\"Setting\":\"Rien\",\"ExpectedState\":1}],\"CreatedUtc\":\"2026-09-26T00:00:00Z\",\"MachineId\":\"m\"}",
        "{\"Targets\":[{\"Setting\":0,\"ExpectedState\":99999999999}],\"CreatedUtc\":\"2026-09-26T00:00:00Z\",\"MachineId\":\"m\"}",
        "{\"Targets\":[{\"Setting\":0,\"ExpectedState\":1}],\"CreatedUtc\":\"jamais\",\"MachineId\":\"m\"}",
        "{\"Targets\":[{\"Setting\":0,\"ExpectedState\":1}],\"CreatedUtc\":\"0001-01-01T00:00:00Z\",\"MachineId\":\"m\"}",
        "{\"Targets\":[{\"Setting\":0,\"ExpectedState\":1}],\"CreatedUtc\":\"2026-09-26T00:00:00Z\",\"MachineId\":\"\"}",
        new string('[', 5000) + new string(']', 5000),
    };

    [Theory]
    [MemberData(nameof(Abimes))]
    public void Un_marqueur_abime_ne_leve_jamais_et_ne_se_lit_jamais_comme_un_changement(string contenu)
    {
        var store = new PendingBiosChangeStore(new FakeMarkerFile { Content = contenu });

        var charger = () => store.Load();

        charger.Should().NotThrow();
        var lu = charger();
        (lu.IsOk && lu.Value is not null).Should().BeFalse();
    }
}
