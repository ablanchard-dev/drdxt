using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Les arrets brutaux doivent arriver a l'ecran, pas seulement etre lus.
///
/// Ces tests portent sur le PARCOURS, pas sur le lecteur. Le 09/09/2026, une famille a ete
/// branchee dans le service en annoncant qu'elle rendait visible la signature des scripts
/// d'optimisation — elle etait collectee, comptee, et `RepairTriage` la rangeait parmi les etats
/// « verifies », qui ne s'affichent jamais. Un test qui s'arrete au service aurait ete vert.
///
/// Ils passent donc tous par <see cref="RepairTriage.Trier"/> : c'est lui qui decide si un constat
/// est vu ou seulement compte.
///
/// Et le lecteur d'arrets n'atteignait l'ecran que via l'avis memoire, qui exige XMP actif.
/// Sur une machine au JEDEC, onze arrets en quatre-vingt-dix jours n'etaient dits nulle part.
/// </summary>
public class ArretsBrutauxTests
{
    private sealed class JournalFictif(Result<IReadOnlyList<DateTimeOffset>> reponse)
        : IUnexpectedShutdownLog
    {
        public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest) => reponse;
    }

    private static Result<IReadOnlyList<DateTimeOffset>> Arrets(int combien)
        => Result<IReadOnlyList<DateTimeOffset>>.Ok(
            Enumerable.Range(1, combien)
                .Select(i => DateTimeOffset.UtcNow.AddDays(-i * 3))
                .ToList());

    private static RepairFinding Constat(Result<IReadOnlyList<DateTimeOffset>> reponse)
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(),
                new JournalFictif(reponse), new MemoireMuette(), new VidagesMuets(), new WmiMuet())
            .Lire()
            .Single(c => c.Nom == "arrets brutaux");

    [Fact]
    public void Onze_arrets_ARRIVENT_a_l_ecran_et_pas_seulement_dans_le_compte()
    {
        var constat = Constat(Arrets(11));
        var triage = RepairTriage.Trier([constat]);

        triage.Attention.Should().ContainSingle()
            .Which.Nom.Should().Be("arrets brutaux",
                "un fait de cette taille ne doit pas finir dans les etats comptes mais jamais montres");
        constat.ValeurCourante.Should().Contain("11");
    }

    [Fact]
    public void Un_arret_isole_est_compte_sans_etre_montre()
    {
        var triage = RepairTriage.Trier([Constat(Arrets(1))]);

        triage.Attention.Should().BeEmpty("un arret isole n'est pas un motif : une coupure de "
                                          + "courant en est un aussi");
        triage.Verifies.Should().ContainSingle();
    }

    [Fact]
    public void Aucun_arret_est_une_reponse_et_pas_un_silence()
    {
        var constat = Constat(Result<IReadOnlyList<DateTimeOffset>>.Ok(new List<DateTimeOffset>()));

        constat.Statut.Should().Be(RepairStatus.ExpectedDefault);
        constat.ValeurCourante.Should().Be("aucun");
    }

    [Fact]
    public void Un_journal_illisible_ne_passe_JAMAIS_pour_une_machine_saine()
    {
        var constat = Constat(Result<IReadOnlyList<DateTimeOffset>>.Fail(
            ErrorCodes.WmiFail, "acces refuse"));
        var triage = RepairTriage.Trier([constat]);

        constat.Statut.Should().Be(RepairStatus.Unknown);
        triage.Verifies.Should().BeEmpty("un trou de lecture n'est pas un etat verifie");
        triage.NonLus.Should().ContainSingle();
    }

    // ── 16/09/2026 : le journal dit de quel arret il s'agit, et l'audit doit le dire aussi ──

    private sealed class JournalDetaille(IReadOnlyList<ArretBrutal> arrets) : IUnexpectedShutdownLog
    {
        public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest)
            => Result<IReadOnlyList<DateTimeOffset>>.Ok(ArretBrutal.HorsBouton(arrets).Select(a => a.Quand).ToList());

        public Result<IReadOnlyList<ArretBrutal>> Detail(DateTimeOffset earliest)
            => Result<IReadOnlyList<ArretBrutal>>.Ok(arrets);
    }

    [Fact]
    public void Le_constat_separe_ecrans_bleus_arrets_sans_code_et_appuis_sur_le_bouton()
    {
        // La repartition lue le 16/09/2026 sur la machine de reference : 2 ecrans bleus, 1 arret
        // tout a zero, 1 appui long sur le bouton, 7 arrets pendant une transition d'alimentation.
        var maintenant = DateTimeOffset.UtcNow;
        var arrets = new List<ArretBrutal>
        {
            new(maintenant.AddDays(-12), 239, false, false),
            new(maintenant.AddDays(-14), 340, false, false),
            new(maintenant.AddDays(-29), 0, false, false),
            new(maintenant.AddDays(-48), 0, true, true),
        };
        arrets.AddRange(Enumerable.Range(0, 7).Select(i => new ArretBrutal(maintenant.AddDays(-43 - i * 3), 0, false, true)));

        var constat = new RepairAuditService(new ServicesMuets(), new NoyauMuet(),
                new JournalDetaille(arrets), new MemoireMuette(), new VidagesMuets(), new WmiMuet())
            .Lire()
            .Single(c => c.Nom == "arrets brutaux");

        constat.ValeurCourante.Should().StartWith("10 ", "l'appui long sur le bouton est un geste, pas une instabilite");
        constat.Explication.Should().Contain("2 écrans bleus")
            .And.Contain("0x00000154").And.Contain("0x000000EF")
            .And.Contain("1 arrêt sans aucun code")
            .And.Contain("7 avec une mise en veille signalée en cours")
            .And.Contain("1 appui long sur le bouton");
        constat.Explication.Should().NotContain("—", "pas de tiret cadratin dans ce que le client lit");
    }
    // DOC-QUALITE (09/10/2026) : sur la machine de référence, l'en-tête disait « 11 redémarrages non propres : ce n'est plus
    // un accident isolé » et le test mémoire « avec 11 arrêts brutaux », alors que 8 sont signalés pendant une mise en veille
    // (sens non documenté par Microsoft). Le chiffre reste, la part en veille est dite dès la première phrase.
    [Fact]
    public void L_en_tete_et_le_test_memoire_disent_la_part_en_veille_des_la_premiere_phrase()
    {
        var maintenant = DateTimeOffset.UtcNow;
        var arrets = new List<ArretBrutal>
        {
            new(maintenant.AddDays(-12), 239, false, false),
            new(maintenant.AddDays(-14), 340, false, false),
            new(maintenant.AddDays(-29), 0, false, false),
        };
        arrets.AddRange(Enumerable.Range(0, 7).Select(i => new ArretBrutal(maintenant.AddDays(-43 - i * 3), 0, false, true)));
        var lus = new RepairAuditService(new ServicesMuets(), new NoyauMuet(),
                new JournalDetaille(arrets), new MemoireMuette(), new VidagesMuets(), new WmiMuet()).Lire().ToList();

        lus.Single(c => c.Nom == "arrets brutaux").Explication
            .Should().StartWith("10 redémarrages non propres, dont 7 pendant une mise en veille signalée : ");
        lus.Single(c => c.Nom == "test memoire Windows").Explication
            .Should().StartWith("Avec 10 arrêts brutaux en 90 jours, dont 7 pendant une mise en veille signalée, ");
    }

    // Revue adverse du docteur (09/10/2026, R8 et R9) : « à corriger » sans rien à faire. Des arrêts répétés portent le
    // geste de Microsoft (page Event ID 41) ; un service qui échoue sur 3 journées, le sien.
    [Fact]
    public void Des_arrets_repetes_portent_le_geste_de_Microsoft()
    {
        var c = Constat(Arrets(11));
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("couper tout surcadençage").And.Contain("tester la mémoire");
    }

    private sealed class ServicesQuiEchouent : IServiceFailureLog
    {
        public Result<IReadOnlyList<ServiceFailure>> Depuis(DateTimeOffset depuis) => Result<IReadOnlyList<ServiceFailure>>.Ok(
            [new ServiceFailure("Service X", 7000, 4, 3, DateTimeOffset.UtcNow.AddDays(-9), DateTimeOffset.UtcNow.AddDays(-1), "n'a pas pu démarrer")]);
    }

    [Fact]
    public void Un_service_qui_echoue_sur_trois_journees_porte_son_geste()
    {
        var c = new RepairAuditService(new ServicesQuiEchouent(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(), new WmiMuet())
            .Lire().Single(x => x.Nom == "Service X");
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("mettre à jour ou réinstaller le logiciel");
    }
}
