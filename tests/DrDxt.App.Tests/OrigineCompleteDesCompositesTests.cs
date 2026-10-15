using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Desinstallation;
using DrDxt.Reconciler.Manifest;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// L'apply inscrit l'origine complete des composites.
//
// Le chemin Apply n'inscrivait qu'une valeur posee par levier : celle que le reconciliateur reaffirme au
// demarrage (pour X3D, le service). Les deux autres feuilles du levier (CppcEnable, Game Bar) avaient une
// ORIGINE dans le manifeste, mais aucune valeur posee — la desinstallation ne pouvait donc jamais les
// juger rendables. Depuis le tout-ou-rien du lot 6c, cela ne rendait plus « la moitie du levier » : cela
// bloquait le composite entier, pour toujours.
//
// Le chemin traverse ici est le vrai : le vrai levier capture et pose (X3dCcdParkingFixTweak), le vrai
// mapper de l'App inscrit, le manifeste est signe par une vraie cle de machine dans un dossier temporaire,
// relu verify-only, et rendu par RenduALaDesinstallation. Seuls le registre/service/l'ecriture sont des
// doublures en memoire : rien n'est ecrit sur la machine.
// -----------------------------------------------------------------------
public sealed class OrigineCompleteDesCompositesTests : IDisposable
{
    private const string GameBarPath = @"Software\Microsoft\GameBar";

    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "drdxt-origine-composite-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* dossier temporaire */ }
    }

    /// <summary>Le vrai levier X3D applique : service arrete au depart, Game Bar absent.</summary>
    private static async Task<TweakSnapshot> AppliquerLeVraiLevierX3d()
    {
        var service = Substitute.For<IServiceStateReader>();
        service.GetServiceState("AMD3DVCacheSvc").Returns(Result<ServiceState>.Ok(ServiceState.Stopped));
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(RegistryHiveKind.CurrentUser, GameBarPath, "AllowAutoGameMode").Returns(Result<uint?>.Ok(null));
        var eleve = Substitute.For<IElevatedClient>();
        eleve.ExecuteAsync(default!, default).ReturnsForAnyArgs(Result.Ok());

        var applique = await new X3dCcdParkingFixTweak(service, registre, eleve).ApplyAsync();
        applique.IsOk.Should().BeTrue();
        return applique.Value!;
    }

    [Fact]
    public async Task Apres_un_Apply_du_composite_X3D_TOUTES_ses_feuilles_ont_une_origine_inscrite()
    {
        var snapshot = await AppliquerLeVraiLevierX3d();

        var entrees = new AppliedTweakManifestMapper().ToDesiredTweaks(
            new ApplyReport(new List<ApplyOutcome>
            {
                new("fixit.x3d_ccd", Applied: true, AutoReverted: false, snapshot, Error: null),
            }, Verdict: null));

        entrees.Should().ContainSingle();
        var posees = CompositeSnapshotCodec.Decode(entrees[0].PoseesJson!);
        posees.IsOk.Should().BeTrue("le manifeste doit dire ce que DrDXT a pose sur CHAQUE feuille");
        var origines = CompositeSnapshotCodec.Decode(entrees[0].OriginalValueJson).Value!;
        posees.Value!.Count.Should().Be(origines.Count,
            "autant de valeurs posees que d'origines : une feuille sans valeur posee n'est jamais rendable");
    }

    [Fact]
    public async Task Le_composite_X3D_arme_par_l_Apply_est_rendu_ENTIER_a_la_desinstallation()
    {
        var snapshot = await AppliquerLeVraiLevierX3d();
        var entrees = new AppliedTweakManifestMapper().ToDesiredTweaks(
            new ApplyReport(new List<ApplyOutcome>
            {
                new("fixit.x3d_ccd", Applied: true, AutoReverted: false, snapshot, Error: null),
            }, Verdict: null));

        // Le manifeste, signe par une vraie cle de machine (dossier temporaire, ACL non appliquee).
        var commit = await new MachineManifestCommit(_racine, appliquerAcl: false, new SystemClock())
            .ExecuterAsync(new DesiredState(entrees), repartir: false);
        commit.IsOk.Should().BeTrue();

        var verificateur = MachineManifestKey.ChargerVerificateur(_racine, appliquerAcl: false);
        verificateur.IsOk.Should().BeTrue();

        // La machine porte encore ce que DrDXT y a pose : service demarre, Game Bar = 1. CppcEnable
        // n'est plus ecrit.
        var lecture = new LectureFactice
        {
            ["service|AMD3DVCacheSvc"] = "running",
            [$"{RegistryHiveKind.CurrentUser}|{GameBarPath}|AllowAutoGameMode"] = "1",
        };
        var ecriture = new EcritureFactice();

        var rapport = await new RenduALaDesinstallation(
            new SignedDesiredStateStore(new ProgramDataManifestStorage(_racine, applyAcl: false), signer: null,
                verificateur.Value!, new DefaultReconcilabilityPolicy(), new SystemClock()),
            lecture, ecriture).ExecuterAsync(CancellationToken.None);

        rapport.ManifesteNonLu.Should().BeNull();
        ecriture.Recues.Should().HaveCount(2, "les deux feuilles du levier sont rendues, ou aucune");
        ecriture.Recues.Should().Contain(new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Stop));
        ecriture.Recues.OfType<SetRegistryValueOp>().Should().BeEmpty("CppcEnable n'est plus pose");
        ecriture.Recues.OfType<DeleteRegistryValueOp>().Should().ContainSingle()
            .Which.Name.Should().Be("AllowAutoGameMode", "la valeur n'existait pas avant DrDXT");
        rapport.Lignes.Should().OnlyContain(l => l.Issue == IssueDuRendu.Rendu);
    }

    private static string Cle(PrivilegedOp op) => op switch
    {
        SetRegistryValueOp s => $"{s.Hive}|{s.Path}|{s.Name}",
        DeleteRegistryValueOp d => $"{d.Hive}|{d.Path}|{d.Name}",
        SetActivePowerPlanOp => "plan",
        RunServiceControlOp r => "service|" + r.ServiceName,
        ApplyNvApiDrsSettingOp a => "drs|" + a.SettingId,
        _ => "?",
    };

    private sealed class LectureFactice : Dictionary<string, string?>, ILectureDesCibles
    {
        public Result<string?> Lire(PrivilegedOp ecriture)
            => TryGetValue(Cle(ecriture), out var v)
                ? Result<string?>.Ok(v)
                : Result<string?>.Fail(ErrorCodes.IoFailure, "non lu (test)");

        public Result<bool> CibleExiste(PrivilegedOp ecriture) => Result<bool>.Ok(true);
    }

    private sealed class EcritureFactice : IElevatedClient
    {
        public List<PrivilegedOp> Recues { get; } = new();

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Recues.Add(op);
            return Task.FromResult(Result.Ok());
        }
    }
}
