using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// L'autopilote : la boucle qui transforme DrDXT en autre chose qu'un catalogue de tweaks.
///
/// `OcSweep` avait deja la bonne philosophie — appliquer, mesurer, trancher, garder le meilleur —
/// et n'etait appele que par `tools/octune`. Il ne savait balayer qu'un offset GPU entier. Ce
/// moteur est la meme idee, generique : il ne sait rien du GPU ni des FPS, il sait tenter des
/// actions et **refuser de garder ce qu'il ne peut pas prouver**.
///
/// Ce que ces tests protegent, dans l'ordre d'importance :
///   1. la machine finit toujours dans le meilleur etat prouve, exception comprise ;
///   2. rien n'est garde sans preuve au-dessus de la resolution de la scene ;
///   3. une ecriture qui ne prend pas n'est jamais suivie d'une mesure ;
///   4. une scene qui derive n'autorise aucun essai.
/// </summary>
public class AutopilotEngineTests
{
    /// <summary>Un candidat de laboratoire : il multiplie la mesure par un facteur quand il est
    /// applique, et raconte tout ce qu'on lui fait.</summary>
    private sealed class CandidatFake : IAutopilotCandidate
    {
        private readonly double _facteur;
        public bool Applique;
        public int Applications, Annulations;
        public bool RelectureMenteuse, LeveALApply;
        public int EchoueALApplication;   // 0 = jamais ; sinon, la N-ieme application echoue
        public int EchoueALAnnulation;    // 0 = jamais ; sinon, la N-ieme annulation echoue (etat laisse tel quel)

        public CandidatFake(double facteur, CandidateKind nature = CandidateKind.AutoHot,
            bool stabiliteRequise = false)
        {
            _facteur = facteur;
            Descriptor = new CandidateDescriptor("essai", nature, RiskLevel: 1, stabiliteRequise,
                new[] { "fps.moyen" });
        }

        public CandidateDescriptor Descriptor { get; }
        public double Facteur => Applique ? _facteur : 1.0;

        public Result Apply()
        {
            Applications++;
            if (LeveALApply) throw new InvalidOperationException("le pilote a rendu l'ame");
            if (Applications == EchoueALApplication) return Result.Fail(ErrorCodes.IoFailure, "bascule refusée");
            Applique = true;
            return Result.Ok();
        }

        public Result Revert()
        {
            Annulations++;
            if (Annulations == EchoueALAnnulation) return Result.Fail(ErrorCodes.IoFailure, "retour refusé");
            Applique = false;
            return Result.Ok();
        }

        // `RelectureMenteuse` reproduit un defaut reel : le 01/09, une limite de puissance
        // « appliquee a 110 % » se relisait a 100 % — le pilote clampait en silence.
        public Result<bool> ReadBack() => Result<bool>.Ok(Applique && !RelectureMenteuse);
    }

    /// <summary>Une scene stable a 100, que le candidat applique multiplie.</summary>
    private static Func<double> Scene(CandidatFake c, double bruit = 0.0)
    {
        var i = 0;
        return () => 100.0 * c.Facteur + (i++ % 2 == 0 ? bruit : -bruit);
    }

