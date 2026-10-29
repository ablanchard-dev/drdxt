using DrDxt.Tests.Shared;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>Ce qui se rend sans clic n'écrase pas un réglage changé depuis.</summary>
public sealed partial class CartesAgirTests
{
    // La garde d'incident rend sans clic, hors du filtre de la liste : un réglage changé depuis par le joueur serait réécrit à
    // la valeur d'avant DrDXT. Il ne l'est pas ; plus rien de DrDXT n'y est, il sort, et c'est dit.
    [Fact]
    public async Task Un_rendu_sans_clic_n_ecrase_pas_un_reglage_change_depuis()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            const string chemin = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
            string Op(string v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, chemin,
                "HistoricalCaptureEnabled", RegistryValueType.DWord, v)).Value!;
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("latency.gameDvrBackgroundOff", Op("1"), Op("0"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            var registre = Substitute.For<DrDxt.Application.Tweaks.Hardware.IRegistryReader>();
            registre.ReadDword(RegistryHiveKind.CurrentUser, chemin, "HistoricalCaptureEnabled").Returns(Result<uint?>.Ok(2u));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers, registreGains: registre);
            var levier = Substitute.For<ITweak>();
            levier.Id.Returns(pose.TweakId);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = _ => levier;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());

            await vm.AnnulerLevierAsync(pose.TweakId, parClic: false);

            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
            leviers.Lire().Should().BeEmpty("plus rien de DrDXT n'y est : il ne se garde plus");
            vm.MessageLeviers.Should().Be("Leviers.ChangeDepuis");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // La limite de puissance de la carte ne se relit pas au registre : la garde d'incident se fie au vérificateur de la pose.
    // Le joueur a mis une autre valeur dans un autre outil : rien n'est réécrit par-dessus, et c'est dit.
    [Fact]
    public async Task Un_rendu_sans_clic_n_ecrase_pas_une_limite_de_carte_changee_depuis()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"annuler-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("gpu.power_limit", InversesDeTest.Pour("gpu.power_limit", "100"), InversesDeTest.Pour("gpu.power_limit", "90"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers,
                registreGains: Substitute.For<DrDxt.Application.Tweaks.Hardware.IRegistryReader>());
            var levier = Substitute.For<ITweak>();
            levier.Id.Returns(pose.TweakId);
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(Result.Ok());
            vm.LevierParId = _ => levier;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.VerifierLaPose = _ => DrDxt.Application.Tweaks.EtatApplique.Drifted;

            await vm.AnnulerLevierAsync(pose.TweakId, parClic: false);

            await levier.DidNotReceive().RevertAsync(Arg.Any<TweakSnapshot>());
            vm.MessageLeviers.Should().Be("Leviers.ChangeDepuis");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }

    // La garde d'incident rend seule un réglage posé avant un arrêt brutal. Rendu mais non relu à l'origine, la ligne ne dit
    // pas « la valeur d'avant est remise » : elle dit que la relecture ne le confirme pas.
    [Theory]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.NotApplied, "Garde.Rendu")]
    [InlineData(DrDxt.Application.Tweaks.EtatApplique.Unknown, "Garde.RenduNonRelu")]
    public async Task La_garde_ne_dit_remis_que_relu_a_l_origine(DrDxt.Application.Tweaks.EtatApplique apres, string dit)
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"garde-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var maintenant = DateTimeOffset.Now;
            var leviers = new DrDxt.Application.Tweaks.JournalDesLeviersAppliques(System.IO.Path.Combine(dossier, "l.json"));
            var pose = new TweakSnapshot("gpu.power_limit", InversesDeTest.Pour("gpu.power_limit", "100"), InversesDeTest.Pour("gpu.power_limit", "90"));
            leviers.Enregistrer(new ApplyReport([new ApplyOutcome(pose.TweakId, true, false, pose, null)], null));
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null, journalLeviers: leviers,
                lireStabilite: () => (null, [new ArretClasse(maintenant.AddHours(-12), FamilleDArret.SansCode)]));
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(System.IO.Path.Combine(dossier, "j.json"));
            vm.NoterPoses([pose.TweakId], maintenant.AddDays(-1));
            var levier = Substitute.For<ITweak>();
            levier.Id.Returns(pose.TweakId);
            var rendu = false;
            levier.RevertAsync(Arg.Any<TweakSnapshot>()).Returns(_ => { rendu = true; return Result.Ok(); });
            vm.LevierParId = _ => levier;
            vm.RetirerDuManifeste = _ => Task.FromResult(Result.Ok());
            vm.VerifierLaPose = _ => rendu ? apres : DrDxt.Application.Tweaks.EtatApplique.Applied;

            var dits = await vm.RetirerCeQuiCouteAsync([]);

            rendu.Should().BeTrue();
            dits.Should().ContainSingle().Which.Should().StartWith(dit);
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }
}
