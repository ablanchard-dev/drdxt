using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Ce service existe parce que tout ce travail etait orphelin.
///
/// Mesure du 09/09/2026 : dix types construits en deux jours, **zero** reference par `DrDxt.App`
/// ou `DrDxt.Application`. Tout vivait dans `tools/repairaudit` — je branchais des lecteurs
/// orphelins dans un outil lui-meme orphelin.
///
/// La regression que ces tests empechent est precise et silencieuse : qu'une famille cesse d'etre
/// collectee. Rien ne leverait, rien ne rougirait ailleurs — le rapport serait simplement plus
/// court, et personne ne remarque une absence.
/// </summary>
public class RepairAuditServiceTests
{
    /// <summary>
    /// La frequence dite par le produit ne peut pas etre une moyenne muette.
    ///
    /// L'instance `_Total` fait la moyenne des 28 processeurs logiques, cœurs inactifs compris : mesure,
    /// 3 622 MHz pendant qu'une partie chargeait huit a dix cœurs a 3 861. Elle repond a une question que
    /// personne ne pose.
    ///
    /// La branche prise depend de la charge de la machine au moment du scan, donc ce test ne force
    /// pas laquelle : il exige qu'il y en ait toujours une, et que la mesure ne soit jamais rendue
    /// comme un etat sain : une frequence n'est ni bonne ni mauvaise, elle est observee.
    /// </summary>
    [Fact]
    public void LA_FREQUENCE_EST_TOUJOURS_DITE_ET_JAMAIS_COMME_UN_ETAT_SAIN()
    {
        var cpu = new RepairAuditService().Lire().Where(c => c.Domaine == "CPU").ToList();

        // Un seul nom, quelle que soit la charge de l'instant. Deux noms selon qu'un
        // cœur etait charge ou non faisaient ecrire au journal « Apparu » + « Disparu » a chaque
        // scan pris dans un autre etat : un faux changement recurrent, vu en executant l'audit.
        var frequence = cpu.Should().ContainSingle(c => c.Nom == "frequence effective",
            "le produit doit dire la frequence des cœurs qui TRAVAILLENT quand il y en a, "
            + "et sinon avouer que sa moyenne ne predit rien, sous le meme nom").Subject;

        frequence.Statut.Should().NotBe(RepairStatus.ExpectedDefault,
            "une frequence n'est pas un reglage conforme : elle s'observe, elle ne se certifie pas");
    }

    /// <summary>
    /// L'usure d'un disque s'observe, elle ne se certifie pas.
    ///
    /// Un compteur bas ne promet aucune duree de vie, et un disque qui ne publie pas son
    /// compteur n'est pas un disque neuf. Les deux se disent, aucun ne se tait.
    /// </summary>
    [Fact]
    public void L_USURE_DISQUE_EST_OBSERVEE_ET_JAMAIS_RENDUE_COMME_CONFORME()
    {
        // Le domaine porte aussi des constats conformes (jeux sur SSD, lien du SSD) ; ce test parle de l'usure.
        var stockage = new RepairAuditService().Lire().Where(c => c.Domaine == "STOCKAGE" && c.Nom.StartsWith("usure", StringComparison.Ordinal)).ToList();

        stockage.Should().NotBeEmpty("la famille doit etre collectee meme quand rien n'est usé");
        stockage.Should().OnlyContain(
            c => c.Statut != RepairStatus.ExpectedDefault,
            "une usure n'est pas un reglage conforme : elle s'observe, ou elle n'a pas pu etre lue");
    }

    [Theory]
    [InlineData("SERVICES")]
    [InlineData("MMCSS")]
    [InlineData("BCD")]
    [InlineData("STABILITE")]
    [InlineData("POWER")]
    [InlineData("CPU")]
    [InlineData("DISPLAY")]
    [InlineData("GPU DRIVER")]
    [InlineData("PAGEFILE")]
    [InlineData("BIOS")]
    [InlineData("THERMIQUE")]
    [InlineData("STOCKAGE")]
    public void CHAQUE_FAMILLE_DE_PURE_LECTURE_EST_COLLECTEE_SUR_CETTE_MACHINE(string domaine)
    {
        // Ce test EXECUTE le service sur la vraie machine. Un service qui perdrait une famille
        // ne produirait aucune erreur : le rapport serait juste plus court.
        new RepairAuditService().Lire()
            .Should().Contain(c => c.Domaine == domaine,
                $"la famille {domaine} doit etre collectee");
    }

