using System.Globalization;
using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.FixIt;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using DrDxt.Detection.Scoring;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Le journal des changements et le retrait automatique étaient testés
/// morceau par morceau, jamais tels qu'ils tournent chez un joueur. Ici, une histoire de deux semaines rejouée de bout en
/// bout avec les vrais types : la machine de référence enregistrée est scannée, « Optimiser » pose trois réglages au
/// redémarrage par les vrais leviers (registre et helper factices), les parties arrivent par la vraie veille (jeu,
/// captures et horloge factices) et se gardent sur un disque temporaire, DrDXT est relancé entre deux parties, le relevé
/// dépasse ses 20 parties et un autre jeu passe au milieu. Rien ne lit ni n'écrit la vraie machine (l'Instant Replay se
/// lit par une couture depuis J5b).
///
/// Ce que la chaîne réelle a révélé : (1) la bascule d'un réglage posé au redémarrage se datait au scan suivant, bornée au
/// scan d'avant ; les parties jouées entre les deux ne comptaient d'aucun côté, et le réglage ne se jugeait jamais ;
/// (2) les réglages posés par un même clic basculent au même instant, et chacun bornait l'autre à zéro partie : aucun
/// n'était jugé, rien n'était retiré ; (3) jugés ensemble, leur mesure unique se composait une fois par réglage dans la
/// ligne des gains (-38 % annoncés pour une perte de 15 %) ; (4) la veille datait ses captures sur l'horloge réelle.
/// (5) les bascules ne se relevaient qu'au scan ; un joueur qui ne rescannait pas n'était jamais jugé ni protégé ;
/// (6) la phrase « retiré par DrDXT » disparaissait au premier rafraîchissement de la carte.
/// </summary>
public sealed class HistoireDUnJoueurTests : IDisposable
{
    private const string Cod = "cod.exe";
    private const string Hw = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string Mm = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"histoire-{Guid.NewGuid():N}");
    private readonly MachinesEnregistreesTests.Machine _machine = MachinesEnregistreesTests.Charger("reference-i7-14700f-rtx4060-2026-10-06");
    private readonly List<PrivilegedOp> _ecrits = [];
    private readonly List<string> _sortisDuManifeste = [];
    private readonly IElevatedClient _helper = Substitute.For<IElevatedClient>();
    private readonly IRegistryReader _registre = Substitute.For<IRegistryReader>();
    private readonly Dictionary<string, ITweak> _leviers;

    private DateTimeOffset _maintenant;
    private DateTimeOffset _demarrage;
    private HardwareInventory _inventaire;
    private SettingsState _reglages;
    private ScanViewModel _vm;

    private static DateTimeOffset T(int jour, int heure) => new(2026, 10, jour, heure, 0, 0, TimeSpan.Zero);

    private static string Fr(string cle) => new ResxLocalizer().Get(cle, new CultureInfo("fr"));

    public HistoireDUnJoueurTests()
    {
        Directory.CreateDirectory(_dossier);
        _helper.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>())
            // Un vrai helper répond plus tard ; une tâche déjà finie cachait un retrait non attendu.
            .Returns(async ci => { await Task.Yield(); Ecrire(ci.Arg<PrivilegedOp>()); return Result.Ok(); });
        _registre.ReadDword(default, default!, default!).ReturnsForAnyArgs(Result<uint?>.Ok(0u));
        _registre.ReadDword(RegistryHiveKind.LocalMachine, Hw, "HwSchMode").Returns(Result<uint?>.Ok(1u));
        _registre.ReadMultiString(RegistryHiveKind.LocalMachine, Mm, "PagingFiles").Returns(Result<string?>.Ok(""));
        _leviers = new ITweak[] { new HagsOnTweak(_registre, _helper), new GameModeOnTweak(_registre, _helper), new SanePagefileTweak(_registre, _helper) }
            .ToDictionary(t => t.Id);

        _inventaire = _machine.Inventaire;
        // Avant Optimiser : HAGS, le mode Jeu et le fichier d'échange sont éteints (lus, pas inconnus).
        _reglages = _machine.Reglages with { HagsEffective = TriState.Off, Hags = TriState.Off, GameMode = TriState.Off, PagefileConfigured = TriState.Off };
        _maintenant = T(1, 18);
        _demarrage = T(1, 8);
        _vm = Lancer();
    }

    /// <summary>Le registre factice garde ce qu'on y écrit, comme le vrai. Le mode Jeu et la valeur
    /// HwSchMode se relisent aussitôt ; le fichier d'échange aussi, dans le registre, alors qu'il ne prend effet qu'au
    /// redémarrage ; HAGS effectif, lui, attend le redémarrage (le test le bascule).</summary>
    private void Ecrire(PrivilegedOp op)
    {
        _ecrits.Add(op);
        if (op is not SetRegistryValueOp { Name: var nom, Value: var v }) return;
        _reglages = nom switch
        {
            "AllowAutoGameMode" => _reglages with { GameMode = v == "1" ? TriState.On : TriState.Off },
            "HwSchMode" => _reglages with { Hags = v == "2" ? TriState.On : TriState.Off },
            "PagingFiles" => _reglages with { PagefileConfigured = string.IsNullOrEmpty(v) ? TriState.Off : TriState.On },
            _ => _reglages,
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dossier, true); }
        catch (IOException) { }
    }

    private string Fichier(string nom) => Path.Combine(_dossier, nom);

    /// <summary>DrDXT qui démarre : tout se relit depuis le disque temporaire, comme au lancement de l'appli.</summary>
    /// <param name="fond">Le relevé de fond (« --releve », à l'ouverture de session) : sans fenêtre, il ne pose pas
    /// LevierParId, comme App.ReleverEnFond.</param>
    private ScanViewModel Lancer(bool fond = false)
    {
        var hd = Substitute.For<IHardwareDetector>();
        hd.DetectAsync().Returns(_ => Result<HardwareInventory>.Ok(_inventaire));
        var sr = Substitute.For<ISettingsStateReader>();
        sr.ReadAsync(Arg.Any<HardwareInventory>()).Returns(_ => Result<SettingsState>.Ok(_reglages));
        var br = Substitute.For<IBenchmarkRunner>();
        br.RunAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
          .Returns(Result<BenchmarkResult>.Fail(ErrorCodes.WmiFail, "pas de mesure dans une fixture"));
        var sd = Substitute.For<IStorageDetector>();
        sd.Detect().Returns(Result<IReadOnlyList<StorageDevice>>.Ok(new List<StorageDevice>()));
        var plan = ScanViewModelTestSeams.ProfileConsumer(
            ScanViewModelTestSeams.PlanConsumption(["latency.hagsOn", "latency.gameModeOn", "fixit.sane_pagefile"]));

        var vm = new ScanViewModel(hd, sr, br, new ScanScorer(), sd, ScanViewModelTestSeams.Safety(), new MoteurQuiReussit(_leviers), plan,
            ScanViewModelTestSeams.DesiredStateStore(), new AppliedTweakManifestMapper(), ScanViewModelTestSeams.BiosAdvisor(),
            ScanViewModelTestSeams.GuideGenerator(), Fr, repairAudit: () => _machine.Sante, lireStabilite: () => (null, Array.Empty<ArretClasse>()),
            journalLeviers: new JournalDesLeviersAppliques(Fichier("leviers.json")))
        {
            JournalChangements = new JournalDesChangements(Fichier("changements.json")),
            SuiviReglages = new SuiviDesReglages(Fichier("reglages-suivis.json")),
            MemoireParties = new PartiesMemorisees(Fichier("releve.json"), Fichier("parties.json")),
            LevierParId = fond ? null : id => _leviers.GetValueOrDefault(id),
            LireReglagesSuivis = () => ScanViewModel.EtatsSuivisSansScan(ReglagesLus(), HagsLu(), instantReplay: null),
            Horloge = () => _maintenant,
            DemarrageDuPc = () => _demarrage,
        };
        if (!fond) vm.RetirerDuManifeste = id => { _sortisDuManifeste.Add(id); return Task.FromResult(Result.Ok()); };
        // Le relevé de l'appli lirait le jeu en cours de ce PC ; ici, c'est la veille du test qui relève.
        vm.MemoireParties.Eteindre();
        vm.ChargerParties();
        vm.ChargerLeviersPoses();
        return vm;
    }

    private DrDxt.Detection.Settings.IRegistryProvider ReglagesLus()
    {
        var r = Substitute.For<DrDxt.Detection.Settings.IRegistryProvider>();
        r.GetGameMode().Returns(_ => _reglages.GameMode);
        r.GetPagefile().Returns(_ => _reglages.PagefileConfigured);
        return r;
    }

    private DrDxt.Detection.Settings.IHagsEffectiveReader HagsLu()
    {
        var h = Substitute.For<DrDxt.Detection.Settings.IHagsEffectiveReader>();
        h.Lire().Returns(_ => _reglages.HagsEffective);
        return h;
    }

    /// <summary>Le moteur factice qui réussit : chaque levier du lot est posé par son vrai ApplyAsync (le snapshot du
    /// journal des leviers est donc le vrai), comme le moteur le fait sans mesure au redémarrage.</summary>
    private sealed class MoteurQuiReussit(IReadOnlyDictionary<string, ITweak> leviers) : ITweakEngine
    {
        public async Task<Result<ApplyReport>> ApplyAsync(TweakPlan plan, BenchmarkResult? baseline, int targetPid, CancellationToken ct)
        {
            var issues = new List<ApplyOutcome>();
            foreach (var id in plan.TweakIds)
            {
                var r = await leviers[id].ApplyAsync();
                issues.Add(new ApplyOutcome(id, r.IsOk, false, r.Value, r.Error));
            }
            return Result<ApplyReport>.Ok(new ApplyReport(issues, null));
        }
    }

    /// <summary>Une partie, relevée par la vraie veille : trois captures de 20 s à trois minutes d'écart, puis le jeu se
    /// ferme et la veille s'arrête. L'horloge avance avec les attentes de la veille.</summary>
    private async Task Jouer(DateTimeOffset debut, double low1, string jeu = Cod)
    {
        _maintenant = debut;
        var script = new Queue<string?>([jeu, jeu, jeu, null]);
        using var fin = new CancellationTokenSource();
        var veille = new VeilleDesParties(
            jeu: () =>
            {
                var exe = script.Count > 0 ? script.Dequeue() : null;
                if (script.Count == 0) fin.Cancel();
                return exe is null ? null : (exe, 4242);
            },
            capturer: (_, duree, _) =>
            {
                _maintenant += duree;
                return Task.FromResult<BenchmarkResult?>(new BenchmarkResult(140, low1, low1 * 0.8, [7.0], null, 1, 1));
            },
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: p => _vm.EnregistrerPartie(p),
            patienter: (duree, _) => { _maintenant += duree; return Task.CompletedTask; },
            horloge: () => _maintenant);
        await veille.VeillerAsync(fin.Token);
        await _vm.DernierRetrait;
    }

    private async Task Scanner(DateTimeOffset quand)
    {
        _maintenant = quand;
        await _vm.ScanCommand.ExecuteAsync(null);
        await _vm.DernierRetrait;
    }

    private IReadOnlyList<Changement> Journal() => new JournalDesChangements(Fichier("changements.json")).Lire();

    /// <summary>Les bascules du clic d'Optimiser du jour 4 : parties de la pose, le retour à « éteint » exclu.</summary>
    private static bool DuClic(Changement c) => c.Depuis == T(4, 12) && c.Cle?.EndsWith(":on", StringComparison.Ordinal) == true;

    private int EcritsSur(string nom) => _ecrits.OfType<SetRegistryValueOp>().Count(o => o.Name == nom);

    private string Reglage(string id, bool allume) => string.Format(CultureInfo.CurrentCulture, Fr("Changement.Reglage"),
        Fr("Reglage." + id), Fr(allume ? "Reglage.Eteint" : "Reglage.Allume"), Fr(allume ? "Reglage.Allume" : "Reglage.Eteint"));

    // Le mode Jeu agit au prochain lancement du jeu : son retrait le dit ainsi.
    private string Retire(string id) => string.Format(CultureInfo.CurrentCulture,
        Fr(id == "windows.game_mode" ? "Changement.RetireAuLancementDuJeu" : "Changement.RetireAuRedemarrage"), Reglage(id, true));

    [Fact]
    public async Task Deux_semaines_de_parties_jugent_retirent_et_figent_comme_chez_un_joueur()
    {
        // Jour 1 : premier scan, les réglages suivis sont relevés (aucun changement sans avant).
        await Scanner(T(1, 18));
        Journal().Should().BeEmpty();

        // Jours 1 à 3 : trois parties, DrDXT ouvert.
        await Jouer(T(1, 20), 95);
        await Jouer(T(2, 20), 96);
        await Jouer(T(3, 20), 94);
        _vm.MemoireParties!.Lire().Should().HaveCount(3).And.OnlyContain(p => p.Captures == 3 && p.Jeu == Cod);
        _vm.MemoireParties.Lire().Select(p => p.Debut.Day).Should().Equal([3, 2, 1], "datées sur l'horloge de la veille");

        // Jour 4 : « Optimiser » sur le scan du jour 1 ; trois réglages au redémarrage, par leurs vrais leviers.
        _maintenant = T(4, 12);
        _vm.ApplyCommand.CanExecute(null).Should().BeTrue();
        await _vm.ApplyCommand.ExecuteAsync(null);
        _vm.LeviersPoses.Select(l => l.Id).Should().BeEquivalentTo(_leviers.Keys);
        Journal().Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) == true).Select(c => c.Cle)
            .Should().BeEquivalentTo(_leviers.Keys.Select(id => "pose:" + id));
        EcritsSur("HwSchMode").Should().Be(1);

        // Le PC redémarre ; DrDXT se relance (tout se relit du disque) et trois parties plus lentes sont relevées.
        _demarrage = T(4, 13);
        _reglages = _reglages with { HagsEffective = TriState.On, Hags = TriState.On, GameMode = TriState.On, PagefileConfigured = TriState.On };
        _vm = Lancer();
        _vm.LeviersPoses.Should().HaveCount(3, "relu du journal des leviers");
        await Jouer(T(4, 20), 80);
        await Jouer(T(5, 20), 82);
        await Jouer(T(6, 20), 81);

        // Jour 7 : un scan voit les deux bascules au redémarrage. Elles datent du redémarrage qui a suivi la pose, pas de ce
        // scan ; le mode Jeu, qui agit tout de suite, s'est daté à la pose.
        await Scanner(T(7, 10));
        var bascules = Journal().Where(c => c.Cle?.EndsWith(":on", StringComparison.Ordinal) == true).ToList();
        bascules.Select(c => c.Cle).Should().BeEquivalentTo(["windows.hags:on", "windows.game_mode:on", "memoire.fichier_echange:on"]);
        bascules.Should().OnlyContain(c => c.Quand == (c.Cle == "windows.game_mode:on" ? T(4, 12) : T(4, 13)) && c.Depuis == T(4, 12));

        // Ce que le joueur voit : moins bien en jeu, 95 puis 81, et une seule mesure pour les trois (un seul clic).
        var cartes = _vm.LignesParties.ToList();
        cartes.Should().Contain(Retire("windows.hags")).And.Contain(Retire("windows.game_mode"));
        cartes.Should().NotContain(Retire("memoire.fichier_echange"), "F-9 : le fichier d'échange ne se retire pas sur les images");
        cartes.Should().Contain(l => l.StartsWith("Ce que tes changements", StringComparison.Ordinal) && l.Contains("1 changement jugé avant/après"));
        // Le mode Jeu, jugé dès la pose, a été retiré pendant la partie du jour 6 ; le scan du jour 7 relève son retour
        // à « éteint », et la carte (trois changements au plus) ne montre plus sa bascule de la pose. Son bilan reste le même.
        cartes.Count(l => l.Contains(Fr("Changement.Pire")) && l.Contains("95 puis 81")).Should().Be(2);
        ScanViewModel.BilansDesChangements(Journal(), _vm.MemoireParties!.Lire(), null).Where(x => DuClic(x.C))
            .Should().HaveCount(3).And.OnlyContain(x => x.B.Verdict == VerdictEnJeu.Pire && x.B.Low1Avant == 95 && x.B.Low1Apres == 81);

        // Le retrait : la valeur d'avant remise par le vrai RevertAsync, le journal des leviers et le manifeste la perdent.
        _ecrits.OfType<SetRegistryValueOp>().Last(o => o.Name == "HwSchMode").Value.Should().Be("1");
        EcritsSur("AllowAutoGameMode").Should().Be(2, "posé puis rendu");
        EcritsSur("PagingFiles").Should().Be(1, "posé, jamais rendu");
        new JournalDesLeviersAppliques(Fichier("leviers.json")).Lire().Select(s => s.TweakId).Should().Equal("fixit.sane_pagefile");
        _vm.LeviersPoses.Select(l => l.Id).Should().Equal("fixit.sane_pagefile");
        _sortisDuManifeste.Should().BeEquivalentTo(["latency.hagsOn", "latency.gameModeOn"]);
        // La fenêtre est encore ouverte et le relevé n'est pas plein : rien n'est figé, sauf ce qui vient d'être
        // retiré (J5, 07/10) : son verdict est figé au retrait, pour que les parties d'après ne le réécrivent pas.
        var basculees = Journal().Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true).ToList();
        basculees.Where(c => c.Cle is "windows.hags:on" or "windows.game_mode:on")
            .Should().HaveCount(2).And.OnlyContain(c => c.Fige != null && c.Fige.Verdict == VerdictEnJeu.Pire);
        // Le fichier d'échange, lui, a sa fenêtre close par le retour du mode Jeu à « éteint » relevé à ce scan.
        basculees.Single(c => c.Cle == "memoire.fichier_echange:on").Fige.Should().Match<BilanDuChangement>(b => b.Verdict == VerdictEnJeu.Pire);
        basculees.Where(c => !DuClic(c)).Should().OnlyContain(c => c.Fige == null);

        // Le retour du mode Jeu à « éteint », relevé ce matin, est un changement ; trois parties et un scan le suivent
        // avant le BIOS, pour que la mémoire ait un avant.
        await Jouer(T(7, 12), 81);
        await Jouer(T(7, 14), 80);
        await Jouer(T(7, 16), 82);
        await Scanner(T(7, 18));

        // Jour 8 : les retraits ont pris effet au redémarrage, et le joueur a baissé sa mémoire au BIOS. Un scan les voit.
        _demarrage = T(8, 9);
        _reglages = _reglages with { HagsEffective = TriState.Off, Hags = TriState.Off, GameMode = TriState.Off };
        _inventaire = _inventaire with { Memory = _inventaire.Memory.Select(m => m with { ConfiguredMhz = 3600 }).ToList() };
        await Scanner(T(8, 10));
        // Le changement suivant ferme la fenêtre du premier ; son bilan est figé, tranché.
        Journal().Where(DuClic).Should().HaveCount(3).And.OnlyContain(c => c.Fige != null && c.Fige.Verdict == VerdictEnJeu.Pire);

        await Jouer(T(8, 20), 70);
        await Jouer(T(9, 20), 71);
        _vm = Lancer(); // relancé entre deux parties
        await Jouer(T(10, 20), 72);
        var memoire = Journal().Single(c => c.Cle?.StartsWith("memoire.frequence:", StringComparison.Ordinal) == true);
        ScanViewModel.BilansDesChangements(Journal(), _vm.MemoireParties!.Lire(), null).Single(x => x.C == memoire).B
            .Should().Match<BilanDuChangement>(b => b.Verdict == VerdictEnJeu.Pire && b.Avant == 3 && b.Apres == 3 && b.Low1Avant == 81,
                "jugé sur les parties d'entre les deux changements, pas sur celles d'avant le premier");

        // Jour 11, un scan sans changement ; jour 12, le joueur a rallumé lui-même le mode Jeu. Une partie d'un autre jeu,
        // puis quinze parties plus lentes.
        var ecritsAvant = _ecrits.Count;
        await Scanner(T(11, 10));
        _demarrage = T(12, 9);
        _reglages = _reglages with { GameMode = TriState.On };
        await Scanner(T(12, 10));
        await Jouer(T(12, 20), 200, "valorant.exe");
        for (var j = 0; j < 15; j++)
        {
            if (j == 7) _vm = Lancer();
            await Jouer(T(13, 0).AddHours(10 * j), 60 + j % 3);
        }

        // L'anneau : 20 parties gardées, les plus anciennes sorties ; le premier bilan, figé, dit toujours la même chose.
        var parties = _vm.MemoireParties!.Lire();
        parties.Should().HaveCount(ReleveDesParties.PartiesGardees);
        parties.Should().NotContain(p => p.Jeu == Cod && p.Low1Median >= 94, "les parties d'avant le premier changement ont quitté le relevé");
        var bilans = ScanViewModel.BilansDesChangements(Journal(), parties, null);
        bilans.Where(x => DuClic(x.C)).Should().OnlyContain(x => x.B.Verdict == VerdictEnJeu.Pire && x.B.Low1Avant == 95);
        // Le mode Jeu rallumé par le joueur, moins bien en jeu, n'est jamais retiré ; la partie de Valorant ne compte pas.
        var modeJeu = bilans.Single(x => x.C.Cle == "windows.game_mode:on" && x.C.Quand == T(12, 10)).B;
        modeJeu.Verdict.Should().Be(VerdictEnJeu.Pire);
        modeJeu.Jeu.Should().Be(Cod);
        _ecrits.Should().HaveCount(ecritsAvant, "rien n'est retiré de ce que le joueur a basculé lui-même");
        var retire = Fr("Changement.Retire")[..12];
        _vm.LignesParties.Should().NotContain(l => l.StartsWith(retire, StringComparison.Ordinal));
        _vm.LeviersPoses.Select(l => l.Id).Should().Equal("fixit.sane_pagefile");
    }

    // Le joueur optimise, redémarre et joue sans jamais rescanner. Le relevé de fond (ouverture de session) note les
    // bascules et juge les parties, sans rien retirer ; l'ouverture de DrDXT retire ce qui coûte, et la carte le dit, même
    // après la partie suivante.
    [Fact]
    public async Task Sans_rescan_le_releve_de_fond_juge_et_l_ouverture_retire_et_le_dit()
    {
        await Scanner(T(1, 18));
        await Jouer(T(1, 20), 95);
        await Jouer(T(2, 20), 96);
        await Jouer(T(3, 20), 94);
        _maintenant = T(4, 12);
        await _vm.ApplyCommand.ExecuteAsync(null);
        var poses = _ecrits.Count;

        _demarrage = T(4, 13);
        _reglages = _reglages with { HagsEffective = TriState.On, Hags = TriState.On, GameMode = TriState.On, PagefileConfigured = TriState.On };
        _maintenant = T(4, 14);
        _vm = Lancer(fond: true);
        await _vm.ReleverReglagesHorsScanAsync();
        await _vm.DernierRetrait;
        Journal().Where(c => c.Cle?.EndsWith(":on", StringComparison.Ordinal) == true)
            .Should().HaveCount(3).And.OnlyContain(c => c.Quand == (c.Cle == "windows.game_mode:on" ? T(4, 12) : T(4, 13)) && c.Depuis == T(4, 12));
        await Jouer(T(4, 20), 80);
        await Jouer(T(5, 20), 82);
        await Jouer(T(6, 20), 81);

        ScanViewModel.BilansDesChangements(Journal(), _vm.MemoireParties!.Lire(), null).Where(x => DuClic(x.C))
            .Should().HaveCount(3).And.OnlyContain(x => x.B.Verdict == VerdictEnJeu.Pire);
        _ecrits.Should().HaveCount(poses, "sans fenêtre, le relevé de fond ne retire rien");
        new JournalDesLeviersAppliques(Fichier("leviers.json")).Lire().Should().HaveCount(3);

        // Jour 7 : le joueur ouvre DrDXT, sans scanner.
        _maintenant = T(7, 10);
        _vm = Lancer();
        await _vm.ReleverReglagesHorsScanAsync();
        await _vm.DernierRetrait;
        _vm.LignesParties.Should().Contain(Retire("windows.hags")).And.Contain(Retire("windows.game_mode"))
            .And.NotContain(Retire("memoire.fichier_echange"));
        _ecrits.OfType<SetRegistryValueOp>().Last(o => o.Name == "HwSchMode").Value.Should().Be("1");
        new JournalDesLeviersAppliques(Fichier("leviers.json")).Lire().Select(s => s.TweakId).Should().Equal("fixit.sane_pagefile");

        await Jouer(T(7, 20), 95);
        _vm.LignesParties.Should().Contain(Retire("windows.hags"), "la carte se refait, la phrase reste");
        // Le verdict qui a fait retirer est figé : la partie à 95 jouée après le retrait ne le change pas.
        _vm.LignesParties.Should().Contain(l => l.Contains(Fr("Changement.Pire")) && l.Contains("95 puis 81"));
    }

    // Le mode Jeu agit tout de suite. Posé par Optimiser, il n'était relevé qu'à la prochaine ouverture
    // ou au prochain scan, et les parties jouées entre la pose et ce relevé ne comptaient d'aucun côté. Il se relève juste
    // après la pose ; le fichier d'échange, déjà écrit au registre mais actif seulement au redémarrage, attend.
    [Fact]
    public async Task Un_levier_immediat_se_date_a_la_pose_et_se_juge_sans_redemarrage()
    {
        await Scanner(T(1, 18));
        await Jouer(T(1, 20), 95);
        await Jouer(T(2, 20), 96);
        await Jouer(T(3, 20), 94);
        _maintenant = T(4, 12);
        await _vm.ApplyCommand.ExecuteAsync(null);

        var bascules = Journal().Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true).ToList();
        bascules.Should().ContainSingle().Which.Should().Match<Changement>(c =>
            c.Cle == "windows.game_mode:on" && c.Quand == T(4, 12) && c.Depuis == T(4, 12));

        // Le joueur ne redémarre pas, ne rouvre pas DrDXT : trois parties plus lentes, puis un scan.
        await Jouer(T(4, 20), 80);
        await Jouer(T(5, 20), 82);
        await Jouer(T(6, 20), 81);
        // Retiré à la partie du jour 6, il se relève tout de suite : son retour est daté sans attendre un scan.
        Journal().Should().Contain(c => c.Cle == "windows.game_mode:off" && c.Quand < T(7, 0));
        await Scanner(T(7, 10));
        var modeJeu = Journal().Single(c => c.Cle == "windows.game_mode:on");
        modeJeu.Quand.Should().Be(T(4, 12), "daté à la pose, pas au scan qui l'a revu");
        ScanViewModel.BilansDesChangements(Journal(), _vm.MemoireParties!.Lire(), null).Single(x => x.C.Cle == "windows.game_mode:on").B
            .Should().Match<BilanDuChangement>(b => b.Verdict == VerdictEnJeu.Pire && b.Avant == 3 && b.Apres == 3);
    }

    // Les cas qui cassent chez un vrai joueur : un journal abîmé (disque, coupure) ne fait ni planter ni inventer ; les
    // parties restent à l'écran, et rien n'est retiré sur un journal qu'on ne sait pas lire.
    [Fact]
    public async Task Un_journal_abime_n_invente_rien_et_ne_retire_rien()
    {
        await Scanner(T(1, 18));
        await Jouer(T(1, 20), 95);
        await Jouer(T(2, 20), 96);
        await Jouer(T(3, 20), 94);
        _maintenant = T(4, 12);
        await _vm.ApplyCommand.ExecuteAsync(null);
        _demarrage = T(4, 13);
        _reglages = _reglages with { HagsEffective = TriState.On, Hags = TriState.On, GameMode = TriState.On, PagefileConfigured = TriState.On };
        _vm = Lancer();
        await Jouer(T(4, 20), 80);
        await Jouer(T(5, 20), 82);
        File.WriteAllText(Fichier("changements.json"), "[{\"Quand\":\"2026-10-0");
        var ecrits = _ecrits.Count;

        _vm = Lancer();
        await Jouer(T(6, 20), 81);
        await Scanner(T(7, 10));

        _ecrits.Should().HaveCount(ecrits, "les poses sont illisibles : DrDXT ne sait plus ce qu'il a posé, il ne retire rien");
        _vm.LignesParties.Should().Contain(l => l.Contains("Call of Duty") && l.Contains("FPS"), "les parties restent à l'écran");
        _vm.LignesParties.Should().NotContain(l => l.Contains(Fr("Changement.Pire")));
        // La carte se lit en résumé puis détail ; les deux ensemble redonnent tout, dans le même ordre.
        _vm.LignesPartiesResume.Concat(_vm.LignesPartiesDetail).Should().Equal(_vm.LignesParties);
        _vm.LignesPartiesDetail.Should().Contain(l => l.Contains("Call of Duty") && l.Contains("FPS"), "une partie est du détail, repliée");

        File.WriteAllText(Fichier("parties.json"), "{ pas du json");
        _vm = Lancer();
        _vm.AucunePartie.Should().BeTrue("une liste abîmée n'invente aucune partie");
    }
}
