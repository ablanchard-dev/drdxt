using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// L'adaptateur levier → candidat. Le test qui compte est celui du protocole
/// A-b-b-a : deux applications de suite ne doivent jamais faire recapturer la cible comme
/// « original ».
/// </summary>
public class TweakCandidateTests
{
    private const string Chemin = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    private static string Fil(PrivilegedOp op) => PrivilegedOpCodec.Serialize(op).Value!;

    private sealed class LevierFaux : ITweak
    {
        public List<TweakSnapshot> Rendus { get; } = new();
        public List<TweakSnapshot> Annules { get; } = new();

        public string Id => "latency.hagsOn";
        public TweakCategory Category => TweakCategory.Latency;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => true;
        public string SummaryKey => "tweak.latency.hags.enable";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;

        public Task<Result<TweakSnapshot>> ApplyAsync()
        {
            if (MeurtPendantApply) throw new InvalidOperationException("l'App est tuee ici");

            var s = new TweakSnapshot(Id,
                Fil(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, "HwSchMode", RegistryValueType.DWord, "1")),
                Fil(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, Chemin, "HwSchMode", RegistryValueType.DWord, "2")));
            Rendus.Add(s);
            // Comme le client élevé de l'App : une écriture journalisée posée dans une portée note son acquit.
            PorteeDePose.Courante?.Noter(() => { Log?.Add("acquit"); return Task.FromResult(Result.Ok()); });
            return Task.FromResult(Result<TweakSnapshot>.Ok(s));
        }

        public List<string>? Log;

        public bool RevertEchoue;

        /// <summary>La session meurt pendant l'ecriture machine — la fenetre exacte ou le journal
        /// porte « essai en cours » sans savoir encore revenir.</summary>
        public bool MeurtPendantApply;

        public Task<Result> RevertAsync(TweakSnapshot snapshot)
        {
            Annules.Add(snapshot);
            return Task.FromResult(RevertEchoue ? Result.Fail("X", "le pilote refuse") : Result.Ok());
        }
    }

    /// <summary>Un journal en memoire. `Sonde` dit combien d'ecritures machine avaient eu lieu au
    /// Premier `Poser` — la preuve que le journal passe avant la machine.</summary>
    private sealed class JournalFaux : IRevertEnAttente
    {
        public RevertEnAttenteEntree? Entree;
        public bool Refuse;
        public Func<int>? Sonde;
        public int? EcrituresAuPremierPoser;

        public bool RefuseApresPremier;
        public List<string>? Log;
        private int _poses;

        public Result Poser(string tweakId, TweakSnapshot? snapshot)
        {
            if (Refuse || (RefuseApresPremier && _poses++ >= 1)) return Result.Fail("X", "disque plein");
            EcrituresAuPremierPoser ??= Sonde?.Invoke();
            if (snapshot is not null) Log?.Add("origine");
            Entree = new RevertEnAttenteEntree(tweakId, snapshot);
            return Result.Ok();
        }

        public Result Lever() { Entree = null; return Result.Ok(); }
        public Result Garder(string tweakId, TweakSnapshot snapshot) { Entree = null; return Result.Ok(); }
        public Result<RevertEnAttenteEntree?> Lire() => Result<RevertEnAttenteEntree?>.Ok(Entree);
    }

    private sealed class Registre(uint? valeur) : IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name) => Result<uint?>.Ok(valeur);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name) => Result<string?>.Ok(null);
    }

    private static CandidateDescriptor Descripteur(string id = "latency.hagsOn") =>
        new(id, CandidateKind.AutoHot, RiskLevel: 1, NeedsStability: false, new[] { "latence" });

    // Le helper garde l'origine de la pose jusqu'à ce que le journal de l'essai la porte : l'acquit part après, une fois.
    [Fact]
    public void La_pose_s_acquitte_une_fois_apres_que_le_journal_porte_l_origine()
    {
        var log = new List<string>();
        var c = new TweakCandidate(new LevierFaux { Log = log }, Descripteur(), new Registre(2), new JournalFaux { Log = log });

        c.Apply().IsOk.Should().BeTrue();

        log.Should().Equal("origine", "acquit");
        PorteeDePose.Courante.Should().BeNull("la portée ne survit pas à la pose");
    }

    // Sans origine au journal de l'essai, seul le helper sait revenir : rien n'est acquitté.
    [Fact]
    public void Une_origine_que_le_journal_refuse_n_est_pas_acquittee()
    {
        var log = new List<string>();
        var c = new TweakCandidate(new LevierFaux { Log = log, RevertEchoue = true }, Descripteur(), new Registre(2),
            new JournalFaux { Log = log, RefuseApresPremier = true });

        c.Apply().IsOk.Should().BeFalse();

        log.Should().NotContain("acquit");
    }

    [Fact]
    public void Une_cible_RELUE_en_place_autorise_la_mesure()
    {
        var c = new TweakCandidate(new LevierFaux(), Descripteur(), new Registre(2), new JournalFaux());

        c.Apply().IsOk.Should().BeTrue();
        c.ReadBack().Value.Should().BeTrue();
        c.DernierePreuve!.Verdict.Should().Be(EtatApplique.Applied);
    }

    [Fact]
    public void Une_cible_qui_n_a_PAS_pris_interdit_la_mesure()
    {
        var c = new TweakCandidate(new LevierFaux(), Descripteur(), new Registre(1), new JournalFaux());

        c.Apply();
        c.ReadBack().Value.Should().BeFalse();
        c.DernierePreuve!.Verdict.Should().Be(EtatApplique.NotApplied);
    }

    [Fact]
    public void A_B_B_A_UNE_seule_vraie_application_et_le_revert_rend_le_PREMIER_original()
    {
        // Le piege : une seconde application recapturerait la cible comme « original ».
        var levier = new LevierFaux();
        var c = new TweakCandidate(levier, Descripteur(), new Registre(2), new JournalFaux());

        c.Apply(); c.Apply();     // B, B
        c.Revert(); c.Revert();   // A, A

        levier.Rendus.Should().HaveCount(1, "un levier deja en place n'est jamais reapplique");
        levier.Annules.Should().ContainSingle().Which.Should().BeSameAs(levier.Rendus[0]);
    }

    [Fact]
    public void Un_revert_sans_rien_d_applique_ne_touche_rien()
    {
        var levier = new LevierFaux();

        new TweakCandidate(levier, Descripteur(), new Registre(2), new JournalFaux()).Revert().IsOk.Should().BeTrue();
        levier.Annules.Should().BeEmpty();
    }

    [Fact]
    public void Relire_sans_avoir_applique_ne_pretend_rien()
        => new TweakCandidate(new LevierFaux(), Descripteur(), new Registre(2), new JournalFaux()).ReadBack().Value.Should().BeFalse();

    [Fact]
    public void Un_descripteur_qui_ne_decrit_pas_CE_levier_est_refuse()
    {
        var act = () => new TweakCandidate(new LevierFaux(), Descripteur("gpu.core_offset"), new Registre(2), new JournalFaux());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dans_le_moteur_la_machine_finit_dans_l_etat_de_reference()
    {
        var levier = new LevierFaux();
        var c = new TweakCandidate(levier, Descripteur(), new Registre(2), new JournalFaux());

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, () => 100.0,
            passesAa: 3, stable: null, but: OptimizationGoal.Competitive, alternancesMax: 4);

        profil.EndedInBestState.Should().BeTrue();
        levier.Annules.Should().HaveCount(levier.Rendus.Count, "chaque application a ete rendue");
    }

    // ══ Le filet d'une session tuee ═══════════════════════════════════════════════════

    [Fact]
    public void Le_journal_est_pose_AVANT_la_premiere_ecriture_machine()
    {
        var levier = new LevierFaux();
        var journal = new JournalFaux { Sonde = () => levier.Rendus.Count };

        new TweakCandidate(levier, Descripteur(), new Registre(2), journal).Apply();

        journal.EcrituresAuPremierPoser.Should().Be(0, "le journal passe avant la machine");
        journal.Entree!.Snapshot.Should().BeSameAs(levier.Rendus[0], "puis il est complete de quoi revenir");
    }

    [Fact]
    public void Sans_journal_rien_n_est_ecrit()
    {
        var levier = new LevierFaux();

        new TweakCandidate(levier, Descripteur(), new Registre(2), new JournalFaux { Refuse = true })
            .Apply().IsOk.Should().BeFalse();
        levier.Rendus.Should().BeEmpty();
    }

    [Fact]
    public void Un_journal_qu_on_ne_peut_pas_completer_fait_rendre_le_levier_aussitot()
    {
        // Le second Poser etait ignore — le levier restait pose avec un
        // journal sans inverse, et le demarrage suivant ne pouvait que dire « verifie ce reglage ».
        var levier = new LevierFaux();
        var journal = new JournalFaux { RefuseApresPremier = true };

        new TweakCandidate(levier, Descripteur(), new Registre(2), journal).Apply().IsOk.Should().BeFalse();

        levier.Annules.Should().ContainSingle().Which.Should().BeSameAs(levier.Rendus[0]);
        journal.Entree.Should().BeNull();
    }

    [Fact]
    public void Un_revert_reussi_leve_le_journal_un_revert_rate_le_garde()
    {
        var levier = new LevierFaux { RevertEchoue = true };
        var journal = new JournalFaux();
        var c = new TweakCandidate(levier, Descripteur(), new Registre(2), journal);

        c.Apply();
        c.Revert();
        journal.Entree.Should().NotBeNull("un revert rate doit pouvoir se retenter au demarrage");

        levier.RevertEchoue = false;
        c.Revert();
        journal.Entree.Should().BeNull();
    }

    [Fact]
    public void Une_session_TUEE_est_rendue_au_demarrage_suivant()
    {
        var chemin = Path.Combine(Path.GetTempPath(), $"drdxt-revert-{Guid.NewGuid():N}.json");
        try
        {
            var levier = new LevierFaux();
            new TweakCandidate(levier, Descripteur(), new Registre(2), new FichierRevertEnAttente(chemin)).Apply();
            // ... et l'App meurt ici : aucun Revert.

            var phrase = RevertAuDemarrage.Executer(new FichierRevertEnAttente(chemin),
                new TweakCatalog(new ITweak[] { levier }));

            levier.Annules.Should().ContainSingle().Which.Should().Be(levier.Rendus[0]);
            File.Exists(chemin).Should().BeFalse("rendu, le journal est leve");
            phrase.Should().Contain("remis comme avant");
        }
        finally { File.Delete(chemin); }
    }

    [Fact]
    public void Au_demarrage_un_revert_rate_garde_le_journal_pour_la_fois_suivante()
    {
        var chemin = Path.Combine(Path.GetTempPath(), $"drdxt-revert-{Guid.NewGuid():N}.json");
        try
        {
            var levier = new LevierFaux { RevertEchoue = true };
            new TweakCandidate(levier, Descripteur(), new Registre(2), new FichierRevertEnAttente(chemin)).Apply();

            RevertAuDemarrage.Executer(new FichierRevertEnAttente(chemin), new TweakCatalog(new ITweak[] { levier }))
                .Should().Contain("Nouvel essai");
            File.Exists(chemin).Should().BeTrue();
        }
        finally { File.Delete(chemin); }
    }

    [Fact]
    public void Une_session_tuee_AVANT_que_l_inverse_soit_garde_leve_le_journal_et_le_DIT()
    {
        // La fenetre pour laquelle ce journal existe, et le seul etat qu'aucun test ne faisait
        // Ecrire par le produit : entre « essai en cours » et le snapshot complete. Rien n'est pose
        // a la main ici — c'est `Apply()` qui ecrit le fichier, puis meurt dedans.
        var chemin = Path.Combine(Path.GetTempPath(), $"drdxt-revert-{Guid.NewGuid():N}.json");
        try
        {
            var levier = new LevierFaux { MeurtPendantApply = true };
            var mort = () => new TweakCandidate(levier, Descripteur(), new Registre(2),
                new FichierRevertEnAttente(chemin)).Apply();

            mort.Should().Throw<InvalidOperationException>();
            levier.Rendus.Should().BeEmpty("la machine n'a rien recu : on est bien dans la fenetre");

            var phrase = RevertAuDemarrage.Executer(new FichierRevertEnAttente(chemin),
                new TweakCatalog(new ITweak[] { levier }));

            // L'App ne sait pas revenir : le helper, si la pose a eu lieu. Le message le DIT, sans promettre une remise.
            phrase.Should().Be(Docteur.T("RV.SansOrigineGardee", "latency.hagsOn"));
            levier.Annules.Should().BeEmpty("il n'y a aucun inverse a jouer : en inventer un serait pire");
            File.Exists(chemin).Should().BeFalse(
                "le journal est leve, sinon la meme phrase revient a CHAQUE demarrage, pour toujours");
        }
        finally { File.Delete(chemin); }
    }

    [Fact]
    public void Au_demarrage_un_levier_DISPARU_de_cette_version_est_dit_et_le_journal_RESTE()
    {
        // La mise a jour qui retire un levier : le journal de la session precedente le nomme
        // encore, le catalogue de cette version ne le connait plus. Rien n'est pose a la main —
        // c'est `Apply()` qui a ecrit le fichier ; seul le catalogue a change de version.
        var chemin = Path.Combine(Path.GetTempPath(), $"drdxt-revert-{Guid.NewGuid():N}.json");
        try
        {
            var levier = new LevierFaux();
            new TweakCandidate(levier, Descripteur(), new Registre(2), new FichierRevertEnAttente(chemin)).Apply();

            var phrase = RevertAuDemarrage.Executer(new FichierRevertEnAttente(chemin),
                new TweakCatalog(Array.Empty<ITweak>()));

            phrase.Should().Contain("n'existe plus dans cette version");
            levier.Annules.Should().BeEmpty("il n'y a plus de code capable de rendre ce levier");
            File.Exists(chemin).Should().BeTrue(
                "rien n'a ete rendu : lever le journal effacerait la seule trace de ce qui reste pose");
        }
        finally { File.Delete(chemin); }
    }

    [Fact]
    public void Au_demarrage_un_journal_ILLISIBLE_est_DIT_et_n_est_pas_pris_pour_un_journal_vide()
    {
        // Le piege : « illisible » et « rien en attente » se ressemblent, et les confondre fait
        // taire le produit alors qu'un reglage d'essai est peut-etre encore pose. Le fichier
        // tronque ici est celui que le produit vient d'ecrire — l'ecriture atomique empeche de
        // l'obtenir autrement, donc la coupure est simulee apres coup, sur un vrai contenu.
        var chemin = Path.Combine(Path.GetTempPath(), $"drdxt-revert-{Guid.NewGuid():N}.json");
        try
        {
            var levier = new LevierFaux();
            new TweakCandidate(levier, Descripteur(), new Registre(2), new FichierRevertEnAttente(chemin)).Apply();

            var ecrit = File.ReadAllText(chemin);
            File.WriteAllText(chemin, ecrit[..(ecrit.Length / 2)]);

            var phrase = RevertAuDemarrage.Executer(new FichierRevertEnAttente(chemin),
                new TweakCatalog(new ITweak[] { levier }));

            phrase.Should().NotBeNull("se taire ferait croire qu'il n'y avait rien en attente")
                .And.Contain("n'a pas pu être lu")
                .And.Contain("peut-être encore en place");
            levier.Annules.Should().BeEmpty("on ne sait pas QUOI rendre : inventer serait pire");
            File.Exists(chemin).Should().BeTrue("un journal illisible n'est pas un journal a effacer");
        }
        finally { File.Delete(chemin); }
    }

    [Fact]
    public void Au_demarrage_rien_en_attente_ne_dit_rien()
        => RevertAuDemarrage.Executer(new JournalFaux(), new TweakCatalog(Array.Empty<ITweak>()))
            .Should().BeNull();
    // Le service du composite X3D se relit par son état : sans lecteur de services, le candidat rendait Unavailable et ne
    // pouvait jamais mesurer.
    private sealed class LevierService : ITweak
    {
        public string Id => "latency.hagsOn";
        public TweakCategory Category => default;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => false;
        public string SummaryKey => Id;
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => Task.FromResult(Result<TweakSnapshot>.Ok(new TweakSnapshot(Id,
            PrivilegedOpCodec.Serialize(new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Stop)).Value!,
            PrivilegedOpCodec.Serialize(new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start)).Value!)));
        public Task<Result> RevertAsync(TweakSnapshot snapshot) => Task.FromResult(Result.Ok());
    }

    [Fact]
    public void Un_service_demarre_se_relit_par_le_lecteur_de_services()
    {
        var services = NSubstitute.Substitute.For<IServiceStateReader>();
        NSubstitute.SubstituteExtensions.Returns(services.GetServiceState("AMD3DVCacheSvc"), Result<ServiceState>.Ok(ServiceState.Running));
        var c = new TweakCandidate(new LevierService(), Descripteur(), new Registre(null), new JournalFaux(), services: services);

        c.Apply().IsOk.Should().BeTrue();
        c.DernierePreuve.Should().BeNull();
        c.ReadBack().Value.Should().BeTrue();
        c.DernierePreuve!.Verdict.Should().Be(EtatApplique.Applied);
    }
}
