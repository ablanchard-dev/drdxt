using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>La ligne processeur : sa référence, sa remesure et ce qu'elle dit.</summary>
public sealed partial class CartesAgirTests
{
    // ── Remesurer le processeur a la demande ─────────────────────────────────────
    private sealed class FichierCpu(string? contenu) : DrDxt.Measurement.Cpu.ICpuBaselineFile
    {
        public string? Ecrit { get; private set; }
        public Result WriteAtomic(string json) { Ecrit = json; return Result.Ok(); }
        public Result<string?> Read() => Result<string?>.Ok(contenu);
    }

    private sealed class BancCpu(DrDxt.Measurement.Cpu.CpuBaseline? rendu) : DrDxt.Measurement.Cpu.ICpuSustainedBench
    {
        public int Appels { get; private set; }
        public DrDxt.Measurement.Cpu.CpuBaseline? Mesurer(string machineId) { Appels++; return rendu; }
    }

    private static DrDxt.Measurement.Cpu.CpuBaseline Ref(double rafale, double soutenu, double? watts = null)
        => new("m1", DateTimeOffset.UtcNow, 8, rafale, soutenu, 1.0, watts, watts is null ? null : 5);

    [Fact]
    public async Task Remesurer_remplace_la_phrase_du_processeur_par_la_nouvelle_mesure()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var banc = new BancCpu(Ref(1790, 1606, watts: 66.5));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)));
        await vm.ScanCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(0, "le scan relit la reference existante");
        vm.RemesureCpuVisible.Should().BeTrue();
        await vm.RemesurerProcesseurCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(1);
        vm.RemesureCpuFaite.Should().BeTrue();
        vm.CpuSustainedLabel.Should().Contain("limite de puissance longue");
        // Vu sur la photo du 25/09 : la carte du domaine gardait la phrase de l'ancienne mesure.
        vm.Domaines.Single(d => d.Cle == "processeur").Mesures.Should().Contain(m => m.Contains("limite de puissance longue"));
    }

    [Fact]
    public void La_phrase_du_processeur_nomme_le_reglage_BIOS_lu_quand_la_limite_est_prouvee()
    {
        var limite = new DrDxt.Measurement.Cpu.CpuDeficitSoutenu(10.3, 1.0, true, "Base.", LimiteDePuissance: true);
        var bios = new[] { new BiosSettingReading("CPU Cooler Tuning", 1, "Intel Default Settings", IsDefault: true) };

        ScanViewModel.PhraseProcesseur(limite, bios).Should()
            .Be("Base. Sur ta carte, les limites de puissance laissées sur Auto suivent le réglage BIOS "
                + "« CPU Cooler Tuning », lu ici : « Intel Default Settings ». C'est le profil qu'Intel et MSI "
                + "recommandent pour la stabilité des Core de 13e et 14e génération : DrDXT ne te conseille pas de "
                + "le changer, la puissance en plus se paierait en chaleur et en risque d'instabilité.");
        // Un autre profil est nommé sans la recommandation, qui ne vaut que pour celui d'Intel.
        var autre = new[] { new BiosSettingReading("CPU Cooler Tuning", 2, "MSI Performance Settings", IsDefault: false) };
        ScanViewModel.PhraseProcesseur(limite, autre).Should().EndWith("« MSI Performance Settings ».");
        ScanViewModel.PhraseProcesseur(limite, null).Should().Be("Base.", "sans lecture BIOS, rien n'est ajoute");
        ScanViewModel.PhraseProcesseur(limite with { LimiteDePuissance = false }, bios).Should()
            .Be("Base.", "sans limite prouvee, le reglage n'est pas mis en cause");
        ScanViewModel.PhraseProcesseur(null, bios).Should().BeNull();
        // La date ferme la phrase : le réglage BIOS se place avant elle.
        ScanViewModel.PhraseProcesseur(limite with { Lisible = "Base. Mesuré le 25/09/2026." }, bios).Should()
            .EndWith("en risque d'instabilité. Mesuré le 25/09/2026.");
    }

    // Le banc processeur charge tous les coeurs rapides plus de quarante secondes. Jeu lance,
    // ni le scan ni le bouton ne le lancent (sinon la reference memoire serait faussee).
    [Fact]
    public async Task Un_premier_scan_jeu_lance_ne_lance_pas_le_banc_processeur()
    {
        var fichier = new FichierCpu(null);
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), jeu: 4242);

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(0);
        fichier.Ecrit.Should().BeNull();
        vm.CpuSustainedLabel.Should().BeNull();
        // Pas de ligne, mais la carte, sa raison et son bouton : sans eux, un joueur qui scanne jeu lancé
        // (le parcours normal : le scan capture le jeu) n'obtiendrait jamais de mesure.
        vm.RemesureCpuVisible.Should().BeTrue();
        vm.CpuSauteJeu.Should().BeTrue();
        vm.ErreurRemesureCpu.Should().BeNull("une information, pas une erreur");
    }

    private static DrDxt.Measurement.PerformanceJournalStore CaptureDansCinqMinutes()
    {
        var store = new DrDxt.Measurement.PerformanceJournalStore(new JournalCpu());
        store.Ajouter(new DrDxt.Core.PerformanceSample(DateTimeOffset.UtcNow.AddMinutes(5), "m1", 164.1, 28, 5, 1.0, "cod"));
        return store;
    }

    [Fact]
    public async Task La_ligne_processeur_dit_quand_sa_reference_a_ete_prise_pendant_une_partie()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)).Replace(",\"JeuFermeVerifie\":false", ""));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            new BancCpu(null), new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CpuSustainedLabel.Should().Contain("Cpu.PriseEnJeu");
    }

    // Le rapport est fait pour être envoyé : une baisse de 30 % sans sa réserve y passerait pour un fait.
    [Fact]
    public async Task Le_rapport_porte_la_reserve_de_la_ligne_processeur()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        string? ecrit = null;
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, writeReport: (_, texte) => { ecrit = texte; return "r.txt"; },
            cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
                new BancCpu(null), new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ExportReportCommand.ExecuteAsync(null);

        ecrit.Should().Contain("en moins qu'au premier instant", "temoin : la phrase du processeur est au rapport")
            .And.Contain("Cpu.PriseEnJeu");
    }

    [Fact]
    public async Task Remesurer_une_reference_prise_en_partie_la_remplace_meme_si_la_remesure_est_bruitee()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var bruitee = new DrDxt.Measurement.Cpu.CpuBaseline("m1", DateTimeOffset.UtcNow, 8, 1000, 1075, 7.4);
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            new BancCpu(bruitee), new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CpuSustainedLabel.Should().Contain("Cpu.PriseEnJeu", "temoin : la reference est bien soupconnee");

        await vm.RemesurerProcesseurCommand.ExecuteAsync(null);

        fichier.Ecrit.Should().NotBeNull("un chiffre qu'on sait faux ne se garde pas contre une mesure honnete");
    }

    [Fact]
    public async Task Une_mesure_processeur_de_cette_version_ne_se_soupconne_pas()
    {
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(new FichierCpu(null))), journal: CaptureDansCinqMinutes());

        await vm.ScanCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(1, "temoin : la reference vient d'etre mesuree, jeu ferme");
        vm.CpuSustainedLabel.Should().NotBeNull().And.NotContain("Cpu.PriseEnJeu");
    }

    [Fact]
    public async Task Remesurer_le_processeur_jeu_lance_est_refuse_et_le_dit()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), jeu: 4242);
        await vm.ScanCommand.ExecuteAsync(null);
        var avant = vm.CpuSustainedLabel;
        avant.Should().NotBeNull("temoin : jeu lance, la reference existante est quand meme relue");

        await vm.RemesurerProcesseurCommand.ExecuteAsync(null);

        banc.Appels.Should().Be(0);
        fichier.Ecrit.Should().BeNull();
        vm.CpuSustainedLabel.Should().Be(avant);
        vm.ErreurRemesureCpu.Should().Be("Cpu.Remesure.JeuEnCours");
    }

    [Fact]
    public async Task Poste_au_repos_une_reference_suspecte_se_remesure_seule()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CpuSustainedLabel.Should().Contain("Cpu.PriseEnJeu", "temoin : la reference est soupconnee");
        vm.LirePoste = Poste(TimeSpan.FromMinutes(10), 2);

        await vm.TenterRemesureAuReposAsync(Maintenant, CancellationToken.None);

        banc.Appels.Should().Be(1);
        fichier.Ecrit.Should().NotBeNull();
        vm.CpuSustainedLabel.Should().NotContain("Cpu.PriseEnJeu", "la nouvelle reference est prise jeu ferme");

        await vm.TenterRemesureAuReposAsync(Maintenant.AddHours(2), CancellationToken.None);
        banc.Appels.Should().Be(1, "une reference saine ne se remesure plus");
    }

    // Pendant une autre opération longue (ici le test de refroidissement à pleine charge), la remesure de fond attend : sa
    // référence serait prise sous la charge du test.
    [Fact]
    public async Task Pendant_une_operation_longue_la_remesure_de_fond_attend()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)), journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LirePoste = Poste(TimeSpan.FromMinutes(10), 2);
        vm.TestRefroidissementEnCours = true;

        await vm.TenterRemesureAuReposAsync(Maintenant, CancellationToken.None);

        banc.Appels.Should().Be(0);
    }

    [Fact]
    public async Task Quelqu_un_au_clavier_rien_ne_se_mesure()
    {
        var banc = new BancCpu(Ref(1790, 1606));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260))))),
            journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LirePoste = Poste(TimeSpan.FromMinutes(1), 2);

        await vm.TenterRemesureAuReposAsync(Maintenant, CancellationToken.None);

        banc.Appels.Should().Be(0);
    }

    [Fact]
    public async Task Une_remesure_ratee_ne_se_retente_pas_avant_une_heure()
    {
        var banc = new BancCpu(null);
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            banc, new DrDxt.Measurement.Cpu.CpuBaselineStore(new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260))))),
            journal: CaptureDansCinqMinutes());
        await vm.ScanCommand.ExecuteAsync(null);
        vm.LirePoste = Poste(TimeSpan.FromMinutes(10), 2);

        await vm.TenterRemesureAuReposAsync(Maintenant, CancellationToken.None);
        await vm.TenterRemesureAuReposAsync(Maintenant.AddMinutes(20), CancellationToken.None);
        banc.Appels.Should().Be(1);

        await vm.TenterRemesureAuReposAsync(Maintenant.AddMinutes(61), CancellationToken.None);
        banc.Appels.Should().Be(2);
    }

    [Fact]
    public async Task Une_remesure_ratee_garde_la_phrase_et_le_dit()
    {
        var fichier = new FichierCpu(System.Text.Json.JsonSerializer.Serialize(Ref(1800, 1260)));
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null, cpu: new DrDxt.Measurement.Cpu.CpuBaselineService(
            new BancCpu(null), new DrDxt.Measurement.Cpu.CpuBaselineStore(fichier)));
        await vm.ScanCommand.ExecuteAsync(null);
        var avant = vm.CpuSustainedLabel;

        await vm.RemesurerProcesseurCommand.ExecuteAsync(null);

        vm.CpuSustainedLabel.Should().Be(avant);
        vm.RemesureCpuFaite.Should().BeFalse();
        vm.ErreurRemesureCpu.Should().Be("Cpu.Remesure.Echec");
        fichier.Ecrit.Should().BeNull();
    }
}
