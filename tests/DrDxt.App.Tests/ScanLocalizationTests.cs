using DrDxt.App.ViewModels;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Infrastructure.Localization;
using FluentAssertions;

namespace DrDxt.App.Tests;

public class ScanLocalizationTests
{
    private static readonly ILocalizer Localizer = new ResxLocalizer();

    [Theory]
    [InlineData("Scan.Button.Start")]
    [InlineData("Scan.State.Scanning")]
    [InlineData("Scan.Degraded.PresentMon")]
    [InlineData("Scan.Degraded.ServiceUnavailable")]
    [InlineData("Scan.Error.Detection")]
    [InlineData("Scan.Error.Analyse")]
    [InlineData("Scan.Error.Settings")]
    [InlineData("Scan.Gating.Note")]
    [InlineData("Scan.Findings.Header")]
    [InlineData("Scan.Bios.Header")]
    [InlineData("Scan.Bios.GuidanceOnly")]
    [InlineData("Scan.Bios.GainRange")]
    [InlineData("Scan.Bios.Guide.Header")]
    [InlineData("Scan.Bios.Guide.Verified")]
    [InlineData("Scan.Bios.Guide.Unverified")]
    [InlineData("Scan.Bios.Guide.CsmSuppressed")]
    [InlineData("Scan.Bios.Guide.EnterKey")]
    [InlineData("Scan.Bios.Guide.SaveKey")]
    [InlineData("Scan.Bios.Guide.RecoveryHeader")]
    // L'attente d'affichage : la signature d'un cap, d'un vsync ou du compositeur.
    [InlineData("Scan.PresentStall.Measured")]
    // La saccade : les 5 % de trames les plus lentes, la grandeur qui resout le mieux.
    [InlineData("Scan.Stutter.Measured")]
    // La cadence : ce qu'une moyenne d'images ne dira jamais.
    [InlineData("Scan.Cadence.Measured")]
    // Ce que la memoire rend vraiment, mesure et non lu sur une etiquette.
    [InlineData("Scan.Memory.Measured")]
    // La derive du journal : trois phrases, dont celle qui dit qu'on n'a rien resolu.
    [InlineData("Scan.Drift.Regression")]
    [InlineData("Scan.Drift.Gain")]
    [InlineData("Scan.Drift.None")]
    [InlineData("Finding.Memory.XmpOff")]
    [InlineData("Finding.Scheduling.X3dCcd")]
    [InlineData("Finding.Latency.HagsOff")]
    [InlineData("Finding.Latency.VrrOff")]
    [InlineData("Finding.Latency.VrrInactiveSupportUnknown")]
    [InlineData("Finding.Latency.VrrUnknown")]
    [InlineData("Finding.Gpu.ReBarOff")]
    [InlineData("Finding.Gpu.ReBarUnknown")]
    // ── Dashboard rebuild (M1) ──
    [InlineData("UI.App.Name")]
    [InlineData("UI.Qual.Excellent")]
    [InlineData("UI.Qual.Good")]
    [InlineData("UI.Qual.Fair")]
    [InlineData("UI.Qual.Poor")]
    [InlineData("UI.Idle.HeadlineLead")]
    [InlineData("UI.Idle.HeadlineAccent")]
    [InlineData("UI.Idle.HeroTitle")]
    [InlineData("UI.Idle.HeroBody")]
    [InlineData("UI.Trust.Local")]
    [InlineData("UI.Trust.Data")]
    [InlineData("UI.Result.HeadlineLead")]
    [InlineData("UI.Result.ScanDone")]
    [InlineData("UI.Result.Subtitle")]
    [InlineData("UI.Result.NoCritical")]
    [InlineData("UI.Cta.Optimize")]
    // 11/09/2026 — la scene declaree avant la mesure en jeu.
    [InlineData("Scan.Scene.Question")]
    [InlineData("Scan.Scene.Unknown")]
    [InlineData("Scan.Scene.Gameplay")]
    [InlineData("Scan.Scene.Training")]
    [InlineData("Scan.Scene.Menu")]
    [InlineData("Scan.Scene.Description")]
    [InlineData("Scan.Scene.Declared")]
    // 08/10/2026 — des arrets recents ne bloquent plus : l'ecran dit la garde qui les remplace.
    [InlineData("Apply.SousGarde")]
    // 11/09/2026 — un keep dont l'origine n'a pas atteint le manifeste signe.
    [InlineData("Optimize.KeptOriginNotSigned")]
    // 11/09/2026 — le manifeste signe illisible : refus nomme, bouton de reprise, et son issue.
    [InlineData("Apply.MoatUnarmed")]
    [InlineData("Apply.ManifestUnreadable")]
    // 11/09/2026 — manifeste ecrit, repere de derive non pose par le helper.
    [InlineData("Apply.ReferenceNotSeeded")]
    [InlineData("Apply.ManifestReset")]
    [InlineData("Apply.ManifestResetDone")]
    [InlineData("Apply.ManifestResetFailed")]
    [InlineData("UI.Cta.Rescan")]
    [InlineData("UI.Profile.Unverified")]
    [InlineData("Carte.Propose.NonValide")]
    [InlineData("Ping.Protection.Description.Service")]
    [InlineData("UI.Assistant.Title")]
    [InlineData("UI.Assistant.Online")]
    [InlineData("UI.Assistant.IdleLead")]
    [InlineData("UI.Assistant.WillExamine")]
    [InlineData("UI.Assistant.ViewReport")]
    [InlineData("UI.Component.Cpu")]
    [InlineData("UI.Component.Gpu")]
    [InlineData("UI.Component.Ram")]
    [InlineData("UI.Component.Storage")]
    [InlineData("UI.Component.Windows")]
    [InlineData("UI.Feature.Diagnostic.Title")]
    [InlineData("UI.Feature.Diagnostic.Body")]
    [InlineData("UI.Feature.Gaming.Title")]
    [InlineData("UI.Feature.Gaming.Body")]
    [InlineData("UI.Feature.Security.Title")]
    [InlineData("UI.Feature.Security.Body")]
    [InlineData("UI.Feature.Report.Title")]
    [InlineData("UI.Feature.Report.Body")]
    [InlineData("UI.Footer.Version")]
    [InlineData("UI.Footer.Updates")]
    // ── Measurement spine (M1.5) ──
    [InlineData("Measure.NoPresentingFrames")]
    // ── AMD GPU honest status (§22.3) ──
    public void Key_exists_in_fr_and_en(string key)
    {
        var en = Localizer.Get(key, new CultureInfo("en"));
        var fr = Localizer.Get(key, new CultureInfo("fr"));
        // ResxLocalizer returns the key itself when missing — so a real value must differ from the key
        en.Should().NotBeNullOrEmpty().And.NotBe(key, $"key '{key}' missing from EN resx");
        fr.Should().NotBeNullOrEmpty().And.NotBe(key, $"key '{key}' missing from FR resx");
    }

