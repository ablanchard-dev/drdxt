using DrDxt.Application.Parties;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;
using static DrDxt.Detection.Repair.PlantagesDeJeu;

namespace DrDxt.Detection.Tests.Repair;

// Les plantages et blocages des jeux, jugés sur des événements construits ; aucun test ne lit le vrai journal.
public sealed class PlantagesDeJeuTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly HashSet<string> Cod = new(StringComparer.OrdinalIgnoreCase) { "cod.exe" };
    // Relevé sur la machine de référence : le paquet du pilote installé, et celui (remplacé) où vivait le module fautif du 09/07.
    private static readonly PaquetGraphique[] Nvidia = [new(Prefixe("nvmdi.inf_amd64_3990df11758b8744")!, "NVIDIA")];

    private static Evenement Plantage(int joursAvant, string module = "cod.exe", string code = "c0000005", string? paquet = null, string exe = "cod.exe")
        => new(Maintenant.AddDays(-joursAvant), exe, false, module, code, paquet);

    private static RepairFinding Un(IReadOnlyList<Evenement>? lus, IReadOnlyList<Changement>? changements = null, IReadOnlySet<string>? jeux = null)
        => Juger(lus, jeux ?? Cod, Nvidia, changements ?? [], Maintenant).Should().ContainSingle().Subject;

    // Un jeu jamais relevé en partie (« cod26-cod.exe ») compte quand son programme vit dans un dossier de
    // jeux installé ; un programme hors de ces dossiers, non.
    [Fact]
    public void Un_jeu_jamais_releve_compte_s_il_vit_dans_un_dossier_de_jeux()
    {
        Evenement Crash(string exe, string chemin) => new(Maintenant.AddDays(-2), exe, false, exe, "c0000005", Chemin: chemin);
        var lus = new[]
        {
            Crash("cod26-cod.exe", @"C:\Program Files (x86)\Call of Duty\_retail_\cod26-cod.exe"),
            Crash("wmiprvse.exe", @"C:\WINDOWS\system32\wbem\wmiprvse.exe"),
        };

        var c = Juger(lus, new HashSet<string>(), Nvidia, [], Maintenant, [@"C:\Program Files (x86)\Call of Duty"]).Single();

        c.Explication.Should().Contain("cod26-cod.exe").And.NotContain("wmiprvse.exe");
    }

    [Fact]
    public void Un_journal_illisible_se_dit_non_lu_jamais_aucun_plantage()
    {
        var c = Un(null);
        c.Statut.Should().Be(RepairStatus.Unknown);
        c.Explication.Should().Contain("non lu");
    }

    [Fact]
    public void Sans_jeu_connu_rien_n_est_compte_et_ce_n_est_pas_aucun_plantage()
    {
        var c = Un([Plantage(1)], jeux: new HashSet<string>());
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("Ce n'est pas « aucun plantage »");
    }

    [Fact]
    public void Lu_sans_plantage_de_jeu_les_autres_programmes_ne_comptent_pas()
    {
        var c = Un([Plantage(1, exe: "wmiprvse.exe", module: "ntdll.dll"), Plantage(40)]);
        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.ValeurCourante.Should().Be("aucun en 30 jours");
    }

    [Fact]
    public void Trois_plantages_avec_le_pilote_graphique_d_une_ancienne_version_sont_relies_a_la_carte()
    {
        var vieux = "nvmdi.inf_amd64_72f1798f54a8a57a";
        var c = Un([Plantage(1, "nvwgf2umx.dll", paquet: vieux), Plantage(3, "nvwgf2umx.dll", paquet: vieux), Plantage(5),
            new(Maintenant.AddDays(-2), "cod.exe", true)]);

        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.ValeurCourante.Should().Be("3 plantage(s), 1 blocage(s) en 30 jours");
        c.Explication.Should().Contain("nvwgf2umx.dll (2 sur 3), le pilote graphique NVIDIA")
            .And.Contain("STATUS_ACCESS_VIOLATION");
    }

    [Fact]
    public void Ce_que_designe_le_module_n_est_dit_que_quand_c_est_etabli()
    {
        Designation(Plantage(1), Nvidia).Should().StartWith("le jeu lui-même");
        Designation(Plantage(1, "unknown"), Nvidia).Should().Contain("pas nommé");
        Designation(Plantage(1, "d3d12core.dll"), Nvidia).Should().StartWith("DirectX");
        Designation(Plantage(1, "ntdll.dll"), Nvidia).Should().Contain("rarement la cause");
        Designation(Plantage(1, "x.dll", paquet: "u0412345.inf_amd64_aa"), Nvidia).Should().Contain("pas celui d'une carte graphique");
        Designation(Plantage(1, "EasyAntiCheat_EOS.dll"), Nvidia).Should().Contain("ne classe pas");
    }

    // Les pilotes des cartes non lus, rien n'est affirmé sur la carte, ni oui ni non.
    [Fact]
    public void Sans_pilotes_de_carte_lus_rien_n_est_affirme_sur_la_carte()
        => Designation(Plantage(1, "x.dll", paquet: "u0412345.inf_amd64_aa"), []).Should().Contain("pas vérifiée")
            .And.NotContain("pas celui d'une carte graphique");

    [Fact]
    public void Le_lien_avec_un_changement_est_une_correlation_jamais_une_cause()
    {
        var change = new Changement(Maintenant.AddDays(-4), "le réglage du BIOS « X » passé à « Y »");
        var pose = new Changement(Maintenant.AddDays(-2), "hags", null, "pose:hags");
        var c = Un([Plantage(1), Plantage(3), Plantage(10)], [change, pose]);

        c.Explication.Should().Contain("2 de ces incidents depuis le réglage du BIOS « X » passé à « Y » (03/10), 1 avant")
            .And.Contain("corrélation de dates, pas une cause prouvée")
            .And.NotContain("hags");
    }

    [Fact]
    public void Un_plantage_isole_est_une_observation_et_le_texte_ne_porte_aucun_chemin()
    {
        var c = Un([Plantage(2, "nvwgf2umx.dll", paquet: "nvmdi.inf_amd64_72f1798f54a8a57a")]);
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().NotContain("\\").And.NotContain("FileRepository");
    }

    // La racine Steam contient Steam lui-même ; son navigateur intégré qui plante n'est pas un jeu.
    [Fact]
    public void Steam_qui_plante_sous_sa_propre_racine_n_est_pas_un_plantage_de_jeu()
    {
        var lus = new[] { new Evenement(Maintenant.AddDays(-1), "steamwebhelper.exe", false, "libcef.dll", "c0000005",
            Chemin: @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe") };

        var c = Juger(lus, new HashSet<string>(), Nvidia, [], Maintenant, [@"C:\Program Files (x86)\Steam"]).Single();

        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
    }
}
