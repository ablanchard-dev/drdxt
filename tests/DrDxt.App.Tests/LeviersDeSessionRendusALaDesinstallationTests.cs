using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Desinstallation;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Les leviers de la session Windows (HKCU) posés par « Optimiser » sont rendus à la
// désinstallation.
//
// Lu sur ma machine avant la première vraie désinstallation : « Optimiser » avait posé
// `VRROptimizeEnable=1`, et `%ProgramData%\DrDxt` ne contenait aucun manifeste. Le mapper n'inscrivait
// que les leviers que le service réaffirme au démarrage ; les leviers HKCU (VRR, mode Jeu, Game DVR) en sont
// exclus à raison (le service SYSTEM écrirait la mauvaise ruche), et leur ORIGINE l'était avec eux. Or la
// désinstallation ne relit que ce manifeste : elle aurait laissé le réglage en place, quand le README promet
// qu'elle rend ce que DrDXT a posé.
//
// Le chemin traversé est le vrai : le vrai levier capture et pose, le vrai mapper inscrit en `RevertOnly`
// (rendu à la désinstallation, jamais réaffirmé), une vraie clé de machine signe dans un dossier temporaire,
// RenduALaDesinstallation relit et rend. Seuls le registre et l'écriture sont des doublures.
// -----------------------------------------------------------------------
public sealed class LeviersDeSessionRendusALaDesinstallationTests : IDisposable
{
    private const string PrefsPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string PrefsName = "DirectXUserGlobalSettings";
    private const string DvrPath = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameBarPath = @"Software\Microsoft\GameBar";

    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "drdxt-leviers-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* dossier temporaire */ }
    }

    private static (IRegistryReader Registre, EcritureFactice Eleve) Doublures()
    {
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(default, default!, default!).ReturnsForAnyArgs(Result<uint?>.Ok(null));
        return (registre, new EcritureFactice());
    }

    private static ApplyReport Garde(TweakSnapshot s)
        => new(new List<ApplyOutcome> { new(s.TweakId, Applied: true, AutoReverted: false, s, Error: null) }, Verdict: null);

    private async Task<RapportDuRendu> DesinstallerAsync(IReadOnlyList<AppliedTweak> revertOnly, LectureFactice machine,
        EcritureFactice ecriture)
    {
        var commit = await new MachineManifestCommit(_racine, appliquerAcl: false, new SystemClock())
            .ExecuterAsync(new DesiredState(Array.Empty<AppliedTweak>(), revertOnly), repartir: false);
        commit.IsOk.Should().BeTrue();
        var verificateur = MachineManifestKey.ChargerVerificateur(_racine, appliquerAcl: false);
        verificateur.IsOk.Should().BeTrue();

        ecriture.Machine = machine;
        return await new RenduALaDesinstallation(
            new SignedDesiredStateStore(new ProgramDataManifestStorage(_racine, applyAcl: false), signer: null,
                verificateur.Value!, new DefaultReconcilabilityPolicy(), new SystemClock()),
            machine, ecriture).ExecuterAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Le_VRR_pose_par_Optimiser_est_rendu_a_sa_chaine_d_origine()
    {
        // L'état réel de ma machine avant « Optimiser ».
        const string origine = "SwapEffectUpgradeEnable=1;";
        var (registre, eleve) = Doublures();
        registre.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, PrefsName).Returns(Result<string?>.Ok(origine));
        var pose = await new VrrFlipModelOnTweak(registre, eleve).ApplyAsync();
        pose.IsOk.Should().BeTrue();
        var ecrite = eleve.Recues.OfType<SetRegistryValueOp>().Single().Value;

        var entrees = new AppliedTweakManifestMapper().ToRevertOnly(Garde(pose.Value!));

        entrees.Should().ContainSingle().Which.TweakId.Should().Be("latency.vrrFlipModelOn");
        var desinstallation = new EcritureFactice();
        var rapport = await DesinstallerAsync(entrees,
            new LectureFactice { [$"{RegistryHiveKind.CurrentUser}|{PrefsPath}|{PrefsName}"] = ecrite }, desinstallation);

        rapport.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Rendu);
        desinstallation.Recues.OfType<SetRegistryValueOp>().Should().ContainSingle()
            .Which.Value.Should().Be(origine, "la chaîne d'avant DrDXT, entière");
    }

    [Fact]
    public async Task Un_VRR_change_a_la_main_depuis_n_est_pas_touche()
    {
        var (registre, eleve) = Doublures();
        registre.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, PrefsName)
            .Returns(Result<string?>.Ok("SwapEffectUpgradeEnable=1;"));
        var pose = await new VrrFlipModelOnTweak(registre, eleve).ApplyAsync();
        var entrees = new AppliedTweakManifestMapper().ToRevertOnly(Garde(pose.Value!));

        var desinstallation = new EcritureFactice();
        var rapport = await DesinstallerAsync(entrees,
            new LectureFactice { [$"{RegistryHiveKind.CurrentUser}|{PrefsPath}|{PrefsName}"] = "AutoHDREnable=1;" },
            desinstallation);

        rapport.Lignes.Should().HaveCount(1);
        rapport.Lignes[0].Issue.Should().Be(IssueDuRendu.Respecte);
        desinstallation.Recues.Should().BeEmpty();
    }

    // Auto HDR vit dans la même chaîne, et le joueur le règle lui-même. Basculé depuis la pose, la chaîne entière ne vaut plus
    // celle de DrDXT : jugée en entier, elle serait respectée et les clés de DrDXT resteraient pour toujours. Seules elles se
    // jugent et reprennent leur origine ; l'Auto HDR du joueur reste.
    [Fact]
    public async Task Un_Auto_HDR_bascule_depuis_n_empeche_pas_de_rendre_les_cles_de_DrDXT()
    {
        var (registre, eleve) = Doublures();
        registre.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, PrefsName)
            .Returns(Result<string?>.Ok("SwapEffectUpgradeEnable=1;AutoHDREnable=0"));
        var pose = await new VrrFlipModelOnTweak(registre, eleve).ApplyAsync();
        var entrees = new AppliedTweakManifestMapper().ToRevertOnly(Garde(pose.Value!));

        var desinstallation = new EcritureFactice();
        var rapport = await DesinstallerAsync(entrees, new LectureFactice
        {
            [$"{RegistryHiveKind.CurrentUser}|{PrefsPath}|{PrefsName}"] = "SwapEffectUpgradeEnable=1;AutoHDREnable=1;VRROptimizeEnable=1",
        }, desinstallation);

        rapport.Lignes.Should().ContainSingle().Which.Issue.Should().Be(IssueDuRendu.Rendu);
        desinstallation.Recues.OfType<SetRegistryValueOp>().Should().ContainSingle()
            .Which.Value.Should().Be("AutoHDREnable=1;SwapEffectUpgradeEnable=1", "le VRR de DrDXT part, l'Auto HDR du joueur reste");
    }

    [Fact]
    public async Task Game_DVR_et_mode_Jeu_absents_avant_DrDXT_sont_retires()
    {
        var (registre, eleve) = Doublures();
        var dvr = await new GameDvrBackgroundOffTweak(registre, eleve).ApplyAsync();
        var jeu = await new GameModeOnTweak(registre, eleve).ApplyAsync();
        dvr.IsOk.Should().BeTrue();
        jeu.IsOk.Should().BeTrue();

        var entrees = new AppliedTweakManifestMapper().ToRevertOnly(new ApplyReport(new List<ApplyOutcome>
        {
            new(dvr.Value!.TweakId, true, false, dvr.Value, null),
            new(jeu.Value!.TweakId, true, false, jeu.Value, null),
        }, Verdict: null));

        var machine = new LectureFactice();
        foreach (var op in eleve.Recues.OfType<SetRegistryValueOp>())
            machine[$"{op.Hive}|{op.Path}|{op.Name}"] = op.Value;
        var desinstallation = new EcritureFactice();
        var rapport = await DesinstallerAsync(entrees, machine, desinstallation);

        rapport.Lignes.Should().OnlyContain(l => l.Issue == IssueDuRendu.Rendu);
        desinstallation.Recues.OfType<DeleteRegistryValueOp>().Select(d => d.Name).Should().BeEquivalentTo(
            new[] { "HistoricalCaptureEnabled", "AllowAutoGameMode", "AutoGameModeEnabled" },
            "aucune des trois valeurs n'existait avant DrDXT");
    }

    // Un levier annulé restait au manifeste (la fusion ne retire rien, et le retrait
    // cherchait l'identifiant de l'App parmi ceux du service). Posé à nouveau plus tard, il gardait sa PREMIÈRE
    // origine : la désinstallation écrivait un passé que l'utilisateur avait déjà quitté.
    [Fact]
    public async Task Annule_change_a_la_main_puis_repose_la_desinstallation_rend_la_derniere_origine()
    {
        static AppliedTweak Vrr(string origine, string pose) => new("latency.vrrFlipModelOn",
            PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, PrefsName,
                RegistryValueType.String, pose)).Value!,
            PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, PrefsName,
                RegistryValueType.String, origine)).Value!, DateTimeOffset.UtcNow);
        var commit = new MachineManifestCommit(_racine, appliquerAcl: false, new SystemClock());

        (await commit.ExecuterAsync(new DesiredState(Array.Empty<AppliedTweak>(), new[] { Vrr("A", "B") }), repartir: false))
            .IsOk.Should().BeTrue();
        (await commit.ExecuterAsync(new DesiredState(Array.Empty<AppliedTweak>()), repartir: false,
            retirer: new[] { "latency.vrrFlipModelOn" })).IsOk.Should().BeTrue();
        var rapport = await DesinstallerAsync(new[] { Vrr("C", "D") },
            new LectureFactice { [$"{RegistryHiveKind.CurrentUser}|{PrefsPath}|{PrefsName}"] = "D" },
            new EcritureFactice());

        rapport.Lignes.Should().HaveCount(1);
        rapport.Lignes[0].Detail.Should().Contain("valeur C", "C est ce que l'utilisateur avait choisi avant la 2e pose");
    }

    // Le même retrait côté service : HAGS annulé ne doit plus être réaffirmé au démarrage.
    [Fact]
    public async Task Un_levier_annule_n_est_plus_reaffirme_par_le_service()
    {
        var hags = new AppliedTweak("os.hags", "2", PrivilegedOpCodec.Serialize(new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode",
            RegistryValueType.DWord, "1")).Value!, DateTimeOffset.UtcNow);
        var commit = new MachineManifestCommit(_racine, appliquerAcl: false, new SystemClock());
        (await commit.ExecuterAsync(new DesiredState(new[] { hags }), repartir: false)).IsOk.Should().BeTrue();

        (await commit.ExecuterAsync(new DesiredState(Array.Empty<AppliedTweak>()), repartir: false,
            retirer: new[] { "latency.hagsOn", "os.hags" })).IsOk.Should().BeTrue();

        var relu = await new SignedDesiredStateStore(new ProgramDataManifestStorage(_racine, applyAcl: false), signer: null,
            MachineManifestKey.ChargerVerificateur(_racine, appliquerAcl: false).Value!, new DefaultReconcilabilityPolicy(),
            new SystemClock()).LoadAsync();
        relu.IsOk.Should().BeTrue();
        relu.Value!.Tweaks.Select(t => t.TweakId).Should().NotContain("os.hags");
    }

    [Fact]
    public void Ce_que_le_service_reaffirme_ou_que_la_desinstallation_ne_sait_pas_rendre_n_y_entre_pas()
    {
        static ApplyOutcome Garde(string id, string origine)
            => new(id, Applied: true, AutoReverted: false, new TweakSnapshot(id, origine), Error: null);
        var hags = PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "1")).Value!;
        var dvr = PrivilegedOpCodec.Serialize(new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, DvrPath,
            "HistoricalCaptureEnabled")).Value!;
        var vrr = PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, PrefsName,
            RegistryValueType.String, "SwapEffectUpgradeEnable=1;")).Value!;
        var vrrPose = PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, PrefsName,
            RegistryValueType.String, "SwapEffectUpgradeEnable=1;VRROptimizeEnable=1")).Value!;
        var pagefile = PrivilegedOpCodec.Serialize(new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles")).Value!;

        var entrees = new AppliedTweakManifestMapper().ToRevertOnly(new ApplyReport(new List<ApplyOutcome>
        {
            Garde("latency.hagsOn", hags),                       // déjà au manifeste par ToDesiredTweaks
            Garde("gpu.undervolt", "{\"offset\":-50}"),          // la désinstallation n'écrit jamais la carte
            new("latency.gameDvrBackgroundOff", Applied: true, AutoReverted: true,
                new TweakSnapshot("latency.gameDvrBackgroundOff", dvr), Error: null), // déjà défait
            Garde("fixit.sane_pagefile", pagefile),              // sans valeur posée : jamais rendable
            new("latency.vrrFlipModelOn", Applied: true, AutoReverted: false,
                new TweakSnapshot("latency.vrrFlipModelOn", vrr, vrrPose), Error: null), // témoin : celui-là entre
            // Une limite de puissance gardée, avec la valeur posée : rendue à la désinstallation.
            new("gpu.power_limit", Applied: true, AutoReverted: false, new TweakSnapshot("gpu.power_limit",
                PrivilegedOpCodec.Serialize(new ApplyNvApiPowerLimitOp(0, 100)).Value!,
                PrivilegedOpCodec.Serialize(new ApplyNvApiPowerLimitOp(0, 90)).Value!), Error: null),
        }, Verdict: null));

        entrees.Select(e => e.TweakId).Should().Equal("latency.vrrFlipModelOn", "gpu.power_limit");
    }

    private static string Cle(PrivilegedOp op) => op switch
    {
        SetRegistryValueOp s => $"{s.Hive}|{s.Path}|{s.Name}",
        DeleteRegistryValueOp d => $"{d.Hive}|{d.Path}|{d.Name}",
        _ => "?",
    };

    private sealed class LectureFactice : Dictionary<string, string?>, ILectureDesCibles
    {
        public Result<string?> Lire(PrivilegedOp ecriture)
            => Result<string?>.Ok(TryGetValue(Cle(ecriture), out var v) ? v : null);

        public Result<bool> CibleExiste(PrivilegedOp ecriture) => Result<bool>.Ok(true);
    }

    // Une écriture acceptée se voit à la relecture, comme sur la vraie machine : le rendu n'est dit fait que relu.
    private sealed class EcritureFactice : IElevatedClient
    {
        public List<PrivilegedOp> Recues { get; } = new();
        public LectureFactice? Machine { get; set; }

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Recues.Add(op);
            switch (op)
            {
                case DeleteRegistryValueOp when Machine is not null: Machine[Cle(op)] = null; break; // relue « absente »
                case SetRegistryValueOp s when Machine is not null: Machine[Cle(op)] = s.Value; break;
                case RunServiceControlOp { Action: ServiceAction.Stop } when Machine is not null: Machine[Cle(op)] = "stopped"; break;
                case RunServiceControlOp { Action: ServiceAction.Start } when Machine is not null: Machine[Cle(op)] = "running"; break;
            }
            return Task.FromResult(Result.Ok());
        }
    }
    // Un essai sur le fichier d'échange inscrit son origine au manifeste comme les autres : c'est le rendu de désinstallation
    // qui refuse d'écrire une origine qui coupe le fichier d'échange, quel que soit le chemin qui l'a inscrite.
    [Fact]
    public void Un_essai_sur_le_fichier_d_echange_inscrit_son_origine()
    {
        const string Memoire = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
        var origine = PrivilegedOpCodec.Serialize(new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, Memoire, "PagingFiles")).Value!;
        var pose = PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Memoire, "PagingFiles",
            RegistryValueType.MultiString, @"?:\pagefile.sys")).Value!;
        var snapshot = new TweakSnapshot("fixit.sane_pagefile", origine, pose);

        new AppliedTweakManifestMapper().ToRevertOnly(new ApplyReport([new ApplyOutcome(snapshot.TweakId, true, false, snapshot, null)], null),
                pourUnEssai: true)
            .Should().HaveCount(1).And.Contain(e => e.TweakId == "fixit.sane_pagefile");
    }
}
