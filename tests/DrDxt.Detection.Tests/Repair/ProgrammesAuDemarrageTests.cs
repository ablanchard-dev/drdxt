using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// 06/10/2026 (chantier B7) : ce qui se lance au démarrage, lu seulement. Les clés d'approbation gardent des restes de
// programmes désinstallés (relevé du 06/10 : Battle.net, Discord, Steam approuvés sans clé Run) : seul ce qui se lance
// vraiment compte.
public sealed class ProgrammesAuDemarrageTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2));
    private static Result<IReadOnlyList<ProgrammesAuDemarrage.Entree>> Ok(params ProgrammesAuDemarrage.Entree[] e) => Result<IReadOnlyList<ProgrammesAuDemarrage.Entree>>.Ok(e);
    private static ProgrammesAuDemarrage.Entree E(string nom, bool actif = true, string commande = @"C:\x.exe") => new(nom, actif, commande);
    private static bool Existe(string chemin) => !chemin.Contains("absent", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Un_programme_desactive_dans_le_gestionnaire_des_taches_n_est_pas_compte_actif()
    {
        var approuve = new Dictionary<string, byte[]> { ["LGHUB"] = [0x03, 0, 0, 0], ["OneDrive"] = [0x02, 0, 0, 0], ["SecurityHealth"] = [0x06, 0, 0, 0] };

        ProgrammesAuDemarrage.Actif("LGHUB", approuve).Should().BeFalse();
        ProgrammesAuDemarrage.Actif("OneDrive", approuve).Should().BeTrue();
        ProgrammesAuDemarrage.Actif("SecurityHealth", approuve).Should().BeTrue();
        // Sans entrée d'approbation, Windows le lance.
        ProgrammesAuDemarrage.Actif("Teams", approuve).Should().BeTrue();
    }

    [Fact]
    public void La_liste_dit_combien_sont_lances_et_le_cout_que_Windows_a_mesure()
    {
        var c = ProgrammesAuDemarrage.Juger(Ok(E("OneDrive"), E("MicrosoftEdgeAutoLaunch_E5F5B93CF6EF4BD5E8B2F77DF8472624"), E("LGHUB", false)),
            [new DemarrageEtDisque.Ralentisseur(T, "Microsoft OneDrive", 2300)], Existe).Single();

        c.Nom.Should().Be("programmes au démarrage");
        c.ValeurCourante.Should().Be("2 lancés, 1 désactivé");
        c.Explication.Should().Contain("OneDrive (Windows a mesuré jusqu'à 2,3 s de retard)")
            .And.Contain("MicrosoftEdgeAutoLaunch").And.NotContain("E5F5B93CF6EF")
            .And.Contain("Gestionnaire des tâches");
        c.Action.Should().Be(RepairAction.Recommandation);
    }

    [Fact]
    public void La_securite_de_Windows_est_dite_a_garder()
        => ProgrammesAuDemarrage.Juger(Ok(E("SecurityHealth")), [], Existe).Single().Explication.Should().Contain("Sécurité Windows, à garder");

    [Fact]
    public void Rien_au_demarrage_rien_n_est_dit()
        => ProgrammesAuDemarrage.Juger(Ok(), [], Existe).Should().BeEmpty();

    // Revue du 06/10 (F-8) : une lecture qui échoue n'est pas une liste courte.
    [Fact]
    public void Une_lecture_ratee_se_dit_non_lue_sans_compte()
    {
        var c = ProgrammesAuDemarrage.Juger(Result<IReadOnlyList<ProgrammesAuDemarrage.Entree>>.Fail(ErrorCodes.WmiFail, "refusé"), [], Existe).Single();

        c.Statut.Should().Be(RepairStatus.Unknown);
        c.ValeurCourante.Should().BeNull();
    }

    // F-9 : une entrée vers un programme désinstallé ne se lance pas.
    [Theory]
    [InlineData("\"C:\\Program Files\\X\\x.exe\" --tray", "C:\\Program Files\\X\\x.exe")]
    [InlineData("C:\\Program Files\\Y\\y.exe /background", "C:\\Program Files\\Y\\y.exe")]
    [InlineData("rundll32.exe C:\\z.dll,Lancer", "rundll32.exe")]
    [InlineData("", null)]
    public void La_cible_d_une_commande_se_lit_sans_ses_arguments(string commande, string? cible)
        => ProgrammesAuDemarrage.Cible(commande).Should().Be(cible);

    [Fact]
    public void Les_variables_d_environnement_sont_developpees()
        => ProgrammesAuDemarrage.Cible(@"%WINDIR%\explorer.exe /s").Should().Be(Environment.ExpandEnvironmentVariables(@"%WINDIR%\explorer.exe"));

    [Fact]
    public void Une_entree_morte_est_dite_et_n_est_pas_comptee_lancee()
    {
        var c = ProgrammesAuDemarrage.Juger(Ok(E("OneDrive"), E("Ancien", commande: @"C:\absent\a.exe")), [], Existe).Single();

        c.ValeurCourante.Should().Be("1 lancé, 0 désactivé, 1 entrée morte");
        c.Explication.Should().Contain("Ancien").And.Contain("n'existe plus");
    }

    // Relevé réel du 06/10 : un raccourci du dossier Démarrage, chemin avec espaces sans guillemets, n'est pas une entrée morte.
    [Fact]
    public void Un_raccourci_du_dossier_Demarrage_n_est_jamais_mort()
        => ProgrammesAuDemarrage.Juger(Ok(new ProgrammesAuDemarrage.Entree("BureauDistant", true,
                @"C:\absent\Start Menu\Programs\Startup\BureauDistant.lnk", DansRun: false)), [], Existe)
            .Single().ValeurCourante.Should().Be("1 lancé, 0 désactivé");

    // F-11 : rien de lancé, rien à couper.
    [Fact]
    public void Sans_programme_lance_aucun_geste()
    {
        var c = ProgrammesAuDemarrage.Juger(Ok(E("LGHUB", false)), [], Existe).Single();

        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().NotContain("Gestionnaire des tâches");
    }
}