    [Fact]
    public void Une_session_ANNULEE_rend_l_etat_de_reference_et_ne_tente_plus_rien()
    {
        // L8 a — la passe avalait toute exception comme « passe a 0 » : une annulation serait
        // passee inapercue, et la session aurait continue levier pose.
        var c = new CandidatFake(1.10);
        using var cts = new CancellationTokenSource();
        var appels = 0;
        GameMetrics? Mesurer()
        {
            if (++appels == 5) cts.Cancel();   // en pleine alternance, levier pose
            var v = 100.0 * c.Facteur + (appels % 2 == 0 ? 0.5 : -0.5);
            return new GameMetrics(v, v);
        }

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c, new CandidatFake(1.20) }, Mesurer,
            passesAa: 3, alternancesMax: 8, ct: cts.Token);

        c.Applique.Should().BeFalse("l'etat de reference est rendu");
        profil.Results.Should().ContainSingle("le second candidat n'est jamais tente")
            .Which.Outcome.Should().Be(CandidateOutcome.NotAttempted);
    }

    [Fact]
    public void Un_gain_PROUVE_est_conserve_et_la_machine_reste_dessus()
    {
        var c = new CandidatFake(facteur: 1.20);

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c, bruit: 0.2));

        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Kept);
        c.Applique.Should().BeTrue("la machine doit FINIR sur le reglage gagnant");
        profil.EndedInBestState.Should().BeTrue();
    }

    // 26/09/2026 : ce chemin n'avait aucun test (une mutation qui le retirait restait verte). Une bascule
    // qui echoue pendant l'alternance laisse un etat que le moteur n'a pas relu : il ne pretend pas
    // avoir fini dans le meilleur etat.
    [Fact]
    public void Une_bascule_ratee_pendant_l_alternance_ne_se_dit_pas_dans_le_meilleur_etat()
    {
        // 1re application = l'essai (relu, pris) ; 2e = la premiere bascule vers B de l'alternance.
        var c = new CandidatFake(facteur: 1.20) { EchoueALApplication = 2 };

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c, bruit: 0.2));

        profil.EndedInBestState.Should().BeFalse("une bascule a echoue en cours d'essai");
        profil.Results.Single().Outcome.Should().NotBe(CandidateOutcome.Kept);
    }

    // 26/09/2026 : les cinq autres chemins de retour. Une mutation qui retirait le compte de l'echec
    // restait verte sur chacun : le moteur pouvait dire « meilleur etat » apres un retour refuse.
    // Les rangs (N-ieme annulation ou application) suivent le protocole par defaut, un bloc A-b-b-a.

    [Fact]
    public void Un_retour_refuse_apres_une_ecriture_qui_n_a_pas_pris_se_dit()
    {
        var c = new CandidatFake(facteur: 1.20) { RelectureMenteuse = true, EchoueALAnnulation = 1 };
        AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c)).EndedInBestState.Should().BeFalse();
    }

    [Fact]
    public void Un_retour_refuse_apres_une_instabilite_se_dit()
    {
        var c = new CandidatFake(facteur: 1.20, stabiliteRequise: true) { EchoueALAnnulation = 1 };
        AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c), stable: () => false)
            .EndedInBestState.Should().BeFalse();
    }

    [Fact]
    public void Un_retour_refuse_apres_une_regression_se_dit()
    {
        // Les annulations 1 et 2 sont les « a » de l'alternance ; la 3e est le retour final.
        var c = new CandidatFake(facteur: 0.85) { EchoueALAnnulation = 3 };
        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c, bruit: 0.2));
        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.RejectedRegression);
        profil.EndedInBestState.Should().BeFalse();
    }

    [Fact]
    public void Un_retour_refuse_apres_une_exception_se_dit()
    {
        var c = new CandidatFake(facteur: 1.20) { LeveALApply = true, EchoueALAnnulation = 1 };
        AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c)).EndedInBestState.Should().BeFalse();
    }

    [Fact]
    public void Un_gain_garde_qui_ne_reprend_pas_se_dit()
    {
        // Applications : 1 l'essai, 2 et 3 les « b » de l'alternance, 4 la reprise du reglage garde.
        var c = new CandidatFake(facteur: 1.20) { EchoueALApplication = 4 };
        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c, bruit: 0.2));
        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Kept);
        profil.EndedInBestState.Should().BeFalse();
    }

    [Fact]
    public void Une_REGRESSION_prouvee_est_annulee()
    {
        var c = new CandidatFake(facteur: 0.85);

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c, bruit: 0.2));

        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.RejectedRegression);
        c.Applique.Should().BeFalse("on ne garde jamais une degradation prouvee");
    }

    [Fact]
    public void Un_effet_SOUS_LA_RESOLUTION_est_annule_au_lieu_d_etre_garde()
    {
        // La difference entre un autopilote honnete et un placebo. +0,3 % dans une scene qui
        // bruite a 2 % : on ne garde pas un changement qu'on ne sait pas justifier.
        var c = new CandidatFake(facteur: 1.003);

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c, bruit: 2.0));

        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.RejectedUnresolved);
        c.Applique.Should().BeFalse();
    }

    [Fact]
    public void Une_ecriture_qui_NE_PREND_PAS_n_est_jamais_suivie_d_une_mesure()
    {
        // Le cas mesure le 01/09 : « power limit applied: 110% » puis relecture a 100 %.
        var c = new CandidatFake(facteur: 1.20) { RelectureMenteuse = true };

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c));

        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.ApplyFailed);
        profil.Results.Single().Evidence.Should().BeNull("rien n'a ete mesure, donc rien n'est affirme");
        c.Applique.Should().BeFalse();
    }

    [Fact]
    public void Une_INSTABILITE_annule_immediatement_sans_mesurer()
    {
        var c = new CandidatFake(facteur: 1.20, stabiliteRequise: true);

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c), stable: () => false);

        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.Unstable);
        c.Applique.Should().BeFalse();
    }

    [Fact]
    public void Une_EXCEPTION_en_plein_essai_laisse_la_machine_a_l_etat_de_reference()
    {
        // La garantie qui compte le plus. Un autopilote qui plante au milieu et laisse un
        // overclock applique est pire que pas d'autopilote du tout.
        var c = new CandidatFake(facteur: 1.20) { LeveALApply = true };

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c));

        c.Applique.Should().BeFalse();
        c.Annulations.Should().BeGreaterThan(0, "l'annulation doit avoir ete tentee");
        profil.Results.Single().Detail.Should().Contain("restauré");
    }

    [Fact]
    public void Une_scene_qui_DERIVE_interdit_tout_essai()
    {
        // La machine monte toute seule : tenter un levier ici crediterait le temps ecoule.
        var c = new CandidatFake(facteur: 1.20);
        var t = 0;

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, () => 100.0 + 10 * t++);

        profil.Scene.Verdict.Should().Be(SceneVerdict.DriftDominant);
        profil.Results.Single().Outcome.Should().Be(CandidateOutcome.NotAttempted);
        c.Applications.Should().Be(0, "rien ne doit avoir ete ecrit sur la machine");
    }

    [Fact]
    public void Une_action_qui_demande_un_REBOOT_n_est_pas_tentee_mais_est_DITE()
    {
        // Elle apparait dans le profil avec sa raison : « non teste » n'est pas « inexistant ».
        var c = new CandidatFake(facteur: 1.20, nature: CandidateKind.AutoReboot);

        var profil = AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c, bruit: 0.2));

        var r = profil.Results.Single();
        r.Outcome.Should().Be(CandidateOutcome.NotAttempted);
        r.Detail.Should().Contain("AutoReboot");
        c.Applications.Should().Be(0);
    }

    [Fact]
    public void Un_reglage_BIOS_est_recommande_jamais_applique()
    {
        var c = new CandidatFake(facteur: 1.20, nature: CandidateKind.GuidedBios);

        AutopilotEngine.Run(new IAutopilotCandidate[] { c }, Scene(c, bruit: 0.2));

        c.Applications.Should().Be(0, "DrDXT n'ecrit pas dans le BIOS");
    }

    [Fact]
    public void Plusieurs_candidats_sont_tentes_et_seuls_les_gagnants_restent()
    {
        var bon = new CandidatFake(facteur: 1.20);
        var mauvais = new CandidatFake(facteur: 0.80);

        // Chaque candidat a sa propre scene : ce test verifie l'enchainement, pas l'interaction
        // entre reglages — qui est un autre probleme, et un autre lot.
        var profil = AutopilotEngine.Run(
            new IAutopilotCandidate[] { bon, mauvais },
            () => 100.0 * bon.Facteur * mauvais.Facteur);

        profil.Results.Should().HaveCount(2);
        bon.Applique.Should().BeTrue();
        mauvais.Applique.Should().BeFalse();
        profil.Kept.Should().HaveCount(1);
    }
}