    // 29/09/2026 : retour sur l'app, « l'app c'est bien plus que simplement la santé de ton PC ». L'accueil ne résume
    // plus DrDXT à un bilan de santé : il règle, mesure et prouve.
    [Fact]
    public void L_accueil_ne_resume_pas_DrDXT_a_la_sante_du_PC()
    {
        var fr = Localizer.Get("UI.Idle.HeadlineLead", new CultureInfo("fr")) + " " + Localizer.Get("UI.Idle.HeadlineAccent", new CultureInfo("fr"));
        var en = Localizer.Get("UI.Idle.HeadlineLead", new CultureInfo("en")) + " " + Localizer.Get("UI.Idle.HeadlineAccent", new CultureInfo("en"));
        fr.Should().NotContainEquivalentOf("santé");
        en.Should().NotContainEquivalentOf("health");
        // 29/09/2026 : retour sur l'app, « réglé au neurone près, pas correct avec un PC ». Un PC n'a pas de neurones ;
        // la précision se dit dans l'unité du joueur, l'image.
        fr.Should().NotContainEquivalentOf("neurone").And.Contain("image");
        en.Should().NotContainEquivalentOf("neuron").And.Contain("frame");
    }

    // 29/09/2026, critiques 3 et 4 : dans le sélecteur de scène, « Je ne suis pas en jeu (PC au repos) » tenait sur
    // trois lignes et « Entraînement / stand de tir » sur deux ; les cellules n'avaient pas la même hauteur.
    [Theory]
    [InlineData("Scan.Scene.Unknown")]
    [InlineData("Scan.Scene.Gameplay")]
    [InlineData("Scan.Scene.Training")]
    [InlineData("Scan.Scene.Menu")]
    [InlineData("Scan.Scene.HorsJeu")]
    public void Chaque_choix_de_scene_tient_sur_une_ligne(string key)
        => Localizer.Get(key, new CultureInfo("fr")).Length.Should().BeLessThanOrEqualTo(14);

