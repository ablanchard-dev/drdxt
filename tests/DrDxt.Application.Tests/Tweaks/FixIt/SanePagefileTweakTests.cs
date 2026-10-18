using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Réparation du fichier d'échange désactivé.
//
// Ce que ces tests défendent, dans l'ordre d'importance :
//   * on ne répare que ce qu'on a lu (Unknown ne déclenche rien),
//   * l'original est capturé avant l'écriture, et « valeur absente » n'est pas
//     « chaîne vide » — l'inverse d'un absent est une SUPPRESSION,
//   * la phrase montrée ne promet pas d'images par seconde : c'est de la fiabilité.
// -----------------------------------------------------------------------
public sealed class SanePagefileTweakTests
{
    private const string MmPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    private readonly IRegistryReader _registry = Substitute.For<IRegistryReader>();
    private readonly IElevatedClient _elevated = Substitute.For<IElevatedClient>();

    private SanePagefileTweak Build(string? current = "")
    {
        _registry.ReadMultiString(RegistryHiveKind.LocalMachine, MmPath, "PagingFiles")
                 .Returns(Result<string?>.Ok(current));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Result.Ok()));
        return new SanePagefileTweak(_registry, _elevated);
    }

    private static HardwareInventory Hw() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        new[] { new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "610.88", TriState.On) },
        new[] { new MemoryModule("PN", "G.Skill", 6000, 6000) },
        new MotherboardInfo("MSI", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    private static SettingsState State(TriState pagefile) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On,
        PagefileConfigured: pagefile);

    [Fact]
    public void Identity_and_flags()
    {
        var t = Build();
        t.Id.Should().Be("fixit.sane_pagefile");
        t.Category.Should().Be(TweakCategory.FixIt);
        t.RequiresElevation.Should().BeTrue();
        t.RequiresRestorePoint.Should().BeTrue();
        // La config du fichier d'échange ne s'applique qu'au reboot : un A/B immédiat
        // mesurerait le bruit de session et pourrait l'annuler à tort.
        t.TakesEffectAfterReboot.Should().BeTrue();
    }

    [Fact]
    public void The_sentence_never_promises_frames()
    {
        // C'est de la FIABILITÉ. Vendre ça comme du FPS serait exactement le mensonge
        // que le produit existe pour ne pas faire.
        Build().SummaryKey.Should().Be("tweak.fixit.sane_pagefile.reliability");
        Build().SummaryKey.Should().NotContain("fps");
    }

    [Fact]
    public void Applies_only_when_the_page_file_is_read_as_DISABLED()
    {
        Build().AppliesTo(Hw(), State(TriState.Off)).Should().BeTrue();
        Build().AppliesTo(Hw(), State(TriState.On)).Should().BeFalse();
        // On ne répare pas ce qu'on n'a pas su lire : Unknown ne doit rien déclencher.
        Build().AppliesTo(Hw(), State(TriState.Unknown)).Should().BeFalse();
    }

    [Fact]
    public async Task Writes_the_system_managed_entry()
    {
        var t = Build(current: "");

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeTrue();
        await _elevated.Received().ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Hive == RegistryHiveKind.LocalMachine
                                         && o.Path == MmPath
                                         && o.Name == "PagingFiles"
                                         && o.Kind == RegistryValueType.MultiString
                                         // La valeur EXACTE que Windows écrit en gestion
                                         // automatique — relevée sur une machine réelle, pas
                                         // déduite. La forme « 0 0 » décrit autre chose : une
                                         // taille système sur un lecteur NOMMÉ.
                                         && o.Value == @"?:\pagefile.sys"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_snapshot_carries_the_captured_original_not_a_guess()
    {
        var t = Build(current: @"D:\pagefile.sys 4096 8192");

        var snap = (await t.ApplyAsync()).Value!;

        var inverse = PrivilegedOpCodec.Deserialize(snap.OriginalValueJson);
        inverse.Value.Should().BeOfType<SetRegistryValueOp>()
            .Which.Value.Should().Be(@"D:\pagefile.sys 4096 8192");
    }

    [Fact]
    public async Task An_ABSENT_value_reverts_by_DELETING_not_by_writing_an_empty_string()
    {
        // Le test qui mord. « Valeur absente » et « valeur présente mais vide » sont deux
        // états différents pour Windows. Rejouer une chaîne vide sur une machine où la valeur
        // n'existait pas laisserait une trace que DrDXT a créée — un revert qui ne restaure pas.
        var t = Build(current: null);

        var snap = (await t.ApplyAsync()).Value!;

        var inverse = PrivilegedOpCodec.Deserialize(snap.OriginalValueJson);
        inverse.Value.Should().BeOfType<DeleteRegistryValueOp>()
            .Which.Name.Should().Be("PagingFiles");
    }

    [Fact]
    public async Task Fails_clean_without_writing_when_the_capture_cannot_be_read()
    {
        _registry.ReadMultiString(RegistryHiveKind.LocalMachine, MmPath, "PagingFiles")
                 .Returns(Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "denied"));
        var t = new SanePagefileTweak(_registry, _elevated);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        // Sans original capturé il n'y a pas de retour en arrière possible : on n'écrit pas.
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Surfaces_a_helper_failure_instead_of_claiming_success()
    {
        _registry.ReadMultiString(RegistryHiveKind.LocalMachine, MmPath, "PagingFiles")
                 .Returns(Result<string?>.Ok(""));
        _elevated.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, "refuse")));
        var t = new SanePagefileTweak(_registry, _elevated);

        var r = await t.ApplyAsync();

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
    }

    [Fact]
    public async Task Revert_replays_the_captured_op()
    {
        var t = Build();
        var original = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, MmPath, "PagingFiles",
                                              RegistryValueType.MultiString, @"C:\pagefile.sys 0 0");
        var snap = new TweakSnapshot("fixit.sane_pagefile", PrivilegedOpCodec.Serialize(original).Value!);

        (await t.RevertAsync(snap)).IsOk.Should().BeTrue();

        await _elevated.Received().ExecuteAsync(
            Arg.Is<SetRegistryValueOp>(o => o.Value == @"C:\pagefile.sys 0 0"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revert_rejects_a_malformed_snapshot_without_touching_the_registry()
    {
        var t = Build();

        var r = await t.RevertAsync(new TweakSnapshot("fixit.sane_pagefile", "not-json"));

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.SnapshotInvalid);
        await _elevated.DidNotReceive().ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
    }
    // Sans la valeur posée dans le snapshot, la relecture après pose rendait Unknown : le levier ne pouvait jamais être
    // « validé ». Le snapshot porte la gestion automatique écrite, et la relecture la reconnaît.
    [Fact]
    public async Task Le_snapshot_porte_la_valeur_posee_et_la_relecture_la_reconnait()
    {
        var r = await Build(current: "").ApplyAsync();

        r.IsOk.Should().BeTrue(r.Error?.Message);
        PrivilegedOpCodec.Deserialize(r.Value!.AppliedValueJson).Value.Should().Be(
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, MmPath, "PagingFiles", RegistryValueType.MultiString, @"?:\pagefile.sys"));
        _registry.ReadMultiString(RegistryHiveKind.LocalMachine, MmPath, "PagingFiles").Returns(Result<string?>.Ok(@"?:\pagefile.sys"));
        AppliedStateVerifier.Verifier(r.Value, _registry).Verdict.Should().Be(EtatApplique.Applied);
    }
}
