using DrDxt.App;
using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Reconciler.Desinstallation;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// =======================================================================
// Les invariants de sûreté, pour TOUS les leviers que le produit enregistre (la liste du banc est confrontée à
// l'enregistrement par ChaqueLevierDuProduitSaitSeDefaireTests) : aucun levier ne laisse la machine autrement qu'il
// l'a trouvée.
// =======================================================================
public sealed class InvariantsDesLeviersTests : IDisposable
{
    private readonly string _journal = Path.Combine(Path.GetTempPath(), $"drdxt-invariants-{Guid.NewGuid():N}.json");
    private readonly string _dossierJea = Path.Combine(Path.GetTempPath(), $"drdxt-invariants-jea-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { File.Delete(_journal); } catch (IOException) { }
        try { Directory.Delete(_dossierJea, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static TheoryData<string> Leviers() => ChaqueLevierDuProduitSaitSeDefaireTests.Leviers();

    // Une machine où aucune feuille des leviers n'existe : pas de valeur de registre, pas de réglage DRS. L'origine
    // « absente » est la plus fréquente sur une installation neuve, et la plus facile à mal rendre (une annulation qui
    // écrit le défaut au lieu de supprimer laisse une valeur que personne n'avait posée).
    private static FakeSystemState SansOrigine() => new()
    {
        NvApiAvailable = true,
        ActivePowerPlan = KnownPowerPlans.Balanced,
        PrimaryGameDisplay = new DisplayInfo(IsPrimaryGameDisplay: true, SupportsVrr: true, RefreshHz: 144,
            MaxRefreshHz: 165, DeviceName: @"\\.\DISPLAY1"),
        ModeEcranCourant = new ModeEcran(@"\\.\DISPLAY1", 2560, 1440, 32, 144),
        ModeEcranEnregistre = new ModeEcran(@"\\.\DISPLAY1", 2560, 1440, 32, 144),
    };

    [Theory]
    [MemberData(nameof(Leviers))]
    public async Task Sur_une_origine_absente_appliquer_puis_annuler_ne_laisse_rien(string id)
    {
        var etat = SansOrigine();
        var client = new FakeElevatedClient(etat);
        var levier = ChaqueLevierDuProduitSaitSeDefaireTests.TousLesLeviers(etat, client).Single(t => t.Id == id);
        var avant = etat.Snapshot();

        var applique = await levier.ApplyAsync();
        if (!applique.IsOk)
        {
            // Refuser d'agir sur ce qu'il ne sait pas lire est permis ; laisser une trace ne l'est pas.
            etat.Snapshot().Should().Be(avant, $"{id} a refusé ({applique.Error?.Code}) mais a changé la machine");
            return;
        }
        var annule = await levier.RevertAsync(applique.Value!);

        annule.IsOk.Should().BeTrue($"{id} doit savoir se défaire : {annule.Error?.Message}");
        etat.Snapshot().Should().Be(avant, $"{id} laisse après annulation une valeur qui n'existait pas avant");
    }

    // Une annulation marquée serait rejouée par le helper après un arrêt sec de l'App, et remettrait le levier. Un levier
    // annulé en pleine portée de pose (moteur qui rend un levier, essai, exercice profond) n'en voit donc aucune.
    [Theory]
    [MemberData(nameof(Leviers))]
    public async Task Une_annulation_dans_une_portee_de_pose_part_hors_portee(string id)
    {
        var etat = ChaqueLevierDuProduitSaitSeDefaireTests.SocleDe(id);
        var client = new PorteeVue(new FakeElevatedClient(etat));
        var levier = ChaqueLevierDuProduitSaitSeDefaireTests.TousLesLeviers(etat, client).Single(t => t.Id == id);
        var applique = await levier.ApplyAsync();
        applique.IsOk.Should().BeTrue($"{id} : {applique.Error?.Message}");
        PorteeDePose.Ouvrir();
        client.DansLaPortee.Clear();

        (await levier.RevertAsync(applique.Value!)).IsOk.Should().BeTrue();

        client.DansLaPortee.Should().BeEmpty($"{id} annule dans la portée de pose");
    }

    // Les deux poses Expert de la carte n'ouvrent pas de portée : elles n'écrivent que du volatil (décalage, vitesse du
    // ventilateur), que le helper ne journalise jamais, et leur origine part au journal de l'App avant l'écriture. Une
    // écriture journalisée ajoutée là sans portée ferait rougir ce test.
    [Fact]
    public async Task Les_poses_expert_de_la_carte_n_ecrivent_rien_de_journalise()
    {
        var etat = ChaqueLevierDuProduitSaitSeDefaireTests.SocleDe("gpu.undervolt");
        var client = new ToutAccepte();
        var cibles = new DrDxt.Application.Tweaks.Gpu.GpuTuningTargetSource(new DrDxt.Application.Tweaks.Gpu.GpuTuningTarget(0, 0, 0), new DrDxt.Application.Tweaks.Gpu.GpuPowerLimitTarget(0, 90));
        var expert = new DrDxt.Application.Tweaks.Gpu.ReglageCarteExpert(new DrDxt.Application.Tweaks.Gpu.GpuUndervoltTweak(
            new FakeNvApiGpuTuning(etat), new FakeAdlxGpuTuning(etat), new StableStabilityTester(), client, cibles), cibles,
            new FichierRevertEnAttente(_journal));

        var carte = await expert.PoserAsync(coeurMhz: 75);
        carte.IsOk.Should().BeTrue(carte.Error?.Message);
        await carte.Value!.DisposeAsync();
        File.Delete(_journal);
        var ventilateur = await new DrDxt.Application.Tweaks.Gpu.VentilateurExpert(client, new FichierRevertEnAttente(_journal)).PoserAsync(70);
        ventilateur.IsOk.Should().BeTrue(ventilateur.Error?.Message);
        await ventilateur.Value!.DisposeAsync();

        client.Envoyees.Should().Contain(op => op is ApplyNvApiClockOffsetOp).And.Contain(op => op is ApplyGpuFanCurveOp);
        client.Envoyees.Should().OnlyContain(op =>
            DrDxt.ElevatedHelper.Journal.ClassesDuJournal.De(op) != DrDxt.ElevatedHelper.Journal.ClasseDuJournal.Journalisee);
    }

    // Le helper qui accepte tout (bail et ventilateur compris) : seules les ops envoyées comptent.
    private sealed class ToutAccepte : IElevatedClient
    {
        public readonly List<PrivilegedOp> Envoyees = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) { Envoyees.Add(op); return Task.FromResult(Result.Ok()); }
    }

    private sealed class PorteeVue(IElevatedClient interne) : IElevatedClient
    {
        public readonly List<PrivilegedOp> DansLaPortee = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (PorteeDePose.Courante is not null) DansLaPortee.Add(op);
            return interne.ExecuteAsync(op, ct);
        }
        public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => interne.ReadAsync(op, ct);
    }

    // Le helper n'exécute que ce que son allowlist admet : un levier qui écrit ou rend hors d'elle échouerait chez le
    // joueur à mi-chemin (posé sans pouvoir être rendu), ou viserait une surface que personne n'a revue.
    [Theory]
    [MemberData(nameof(Leviers))]
    public async Task Chaque_ecriture_d_un_levier_pose_et_rendu_passe_l_allowlist_du_helper(string id)
    {
        var etat = ChaqueLevierDuProduitSaitSeDefaireTests.SocleDe(id);
        var client = new FakeElevatedClient(etat);
        var levier = ChaqueLevierDuProduitSaitSeDefaireTests.TousLesLeviers(etat, client).Single(t => t.Id == id);

        var applique = await levier.ApplyAsync();
        applique.IsOk.Should().BeTrue($"{id} doit s'appliquer sur le banc : {applique.Error?.Message}");
        await levier.RevertAsync(applique.Value!);

        var allowlist = new OpAllowlist();
        foreach (var op in client.Writes)
            allowlist.Validate(op).IsOk.Should().BeTrue($"{id} écrit {op.GetType().Name} hors de l'allowlist : {allowlist.Validate(op).Error?.Message}");
    }

    // Le plantage, dans l'ordre réel de TweakCandidate.Apply : le journal « essai en cours », l'écriture, le journal
    // complété par l'origine, puis la levée. Chaque « redémarrage » reconstruit les leviers et relit le même fichier sur
    // la même machine, comme le lancement suivant de l'App (RevertAuDemarrage).
    private string? Redemarrer(FakeSystemState etat)
        => RevertAuDemarrage.Executer(new FichierRevertEnAttente(_journal),
            new TweakCatalog(ChaqueLevierDuProduitSaitSeDefaireTests.TousLesLeviers(etat, new FakeElevatedClient(etat))));

    [Theory]
    [MemberData(nameof(Leviers))]
    public void Tue_avant_d_ecrire_la_machine_reste_intacte_et_le_joueur_est_prevenu(string id)
    {
        var etat = ChaqueLevierDuProduitSaitSeDefaireTests.SocleDe(id);
        var avant = etat.Snapshot();
        new FichierRevertEnAttente(_journal).Poser(id, null).IsOk.Should().BeTrue();

        var phrase = Redemarrer(etat);

        etat.Snapshot().Should().Be(avant);
        phrase.Should().Be(Docteur.T(RevertAuDemarrage.LeviersJournalises.Contains(id) ? "RV.SansOrigineGardee" : "RV.SansOrigine", id));
        new FichierRevertEnAttente(_journal).Lire().Value.Should().BeNull("le journal est levé");
    }

    // Tué entre l'écriture et le journal complété : l'origine n'est pas chez l'App, mais le helper l'a notée avant d'écrire
    // (journal d'écriture anticipée). Son rejeu, l'App morte, la rend. Le démarrage de l'App, qui n'a pas l'origine, prévient
    // toujours le joueur.
    private static readonly DrDxt.ElevatedHelper.Journal.AppelantDuJournal AppTuee = new("S-1-5-21-banc", 4242, new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));

