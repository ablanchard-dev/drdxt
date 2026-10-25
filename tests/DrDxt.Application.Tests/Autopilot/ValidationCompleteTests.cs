using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// La validation complète pose, relit, rend, relit ; un levier qui ne revient pas est dit.
public sealed class ValidationCompleteTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"drdxt-validation-{Guid.NewGuid():N}.json");
    private readonly Dictionary<string, int> _machine = new();

    public void Dispose() { try { File.Delete(_chemin); } catch (IOException) { } }

    private sealed class Refuse(string id, string code) : ITweak
    {
        public string Id => id;
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => Task.FromResult(Result<TweakSnapshot>.Fail(code, "refusé"));
        public Task<Result> RevertAsync(TweakSnapshot snapshot) => Task.FromResult(Result.Ok());
    }

    private sealed class Levier(string id, Dictionary<string, int> machine, bool concerne = true, bool rendRefuse = false) : ITweak
    {
        public int Rendus;
        public string Id => id;
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => concerne;
        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            var origine = machine.GetValueOrDefault(id);
            machine[id] = 1;
            return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, origine.ToString())));
        }
        public Task<Result> RevertAsync(TweakSnapshot snapshot)
        {
            Rendus++;
            if (rendRefuse) return Task.FromResult(Result.Fail("REFUS", "clé protégée"));
            machine[id] = int.Parse(snapshot.OriginalValueJson);
            return Task.FromResult(Result.Ok());
        }
    }

    // Une écriture journalisée posée dans une portée note son acquit, comme le client élevé de l'App.
    private sealed class Acquitte(string id, List<string> log) : ITweak
    {
        public string Id => id;
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            PorteeDePose.Courante?.Noter(() => { log.Add("acquit"); return Task.FromResult(Result.Ok()); });
            return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, "0", "1")));
        }
        public Task<Result> RevertAsync(TweakSnapshot snapshot) { log.Add("rendu"); return Task.FromResult(Result.Ok()); }
    }

    private sealed class JournalTrace(IRevertEnAttente journal, List<string> log, bool refuseLOrigine = false) : IRevertEnAttente
    {
        public Result Poser(string tweakId, TweakSnapshot? snapshot)
        {
            if (snapshot is null) return journal.Poser(tweakId, null);
            if (refuseLOrigine) return Result.Fail("X", "disque plein");
            log.Add("origine");
            return journal.Poser(tweakId, snapshot);
        }
        public Result Lever() => journal.Lever();
        public Result Garder(string tweakId, TweakSnapshot snapshot) => journal.Garder(tweakId, snapshot);
        public Result<RevertEnAttenteEntree?> Lire() => journal.Lire();
    }

    // Le helper garde la pose jusqu'à ce que la vérification l'ait rendue : tuée entre la pose et le rendu, l'App laissait
    // l'origine dans le seul fichier du compte, que la désinstallation élevée ne lit pas. L'acquit part après le rendu, une fois.
    [Fact]
    public async Task La_pose_verifiee_s_acquitte_une_fois_apres_que_le_journal_porte_l_origine()
    {
        var log = new List<string>();

        await ValidationComplete.ValiderAsync([new Acquitte("fixit.power_plan", log)], null!, null!,
            new JournalTrace(new FichierRevertEnAttente(_chemin), log), Relire);

        log.Should().Equal("origine", "rendu", "acquit");
    }

    [Fact]
    public async Task Une_origine_que_le_journal_refuse_n_est_pas_acquittee()
    {
        var log = new List<string>();

        await ValidationComplete.ValiderAsync([new Acquitte("fixit.power_plan", log)], null!, null!,
            new JournalTrace(new FichierRevertEnAttente(_chemin), log, refuseLOrigine: true), Relire);

        log.Should().NotContain("acquit");
    }

    private sealed class AucunJeu : DrDxt.Application.Tweaks.Hardware.IGameExecutable
    {
        public string? RunningGameExecutablePath() => null;
    }

    // Sur NVIDIA, la file de rendu se lit au profil du jeu, et la vérification tourne toujours sans jeu (un jeu ouvert
    // l'arrête). « Son état n'a pas pu être lu » annoncerait une panne : sans jeu, il n'y a rien à lire.
    [Fact]
    public async Task Un_levier_qui_ne_se_lit_qu_en_jeu_ne_se_dit_pas_en_panne()
    {
        var nv = Substitute.For<DrDxt.Application.Tweaks.Hardware.INvApi>();
        nv.IsAvailable.Returns(true);
        var adlx = Substitute.For<DrDxt.Application.Tweaks.Hardware.IAdlx>();
        adlx.IsAvailable.Returns(false);
        var levier = new DrDxt.Application.Tweaks.Latency.RenderQueueCapTweak(nv, adlx, Substitute.For<IElevatedClient>(), new AucunJeu());
        var hw = new HardwareInventory(new CpuInfo("Intel Core i7-14700F", "GenuineIntel", 20, 28, false, 1),
            [new GpuInfo("NVIDIA GeForce RTX 4060", "NVIDIA", 8L << 30, "32.0.15.6094", TriState.On)],
            [], new MotherboardInfo("MSI", "B760"), new BiosInfo("1.0", null, true), ChassisType.Desktop);
        var etat = new SettingsState(TriState.On, TriState.On, "", TriState.On, GameMode: TriState.On, ParkedCoreIndexes: [], Vrr: TriState.On);

        var r = await ValidationComplete.ValiderAsync([levier], hw, etat, new FichierRevertEnAttente(_chemin), Relire);

        r.Texte.Should().Contain("pas touché : son état n'a pas été lu ici (certains réglages ne se lisent qu'avec un jeu ouvert, et la vérification tourne sans jeu).")
            .And.NotContain("n'a pas pu être lu");
    }

    /// <summary>Un levier que la machine porte déjà : sa pose réécrit la valeur en place (origine 90, posé 90).</summary>
    private sealed class DejaEnPlace(string id) : ITweak
    {
        public string Id => id;
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, "90", "90")));
        public Task<Result> RevertAsync(TweakSnapshot snapshot) => Task.FromResult(Result.Ok());
    }

    // Le cas mesuré le 17/10 : l'essai en alternance tenait déjà la limite de puissance de la carte à 90 %, la cible du levier.
    // Posée à 90 sur 90, relue 90 : « pas pris », et le verdict « l'écriture n'a pas été vue » envoyait vérifier à la main un
    // levier qui n'avait rien à montrer. Déjà à sa valeur, il est dit déjà réglé.
    [Fact]
    public async Task Un_levier_deja_a_sa_valeur_est_dit_deja_regle_pas_une_ecriture_non_vue()
    {
        var journal = new FichierRevertEnAttente(_chemin);

        var r = await ValidationComplete.ValiderAsync([new DejaEnPlace("gpu.power_limit")], null!, null!, journal,
            _ => new ValidationComplete.Lecture(false, "limite de puissance (%) : origine 90, lu 90"));

        r.NonTouches.Should().Be(1);
        r.Valides.Should().Be(0);
        r.NonRemis.Should().BeEmpty("rien n'est à vérifier à la main");
        r.EssayesIds.Should().BeEmpty("déjà réglé : une preuve d'avant n'est pas retirée");
        r.Texte.Should().Contain(Docteur.T("VC.PasTouche.DejaRegle")).And.NotContain(Docteur.T("VC.Verdict.NonVu"));
        journal.Lire().Value.Should().BeNull("le journal ne garde rien à rendre");
    }

    // Un essai en alternance tient la limite de puissance, en phase « sans » (la carte à son origine). Posée puis rendue par la
    // vérification, elle aurait faussé la partie suivante de l'essai : le levier est sauté, et c'est dit ; les autres sont vérifiés.
    [Fact]
    public async Task Un_levier_tenu_par_un_essai_en_alternance_n_est_pas_touche()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var tenu = new Levier("gpu.power_limit", _machine);
        var libre = new Levier("latency.hagsOn", _machine);

        var r = await ValidationComplete.ValiderAsync([tenu, libre], null!, null!, journal, Relire,
            tenuParUnEssai: id => id == "gpu.power_limit");

        _machine.Should().NotContainKey("gpu.power_limit", "rien n'est écrit sur le levier de l'essai");
        tenu.Rendus.Should().Be(0);
        r.Texte.Should().Contain(Docteur.T("VC.PasTouche.Essai"));
        r.NonTouches.Should().Be(1);
        r.Valides.Should().Be(1, "le levier libre est vérifié");
        r.EssayesIds.Should().Equal(["latency.hagsOn"]);
    }

    // ── --valider-profond : un levier déjà dans son état cible est exercé ──────────────────────────────────────────────
    // Le vrai test chez le joueur n'a exercé qu'un levier : tous les autres étaient « déjà réglés ». En mode profond, un levier
    // déjà réglé qui sait préparer son exercice est mis dans l'état NON cible, relu, posé par le levier, relu, puis rendu à son
    // origine EXACTE, relue ; le journal porte l'origine avant toute écriture. Sans exercice sûr, il reste « pas touché » et le dit.
    private sealed class DejaRegle(string id, Dictionary<string, int> machine, bool exercable, Action<string>? avantEcriture = null) : ITweak
    {
        public readonly List<int> Ecrits = [];
        public string Id => id;
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => machine.GetValueOrDefault(id) != 1;
        public ExerciceProfond? PreparerExercice(HardwareInventory hw, SettingsState state)
            => exercable ? new ExerciceProfond(machine[id].ToString(), "0") : null;
        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            var origine = machine.GetValueOrDefault(id);
            Ecrire(1);
            return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(id, origine.ToString(), "1")));
        }
        public Task<Result> RevertAsync(TweakSnapshot snapshot)
        {
            Ecrire(int.Parse(snapshot.OriginalValueJson));
            return Task.FromResult(Result.Ok());
        }
        private void Ecrire(int v) { avantEcriture?.Invoke(id); Ecrits.Add(v); machine[id] = v; }
    }

    // Lit la valeur que le snapshot demande, comme le vrai vérificateur : posé = la machine porte AppliedValueJson.
    private ValidationComplete.Lecture RelireLaDemande(TweakSnapshot s)
        => new(_machine.GetValueOrDefault(s.TweakId).ToString() == s.AppliedValueJson, $"lu {_machine.GetValueOrDefault(s.TweakId)}");

    [Fact]
    public async Task En_profond_un_levier_deja_regle_est_exerce_puis_rendu_a_son_origine_exacte()
    {
        _machine["a"] = 1;
        var journal = new FichierRevertEnAttente(_chemin);
        var journalAvantEcriture = true;
        var levier = new DejaRegle("a", _machine, exercable: true, avantEcriture: id => journalAvantEcriture &= journal.Lire().Value is not null);

        var r = await ValidationComplete.ValiderAsync([levier], null!, null!, journal, RelireLaDemande, profond: true);

        levier.Ecrits.Should().Equal([0, 1, 1], "non cible, puis le levier, puis l'origine exacte");
        journalAvantEcriture.Should().BeTrue("le journal porte l'origine avant toute écriture");
        _machine["a"].Should().Be(1);
        journal.Lire().Value.Should().BeNull("origine relue : journal levé");
        r.Texte.Should().Contain("exercé");
        r.NonRemis.Should().BeEmpty();
    }

    // Les branches de sûreté de l'exercice : quoi qu'il arrive après la première écriture, l'origine est rendue et relue ;
    // le journal n'est levé que si elle l'est.
    private sealed class Exercable(Dictionary<string, int> machine) : ITweak
    {
        public bool HorsCibleRefuse, ApplyEchoue, ApplyLeve;
        public int RendusRefuses;          // les N premiers rendus de l'origine sont refusés
        public readonly List<int> Ecrits = [];
        public string Id => "a";
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => "a";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => machine.GetValueOrDefault("a") != 1;
        public ExerciceProfond? PreparerExercice(HardwareInventory hw, SettingsState state) => new("1", "0");
        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            if (ApplyLeve) throw new InvalidOperationException("pilote planté");
            if (ApplyEchoue) return Task.FromResult(Result<TweakSnapshot>.Fail("REFUS", "refusé"));
            var origine = machine.GetValueOrDefault("a");
            Ecrire(1);
            return Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot("a", origine.ToString(), "1")));
        }
        public Task<Result> RevertAsync(TweakSnapshot snapshot)
        {
            var v = int.Parse(snapshot.OriginalValueJson);
            if (v == 0 && HorsCibleRefuse) return Task.FromResult(Result.Fail("REFUS", "refusé"));
            if (v == 1 && RendusRefuses-- > 0) return Task.FromResult(Result.Fail("REFUS", "occupé"));
            Ecrire(v);
            return Task.FromResult(Result.Ok());
        }
        private void Ecrire(int v) { Ecrits.Add(v); machine["a"] = v; }
    }

    private async Task<(ValidationComplete.Resultat R, FichierRevertEnAttente Journal)> Exercer(Exercable levier,
        Func<TweakSnapshot, ValidationComplete.Lecture>? relire = null)
    {
        _machine["a"] = 1;
        var journal = new FichierRevertEnAttente(_chemin);
        return (await ValidationComplete.ValiderAsync([levier], null!, null!, journal, relire ?? RelireLaDemande, profond: true), journal);
    }

    [Fact]
    public async Task Exercice_journal_impossible_rien_n_est_ecrit()
    {
        var levier = new Exercable(_machine);
        Directory.CreateDirectory(_chemin);   // le journal ne peut pas s'écrire là
        try
        {
            var (r, _) = await Exercer(levier);
            levier.Ecrits.Should().BeEmpty();
            r.Texte.Should().Contain("journal");
        }
        finally { Directory.Delete(_chemin); }
    }

    [Fact]
    public async Task Exercice_non_cible_refuse_le_levier_n_est_pas_pose_et_l_origine_revient()
    {
        var levier = new Exercable(_machine) { HorsCibleRefuse = true };
        var (r, journal) = await Exercer(levier);
        levier.Ecrits.Should().Equal([1], "rien que l'origine, rendue");
        r.Texte.Should().Contain("non conclu");
        journal.Lire().Value.Should().BeNull();
    }

    [Fact]
    public async Task Exercice_non_cible_non_relu_le_levier_n_est_pas_pose()
    {
        var levier = new Exercable(_machine);
        var (r, _) = await Exercer(levier, s => s.AppliedValueJson == "0" ? new(null, "illisible") : RelireLaDemande(s));
        levier.Ecrits.Should().Equal([0, 1], "le non cible, puis l'origine : le levier n'est jamais posé");
        r.Texte.Should().Contain("non conclu");
    }

    [Fact]
    public async Task Exercice_pose_refusee_l_origine_revient()
    {
        var levier = new Exercable(_machine) { ApplyEchoue = true };
        var (r, journal) = await Exercer(levier);
        _machine["a"].Should().Be(1);
        r.Texte.Should().Contain("non conclu");
        journal.Lire().Value.Should().BeNull();
    }

    [Fact]
    public async Task Exercice_une_exception_a_la_pose_rend_quand_meme_l_origine()
    {
        var levier = new Exercable(_machine) { ApplyLeve = true };
        var (_, journal) = await Exercer(levier);
        _machine["a"].Should().Be(1, "le non cible ne reste jamais");
        journal.Lire().Value.Should().BeNull("origine relue");
    }

    [Fact]
    public async Task Exercice_une_relecture_qui_leve_ne_saute_pas_le_rendu()
    {
        var levier = new Exercable(_machine);
        var plante = true;   // la relecture de la pose lève, une fois ; celle de l'origine répond
        var (_, journal) = await Exercer(levier, s => s.AppliedValueJson == "1" && s.OriginalValueJson == "0" && plante && !(plante = false)
            ? throw new InvalidOperationException("lecteur planté") : RelireLaDemande(s));
        _machine["a"].Should().Be(1);
        journal.Lire().Value.Should().BeNull();
    }

    [Fact]
    public async Task Exercice_rendu_refuse_une_fois_la_seconde_chance_rend_l_origine()
    {
        var levier = new Exercable(_machine) { RendusRefuses = 1 };
        var (r, journal) = await Exercer(levier);
        _machine["a"].Should().Be(1);
        journal.Lire().Value.Should().BeNull();
        r.NonRemis.Should().BeEmpty();
    }

    [Fact]
    public async Task Exercice_origine_non_relue_le_journal_la_garde_et_c_est_dit()
    {
        var levier = new Exercable(_machine) { RendusRefuses = 2 };
        var (r, journal) = await Exercer(levier);
        journal.Lire().Value.Should().NotBeNull("REMETTRE.cmd la rendra");
        r.NonRemis.Should().NotBeEmpty();
    }

    [Fact]
    public async Task En_profond_un_levier_sans_exercice_sur_reste_pas_touche_et_le_dit()
    {
        _machine["a"] = 1;
        var levier = new DejaRegle("a", _machine, exercable: false);

        var r = await ValidationComplete.ValiderAsync([levier], null!, null!, new FichierRevertEnAttente(_chemin), RelireLaDemande, profond: true);

        levier.Ecrits.Should().BeEmpty();
        r.Texte.Should().Contain("pas touché : déjà réglé ou ne concerne pas ce PC, et pas d'exercice sûr");
    }

    // Un levier tenu par un essai en alternance n'est pas exercé non plus : l'exercer fausserait l'essai.
    [Fact]
    public async Task En_profond_un_levier_tenu_par_un_essai_n_est_pas_exerce()
    {
        _machine["a"] = 1;
        var levier = new DejaRegle("a", _machine, exercable: true);

        var r = await ValidationComplete.ValiderAsync([levier], null!, null!, new FichierRevertEnAttente(_chemin), RelireLaDemande,
            tenuParUnEssai: id => id == "a", profond: true);

        levier.Ecrits.Should().BeEmpty();
        r.Texte.Should().NotContain("exercé");
    }

    [Fact]
    public async Task Sans_profond_un_levier_deja_regle_n_est_pas_touche()
    {
        _machine["a"] = 1;
        var levier = new DejaRegle("a", _machine, exercable: true);

        await ValidationComplete.ValiderAsync([levier], null!, null!, new FichierRevertEnAttente(_chemin), RelireLaDemande);

        levier.Ecrits.Should().BeEmpty();
    }

    private ValidationComplete.Lecture Relire(TweakSnapshot s)
        => new(_machine.GetValueOrDefault(s.TweakId) == 1, $"lu {_machine.GetValueOrDefault(s.TweakId)}");

    [Fact]
    public async Task Le_rapport_dit_le_materiel_et_prend_la_carte_dediee_pas_l_igpu()
    {
        // Un rapport reçu d'un inconnu doit dire de quelle machine il vient. Sur un APU + Radeon, la première
        // carte listée est souvent l'iGPU : c'est la carte qui a de la mémoire vidéo qui fait tourner les jeux.
        var hw = new HardwareInventory(new CpuInfo("AMD Ryzen 7 7800X3D", "AuthenticAMD", 8, 16, true, 1),
            [new GpuInfo("AMD Radeon(TM) Graphics", "AMD", 512L << 20, "32.0.21013.1000", TriState.Unknown),
             new GpuInfo("AMD Radeon RX 9070 XT", "AMD", 16L << 30, "32.0.21013.1000", TriState.On)],
            [], new MotherboardInfo("ASUS", "B650"), new BiosInfo("1.0", null, true), ChassisType.Desktop);

        ValidationComplete.CartePrincipale(hw)!.Model.Should().Be("AMD Radeon RX 9070 XT");
        var r = await ValidationComplete.ValiderAsync([], hw, null!, new FichierRevertEnAttente(_chemin), Relire);
        r.Texte.Should().StartWith("Matériel : AMD Ryzen 7 7800X3D | AMD Radeon RX 9070 XT (pilote 32.0.21013.1000) | aussi : AMD Radeon(TM) Graphics");
        ValidationComplete.Materiel(null).Should().Be("non lu");
    }

    [Fact]
    public async Task Chaque_levier_est_pose_relu_rendu_et_la_machine_finit_comme_elle_a_commence()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var leviers = new ITweak[] { new Levier("a", _machine), new Levier("b", _machine), new Levier("c", _machine, concerne: false) };

        var r = await ValidationComplete.ValiderAsync(leviers, null!, null!, journal, Relire);

        r.NonRemis.Should().BeEmpty();
        r.Texte.Should().Contain("=== a").And.Contain("validé (écrit, relu, rendu, relu)");
        r.Texte.Should().Contain("=== c").And.Contain("pas touché");
        _machine.Values.Should().OnlyContain(v => v == 0);
        journal.Lire().Value.Should().BeNull("rien ne reste en essai");
    }

    [Fact]
    public async Task Un_levier_qui_ne_revient_pas_est_dit_et_reste_au_journal()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var refuse = new Levier("a", _machine, rendRefuse: true);

        var r = await ValidationComplete.ValiderAsync([refuse], null!, null!, journal, Relire);

        refuse.Rendus.Should().Be(2, "une seconde chance avant de conclure");
        r.NonRemis.Should().ContainSingle().Which.Should().StartWith("a");
        r.Texte.Should().Contain("PAS REMIS D'ORIGINE");
        journal.Lire().Value!.TweakId.Should().Be("a", "le lancement suivant doit pouvoir le rendre");
    }

    // Le banc rend au lancement suivant ce qu'une fenêtre morte a laissé : changé depuis hors de DrDXT, rien n'est réécrit ;
    // rendu, « remis d'origine » ne se dit que relu à l'origine.
    [Theory]
    [InlineData(true, false, "VC.Remise.ChangeDepuis")]
    [InlineData(false, true, "VC.Remise.EncorePose")]
    public async Task Un_essai_interrompu_ne_se_rend_que_sur_la_valeur_posee_et_se_relit(bool changeDepuis, bool rendu, string cle)
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var levier = new Levier("a", _machine);
        _machine["a"] = 0;
        journal.Poser("a", (await levier.ApplyAsync()).Value);
        var ecrit = false;
        DrDxt.Application.Tweaks.EtatApplique Verdict(TweakSnapshot _) => changeDepuis ? DrDxt.Application.Tweaks.EtatApplique.Drifted
            : DrDxt.Application.Tweaks.EtatApplique.Applied;

        var remise = await ValidationComplete.RemettreAsync(journal, _ => new LevierQuiNote(levier, () => ecrit = true), (_, _) => true,
            verdict: Verdict);

        ecrit.Should().Be(rendu);
        remise.Propre.Should().BeFalse();
        remise.Texte.Should().Be(Docteur.T(cle, "a"));
    }

    private sealed class LevierQuiNote(ITweak levier, Action note) : ITweak
    {
        public string Id => levier.Id;
        public TweakCategory Category => levier.Category;
        public bool RequiresElevation => levier.RequiresElevation;
        public bool RequiresRestorePoint => levier.RequiresRestorePoint;
        public string SummaryKey => levier.SummaryKey;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => levier.AppliesTo(hw, state);
        public Task<Result<TweakSnapshot>> ApplyAsync() => levier.ApplyAsync();
        public Task<Result> RevertAsync(TweakSnapshot s) { note(); return levier.RevertAsync(s); }
    }

    [Fact]
    public async Task Un_essai_interrompu_est_rendu_au_lancement_suivant()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var levier = new Levier("a", _machine);
        _machine["a"] = 0;
        var pose = await levier.ApplyAsync();
        journal.Poser("a", pose.Value);   // la fenêtre meurt ici

        var texte = await ValidationComplete.RemettreAsync(new FichierRevertEnAttente(_chemin), id => id == "a" ? levier : null, (_, _) => true);

        texte.Propre.Should().BeTrue();
        texte.Texte.Should().Contain("remis d'origine");
        _machine["a"].Should().Be(0);
        journal.Lire().Value.Should().BeNull();
    }

    // Un journal piégé ne fait rien rejouer en administrateur.
    [Fact]
    public async Task Un_inverse_qui_n_est_pas_celui_du_levier_n_est_jamais_rejoue()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var levier = new Levier("a", _machine);
        _machine["a"] = 0;
        journal.Poser("a", (await levier.ApplyAsync()).Value);

        var remise = await ValidationComplete.RemettreAsync(journal, _ => levier, (_, _) => false);

        remise.Propre.Should().BeFalse();
        _machine["a"].Should().NotBe(0, "rien n'a été rejoué");
        journal.Lire().Value!.TweakId.Should().Be("a");
    }

    // Le levier suivant écraserait l'essai au journal ; la validation s'arrête au premier non rendu.
    [Fact]
    public async Task Le_premier_levier_non_rendu_arrete_la_validation_et_reste_seul_au_journal()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var suivant = new Levier("b", _machine);

        var r = await ValidationComplete.ValiderAsync([new Levier("a", _machine, rendRefuse: true), suivant], null!, null!, journal, Relire);

        r.Texte.Should().Contain("ARRÊT").And.NotContain("=== b");
        _machine.Should().NotContainKey("b");
        journal.Lire().Value!.TweakId.Should().Be("a");
    }

    // Arrêté pendant l'écriture (origine pas encore gardée) = à vérifier, jamais « tout est remis ».
    [Fact]
    public async Task Un_essai_coupe_pendant_l_ecriture_n_est_jamais_dit_propre()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        journal.Poser("a", null);

        var remise = await ValidationComplete.RemettreAsync(journal, _ => null, (_, _) => true);

        remise.Propre.Should().BeFalse();
        remise.Texte.Should().Contain("À vérifier à la main");
    }

    [Fact]
    public async Task Une_relecture_qui_leve_n_arrete_pas_la_validation()
    {
        var journal = new FichierRevertEnAttente(_chemin);

        var r = await ValidationComplete.ValiderAsync([new Levier("a", _machine), new Levier("b", _machine)], null!, null!, journal,
            s => s.TweakId == "a" ? throw new InvalidOperationException("ADLX absent") : Relire(s));

        r.Texte.Should().Contain("relecture impossible : ADLX absent").And.Contain("=== b");
        r.NonRemis.Should().ContainSingle().Which.Should().Contain("n'a pas pu le confirmer");
        _machine.Values.Should().OnlyContain(v => v == 0);
    }

    // Le bilan compte, et un refus de l'accord de Windows arrête tout au lieu de se reproposer.
    [Fact]
    public async Task Le_bilan_compte_et_un_refus_de_Windows_arrete_la_boucle()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var r = await ValidationComplete.ValiderAsync(
            [new Levier("a", _machine), new Levier("b", _machine, concerne: false), new Refuse("c", "TWEAK.SDK_UNAVAILABLE"),
             new Refuse("d", ErrorCodes.ElevationDeclined), new Levier("e", _machine)], null!, null!, journal, Relire);

        (r.Valides, r.NonTouches, r.Refuses).Should().Be((1, 1, 1));
        r.ValidesIds.Should().Equal("a");
        r.EssayesIds.Should().Equal(["a", "c"], "b non concerné ; d : l'accord Windows refusé ne dit rien du levier");
        r.ArretPar.Should().Be(ValidationComplete.Arret.RefusDeWindows);
        r.Texte.Should().NotContain("=== e");
        journal.Lire().Value.Should().BeNull();
    }

    // Posé en partie, retour arrière raté : à vérifier, journal gardé, boucle arrêtée.
    [Fact]
    public async Task Un_levier_pose_en_partie_reste_au_journal_et_arrete_la_boucle()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var r = await ValidationComplete.ValiderAsync([new Refuse("a", ErrorCodes.TweakPartiallyApplied), new Levier("b", _machine)],
            null!, null!, journal, Relire);

        r.NonRemis.Should().ContainSingle().Which.Should().Contain("posé en partie");
        r.ArretPar.Should().Be(ValidationComplete.Arret.LevierNonRendu);
        journal.Lire().Value!.TweakId.Should().Be("a");
        _machine.Should().NotContainKey("b");
    }

    [Fact]
    public async Task Un_jeu_ouvert_en_cours_arrete_avant_le_levier_suivant()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var appels = 0;
        var r = await ValidationComplete.ValiderAsync([new Levier("a", _machine), new Levier("b", _machine)], null!, null!,
            journal, Relire, jeuOuvert: () => appels++ > 0);

        r.ArretPar.Should().Be(ValidationComplete.Arret.JeuOuvert);
        r.Valides.Should().Be(1);
        _machine.Should().NotContainKey("b");
    }

    // L'arrêt demandé par le joueur se lit entre deux leviers : celui en cours est rendu, le suivant n'est pas touché.
    [Fact]
    public async Task Un_arret_demande_arrete_avant_le_levier_suivant_sans_rien_laisser()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        var appels = 0;
        var r = await ValidationComplete.ValiderAsync([new Levier("a", _machine), new Levier("b", _machine)], null!, null!,
            journal, Relire, arretDemande: () => appels++ > 0);

        r.ArretPar.Should().Be(ValidationComplete.Arret.ParLeJoueur);
        r.Valides.Should().Be(1);
        r.NonRemis.Should().BeEmpty("le levier en cours a fini son tour : rendu et relu");
        _machine.Should().NotContainKey("b");
        journal.Lire().Value.Should().BeNull("rien ne reste au journal");
    }

    // Un autre levier ne s'écrit pas par-dessus un essai pas encore rendu.
    [Fact]
    public void Le_journal_refuse_un_autre_essai_par_dessus_un_essai_pas_rendu()
    {
        var journal = new FichierRevertEnAttente(_chemin);
        journal.Poser("a", new TweakSnapshot("a", "0")).IsOk.Should().BeTrue();

        journal.Poser("b", null).IsOk.Should().BeFalse();
        journal.Poser("a", new TweakSnapshot("a", "1")).IsOk.Should().BeTrue("le même levier complète son essai");
        journal.Lire().Value!.TweakId.Should().Be("a");

        // Le MÊME levier, retenté, ne recapture pas par-dessus son origine gardée.
        journal.Poser("a", null).IsOk.Should().BeFalse();
        journal.Lire().Value!.Snapshot!.OriginalValueJson.Should().Be("1");

        journal.Lever();
        journal.Poser("b", null).IsOk.Should().BeTrue();
    }
    // Le rapport de l'exercice nomme ses étapes et dit ce qui a été lu à chacune ; il ne reprend pas l'« origine » de chaque
    // relecture, qui aux étapes 2 et 3 était l'état intermédiaire.
    [Fact]
    public async Task L_exercice_nomme_ses_etapes_et_ce_qui_a_ete_lu()
    {
        var (r, _) = await Exercer(new Exercable(_machine), s => new ValidationComplete.Lecture(
            _machine["a"].ToString() == s.AppliedValueJson, "Applied : origine 0, demandé ?, lu 1", Lu: _machine["a"].ToString()));

        r.Texte.Should().Contain(Docteur.T("VC.Exercice.Etapes", Docteur.T("VC.Exercice.Lu", "0"), Docteur.T("VC.Exercice.Lu", "1"),
            Docteur.T("VC.Exercice.Lu", "1"))).And.NotContain("demandé");
    }
    // Une étape sans valeur lue (relecture illisible) reprend le texte complet de sa relecture, jamais « lu » de rien.
    [Fact]
    public async Task Une_etape_sans_valeur_lue_reprend_le_texte_de_sa_relecture()
    {
        var relectures = 0;   // hors cible, posé, rendu : la deuxième ne sait pas lire
        var (r, _) = await Exercer(new Exercable(_machine), s => ++relectures == 2
            ? new ValidationComplete.Lecture(true, "relecture du levier impossible ici")
            : new ValidationComplete.Lecture(_machine["a"].ToString() == s.AppliedValueJson, "-", Lu: _machine["a"].ToString()));

        r.Texte.Should().Contain(Docteur.T("VC.Exercice.Etapes", Docteur.T("VC.Exercice.Lu", "0"), "relecture du levier impossible ici",
            Docteur.T("VC.Exercice.Lu", "1")));
    }
}