    [Fact]
    public void LE_CONSTAT_DE_BRIDAGE_CPU_NE_CERTIFIE_JAMAIS_UNE_ABSENCE_DE_LIMITE()
    {
        // `Performance Limit Flags` ne voit pas les limites de puissance du paquet (RAPL) : sur
        // cette machine il vaut 0 pendant que la frequence tombe de 5292 a 3698 MHz. Un 0 est une
        // absence d'indicateur, pas une absence de limite — donc jamais ExpectedDefault.
        var c = new RepairAuditService().Lire()
            .Should().ContainSingle(x => x.Domaine == "CPU" && x.Nom == "bridage").Subject;
        c.Statut.Should().NotBe(RepairStatus.ExpectedDefault);
        // `% of Maximum Frequency` etait rendu « la limite est atteinte a 60 % ». Ce
        // compteur n'est pas une limite : au repos, sur cette machine, il passe de 56 a 99 % en deux
        // secondes. Une lecture instantanee ne dit rien ; le constat ne la cite plus.
        c.Explication.Should().NotContain("limite est atteinte");
    }

    // « la machine où DrDXT a été mis au point » dans une explication
    // affichée au joueur est du texte de développeur. Le fait reste (une machine de test), la tournure part.
    [Fact]
    public void Aucune_explication_affichee_ne_parle_de_la_mise_au_point_de_DrDXT()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "src", "DrDxt.Detection", "Repair", "RepairAuditService.cs"));

        System.Text.RegularExpressions.Regex.Matches(source, "\"[^\"\\n]*(mis au point|machine de test)[^\"\\n]*\"")
            .Should().BeEmpty("une chaîne affichée ne raconte pas la fabrication du produit (UI1, 08/10 : « machine de test » aussi)");
    }

    [Fact]
    public void LE_TYPE_DE_VIDAGE_EST_RENDU_UNE_SEULE_FOIS()
    {
        // Il a deja existe en DOUBLE dans ce produit, avec deux verdicts contradictoires sur la
        // meme valeur de registre — l'un declarait un defaut Windows que personne n'a publie.
        new RepairAuditService().Lire()
            .Count(c => c.Nom == "type de vidage").Should().Be(1);
    }

    [Fact]
    public void UN_JOURNAL_DE_SERVICES_ILLISIBLE_SE_DIT_ET_NE_DISPARAIT_PAS()
    {
        // « Je n'ai pas pu lire » ne doit jamais se confondre avec « aucun service n'echoue ».
        // Une famille muette est indistinguable d'une famille saine, et c'est la difference qui
        // decide de ce que le produit a le droit d'affirmer.
        var service = new RepairAuditService(new JournalEnPanne(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(), new WmiMuet());

        var c = service.Lire().Should().ContainSingle(x => x.Domaine == "SERVICES").Subject;
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("journal casse");
    }

    [Fact]
    public void AUCUN_ECHEC_DE_SERVICE_EST_UNE_REPONSE_ET_NON_UN_SILENCE()
    {
        // Le controle POSITIF : une machine saine doit produire un constat qui DIT qu'elle est
        // saine. Ne rien rendre laisserait croire que la famille n'a pas ete regardee.
        var service = new RepairAuditService(new JournalVide(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(), new WmiMuet());

        var c = service.Lire().Should().ContainSingle(x => x.Domaine == "SERVICES").Subject;
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.ValeurCourante.Should().Be("aucun");
    }

    [Fact]
    public void UNE_FAMILLE_QUI_LEVE_NE_COUTE_QUE_CETTE_FAMILLE_ET_LE_DIT()
    {
        // Le journal ne rend pas un Fail : il LEVE. Avant l'isolation, cette exception sortait de
        // Lire() et l'appelant perdait toutes les familles d'un coup, muettement.
        var constats = new RepairAuditService(new JournalQuiLeve(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(), new VidagesMuets(), new WmiMuet()).Lire();

        var c = constats.Should().ContainSingle(x => x.Domaine == "SERVICES").Subject;
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("journal explose");
        constats.Should().Contain(x => x.Domaine == "MMCSS").And.Contain(x => x.Domaine == "CPU");
    }

    private sealed class JournalQuiLeve : IServiceFailureLog
    {
        public Result<IReadOnlyList<ServiceFailure>> Depuis(DateTimeOffset depuis)
            => throw new InvalidOperationException("journal explose");
    }

    private sealed class JournalEnPanne : IServiceFailureLog
    {
        public Result<IReadOnlyList<ServiceFailure>> Depuis(DateTimeOffset depuis)
            => Result<IReadOnlyList<ServiceFailure>>.Fail(ErrorCodes.DetectionFailed, "journal casse");
    }

    private sealed class JournalVide : IServiceFailureLog
    {
        public Result<IReadOnlyList<ServiceFailure>> Depuis(DateTimeOffset depuis)
            => Result<IReadOnlyList<ServiceFailure>>.Ok([]);
    }

    // Le seuil d'usure est une convention du constructeur : il dit quand regarder, il ne prouve aucun tort.
    [Theory]
    [InlineData(79, RepairStatus.Observed)]
    [InlineData(80, RepairStatus.NonDefault)]
    [InlineData(100, RepairStatus.NonDefault)]
    public void L_usure_au_seuil_du_constructeur_est_a_regarder(int usure, RepairStatus attendu)
        => RepairAuditService.StatutDUsure(usure).Should().Be(attendu);
}