    private sealed class ExecuteurDuBanc(IElevatedClient machine) : IPrivilegedOpExecutor
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) => machine.ExecuteAsync(op, ct);
    }

    /// <summary>L'App vers le helper du banc : la même identité d'appelant pour chaque op, comme le tube.</summary>
    private sealed class TubeDuBanc(IPrivilegedOpExecutor helper) : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
            => helper.ExecuteAsync(op, ct, new DrDxt.ElevatedHelper.Ipc.CallerInfo(AppTuee.Pid));
    }

    // Ce que le rejeu ne rend pas, et pourquoi. Chacun fait rougir ce test dès qu'il est rendu : son entrée part avec la
    // correction.
    private static readonly HashSet<string> PasEncoreRenduParLeRejeu = new(StringComparer.Ordinal)
    {
        "fixit.ecran_frequence", // le mode d'écran s'écrit dans la session, jamais par le helper
        "gpu.undervolt",         // le décalage de fréquence est volatil : jamais journalisé, le bail le rend
        "gpu.fan_curve",         // la courbe du ventilateur est volatile : jamais journalisée, le bail la rend
        "fixit.sane_pagefile",   // une origine qui coupe le fichier d'échange n'est jamais réécrite, même pour rendre
    };

    [Fact]
    public void Les_leviers_journalises_sont_des_leviers_du_produit()
        => RevertAuDemarrage.LeviersJournalises.Should().BeSubsetOf(
            ChaqueLevierDuProduitSaitSeDefaireTests.TousLesLeviers(SansOrigine(), new FakeElevatedClient(SansOrigine())).Select(t => t.Id));

    [Theory]
    [MemberData(nameof(Leviers))]
    public async Task Tue_entre_l_ecriture_et_l_origine_gardee_le_rejeu_du_helper_rend_l_origine(string id)
    {
        var etat = ChaqueLevierDuProduitSaitSeDefaireTests.SocleDe(id);
        var avant = etat.Snapshot();
        var lecture = new CiblesDuBanc(etat);
        var machine = new FakeElevatedClient(etat);
        var jea = new DrDxt.ElevatedHelper.Journal.JournalDEcritureAnticipee(Path.Combine(_dossierJea, "jea.json"));
        var helper = new DrDxt.ElevatedHelper.Journal.ExecuteurJournalise(new ExecuteurDuBanc(machine), jea,
            op => () => lecture.Lire(op), _ => AppTuee, () => DateTimeOffset.UtcNow);
        new FichierRevertEnAttente(_journal).Poser(id, null).IsOk.Should().BeTrue();
        using (PorteeDePose.Ouvrir())
            (await ChaqueLevierDuProduitSaitSeDefaireTests.TousLesLeviers(etat, new DrDxt.App.Platform.ClientDesPoses(new TubeDuBanc(helper)))
                .Single(t => t.Id == id).ApplyAsync()).IsOk.Should().BeTrue();

        var rejeu = await new DrDxt.ElevatedHelper.Journal.RejeuDuJournal(jea, lecture, machine, _ => false, () => AppTuee.Sid)
            .ExecuterAsync(CancellationToken.None);

        rejeu.IsOk.Should().BeTrue(rejeu.Error?.Message);
        // L'App relancée dit juste : « le helper la rendra » seulement pour un levier que le rejeu rend vraiment.
        RevertAuDemarrage.LeviersJournalises.Contains(id).Should().Be(!PasEncoreRenduParLeRejeu.Contains(id),
            $"{id} : la liste des leviers journalisés ne dit plus ce que le rejeu rend");
        Redemarrer(etat).Should().Be(Docteur.T(PasEncoreRenduParLeRejeu.Contains(id) ? "RV.SansOrigine" : "RV.SansOrigineGardee", id));
        if (PasEncoreRenduParLeRejeu.Contains(id))
        {
            etat.Snapshot().Should().NotBe(avant, "défaut connu ; s'il est rendu, retirer le levier de PasEncoreRenduParLeRejeu");
            return;
        }
        etat.Snapshot().Should().Be(avant, $"{id} n'est pas rendu par le rejeu : {string.Join(" | ", rejeu.Value!.Lignes.Select(l => $"{l.Issue} {l.Cible} {l.Detail}"))}");
        jea.EnAttente().Value.Should().HaveCount(0, "le rejeu a vidé le journal");
    }

    // Défauts connus : le démarrage refuse de rejouer l'inverse que ces leviers produisent eux-mêmes. Chacun fait rougir
    // ce test dès qu'il est corrigé : son entrée part avec la correction.
    private static readonly HashSet<string> InverseRefuseAuDemarrage = new(StringComparer.Ordinal)
    {
        // Seul le retour à l'automatique est rejoué (VentilateurExpertTests le garde) : le journal du compte peut forger une
        // courbe. La courbe d'origine se rendra par le journal d'écriture anticipée du helper, sous ACL admin.
        "gpu.fan_curve",
    };

    [Theory]
    [MemberData(nameof(Leviers))]
    public async Task Tue_apres_l_origine_gardee_le_demarrage_suivant_rend_l_origine(string id)
    {
        var etat = ChaqueLevierDuProduitSaitSeDefaireTests.SocleDe(id);
        var avant = etat.Snapshot();
        var journal = new FichierRevertEnAttente(_journal);
        journal.Poser(id, null).IsOk.Should().BeTrue();
        var applique = await ChaqueLevierDuProduitSaitSeDefaireTests.TousLesLeviers(etat, new FakeElevatedClient(etat))
            .Single(t => t.Id == id).ApplyAsync();
        applique.IsOk.Should().BeTrue();
        journal.Poser(id, applique.Value).IsOk.Should().BeTrue();

        var phrase = Redemarrer(etat);

        if (InverseRefuseAuDemarrage.Contains(id))
        {
            phrase.Should().Be(Docteur.T("RV.InverseRefuse", id),
                "défaut connu ; s'il ne refuse plus, retirer le levier de InverseRefuseAuDemarrage");
            return;
        }
        phrase.Should().Be(Docteur.T("RV.Remis", id));
        etat.Snapshot().Should().Be(avant, $"{id} n'est pas rendu à son origine par le démarrage suivant");
        new FichierRevertEnAttente(_journal).Lire().Value.Should().BeNull("le journal est levé");
    }

    // Ce que la désinstallation laisse au joueur : la sous-tension et la courbe des ventilateurs de la carte, le fichier
    // d'échange remis en état (un fichier d'échange coupé ne revient jamais), la fréquence de l'écran (rendue hors du
    // helper). Inscrit ou non au manifeste, le rendu ne doit jamais les réécrire. En changer est une décision.
    private static readonly HashSet<string> RestentAuJoueur = new(StringComparer.Ordinal)
    {
        "gpu.undervolt", "gpu.fan_curve", "fixit.sane_pagefile", "fixit.ecran_frequence",
    };

    // Le rendu de désinstallation est le seul qui écrit : tout chemin qui inscrit une origine au manifeste (le mapper
    // d'« Optimiser », le « garder » de l'autopilote, un chemin à venir) finit par lui. L'invariant est donc vérifié là.
    public static TheoryData<string, string> LeviersEtChemins()
    {
        var data = new TheoryData<string, string>();
        foreach (var ligne in Leviers())
            foreach (var chemin in new[] { "optimiser", "autopilote" })
                data.Add(ligne, chemin);
        return data;
    }

    [Theory]
    [MemberData(nameof(LeviersEtChemins))]
    public async Task Le_rendu_de_desinstallation_ne_rend_que_l_origine_quel_que_soit_le_chemin_d_inscription(string id, string chemin)
    {
        var etat = ChaqueLevierDuProduitSaitSeDefaireTests.SocleDe(id);
        var avant = etat.Snapshot();
        var levier = ChaqueLevierDuProduitSaitSeDefaireTests.TousLesLeviers(etat, new FakeElevatedClient(etat)).Single(t => t.Id == id);
        var manifeste = new ManifesteDuBanc();
        if (chemin == "autopilote")
        {
            var candidat = new TweakCandidate(levier, new CandidateDescriptor(id, CandidateKind.AutoHot, RiskLevel: 1, NeedsStability: false, ["latence"]),
                new FakeRegistryReader(etat), new FichierRevertEnAttente(_journal), manifeste: manifeste);
            candidat.Apply().IsOk.Should().BeTrue();
            candidat.Garder().IsOk.Should().BeTrue();
        }
        else
        {
            var applique = await levier.ApplyAsync();
            applique.IsOk.Should().BeTrue();
            var rapport = new ApplyReport([new ApplyOutcome(id, Applied: true, AutoReverted: false, applique.Value, Error: null)], Verdict: null);
            var mapper = new AppliedTweakManifestMapper();
            await manifeste.CommitAsync(new DesiredState(mapper.ToDesiredTweaks(rapport), mapper.ToRevertOnly(rapport)));
        }
        var pose = etat.Snapshot();

        await new RenduALaDesinstallation(manifeste, new CiblesDuBanc(etat), new FakeElevatedClient(etat)).ExecuterAsync(CancellationToken.None);

        if (RestentAuJoueur.Contains(id))
            etat.Snapshot().Should().Be(pose, $"{id} reste au joueur : la désinstallation ({chemin}) ne doit pas le rendre");
        else
            etat.Snapshot().Should().Be(avant, $"la désinstallation ({chemin}) de {id} ne rend pas exactement l'origine");
    }

    private sealed class ManifesteDuBanc : IDesiredStateStore
    {
        private DesiredState _etat = new([]);
        public Task<Result> CommitAsync(DesiredState state) { _etat = state; return Task.FromResult(Result.Ok()); }
        public Task<Result<DesiredState>> LoadAsync() => Task.FromResult(Result<DesiredState>.Ok(_etat));
    }

    // Ce que le vrai lecteur (LectureDesCiblesWin32) rend, sous la forme que le rendu compare, lu sur la machine du banc.
    private sealed class CiblesDuBanc(FakeSystemState m) : ILectureDesCibles
    {
        public Result<string?> Lire(PrivilegedOp op) => Result<string?>.Ok(op switch
        {
            SetRegistryValueOp { Kind: RegistryValueType.DWord } s => m.ReadRegistry(s.Hive, s.Path, s.Name)?.ToString(CultureInfo.InvariantCulture),
            SetRegistryValueOp { Kind: RegistryValueType.String } s => m.ReadRegistryText(s.Hive, s.Path, s.Name),
            SetRegistryValueOp { Kind: RegistryValueType.MultiString } s => m.ReadRegistryMultiText(s.Hive, s.Path, s.Name) is { } texte
                ? MultiTexteDuRegistre.Canonique(texte) : null,
            SetActivePowerPlanOp => m.ActivePowerPlan.ToString("D", CultureInfo.InvariantCulture),
            SetProcessorBoostModeOp => m.ModeBoostSecteur.ToString(CultureInfo.InvariantCulture),
            SetCpuPackagePowerLimitOp => m.CpuPkgPowerLimit.ToString(CultureInfo.InvariantCulture),
            SetWirelessPowerSavingOp => m.EconomieSansFilSecteur.ToString(CultureInfo.InvariantCulture),
            RunServiceControlOp r => m.GetService(r.ServiceName) switch
            {
                ServiceState.Running => "running",
                ServiceState.Stopped => "stopped",
                _ => null,
            },
            ApplyNvApiDrsSettingOp a => m.LireDrs(a.SettingId)?.ToString(CultureInfo.InvariantCulture),
            ApplyNvApiPowerLimitOp => m.GpuPowerLimitPct.ToString(CultureInfo.InvariantCulture),
            ApplyAdlxTuningOp { Kind: AdlxTuningKind.AntiLag } => m.AdlxAntiLag.ToString(CultureInfo.InvariantCulture),
            ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit } => m.AdlxPowerLimitPct.ToString(CultureInfo.InvariantCulture),
            _ => null,
        });

        public Result<bool> CibleExiste(PrivilegedOp op)
            => Result<bool>.Ok(op is not RunServiceControlOp r || m.GetService(r.ServiceName) != ServiceState.NotInstalled);
    }
}