    // 29/09/2026, critiques : « modèle flip » est un terme de développeur. Le réglage porte le nom que Windows lui
    // donne dans ses paramètres, celui que le joueur y retrouvera.
    [Fact]
    public void Le_reglage_VRR_porte_le_nom_que_Windows_lui_donne()
    {
        Localizer.Get("tweak.latency.vrrFlipModelOn.title", new CultureInfo("fr"))
            .Should().NotContainEquivalentOf("flip").And.Contain("jeux en fenêtre");
    }

    // 29/09/2026, quatrième critique : la même pilule « ▶ » servait à un test de cinq minutes, à une réparation qui
    // télécharge et à une remesure de trente secondes. Chaque capsule porte son verbe, et sa durée quand elle est connue.
    [Theory]
    [InlineData("Agir.Capsule.TestMemoire", "5 min")]
    [InlineData("Agir.Capsule.RemesureCpu", "45 s")]
    [InlineData("Agir.Capsule.RemesureMemoire", "30 s")]
    [InlineData("Agir.Capsule.Bcd", "Windows")]
    [InlineData("Agir.Capsule.Reparation", "long")]
    public void Chaque_capsule_Agir_dit_ce_qu_elle_fait(string key, string attendu)
    {
        var fr = Localizer.Get(key, new CultureInfo("fr"));
        fr.Should().NotBe(key).And.Contain(attendu);
        fr.Length.Should().BeLessThanOrEqualTo(18, "le libellé tient dans la capsule");
    }

    // 29/09/2026, quatrième critique : « 12 points à corriger » au-dessus de « 0 réglages à revoir · 7 lus sur 7 »
    // se lisaient comme deux comptes de la même chose. Le registre dit de quoi il parle : les réglages que DrDXT
    // sait poser lui-même.
    [Fact]
    public void Le_registre_dit_qu_il_compte_les_reglages_que_DrDXT_sait_poser()
    {
        Localizer.Get("UI.Register.LeversRead", new CultureInfo("fr")).Should().StartWith("Réglages que DrDXT pose lui-même");
        Localizer.Get("UI.Register.Problems", new CultureInfo("fr")).Should().Be("{0} à changer");
    }

    // 03/10/2026 (D8.4, revue n° 3) : l'en-tête de « À faire d'abord » rappelle le total et où sont les autres ;
    // à zéro, le registre dit « aucun à changer » plutôt que « 0 à changer ».
    [Fact]
    public void L_en_tete_a_faire_rappelle_le_total_et_le_registre_dit_aucun()
    {
        var fr = new CultureInfo("fr");
        string Loc(string k) => Localizer.Get(k, fr);
        ScanViewModel.EnTeteAFaire(Loc, 13).Should().Contain("13").And.Contain("par domaine");
        ScanViewModel.EnTeteAFaire(Loc, 0).Should().Be("À faire d'abord");
        ScanViewModel.EnTeteAFaire(Loc, 1).Should().Contain("ce point").And.NotContain("1 points");
        Loc("UI.Register.None").Should().Be("aucun à changer");
    }

    // 29/09/2026, quatrième critique : Optimiser, posé sous « À faire d'abord », laissait croire qu'il corrigeait la
    // liste ; il n'essaie que ses propres réglages. L'aperçu le dit et vient avant le bouton, séparé de la liste.
    [Fact]
    public void Optimiser_dit_qu_il_essaie_ses_propres_reglages_avant_le_bouton()
    {
        Localizer.Get("Optimiser.Apercu", new CultureInfo("fr")).Should().Contain("lui-même");

        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.App", "Views", "ScanView.xaml"));
        xaml.IndexOf("{Binding ApercuOptimisation}", StringComparison.Ordinal)
            .Should().BeLessThan(xaml.IndexOf("Style=\"{StaticResource CapsuleChrome}\"", StringComparison.Ordinal));
    }

    // 29/09/2026, troisième critique : « réglages bons, reste à revoir » au-dessus de « 12 points à corriger » se
    // lisait encore comme une contradiction. Quand il reste des points, le verdict ne dit rien de bon.
    [Fact]
    public void Un_verdict_avec_des_points_a_reprendre_ne_dit_rien_de_bon()
    {
        Localizer.Get("UI.Verdict.SanteACorriger", new CultureInfo("fr")).Should().NotContainEquivalentOf("bon");
        Localizer.Get("UI.Verdict.SanteACorriger", new CultureInfo("en")).Should().NotContainEquivalentOf("fine");
    }

    // 29/09/2026, seconde critique : « 12 points à corriger, aucun critique » à côté de dix arrêts brutaux
    // se lisait comme un mensonge. « Critique » est une gravité du moteur, pas une promesse au joueur : le
    // résumé compte, il ne rassure pas.
    [Theory]
    [InlineData("UI.Result.ACorrigerSansCritique")]
    [InlineData("UI.Result.ACorrigerSansCritique.Un")]
    public void Le_resume_ne_rassure_pas_sur_la_gravite(string key)
    {
        Localizer.Get(key, new CultureInfo("fr")).Should().NotContainEquivalentOf("critique");
        Localizer.Get(key, new CultureInfo("en")).Should().NotContainEquivalentOf("critical");
    }

    // 29/09/2026 : vu sur photo, le titre après scan tenait sur trois lignes en police de titre. Un verdict
    // se lit d'un coup d'œil : 30 caractères au plus derrière « Côté config : », soit deux lignes de titre.
    [Theory]
    [InlineData("UI.Verdict.Partial")]
    [InlineData("UI.Verdict.Clean")]
    [InlineData("UI.Verdict.Problems")]
    [InlineData("UI.Verdict.SanteACorriger")]
    public void Un_verdict_tient_en_un_coup_d_oeil(string key)
    {
        Localizer.Get(key, new CultureInfo("fr")).Length.Should().BeLessThanOrEqualTo(30);
    }

    // 29/09/2026 : critique de l'app, « aucun serveur configuré » en pied de page est du vocabulaire de
    // développeur ; le joueur doit lire ce que ça veut dire pour lui.
    [Fact]
    public void Le_pied_de_page_sans_serveur_parle_au_joueur()
    {
        // 03/10/2026 (tour G) : décision produit, DrDXT interroge le fabricant de la carte pour la version du pilote. Le pied
        // de page ne promet plus « aucun serveur » ; il dit exactement ce qui part sur le réseau.
        var fr = Localizer.Get("UI.Footer.Updates.NotConfigured", new CultureInfo("fr"));
        fr.Should().NotContain("configuré").And.NotContain("aucun serveur");
        fr.Should().Contain("pings").And.Contain("fabricant de ta carte graphique");
    }

    // 29/09/2026 : critique de l'app, « Maintiens pour lancer » ne disait pas qu'Entrée déclenche tout de suite :
    // au clavier, le geste restait à deviner.
    [Theory]
    [InlineData("Scan.Hold.Hint")]
    [InlineData("Memoire.Test.Maintenir")]
    [InlineData("Bcd.Rendre.Maintenir")]
    [InlineData("Cpu.Remesure.Maintenir")]
    [InlineData("Reparation.Windows.Maintenir")]
    public void Chaque_consigne_de_maintien_dit_que_Entree_suffit(string key)
    {
        Localizer.Get(key, new CultureInfo("fr")).Should().Contain("Entrée");
        Localizer.Get(key, new CultureInfo("en")).Should().Contain("Enter");
    }

    // 29/09/2026 : critique de l'app, « réglages bons, santé à corriger » au-dessus de « 0 leviers mal réglés »
    // et « 12 points à corriger » se lisait comme une contradiction. Le verdict dit où sont les points, et le
    // décompte parle de réglages, pas de « leviers ».
    [Theory]
    [InlineData("UI.Verdict.SanteACorriger")]
    [InlineData("UI.Register.Problem")]
    [InlineData("UI.Register.Problems")]
    [InlineData("UI.Register.LeversRead")]
    public void Le_verdict_parle_la_langue_du_joueur(string key)
    {
        var fr = Localizer.Get(key, new CultureInfo("fr"));
        fr.Should().NotBe(key);
        fr.Should().NotContainEquivalentOf("levier").And.NotContainEquivalentOf("santé");
    }

    // 03/10/2026 (D11.6 reporté, revue n° 5) : le titre disait « des points à reprendre » au-dessus de « 13 points à
    // corriger ». Un seul mot pour la même chose.
    [Fact]
    public void Le_titre_et_le_compteur_disent_le_meme_mot()
    {
        var fr = new CultureInfo("fr");
        Localizer.Get("UI.Verdict.SanteACorriger", fr).Should().Contain("à corriger");
        Localizer.Get("UI.Result.ACorrigerSansCritique", fr).Should().Contain("à corriger");
    }
}
